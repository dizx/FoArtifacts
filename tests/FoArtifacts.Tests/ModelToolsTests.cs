using System.Text.Json;
using FoArtifacts;
using FoArtifacts.Core;
using ModelContextProtocol.Protocol;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelToolsTests
{
    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;

    private static BuildModelTool BuildTool(FakeD365fo fake, ModelLock? gate = null) =>
        new(new ModelBuild(fake, new ModelCheck(fake)), gate ?? new ModelLock());

    private static RefreshIndexTool RefreshTool(FakeD365fo fake) =>
        new(new IndexRefresh(fake, new ModelCheck(fake)), new ModelLock());

    [Fact]
    public async Task Build_model_returns_a_clean_build()
    {
        var result = await BuildTool(new FakeD365fo()).BuildModel("ContosoFleet", Ct);

        Assert.False(result.IsError);
        Assert.True(Json(result).GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Build_model_flags_a_failed_build_as_an_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "build" ? Fixture("build-failed.json", 1) : null };

        var result = await BuildTool(fake).BuildModel("ContosoFleet", Ct);

        Assert.True(result.IsError);
        Assert.Equal(2, Json(result).GetProperty("build").GetProperty("error_count").GetInt32());
    }

    [Fact]
    public async Task Build_model_waits_while_a_batch_holds_the_lock()
    {
        var fake = new FakeD365fo();
        var gate = new ModelLock();
        var held = await gate.AcquireAsync(Ct);

        var call = BuildTool(fake, gate).BuildModel("ContosoFleet", Ct);
        await Task.Delay(50, Ct);
        Assert.Empty(fake.Calls);

        held.Dispose();
        Assert.False((await call).IsError);
    }

    [Fact]
    public async Task Refresh_index_returns_the_refresh_summary()
    {
        var result = await RefreshTool(new FakeD365fo()).RefreshIndex("ContosoFleet", force: true, Ct);

        Assert.False(result.IsError);
        Assert.True(Json(result).GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task Refresh_index_flags_a_missing_model_as_an_error()
    {
        var result = await RefreshTool(new FakeD365fo()).RefreshIndex("Nope", force: false, Ct);

        Assert.True(result.IsError);
    }
}
