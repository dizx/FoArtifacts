using FoArtifacts.Core;
using Xunit;

namespace FoArtifacts.Tests;

public class ReferenceMapTests
{
    // `generate` subcommands registered in d365fo's src/D365FO.Cli/CliApp.cs at the pinned commit.
    private static readonly string[] D365foGenerateCommands =
    [
        "table", "table-relation", "find-methods", "class", "coc", "form", "datasource-method",
        "control-method", "form-clone", "simple-list", "entity", "extension", "event-handler",
        "privilege", "duty", "role", "report", "report-extension", "sysoperation", "number-sequence",
        "workflow", "menu-item", "edt", "enum", "query", "view", "map", "business-event",
        "custom-service", "migration-script", "runbase", "security-policy", "systest",
        "configuration-key", "form-part", "label-file", "menu", "resource", "tile",
        "workflow-category", "composite-entity", "aggregate-entity",
    ];

    private static readonly ReferenceMap Map = ReferenceMap.LoadEmbedded();

    [Fact]
    public void Covers_every_d365fo_generate_command_except_the_deprecated_simple_list()
    {
        var expected = D365foGenerateCommands.Where(c => c != "simple-list").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(41, expected.Count);
        Assert.Equal(expected, Map.Types);
    }

    [Fact]
    public void Is_pinned_to_the_tested_d365fo_commit() =>
        Assert.Equal("c1761561269709657484e596a5c3d378ba9a5b18", Map.D365foCommit);

    [Fact]
    public void Uses_only_known_kinds()
    {
        foreach (var (type, rule) in Map.Commands)
        {
            var kinds = rule.Name.Kinds.Concat(rule.Produces)
                .Concat(rule.Defines.SelectMany(d => d.Kinds))
                .Concat(rule.Refs.SelectMany(r => r.Kinds));
            foreach (var kind in kinds)
                Assert.True(kind == ReferenceMap.AnyKind || ReferenceMap.KnownKinds.Contains(kind), $"{type}: unknown kind '{kind}'");
        }
    }

    [Fact]
    public void Rules_are_well_formed()
    {
        foreach (var (type, rule) in Map.Commands)
        {
            Assert.Contains(rule.Name.Role, new[] { NameRoles.Define, NameRoles.Reference, NameRoles.Literal });
            Assert.Contains(rule.Install, new[] { InstallModes.InstallTo, InstallModes.ApplyTo });
            if (rule.Name.Role != NameRoles.Literal)
                Assert.True(rule.Name.Kinds.Count > 0, $"{type}: a define/reference name needs kinds");
            foreach (var r in rule.Refs)
            {
                Assert.True(r.Kinds.Count > 0, $"{type} --{r.Option}: a reference needs kinds");
                if (r.Split is null) Assert.True(r.Segment == 0, $"{type} --{r.Option}: segment set without split");
                if (r.Subsplit is not null) Assert.True(r.Split is not null, $"{type} --{r.Option}: subsplit without split");
            }
        }
    }

    [Theory]
    [InlineData("table-relation")]
    [InlineData("find-methods")]
    public void Table_augmenting_commands_apply_to_an_existing_table_file(string type)
    {
        Assert.True(Map.TryGet(type, out var rule));
        Assert.Equal(InstallModes.ApplyTo, rule.Install);
        Assert.Equal(NameRoles.Reference, rule.Name.Role);
        Assert.Equal(new[] { "table" }, rule.Name.Kinds);
    }

    [Fact]
    public void Table_field_type_is_the_second_colon_segment()
    {
        Assert.True(Map.TryGet("table", out var rule));
        var field = Assert.Single(rule.Refs, r => r.Option == "field");
        Assert.Equal(":", field.Split);
        Assert.Equal(1, field.Segment);
        Assert.Equal(new[] { "edt", "enum" }, field.Kinds);
    }

    [Fact]
    public void Extension_takes_kind_as_a_leading_positional()
    {
        Assert.True(Map.TryGet("extension", out var rule));
        Assert.Equal(new[] { "kind" }, rule.Leading);
        Assert.Equal(NameRoles.Reference, rule.Name.Role);
    }

    [Fact]
    public void Lookup_ignores_case()
    {
        Assert.True(Map.TryGet("TABLE", out var rule));
        Assert.Equal(30, rule.Rank);
    }
}
