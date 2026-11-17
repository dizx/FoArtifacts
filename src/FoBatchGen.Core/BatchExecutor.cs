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
        var tracker = new OwnershipTracker(cli, anchorId);

        // From here on d365fo calls are never cancelled: killing d365fo between publishing a file and
        // journaling it would leave a file no rollback can find. Cancellation is honoured between calls.
        var none = CancellationToken.None;
        var completed = new List<TaskOutcome>();
        BatchFailure? failure = null;
        try
        {
            foreach (var stage in stages)
            {
                foreach (var task in stage.Tasks)
                {
                    ct.ThrowIfCancellationRequested();
                    string? applyTo = null;
                    if (task.Install == InstallModes.ApplyTo)
                    {
                        var (path, lookupError) = await ResolveTableFileAsync(task, batch.Model, completed);
                        if (path is null)
                        {
                            failure = Fail("generate", task, lookupError!);
                            break;
                        }
                        applyTo = path;
                    }

                    var result = await cli.RunAsync(CommandBuilder.Build(task, batch.Model, applyTo), none);
                    await tracker.ClaimAsync();
                    if (!result.Ok)
                    {
                        failure = Fail("generate", task, result);
                        break;
                    }
                    completed.Add(new TaskOutcome(task, stage.Number, result));
                }
                if (failure is not null) break;

                ct.ThrowIfCancellationRequested();
                var refresh = await cli.RunAsync(RefreshArgs(batch.Model), none);
                if (!refresh.Ok)
                {
                    failure = Fail("refresh", null, refresh);
                    break;
                }
                await tracker.SkipAsync();
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

        var rollback = await RollBackAsync(batch, anchorId, tracker, completed, failure);
        return new BatchResult(false, false, batch.Model, [], warnings, stages, completed, failure, rollback, clock.ElapsedMilliseconds);
    }

    /// <summary>
    /// Follows the journal during a batch. Entries that appear while one of the batch's commands runs are
    /// the batch's (d365fo journals companion objects under unrelated names, and with the bridge without a
    /// path); entries that appear between commands are someone else's.
    /// </summary>
    private sealed class OwnershipTracker(ID365fo cli, string? anchorId)
    {
        private const int Window = 50;
        private string? _lastSeen = anchorId;

        public HashSet<string> Owned { get; } = new(StringComparer.Ordinal);

        public string? Problem { get; private set; }

        /// <summary>After a batch command: everything newer than the last entry seen is the batch's.</summary>
        public async Task ClaimAsync()
        {
            var entries = await ReadAsync(Window);
            if (entries is null) return;
            var found = _lastSeen is null;
            foreach (var entry in entries)
            {
                if (entry.Id == _lastSeen)
                {
                    found = true;
                    break;
                }
                Owned.Add(entry.Id);
            }
            if (!found)
                Problem ??= $"The d365fo journal changed in a way fo-batchgen could not follow (more than {Window} new entries during one command, or entries pruned)";
            _lastSeen = entries.FirstOrDefault()?.Id ?? _lastSeen;
        }

        /// <summary>Between commands: whatever is newest now is not the batch's.</summary>
        public async Task SkipAsync()
        {
            var entries = await ReadAsync(1);
            if (entries is not null) _lastSeen = entries.FirstOrDefault()?.Id ?? _lastSeen;
        }

        private async Task<IReadOnlyList<JournalEntry>?> ReadAsync(int limit)
        {
            var result = await cli.RunAsync(Journal.ListArgs(limit), CancellationToken.None);
            if (result.Ok) return Journal.Parse(result.Data);
            Problem ??= $"Could not read the d365fo journal during the batch ({result.ErrorCode}: {result.ErrorMessage})";
            return null;
        }
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
        ExpandedTask task, string model, IReadOnlyList<TaskOutcome> completed)
    {
        var created = completed.LastOrDefault(o =>
            o.Task.Type == "table" && string.Equals(o.Task.Name, task.Name, StringComparison.OrdinalIgnoreCase));
        if (created is not null && Str(created.Result.Data, "path") is { Length: > 0 } createdPath)
            return (createdPath, null);

        var lookup = await cli.RunAsync(["get", "table", task.Name, "--output", "json"], CancellationToken.None);
        if (lookup.Ok
            && lookup.Data.ValueKind == JsonValueKind.Object
            && lookup.Data.TryGetProperty("table", out var table)
            && Str(table, "sourcePath") is { Length: > 0 } sourcePath)
        {
            var tableModel = Str(table, "model");
            if (!string.Equals(tableModel, model, StringComparison.OrdinalIgnoreCase))
                return (null, CliResult.Failure(lookup.ExitCode, "TARGET_IN_OTHER_MODEL",
                    $"Table '{task.Name}' is in model '{tableModel}', not '{model}'. {task.Type} edits the table's own file, "
                    + "so it may only change tables of the model being written.",
                    "To change a table of another model, use an extension task with options.kind \"Table\"."));
            return (sourcePath, null);
        }

        return (null, CliResult.Failure(lookup.ExitCode, "TARGET_NOT_FOUND",
            $"Could not find the file of table '{task.Name}' for {task.Type}: {lookup.ErrorMessage ?? "d365fo get table returned no sourcePath."}",
            "Create the table earlier in the same batch, or check the name with `d365fo search table`."));
    }

    private async Task<RollbackReport> RollBackAsync(
        ExpandedBatch batch, string? anchorId, OwnershipTracker tracker, IReadOnlyList<TaskOutcome> completed, BatchFailure failure)
    {
        // The caller may have cancelled; the rollback must still run.
        var none = CancellationToken.None;
        var reported = completed.SelectMany(o => ArtifactPaths.From(o.Result.Data))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // install-to never overwrites (d365fo refuses an existing target), so these files are new.
        var created = completed.Where(o => o.Task.Install == InstallModes.InstallTo)
            .SelectMany(o => ArtifactPaths.From(o.Result.Data))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (tracker.Problem is not null)
            return new RollbackReport(false, 0, reported, $"{tracker.Problem}, so it is unclear which writes belong to this batch. Nothing was undone.");

        var list = await cli.RunAsync(Journal.ListArgs(JournalLimit), none);
        if (!list.Ok)
            return new RollbackReport(false, 0, reported, $"Could not read the d365fo journal to roll back: {list.ErrorMessage}");

        var decision = RollbackPlanner.Decide(anchorId, Journal.Parse(list.Data), JournalLimit, batch.Model, tracker.Owned);
        var leftBehind = decision.Entries.Select(e => e.Target).Where(t => t.Length > 0)
            .Concat(reported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!decision.CanUndo) return new RollbackReport(false, 0, leftBehind, decision.Reason);

        var reverted = 0;
        string? note = null;
        if (decision.Steps > 0)
        {
            var undo = await cli.RunAsync(Journal.UndoArgs(decision.Steps), none);
            if (!undo.Ok)
                return new RollbackReport(false, 0, leftBehind, $"d365fo undo failed: {undo.ErrorCode}: {undo.ErrorMessage}");

            reverted = undo.Data.ValueKind == JsonValueKind.Object
                && undo.Data.TryGetProperty("reverted", out var r)
                && r.ValueKind == JsonValueKind.Number
                && r.TryGetInt32(out var n) ? n : decision.Steps;
            if (reverted < decision.Steps)
                return new RollbackReport(false, reverted, leftBehind, $"d365fo undo reverted only {reverted} of {decision.Steps} writes.");

            var refresh = await cli.RunAsync(RefreshArgs(batch.Model), none);
            if (!refresh.Ok)
                note = $"All writes were undone, but `d365fo index refresh --model {batch.Model}` failed afterwards ({refresh.ErrorMessage}); run it by hand.";
        }

        var remaining = created.Where(File.Exists).ToList();
        if (remaining.Count > 0)
            return new RollbackReport(false, reverted, remaining,
                "d365fo undo finished, but these files the batch created are still there because d365fo did not journal them. Delete them by hand.");
        if (failure is { Step: "generate", Code: "TIMEOUT" })
            return new RollbackReport(false, reverted, [],
                $"{failure.Task} was stopped while it ran, so it may have written files that d365fo never journaled. "
                + "Check the model for objects of that name before running the batch again.");

        return new RollbackReport(true, reverted, [], note);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
