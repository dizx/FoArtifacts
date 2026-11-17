using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class JournalTests
{
    private static JournalEntry E(string id, string name, string model = "ContosoFleet", string kind = "Table") =>
        new(id, kind, name, model, $"/pkg/{model}/{name}.xml");

    private static readonly string[] Touch = ["CFltVehicle", "CFltVehicleId", "CustTable"];

    private static RollbackDecision Decide(string? anchor, params JournalEntry[] newestFirst) =>
        RollbackPlanner.Decide(anchor, newestFirst, 2000, "ContosoFleet", Touch, Array.Empty<string>());

    [Fact]
    public void Parses_d365fo_journal_output()
    {
        using var doc = JsonDocument.Parse("""
            {"journalDirectory":"/x","count":1,"totalCount":1,"entries":[{"id":"6f50","timestampUtc":"2026-11-16T10:58:34+00:00",
             "command":"scaffold-write","targetType":"AotObject","kind":"SecurityRole","name":"FmTruckManager","model":"TestModel",
             "operation":"Create","writePath":"Disk","target":"/p/AxSecurityRole/FmTruckManager.xml","hasPreImage":false,"isTombstone":true}]}
            """);
        var entry = Assert.Single(Journal.Parse(doc.RootElement));
        Assert.Equal(new JournalEntry("6f50", "SecurityRole", "FmTruckManager", "TestModel", "/p/AxSecurityRole/FmTruckManager.xml"), entry);
    }

    [Fact]
    public void Parse_tolerates_missing_data() => Assert.Empty(Journal.Parse(default));

    [Fact]
    public void Builds_list_and_undo_arguments()
    {
        Assert.Equal(new[] { "journal", "list", "-n", "2000", "--output", "json" }, Journal.ListArgs(2000));
        Assert.Equal(new[] { "undo", "--steps", "3", "--output", "json" }, Journal.UndoArgs(3));
    }

    [Fact]
    public void Entries_above_the_anchor_belong_to_the_batch()
    {
        var d = Decide("a", E("c", "CFltVehicle"), E("b", "CFltVehicleId"), E("a", "Older"), E("0", "Oldest"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void An_empty_journal_before_the_batch_means_every_entry_is_ours()
    {
        var d = Decide(null, E("b", "CFltVehicle"), E("a", "CFltVehicleId"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void Nothing_written_means_nothing_to_undo()
    {
        var d = Decide("a", E("a", "Older"));
        Assert.True(d.CanUndo);
        Assert.Equal(0, d.Steps);
    }

    [Fact]
    public void A_missing_anchor_blocks_the_undo()
    {
        var d = Decide("gone", E("b", "CFltVehicle"));
        Assert.False(d.CanUndo);
        Assert.Contains("not among the newest", d.Reason);
    }

    [Fact]
    public void A_full_journal_without_an_anchor_blocks_the_undo()
    {
        var d = RollbackPlanner.Decide(null, [E("b", "CFltVehicle"), E("a", "CFltVehicleId")], 2, "ContosoFleet", Touch, Array.Empty<string>());
        Assert.False(d.CanUndo);
    }

    [Fact]
    public void A_write_to_another_model_blocks_the_undo()
    {
        var d = Decide("a", E("c", "CFltVehicle"), E("b", "Other", model: "OtherModel"), E("a", "Older"));
        Assert.False(d.CanUndo);
        Assert.Contains("not from this batch", d.Reason);
        Assert.Contains("Other (OtherModel)", d.Reason);
        Assert.Equal(2, d.Entries.Count);
    }

    [Fact]
    public void A_write_with_an_unrelated_name_blocks_the_undo() =>
        Assert.False(Decide("a", E("b", "SalesTable"), E("a", "Older")).CanUndo);

    [Theory]
    [InlineData("CustTable_Extension")]
    [InlineData("CustTable.ContosoFleet")]
    [InlineData("CFltVehicleDP")]
    [InlineData("cfltvehicle")]
    public void Derived_names_count_as_ours(string journalName) =>
        Assert.True(Decide("a", E("b", journalName), E("a", "Older")).CanUndo);

    [Fact]
    public void Model_comparison_ignores_case() =>
        Assert.True(Decide("a", E("b", "CFltVehicle", model: "contosofleet"), E("a", "Older")).CanUndo);

    [Fact]
    public void A_path_a_task_reported_counts_as_ours()
    {
        var entry = E("b", "SomethingElse");
        var d = RollbackPlanner.Decide("a", [entry, E("a", "Older")], 2000, "ContosoFleet", Touch, [entry.Target]);
        Assert.True(d.CanUndo);
    }
}
