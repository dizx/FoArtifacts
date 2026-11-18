using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelBuildTests
{
    private const string BuildCall = "build --model ContosoFleet --output json";

    private static ModelBuild Make(FakeD365fo fake) => new(fake, new ModelCheck(fake));

    [Fact]
    public async Task Builds_a_custom_model()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.True(report.Succeeded);
        Assert.Equal("/pkg/ContosoFleet/BuildModelResult.log", report.Log);
        Assert.Contains(BuildCall, fake.Calls);
    }

    [Fact]
    public async Task A_model_not_marked_custom_is_not_built()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.Equal("MODEL_NOT_CUSTOM", report.Error!.Code);
        Assert.Contains("D365FO_CUSTOM_MODELS", report.Error.Hint);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("build"));
    }

    [Fact]
    public async Task An_unknown_model_is_not_built()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("Nope", null, Ct);

        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
        Assert.Contains("Ax", report.Error.Message);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("build"));
    }

    [Fact]
    public async Task An_unusable_d365fo_is_reported()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "models" ? FakeD365fo.Fail("NO_INDEX", "No index") : null };

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.Equal("D365FO_UNUSABLE", report.Error!.Code);
    }

    [Fact]
    public async Task The_build_call_can_be_cancelled()
    {
        // A build writes no source files, so unlike generate, refresh and undo it may be stopped.
        var fake = new FakeD365fo();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Make(fake).RunAsync("ContosoFleet", null, cts.Token);

        Assert.True(fake.Cancellable[fake.Calls.IndexOf(BuildCall)]);
    }

    [Fact]
    public async Task A_cancelled_build_is_reported_not_thrown()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fake = new FakeD365fo();
        fake.Override = a =>
        {
            if (a[0] != "build") return null;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var report = await Make(fake).RunAsync("ContosoFleet", null, cts.Token);

        Assert.Equal("CANCELLED", report.Error!.Code);
    }
}
