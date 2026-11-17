using System.Text.Json;
using FoArtifacts.Core;
using Xunit;

namespace FoArtifacts.Tests;

public class ArtifactPathsTests
{
    [Fact]
    public void Finds_paths_at_any_depth()
    {
        // Shape of `generate report` output at the pinned d365fo commit.
        using var doc = JsonDocument.Parse("""
            {"report":{"path":"/m/AxReport/R.xml"},"dp":{"path":"/m/AxClass/RDP.xml"},
             "tmpTables":[{"path":"/m/AxTable/RTmp.xml"}],"menuItem":{"path":"/m/AxMenuItemOutput/R.xml"},"name":"R"}
            """);
        Assert.Equal(
            new[] { "/m/AxReport/R.xml", "/m/AxClass/RDP.xml", "/m/AxTable/RTmp.xml", "/m/AxMenuItemOutput/R.xml" },
            ArtifactPaths.From(doc.RootElement));
    }

    [Fact]
    public void Includes_label_content_files_but_not_other_content()
    {
        // Shape of `generate label-file` output: the .label.txt is reported under "content".
        using var doc = JsonDocument.Parse("""
            {"path":"/m/AxLabelFile/L_en-US.xml","content":"/m/AxLabelFile/LabelResources/en-US/L.en-US.label.txt",
             "method":{"content":"public void run() {}"}}
            """);
        Assert.Equal(
            new[] { "/m/AxLabelFile/L_en-US.xml", "/m/AxLabelFile/LabelResources/en-US/L.en-US.label.txt" },
            ArtifactPaths.From(doc.RootElement));
    }

    [Fact]
    public void Missing_data_has_no_paths() => Assert.Empty(ArtifactPaths.From(default));
}
