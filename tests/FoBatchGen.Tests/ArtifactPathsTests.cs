using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

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
    public void Missing_data_has_no_paths() => Assert.Empty(ArtifactPaths.From(default));
}
