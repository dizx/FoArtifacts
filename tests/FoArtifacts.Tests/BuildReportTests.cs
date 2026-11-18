using System.Text.Json;
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class BuildReportTests
{
    // Lower case on purpose: d365fo reports FALVehicle.
    private static readonly IReadOnlySet<string> Batch = new HashSet<string> { "falvehicle" };

    private static object Diag(string obj, int i) =>
        new { severity = "error", kind = "AxClass", @object = obj, member = $"m{i}", message = $"problem {i}" };

    /// <summary>A failed build as d365fo prints it: ok true, exit code 1.</summary>
    private static CliResult Failed(object[] errors, object[] warnings) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new
        {
            ok = true,
            data = new { buildSucceeded = false, errorCount = errors.Length, warningCount = warnings.Length, errors, warnings },
        }), "");

    [Fact]
    public void A_clean_build_reads_as_succeeded()
    {
        var report = BuildReport.From(Fixture("build-clean.json", 0), Batch, 1);

        Assert.True(report.Succeeded);
        Assert.Null(report.Error);
        Assert.Equal(0, report.ErrorCount);
        Assert.Empty(report.Errors);
        Assert.Equal(7664, report.ElapsedMs);
        Assert.Equal(@"C:\AosService\PackagesLocalDirectory\FoArtifactsLab\BuildModelResult.log", report.Log);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void A_failed_build_exits_non_zero_but_its_errors_are_still_read()
    {
        var result = Fixture("build-failed.json", 1);
        Assert.False(result.Ok); // Envelope reports ok:true with exit code 1 as EXIT_CODE.

        var report = BuildReport.From(result, Batch, 1);

        Assert.False(report.Succeeded);
        Assert.Null(report.Error);
        Assert.Equal(2, report.ErrorCount);
        var first = report.Errors[0];
        Assert.Equal("FALVehicle", first.Object);
        Assert.Equal("AxForm", first.Kind);
        Assert.Equal("Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode", first.Member);
        Assert.Contains("ColumnsMode", first.Message);
        Assert.All(report.Errors, e => Assert.True(e.InBatch));
    }

    [Fact]
    public void Without_a_batch_in_batch_is_not_set()
    {
        var report = BuildReport.From(Fixture("build-failed.json", 1), null, 1);

        Assert.All(report.Errors, e => Assert.Null(e.InBatch));
    }

    [Fact]
    public void Errors_in_the_batch_come_first_and_the_list_is_capped()
    {
        var errors = Enumerable.Range(0, 60).Select(i => Diag(i == 55 ? "FALVehicle" : "Other", i)).ToArray();

        var report = BuildReport.From(Failed(errors, []), Batch, 1);

        Assert.Equal(60, report.ErrorCount);
        Assert.Equal(BuildReport.MaxErrors, report.Errors.Count);
        Assert.Equal("m55", report.Errors[0].Member);
        Assert.Equal("m0", report.Errors[1].Member);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Only_warnings_in_the_batch_are_listed_and_at_most_20()
    {
        var warnings = Enumerable.Range(0, 30).Select(i => Diag("FALVehicle", i))
            .Concat(Enumerable.Range(30, 5).Select(i => Diag("Other", i))).ToArray();

        var report = BuildReport.From(Failed([], warnings), Batch, 1);

        Assert.Equal(35, report.WarningCount);
        Assert.Equal(BuildReport.MaxWarnings, report.Warnings.Count);
        Assert.All(report.Warnings, w => Assert.True(w.InBatch));
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Without_a_batch_the_first_warnings_are_listed()
    {
        var warnings = Enumerable.Range(0, 25).Select(i => Diag("Other", i)).ToArray();

        var report = BuildReport.From(Failed([], warnings), null, 1);

        Assert.Equal(BuildReport.MaxWarnings, report.Warnings.Count);
        Assert.Equal("m0", report.Warnings[0].Member);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Short_lists_are_not_truncated()
    {
        var report = BuildReport.From(Failed([Diag("Other", 1)], [Diag("FALVehicle", 2), Diag("Other", 3)]), Batch, 1);

        Assert.Single(report.Errors);
        Assert.Single(report.Warnings);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void A_diagnostic_without_an_object_is_kept_and_not_in_the_batch()
    {
        var report = BuildReport.From(Failed([new { severity = "error", message = "Label file missing" }], []), Batch, 1);

        var error = Assert.Single(report.Errors);
        Assert.Null(error.Object);
        Assert.Equal("Label file missing", error.Message);
        Assert.False(error.InBatch);
    }

    [Fact]
    public void A_build_that_could_not_run_reports_d365fos_error()
    {
        var report = BuildReport.From(FakeD365fo.Fail("XPPC_NOT_FOUND", "Not found at X", "Set D365FO_PACKAGES_PATH"), Batch, 3);

        Assert.Null(report.Succeeded);
        Assert.Equal(new ToolError("XPPC_NOT_FOUND", "Not found at X", "Set D365FO_PACKAGES_PATH"), report.Error);
        Assert.Equal(3, report.ElapsedMs);
    }

    [Fact]
    public void A_timed_out_build_points_at_the_build_time_limit()
    {
        var timeout = CliResult.Failure(-1, "TIMEOUT", "`d365fo build --model` did not finish within 900 s and was stopped.",
            "Raise FO_ARTIFACTS_TIMEOUT_SECONDS if the model is large.");

        var report = BuildReport.From(timeout, Batch, 900_000);

        Assert.Equal("TIMEOUT", report.Error!.Code);
        Assert.Contains("FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS", report.Error.Hint);
        Assert.DoesNotContain("FO_ARTIFACTS_TIMEOUT_SECONDS", report.Error.Hint);
    }

    [Fact]
    public void A_failed_build_with_no_parsed_errors_carries_d365fos_output()
    {
        // LabelC failing stops d365fo before xppc: no errors, no xppc log, the reason only in `tail`.
        var labelFailure = Envelope.Parse(1, JsonSerializer.Serialize(new
        {
            ok = true,
            data = new
            {
                buildSucceeded = false,
                errorCount = 0,
                warningCount = 0,
                errors = Array.Empty<object>(),
                warnings = Array.Empty<object>(),
                models = new[] { new { model = "FoArtifactsLab", xppc = (object?)null } },
                tail = "LabelC: error: label @FAL:Missing is not defined",
            },
        }), "");

        var report = BuildReport.From(labelFailure, Batch, 1);

        Assert.False(report.Succeeded);
        Assert.Empty(report.Errors);
        Assert.Equal("LabelC: error: label @FAL:Missing is not defined", report.Message);
    }

    [Fact]
    public void A_failed_build_with_parsed_errors_has_no_extra_message()
    {
        var report = BuildReport.From(Fixture("build-failed.json", 1), Batch, 1);

        Assert.Null(report.Message);
    }

    [Fact]
    public void An_ok_result_without_buildSucceeded_is_an_error()
    {
        var report = BuildReport.From(FakeD365fo.Ok(new { something = 1 }), null, 1);

        Assert.Equal("NO_BUILD_RESULT", report.Error!.Code);
    }
}
