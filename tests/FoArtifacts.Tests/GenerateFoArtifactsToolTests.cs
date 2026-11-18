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

        var ok = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], dry_run: false, cancellationToken: Ct);
        Assert.False(ok.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(ok.Content)).Text;
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean());

        var bad = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("tabel", "X")], dry_run: false, cancellationToken: Ct);
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Build_is_passed_through_and_a_failed_build_is_not_an_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "build" ? FakeD365fo.BuildFailed(("CFltStatus", "Bad")) : null };
        var tool = new GenerateFoArtifactsTool(new BatchExecutor(fake, Map));

        var result = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], build: true, cancellationToken: Ct);

        Assert.False(result.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
        Assert.False(json.GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void Description_says_a_failed_build_does_not_undo_the_batch() =>
        Assert.Contains("a failed build does not undo the batch", GenerateFoArtifactsTool.ToolDescription);
}
