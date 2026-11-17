using System.Text.Json;
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.IntegrationTests.D365foSandbox;

namespace FoArtifacts.IntegrationTests;

public class BatchIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireD365fo() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    private static TaskInput[] TruckFeature() =>
    [
        T("enum", "VehicleKind", """{"value":["Light:0","Heavy:1"],"label":"Vehicle kind"}"""),
        T("edt", "TruckId", """{"base-type":"String","size":20}"""),
        T("table", "Truck", """{"pattern":"main","field":["TruckId:TruckId:mandatory","Kind:VehicleKind","Vin:VIN"]}"""),
        T("form", "Truck", """{"pattern":"SimpleListDetails","table":"Truck","caption":"Trucks","field":["TruckId","Kind","Vin"]}"""),
        T("menu-item", "Truck", """{"kind":"Display","object":"Truck","object-type":"Form","label":"Trucks"}"""),
        T("privilege", "TruckMaintain", """{"entry-point":"Truck","entry-kind":"MenuItemDisplay","access":"Delete"}"""),
        T("duty", "TruckMaintain", """{"privilege":"TruckMaintain"}"""),
        T("role", "TruckManager", """{"duty":"TruckMaintain"}"""),
    ];

    [Fact]
    public async Task A_feature_is_created_in_dependency_order_and_is_findable()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", TruckFeature()), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        Assert.Equal(7, result.Stages.Count);
        var table = await File.ReadAllTextAsync(Path.Combine(box.ModelDir, "AxTable", "FxTruck.xml"), Ct);
        Assert.Contains("AxTableFieldEnum", table);
        Assert.Contains("<EnumType>FxVehicleKind</EnumType>", table);
        Assert.Contains("<ExtendedDataType>FxTruckId</ExtendedDataType>", table);
        Assert.Contains("<ExtendedDataType>VIN</ExtendedDataType>", table);
        Assert.True((await box.Cli.RunAsync(["get", "table", "FxTruck", "--output", "json"], Ct)).Ok);
        var coverage = await MustAsync(box.Cli, ["security", "coverage", "FxTruck", "--type", "MenuItemDisplay", "--output", "json"], Ct);
        Assert.Contains("FxTruckManager", coverage.Data.GetRawText());
    }

    [Fact]
    public async Task A_failure_in_the_last_stage_rolls_back_everything()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var tasks = TruckFeature().Append(T("role", "Broken", """{"duty":"TruckMaintain","bogus":"x"}""")).ToArray();

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct);

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Failure!.Code);
        Assert.Equal("role FxBroken", result.Failure.Task);
        Assert.True(result.Rollback!.RolledBack, Summary.ToJson(result));
        Assert.Empty(box.ChangesSinceSnapshot());
        var search = await MustAsync(box.Cli, ["search", "any", "FxTruck", "--output", "json"], Ct);
        Assert.Equal(0, search.Data.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Running_a_batch_twice_fails_without_touching_the_first_run()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        TaskInput[] tasks = [T("enum", "VehicleKind", """{"value":["Light:0"]}"""), T("edt", "TruckId")];
        Assert.True((await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct)).Ok);
        box.TakeSnapshot();

        var second = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct);

        Assert.False(second.Ok);
        Assert.Equal("WRITE_FAILED", second.Failure!.Code);
        Assert.True(second.Rollback!.RolledBack);
        Assert.Empty(box.ChangesSinceSnapshot());
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var before = await MustAsync(box.Cli, Journal.ListArgs(5), Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", TruckFeature(), DryRun: true), Ct);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Empty(box.ChangesSinceSnapshot());
        var after = await MustAsync(box.Cli, Journal.ListArgs(5), Ct);
        Assert.Equal(before.Data.GetRawText(), after.Data.GetRawText());
    }

    [Fact]
    public async Task Non_ascii_and_special_characters_reach_the_file()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var options = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["value"] = new[] { "A:0" },
            ["label"] = "Kjøretøy & \"lastebil\"",
        });

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", [T("enum", "Kind", options)]), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        var xml = await File.ReadAllTextAsync(Path.Combine(box.ModelDir, "AxEnum", "FxKind.xml"), Ct);
        Assert.Contains("Kjøretøy &amp;", xml);
        Assert.Contains("lastebil", xml);
    }

    [Fact]
    public async Task Rolling_back_a_change_to_an_existing_table_restores_it()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx",
            [T("find-methods", "FmVehicle"), T("role", "Broken", """{"bogus":"x"}""")]), Ct);

        Assert.Contains(result.Completed, o => o.Task.Type == "find-methods");
        Assert.True(result.Rollback!.RolledBack, Summary.ToJson(result));
        Assert.Empty(box.ChangesSinceSnapshot());
    }

    [Fact]
    public async Task Rolling_back_a_table_relation_restores_the_table()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        // The form runs after the table-relation in the same stage and fails on a bad option.
        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx",
        [
            T("table", "Rel", """{"field":"Vin:VIN"}"""),
            T("table-relation", "Rel"),
            T("form", "Broken", """{"table":"Rel","bogus":"x"}"""),
        ]), Ct);

        Assert.Contains(result.Completed, o => o.Task.Type == "table-relation");
        Assert.Equal("form FxBroken", result.Failure!.Task);
        Assert.True(result.Rollback!.RolledBack, Summary.ToJson(result));
        Assert.Empty(box.ChangesSinceSnapshot());
    }

    [Fact]
    public async Task A_label_file_d365fo_cannot_undo_is_reported_not_hidden()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        // d365fo journals the AxLabelFile manifest but not its .label.txt.
        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx",
            [T("label-file", "FxLabels", """{"entry":"Hello=Hello"}"""), T("role", "Broken", """{"bogus":"x"}""")]), Ct);

        Assert.False(result.Rollback!.RolledBack);
        var leftover = Assert.Single(result.Rollback.LeftBehind);
        Assert.EndsWith(".label.txt", leftover);
        Assert.True(File.Exists(leftover));
        Assert.Equal(new[] { "added " + Path.GetRelativePath(box.Packages, leftover) }, box.ChangesSinceSnapshot());
    }

    [Fact]
    public void Every_option_in_the_reference_map_exists_in_d365fo()
    {
        Assert.SkipWhen(CheckoutPath is null, "Run scripts/build-d365fo.sh first.");
        var source = string.Concat(Directory
            .EnumerateFiles(Path.Combine(CheckoutPath!, "src", "D365FO.Cli", "Commands", "Generate"), "*.cs")
            .Select(File.ReadAllText));
        var map = ReferenceMap.LoadEmbedded();
        var options = map.Commands.Values
            .SelectMany(r => r.Refs.Select(x => x.Option).Concat(r.Defines.Select(d => d.Option)))
            .Distinct();
        foreach (var option in options)
            Assert.True(
                source.Contains($"CommandOption(\"--{option} ") || source.Contains($"CommandOption(\"--{option}\")"),
                $"--{option} is not a d365fo generate option");
    }
}
