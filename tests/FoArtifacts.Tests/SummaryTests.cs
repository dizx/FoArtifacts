using System.Text.Json;
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class SummaryTests
{
    private static BatchRequest Request(bool dryRun = false) => new("ContosoFleet", "CFlt",
    [
        T("edt", "VehicleId"),
        T("table", "Vehicle", """{"field":["VehicleId:VehicleId"]}"""),
    ], dryRun);

    private static async Task<JsonElement> RunAsync(FakeD365fo fake, BatchRequest request)
    {
        var result = await new BatchExecutor(fake, Map).RunAsync(request, Ct);
        return JsonDocument.Parse(Summary.ToJson(result)).RootElement;
    }

    [Fact]
    public async Task Success_lists_artifacts_and_collapses_bridge_warnings()
    {
        var fake = new FakeD365fo();
        fake.Override = a => a[0] == "generate"
            ? FakeD365fo.Ok(new { path = $"/m/{a[2]}.xml" }, "Metadata API unavailable: bridge is not available (set D365FO_BRIDGE_ENABLED=1).", $"Kjøretøy note for {a[2]}")
            : null;

        var json = await RunAsync(fake, Request());

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.GetProperty("stages").GetInt32());
        var artifacts = json.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.Equal("CFltVehicleId", artifacts[0].GetProperty("name").GetString());
        Assert.Equal("/m/CFltVehicle.xml", artifacts[1].GetProperty("paths")[0].GetString());
        var warnings = json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("message").GetString()!).ToList();
        Assert.Single(warnings, w => w.Contains("bridge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Kjøretøy note for CFltVehicle", warnings);
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.TryGetProperty("task", out var t) && t.GetString() == "table CFltVehicle");
    }

    [Fact]
    public async Task Failure_reports_the_step_error_and_rollback()
    {
        var fake = new FakeD365fo
        {
            Override = a => a[0] == "generate" && a[1] == "table" ? FakeD365fo.Fail("WRITE_FAILED", "Target exists", "pass --overwrite") : null,
        };

        var json = await RunAsync(fake, Request());

        Assert.False(json.GetProperty("ok").GetBoolean());
        var failed = json.GetProperty("failed");
        Assert.Equal("generate", failed.GetProperty("step").GetString());
        Assert.Equal("table CFltVehicle", failed.GetProperty("task").GetString());
        Assert.Equal("WRITE_FAILED", failed.GetProperty("code").GetString());
        Assert.Equal("pass --overwrite", failed.GetProperty("hint").GetString());
        Assert.True(json.GetProperty("rollback").GetProperty("rolled_back").GetBoolean());
        Assert.Equal(1, json.GetProperty("rollback").GetProperty("reverted").GetInt32());
        Assert.Equal(1, json.GetProperty("completed_before_failure").GetInt32());
    }

    [Fact]
    public async Task Rejected_requests_list_the_errors()
    {
        var json = await RunAsync(new FakeD365fo(), new BatchRequest("ContosoFleet", "CFlt", [T("tabel", "X")]));
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown type", json.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task Dry_run_shows_the_commands_per_stage()
    {
        var json = await RunAsync(new FakeD365fo(), Request(dryRun: true));
        Assert.True(json.GetProperty("dry_run").GetBoolean());
        var plan = json.GetProperty("plan").EnumerateArray().ToList();
        Assert.Equal(2, plan.Count);
        Assert.Equal(
            "d365fo generate table CFltVehicle --field=VehicleId:CFltVehicleId --install-to ContosoFleet --no-refresh --output json",
            plan[1].GetProperty("commands")[0].GetString());
    }

    [Fact]
    public void Build_model_json_lists_errors_without_in_batch()
    {
        var report = BuildReport.From(Fixture("build-failed.json", 1), null, 1);

        var json = JsonDocument.Parse(Summary.ToJson("FoArtifactsLab", report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("FoArtifactsLab", json.GetProperty("model").GetString());
        var build = json.GetProperty("build");
        Assert.False(build.GetProperty("succeeded").GetBoolean());
        Assert.Equal(2, build.GetProperty("error_count").GetInt32());
        Assert.Equal(7235, build.GetProperty("elapsed_ms").GetInt64());
        var first = build.GetProperty("errors")[0];
        Assert.Equal("FALVehicle", first.GetProperty("object").GetString());
        Assert.Equal("AxForm", first.GetProperty("kind").GetString());
        Assert.False(first.TryGetProperty("in_batch", out _));
        Assert.Equal(0, build.GetProperty("warnings").GetArrayLength());
        Assert.False(build.TryGetProperty("truncated", out _));
        Assert.EndsWith("BuildModelResult.log", build.GetProperty("log").GetString());
    }

    [Fact]
    public void A_clean_build_model_is_ok()
    {
        var json = JsonDocument.Parse(Summary.ToJson("FoArtifactsLab", BuildReport.From(Fixture("build-clean.json", 0), null, 1))).RootElement;

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void A_build_that_could_not_run_has_only_an_error()
    {
        var report = BuildReport.Failed("XPPC_NOT_FOUND", "Not found", "Set D365FO_PACKAGES_PATH");

        var json = JsonDocument.Parse(Summary.ToJson("ContosoFleet", report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        var build = json.GetProperty("build");
        Assert.False(build.TryGetProperty("succeeded", out _));
        Assert.Equal("XPPC_NOT_FOUND", build.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Set D365FO_PACKAGES_PATH", build.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Truncated_lists_say_so()
    {
        var report = new BuildReport(false, 60, 0, 1, [], [], true, null, null);

        var json = JsonDocument.Parse(Summary.ToJson("ContosoFleet", report)).RootElement;

        Assert.True(json.GetProperty("build").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Refresh_json_reports_whether_the_model_was_reread()
    {
        var json = JsonDocument.Parse(Summary.ToJson(new RefreshReport(true, "ContosoFleet", false, 12, [], null))).RootElement;

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        Assert.Equal(12, json.GetProperty("elapsed_ms").GetInt64());
        Assert.False(json.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void A_failed_refresh_json_has_the_error()
    {
        var report = new RefreshReport(false, "Nope", false, 3, [], new ToolError("MODEL_NOT_FOUND", "still not in the index", null));

        var json = JsonDocument.Parse(Summary.ToJson(report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("MODEL_NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("refreshed", out _));
    }
}
