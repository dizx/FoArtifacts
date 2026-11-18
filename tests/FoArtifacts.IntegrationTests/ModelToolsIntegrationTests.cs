using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.IntegrationTests.D365foSandbox;

namespace FoArtifacts.IntegrationTests;

public class ModelToolsIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireD365fo() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    [Fact]
    public async Task Refresh_skips_an_unchanged_model_and_rereads_a_changed_one()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var refresh = new IndexRefresh(box.Cli, new ModelCheck(box.Cli));
        await refresh.RunAsync(Model, force: false, Ct); // settle whatever extract left behind

        var unchanged = await refresh.RunAsync(Model, force: false, Ct);
        Assert.True(unchanged.Ok, Summary.ToJson(unchanged));
        Assert.False(unchanged.Refreshed, Summary.ToJson(unchanged));

        var file = Directory.EnumerateFiles(box.ModelDir, "*.xml", SearchOption.AllDirectories).First();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        var changed = await refresh.RunAsync(Model, force: false, Ct);
        Assert.True(changed.Refreshed, Summary.ToJson(changed));

        var forced = await refresh.RunAsync(Model, force: true, Ct);
        Assert.True(forced.Refreshed, Summary.ToJson(forced));
    }

    [Fact]
    public async Task Refresh_of_an_unknown_model_fails_with_the_Ax_hint()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var report = await new IndexRefresh(box.Cli, new ModelCheck(box.Cli)).RunAsync("NoSuchModel", force: false, Ct);

        Assert.False(report.Ok);
        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
    }

    [Fact]
    public async Task Build_without_xppc_reports_that_it_could_not_run()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var report = await new ModelBuild(box.Cli, new ModelCheck(box.Cli)).RunAsync(Model, null, Ct);

        Assert.Null(report.Succeeded);
        Assert.Equal("XPPC_NOT_FOUND", report.Error!.Code);
    }
}
