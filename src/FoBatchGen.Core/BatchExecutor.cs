using System.Diagnostics;
using System.Text.Json;

namespace FoBatchGen.Core;

public sealed record TaskOutcome(ExpandedTask Task, int Stage, CliResult Result);

public sealed record BatchFailure(string Step, string? Task, string Code, string Message, string? Hint);

public sealed record RollbackReport(bool RolledBack, int Reverted, IReadOnlyList<string> LeftBehind, string? Reason);

public sealed record BatchResult(
    bool Ok,
    bool DryRun,
    string Model,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<Stage> Stages,
    IReadOnlyList<TaskOutcome> Completed,
    BatchFailure? Failure,
    RollbackReport? Rollback,
    long ElapsedMs);

public sealed class BatchExecutor(ID365fo cli, ReferenceMap map)
{
    public const int JournalLimit = 2000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static IReadOnlyList<string> RefreshArgs(string model) =>
        ["index", "refresh", "--model", model, "--output", "json"];

    public async Task<BatchResult> RunAsync(BatchRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var (valid, specErrors) = BatchSpec.Validate(request, map);
        if (valid is null) return Rejected(request, specErrors, clock);
        var (expanded, nameErrors) = NameExpander.Expand(valid, map);
        if (expanded is null) return Rejected(request, nameErrors, clock);
        var (stages, planErrors) = Planner.Plan(expanded, map);
        if (stages is null) return Rejected(request, planErrors, clock);
        if (expanded.DryRun)
            return new BatchResult(true, true, expanded.Model, [], [], stages, [], null, null, clock.ElapsedMilliseconds);

        await _gate.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(expanded, stages, clock, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BatchResult Rejected(BatchRequest request, IReadOnlyList<string> errors, Stopwatch clock) =>
        new(false, request.DryRun, request.Model ?? "", errors, [], [], [], null, null, clock.ElapsedMilliseconds);

    private async Task<BatchResult> ExecuteAsync(ExpandedBatch batch, IReadOnlyList<Stage> stages, Stopwatch clock, CancellationToken ct)
    {
        var warnings = new List<string>();
        var preflightError = await PreflightAsync(batch.Model, warnings, ct);
        if (preflightError is not null)
            return new BatchResult(false, false, batch.Model, [preflightError], warnings, stages, [], null, null, clock.ElapsedMilliseconds);

        var anchor = await cli.RunAsync(Journal.ListArgs(1), ct);
        if (!anchor.Ok)
            return new BatchResult(false, false, batch.Model,
                [$"Could not read the d365fo journal before writing, so a rollback would be impossible: {anchor.ErrorCode}: {anchor.ErrorMessage}"],
                warnings, stages, [], null, null, clock.ElapsedMilliseconds);
        var anchorId = Journal.Parse(anchor.Data).FirstOrDefault()?.Id;

        var completed = new List<TaskOutcome>();
        BatchFailure? failure = null;
        try
        {
            foreach (var stage in stages)
            {
                foreach (var task in stage.Tasks)
                {
                    string? applyTo = null;
                    if (task.Install == InstallModes.ApplyTo)
                    {
                        var (path, lookupError) = await ResolveTableFileAsync(task, completed, ct);
                        if (path is null)
                        {
                            failure = Fail("generate", task, lookupError!);
                            break;
                        }
                        applyTo = path;
                    }

                    var result = await cli.RunAsync(CommandBuilder.Build(task, batch.Model, applyTo), ct);
                    if (!result.Ok)
                    {
                        failure = Fail("generate", task, result);
                        break;
                    }
                    completed.Add(new TaskOutcome(task, stage.Number, result));
                }
                if (failure is not null) break;

                var refresh = await cli.RunAsync(RefreshArgs(batch.Model), ct);
                if (!refresh.Ok)
                {
                    failure = Fail("refresh", null, refresh);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = new BatchFailure("cancelled", null, "CANCELLED", "The batch was cancelled before it finished.", null);
        }
        catch (Exception ex)
        {
            failure = new BatchFailure("internal", null, "INTERNAL", ex.Message, null);
        }

        if (failure is null)
            return new BatchResult(true, false, batch.Model, [], warnings, stages, completed, null, null, clock.ElapsedMilliseconds);

        var rollback = await RollBackAsync(batch, anchorId, completed);
        return new BatchResult(false, false, batch.Model, [], warnings, stages, completed, failure, rollback, clock.ElapsedMilliseconds);
    }

    private static BatchFailure Fail(string step, ExpandedTask? task, CliResult result) =>
        new(step, task?.Label, result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", result.ErrorHint);

    private async Task<string?> PreflightAsync(string model, List<string> warnings, CancellationToken ct)
    {
        var models = await cli.RunAsync(["models", "list", "--output", "json"], ct);
        if (!models.Ok)
            return $"d365fo is not usable: {models.ErrorCode}: {models.ErrorMessage}"
                + (models.ErrorHint is null ? "" : $" Hint: {models.ErrorHint}");

        var items = new List<JsonElement>();
        if (models.Data.ValueKind == JsonValueKind.Object
            && models.Data.TryGetProperty("items", out var array)
            && array.ValueKind == JsonValueKind.Array)
            items.AddRange(array.EnumerateArray());

        var match = items.FirstOrDefault(i => string.Equals(Str(i, "name"), model, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            var known = items.Select(i => Str(i, "name")).Where(n => n is not null).Take(20);
            return $"Model '{model}' is not in the d365fo index. Known models: {string.Join(", ", known)}. "
                + "Check the name, or run `d365fo index refresh` if the model is new.";
        }
        if (!(match.TryGetProperty("isCustom", out var custom) && custom.ValueKind == JsonValueKind.True))
            warnings.Add($"Model '{model}' is not marked as custom in d365fo. Add it to D365FO_CUSTOM_MODELS.");
        return null;
    }

    private async Task<(string? Path, CliResult? Error)> ResolveTableFileAsync(
        ExpandedTask task, IReadOnlyList<TaskOutcome> completed, CancellationToken ct)
    {
        var created = completed.LastOrDefault(o =>
            o.Task.Type == "table" && string.Equals(o.Task.Name, task.Name, StringComparison.OrdinalIgnoreCase));
        if (created is not null && Str(created.Result.Data, "path") is { Length: > 0 } createdPath)
            return (createdPath, null);

        var lookup = await cli.RunAsync(["get", "table", task.Name, "--output", "json"], ct);
        if (lookup.Ok
            && lookup.Data.ValueKind == JsonValueKind.Object
            && lookup.Data.TryGetProperty("table", out var table)
            && Str(table, "sourcePath") is { Length: > 0 } sourcePath)
            return (sourcePath, null);

        return (null, CliResult.Failure(lookup.ExitCode, "TARGET_NOT_FOUND",
            $"Could not find the file of table '{task.Name}' for {task.Type}: {lookup.ErrorMessage ?? "d365fo get table returned no sourcePath."}",
            "Create the table earlier in the same batch, or check the name with `d365fo search table`."));
    }

    private async Task<RollbackReport> RollBackAsync(ExpandedBatch batch, string? anchorId, IReadOnlyList<TaskOutcome> completed)
    {
        // The caller may have cancelled; the rollback must still run.
        var none = CancellationToken.None;
        var reported = completed.SelectMany(o => ArtifactPaths.From(o.Result.Data))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var list = await cli.RunAsync(Journal.ListArgs(JournalLimit), none);
        if (!list.Ok)
            return new RollbackReport(false, 0, reported, $"Could not read the d365fo journal to roll back: {list.ErrorMessage}");

        var decision = RollbackPlanner.Decide(anchorId, Journal.Parse(list.Data), JournalLimit, batch.Model, batch.TouchNames, reported);
        var leftBehind = decision.Entries.Select(e => e.Target).Where(t => t.Length > 0)
            .Concat(reported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!decision.CanUndo) return new RollbackReport(false, 0, leftBehind, decision.Reason);
        if (decision.Steps == 0) return new RollbackReport(true, 0, [], null);

        var undo = await cli.RunAsync(Journal.UndoArgs(decision.Steps), none);
        if (!undo.Ok)
            return new RollbackReport(false, 0, leftBehind, $"d365fo undo failed: {undo.ErrorCode}: {undo.ErrorMessage}");

        var reverted = undo.Data.ValueKind == JsonValueKind.Object
            && undo.Data.TryGetProperty("reverted", out var r)
            && r.ValueKind == JsonValueKind.Number
            && r.TryGetInt32(out var n) ? n : decision.Steps;
        if (reverted < decision.Steps)
            return new RollbackReport(false, reverted, leftBehind, $"d365fo undo reverted only {reverted} of {decision.Steps} writes.");

        var refresh = await cli.RunAsync(RefreshArgs(batch.Model), none);
        return new RollbackReport(true, reverted, [],
            refresh.Ok ? null : $"All writes were undone, but `d365fo index refresh --model {batch.Model}` failed afterwards ({refresh.ErrorMessage}); run it by hand.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
