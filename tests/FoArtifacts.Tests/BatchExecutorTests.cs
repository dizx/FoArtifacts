using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class BatchExecutorTests
{
    private const string Tail = "--install-to ContosoFleet --no-refresh --output json";
    private const string Refresh = "index refresh --model ContosoFleet --output json";

    private static BatchRequest Vehicle(params TaskInput[] extra) => new("ContosoFleet", "CFlt",
    [
        T("enum", "VehicleStatus", """{"value":["Active:0"]}"""),
        T("edt", "VehicleId", """{"base-type":"String"}"""),
        T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
        .. extra,
    ]);

    private static (FakeD365fo Fake, BatchExecutor Executor) Make()
    {
        var fake = new FakeD365fo();
        return (fake, new BatchExecutor(fake, Map));
    }

    private static Func<IReadOnlyList<string>, CliResult?> FailWhen(string type, string code = "WRITE_FAILED") =>
        a => a[0] == "generate" && a[1] == type ? FakeD365fo.Fail(code, $"{type} failed") : null;

    [Fact]
    public async Task A_batch_waits_for_the_shared_model_lock()
    {
        var fake = new FakeD365fo();
        var gate = new ModelLock();
        var executor = new BatchExecutor(fake, Map, gate);
        var held = await gate.AcquireAsync(Ct);

        var run = executor.RunAsync(Vehicle(), Ct);
        await Task.Delay(50, Ct);
        Assert.Empty(fake.Calls);

        held.Dispose();
        Assert.True((await run).Ok);
    }

    [Fact]
    public async Task Runs_stage_by_stage_and_refreshes_after_each_stage()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.True(result.Ok);
        Assert.Equal(
            new[]
            {
                $"generate enum CFltVehicleStatus --value=Active:0 {Tail}",
                $"generate edt CFltVehicleId --base-type=String {Tail}",
                Refresh,
                $"generate table CFltVehicle --field=VehicleId:CFltVehicleId:mandatory --field=Status:CFltVehicleStatus {Tail}",
                Refresh,
            },
            fake.Calls.Where(c => c.StartsWith("generate ") || c.StartsWith("index refresh")));
        Assert.Equal(3, result.Completed.Count);
        Assert.Equal(2, result.Stages.Count);
        Assert.Null(result.Failure);
        Assert.Null(result.Rollback);
    }

    [Fact]
    public async Task A_failed_task_rolls_back_every_write_of_the_batch()
    {
        var (fake, executor) = Make();
        fake.Override = FailWhen("table");

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Ok);
        Assert.Equal(new BatchFailure("generate", "table CFltVehicle", "WRITE_FAILED", "table failed", null), result.Failure);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Equal(2, result.Rollback.Reverted);
        Assert.Empty(result.Rollback.LeftBehind);
        Assert.Null(result.Rollback.Reason);
        Assert.Empty(fake.Journal);
        Assert.Contains("undo --steps 2 --output json", fake.Calls);
        Assert.Equal(Refresh, fake.Calls[^1]);
    }

    [Fact]
    public async Task Writes_from_before_the_batch_are_never_undone()
    {
        var (fake, executor) = Make();
        fake.Journal.Add(new FakeD365fo.Entry("old", "Table", "Older", "ContosoFleet", "/old.xml"));
        fake.Override = FailWhen("table");

        await executor.RunAsync(Vehicle(), Ct);

        Assert.Equal("old", Assert.Single(fake.Journal).Id);
    }

    [Fact]
    public async Task A_foreign_write_during_the_batch_blocks_the_undo()
    {
        var (fake, executor) = Make();
        fake.Override = a =>
        {
            if (a[0] == "generate" && a[1] == "edt")
                fake.Journal.Add(new FakeD365fo.Entry("x", "Table", "SalesTable", "OtherModel", "/other.xml"));
            return FailWhen("table")(a);
        };

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("not from this batch", result.Rollback.Reason);
        Assert.Contains("/pkg/ContosoFleet/enum/CFltVehicleStatus.xml", result.Rollback.LeftBehind);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("undo"));
        Assert.Equal(3, fake.Journal.Count);
    }

    [Fact]
    public async Task A_failed_undo_is_reported_with_the_files_left_behind()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "undo" ? FakeD365fo.Fail("UNDO_FAILED", "disk locked") : FailWhen("table")(a);

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("disk locked", result.Rollback.Reason);
        Assert.Equal(2, result.Rollback.LeftBehind.Count);
    }

    [Fact]
    public async Task Cancelling_mid_batch_still_rolls_back()
    {
        var (fake, executor) = Make();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        fake.Override = a =>
        {
            if (a[0] == "generate" && a[1] == "edt") cts.Cancel();
            return null;
        };

        var result = await executor.RunAsync(Vehicle(), cts.Token);

        Assert.Equal("CANCELLED", result.Failure!.Code);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Equal(2, result.Rollback.Reverted);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task A_failed_refresh_rolls_back()
    {
        var (fake, executor) = Make();
        var refreshes = 0;
        fake.Override = a => a[0] == "index" && ++refreshes == 1 ? FakeD365fo.Fail("INDEX_LOCKED", "locked") : null;

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.Equal("refresh", result.Failure!.Step);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task Apply_to_uses_the_file_of_a_table_made_earlier_in_the_batch()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(T("find-methods", "Vehicle")), Ct);

        Assert.True(result.Ok);
        Assert.Contains("generate find-methods CFltVehicle --apply-to /pkg/ContosoFleet/table/CFltVehicle.xml --no-refresh --output json", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("get table"));
    }

    [Fact]
    public async Task Apply_to_looks_up_an_existing_table()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt", [T("find-methods", "CustTable")]), Ct);

        Assert.True(result.Ok);
        Assert.Contains("get table CustTable --output json", fake.Calls);
        Assert.Contains("generate find-methods CustTable --apply-to /pkg/ContosoFleet/AxTable/CustTable.xml --no-refresh --output json", fake.Calls);
    }

    [Fact]
    public async Task Apply_to_an_unknown_table_fails_and_rolls_back()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(T("find-methods", "NoSuchTable")), Ct);

        Assert.Equal("TARGET_NOT_FOUND", result.Failure!.Code);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task An_unknown_model_is_rejected_before_writing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle() with { Model = "Nope" }, Ct);

        Assert.False(result.Ok);
        Assert.Contains("not in the d365fo index", Assert.Single(result.Errors));
        Assert.Contains("ContosoFleet", result.Errors[0]);
        Assert.Empty(fake.Generates);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("journal"));
    }

    [Fact]
    public async Task An_unknown_model_error_explains_that_an_empty_model_is_not_indexed()
    {
        // d365fo only indexes a model folder that has an Ax* subfolder, so `index refresh` alone
        // never makes a brand-new, empty model known.
        var (_, executor) = Make();
        var result = await executor.RunAsync(Vehicle() with { Model = "Nope" }, Ct);

        var error = Assert.Single(result.Errors);
        Assert.Contains("empty", error);
        Assert.Contains("Ax", error);
        Assert.Contains("d365fo index refresh --model Nope", error);
    }

    [Fact]
    public async Task A_model_not_marked_custom_gives_a_warning()
    {
        var (fake, executor) = Make();
        fake.Models[0] = ("ContosoFleet", false);
        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.True(result.Ok);
        Assert.Contains("D365FO_CUSTOM_MODELS", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task A_dry_run_calls_nothing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle() with { DryRun = true }, Ct);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Equal(2, result.Stages.Count);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Invalid_requests_call_nothing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt", [T("tabel", "X")]), Ct);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_concurrent_write_with_a_matching_name_is_never_undone()
    {
        // Another session writes CFltVehicleStatus2 into the same model while the batch refreshes the index.
        var (fake, executor) = Make();
        var refreshes = 0;
        fake.Override = a =>
        {
            if (a[0] == "index" && ++refreshes == 1)
                fake.Record("Enum", "CFltVehicleStatus2", "ContosoFleet", "/pkg/ContosoFleet/enum/CFltVehicleStatus2.xml");
            return FailWhen("table")(a);
        };

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("not from this batch", result.Rollback.Reason);
        Assert.Contains(fake.Journal, e => e.Name == "CFltVehicleStatus2");
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("undo"));
    }

    [Fact]
    public async Task Companion_objects_a_task_writes_are_undone_with_it()
    {
        // number-sequence, workflow and report-extension journal companions whose names contain no batch name,
        // and with the bridge on a journal entry has no target path.
        var (fake, executor) = Make();
        fake.Override = a =>
        {
            if (a[0] == "generate" && a[1] == "table")
            {
                var result = fake.Default(a);
                fake.Record("Class", "FmVehicle_NumberSeqHandler", "ContosoFleet", "");
                return result;
            }
            return FailWhen("form")(a);
        };

        var result = await executor.RunAsync(Vehicle(T("form", "Vehicle", """{"table":"Vehicle"}""")), Ct);

        Assert.True(result.Rollback!.RolledBack, result.Rollback.Reason);
        Assert.Equal(4, result.Rollback.Reverted);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task Writes_pruned_from_the_journal_make_the_rollback_report_failure()
    {
        var (fake, executor) = Make();
        fake.JournalCap = 2;
        fake.Override = FailWhen("role", "BAD_INPUT");

        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt",
            [T("enum", "E1"), T("enum", "E2"), T("enum", "E3"), T("role", "R")]), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("no longer in the d365fo journal", result.Rollback.Reason);
        Assert.Contains("/pkg/ContosoFleet/enum/CFltE1.xml", result.Rollback.LeftBehind);
    }

    [Fact]
    public async Task Files_still_present_after_undo_make_the_rollback_report_failure()
    {
        var root = Directory.CreateTempSubdirectory("fo-artifacts-test-").FullName;
        try
        {
            var (fake, executor) = Make();
            fake.FilesRoot = root;
            fake.Override = a =>
            {
                if (a[0] == "generate" && a[1] == "edt")
                {
                    // d365fo publishes the file, then fails to journal it (it swallows journal errors).
                    var target = fake.TargetOf("ContosoFleet", "edt", a[2]);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "x");
                    return FakeD365fo.Ok(new { path = target });
                }
                return FailWhen("table")(a);
            };

            var result = await executor.RunAsync(Vehicle(), Ct);

            var leftover = fake.TargetOf("ContosoFleet", "edt", "CFltVehicleId");
            Assert.False(result.Rollback!.RolledBack);
            Assert.Equal(new[] { leftover }, result.Rollback.LeftBehind);
            Assert.False(File.Exists(fake.TargetOf("ContosoFleet", "enum", "CFltVehicleStatus")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Apply_to_refuses_a_table_of_another_model()
    {
        var (fake, executor) = Make();
        fake.ExistingTables["StdTable"] = "StdPkg";

        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt", [T("find-methods", "StdTable")]), Ct);

        Assert.Equal("TARGET_IN_OTHER_MODEL", result.Failure!.Code);
        Assert.Contains("extension", result.Failure.Hint);
        Assert.Empty(fake.Generates);
    }

    [Fact]
    public async Task A_running_command_is_not_interrupted_by_cancellation()
    {
        // Killing d365fo between publishing a file and journaling it would leave an unjournaled file.
        var (fake, executor) = Make();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await executor.RunAsync(Vehicle(), cts.Token);

        var writes = fake.Calls.Select((c, i) => (c, i)).Where(x => x.c.StartsWith("generate ") || x.c.StartsWith("index refresh") || x.c.StartsWith("undo"));
        Assert.All(writes, x => Assert.False(fake.Cancellable[x.i], $"'{x.c}' could be cancelled"));
    }

    [Fact]
    public async Task A_timed_out_task_is_reported_as_possibly_left_behind()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "generate" && a[1] == "table"
            ? CliResult.Failure(-1, "TIMEOUT", "`d365fo generate table` did not finish within 120 s and was stopped.")
            : null;

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.Equal(2, result.Rollback!.Reverted);
        Assert.False(result.Rollback.RolledBack);
        Assert.Contains("table CFltVehicle", result.Rollback.Reason);
        Assert.Contains("stopped", result.Rollback.Reason);
    }
}
