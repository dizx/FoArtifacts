using System.Text.Json;
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

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
}
