using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class CommandBuilderTests
{
    private static ExpandedTask Only(string type, ExpandedBatch batch) => Assert.Single(batch.Tasks, t => t.Type == type);

    [Fact]
    public void Builds_a_generate_command_that_installs_into_the_model()
    {
        var table = Only("table", Expanded("CFlt",
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"pattern":"main","field":["VehicleId:VehicleId:mandatory"],"label":"Vehicles"}""")));

        Assert.Equal(
            new[]
            {
                "generate", "table", "CFltVehicle", "--pattern=main", "--field=VehicleId:CFltVehicleId:mandatory",
                "--label=Vehicles", "--install-to", "ContosoFleet", "--no-refresh", "--output", "json",
            },
            CommandBuilder.Build(table, "ContosoFleet"));
    }

    [Fact]
    public void Flags_follow_the_valued_options()
    {
        var cls = Only("class", Expanded("CFlt", T("class", "Helper", """{"non-final":true,"extends":"Object"}""")));
        Assert.Equal(
            new[] { "generate", "class", "CFltHelper", "--extends=Object", "--non-final", "--install-to", "M", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(cls, "M"));
    }

    [Fact]
    public void Leading_arguments_come_before_the_name()
    {
        var ext = Only("extension", Expanded("CFlt", T("extension", "CustTable", """{"kind":"Table","field":"Flag:NoYes"}""")));
        Assert.Equal(
            new[] { "generate", "extension", "Table", "CustTable", "--field=Flag:NoYes", "--install-to", "M", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(ext, "M"));
    }

    [Fact]
    public void Table_augmenting_commands_apply_to_a_file()
    {
        var find = Only("find-methods", Expanded("CFlt", T("find-methods", "CustTable")));
        Assert.Equal(
            new[] { "generate", "find-methods", "CustTable", "--apply-to", "/pkg/AxTable/CustTable.xml", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(find, "M", "/pkg/AxTable/CustTable.xml"));
        Assert.Contains("<file of table CustTable>", CommandBuilder.Build(find, "M"));
    }

    [Fact]
    public void Unusual_characters_are_passed_unchanged()
    {
        const string label = "Kjøretøy & \"lastebil\" — 100%";
        var enumTask = Only("enum", Expanded("CFlt", T("enum", "Kind", $$"""{"label":{{System.Text.Json.JsonSerializer.Serialize(label)}}}""")));
        Assert.Contains("--label=" + label, CommandBuilder.Build(enumTask, "M"));
    }

    [Theory]
    [InlineData("-x y")]
    [InlineData("--")]
    [InlineData("-Kj")]
    public void Values_starting_with_a_dash_are_attached_to_their_option(string value)
    {
        // d365fo's parser reads a separate "-..." token as another option; "--key=value" keeps it a value.
        var enumTask = Only("enum", Expanded("CFlt", T("enum", "Kind", $$"""{"label":{{System.Text.Json.JsonSerializer.Serialize(value)}}}""")));
        Assert.Contains("--label=" + value, CommandBuilder.Build(enumTask, "M"));
    }

    [Fact]
    public void Render_quotes_only_arguments_that_need_it()
    {
        Assert.Equal(
            "d365fo generate enum CFltKind \"--label=Kjøretøy & x\" --value=A:0",
            CommandBuilder.Render(new[] { "generate", "enum", "CFltKind", "--label=Kjøretøy & x", "--value=A:0" }));
    }
}
