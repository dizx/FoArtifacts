using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class IndexRefreshTests
{
    private const string RefreshCall = "index refresh --model ContosoFleet --output json";

    private static IndexRefresh Make(FakeD365fo fake) => new(fake, new ModelCheck(fake));

    [Fact]
    public async Task Refreshes_and_reports_a_reread_model()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.True(report.Refreshed);
        Assert.Empty(report.Warnings);
        Assert.Contains(RefreshCall, fake.Calls);
    }

    [Fact]
    public async Task Reports_a_model_d365fo_skipped_as_unchanged()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "index" ? FakeD365fo.Ok(new { modelsProcessed = 0, modelsSkipped = 1 }) : null };

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.False(report.Refreshed);
    }

    [Fact]
    public async Task Force_adds_the_force_flag()
    {
        var fake = new FakeD365fo();

        await Make(fake).RunAsync("ContosoFleet", true, Ct);

        Assert.Contains("index refresh --model ContosoFleet --force --output json", fake.Calls);
    }

    [Fact]
    public async Task A_model_still_missing_after_the_refresh_fails_with_the_Ax_hint()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("Nope", false, Ct);

        Assert.False(report.Ok);
        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
        Assert.Contains("still not in the d365fo index", report.Error.Message);
        Assert.Contains("Ax", report.Error.Message);
        Assert.Contains("index refresh --model Nope --output json", fake.Calls);
    }

    [Fact]
    public async Task A_model_not_marked_custom_is_refreshed_with_a_warning()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.Contains("D365FO_CUSTOM_MODELS", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task A_failed_refresh_reports_d365fos_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "index" ? FakeD365fo.Fail("LOCKED", "db locked", "retry") : null };

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.False(report.Ok);
        Assert.Equal(new ToolError("LOCKED", "db locked", "retry"), report.Error);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("models"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Contoso Fleet")]
    public async Task A_blank_or_invalid_model_name_is_refused_before_d365fo_runs(string model)
    {
        // d365fo treats `--model ""` as no filter and would refresh every model in the index.
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync(model, true, Ct);

        Assert.False(report.Ok);
        Assert.Equal("BAD_INPUT", report.Error!.Code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task The_refresh_is_not_cancellable_once_started()
    {
        var fake = new FakeD365fo();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Make(fake).RunAsync("ContosoFleet", false, cts.Token);

        Assert.False(fake.Cancellable[fake.Calls.IndexOf(RefreshCall)]);
    }
}
