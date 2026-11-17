using System.Text.Json;
using FoArtifacts;
using FoArtifacts.Core;
using ModelContextProtocol.Protocol;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class GenerateFoArtifactsToolTests
{
    [Fact]
    public void Description_lists_exactly_the_supported_types()
    {
        const string marker = "Supported types: ";
        var start = GenerateFoArtifactsTool.ToolDescription.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var listed = GenerateFoArtifactsTool.ToolDescription[start..].TrimEnd('.').Split(", ");
        Assert.Equal(Map.Types, listed);
    }

    [Fact]
    public async Task Returns_the_summary_and_flags_failures_as_errors()
    {
        var tool = new GenerateFoArtifactsTool(new BatchExecutor(new FakeD365fo(), Map));

        var ok = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], dry_run: false, Ct);
        Assert.False(ok.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(ok.Content)).Text;
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean());

        var bad = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("tabel", "X")], dry_run: false, Ct);
        Assert.True(bad.IsError);
    }
}
