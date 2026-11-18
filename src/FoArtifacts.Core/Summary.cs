using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoArtifacts.Core;

/// <summary>The compact JSON the tool returns to the agent.</summary>
public static class Summary
{
    public const string BridgeWarningMarker = "bridge is not available";

    public const string BridgeNote =
        "The d365fo metadata bridge is not available, so raw scaffolds were written. "
        + "On the dev VM set D365FO_BRIDGE_ENABLED=1 to have files canonicalised by the Metadata API.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Warning(string? Task, string Message);

    public static string ToJson(BatchResult result) => JsonSerializer.Serialize(Build(result), Options);

    /// <summary>The result of build_model: <c>ok</c> means the build ran and succeeded.</summary>
    public static string ToJson(string model, BuildReport report) =>
        JsonSerializer.Serialize(new { ok = report.Succeeded == true, model, build = BuildSection(report) }, Options);

    public static string ToJson(RefreshReport r) => JsonSerializer.Serialize(
        r.Error is not null
            ? new { ok = false, model = r.Model, error = ErrorJson(r.Error) }
            : (object)new
            {
                ok = true,
                model = r.Model,
                refreshed = r.Refreshed,
                elapsed_ms = r.ElapsedMs,
                warnings = r.Warnings.Count > 0 ? r.Warnings : null,
            },
        Options);

    /// <summary>The <c>build</c> section shared by a batch result and build_model.</summary>
    internal static object BuildSection(BuildReport b) =>
        b.Error is not null
            ? (object)new { error = ErrorJson(b.Error) }
            : new
            {
                succeeded = b.Succeeded,
                error_count = b.ErrorCount,
                warning_count = b.WarningCount,
                elapsed_ms = b.ElapsedMs,
                errors = b.Errors.Select(Diagnostic).ToList(),
                warnings = b.Warnings.Select(Diagnostic).ToList(),
                truncated = b.Truncated ? true : (bool?)null,
                log = b.Log,
            };

    private static object Diagnostic(BuildDiagnostic d) =>
        new { @object = d.Object, kind = d.Kind, member = d.Member, message = d.Message, in_batch = d.InBatch };

    private static object ErrorJson(ToolError e) => new { code = e.Code, message = e.Message, hint = e.Hint };

    private static object Build(BatchResult r)
    {
        if (r.Errors.Count > 0)
            return new
            {
                ok = false,
                model = r.Model,
                errors = r.Errors,
                warnings = NullIfEmpty(r.Warnings.Select(w => new Warning(null, w)).ToList()),
            };

        if (r.DryRun)
            return new
            {
                ok = true,
                dry_run = true,
                model = r.Model,
                plan = r.Stages.Select(s => new
                {
                    stage = s.Number,
                    commands = s.Tasks.Select(t => CommandBuilder.Render(CommandBuilder.Build(t, r.Model))).ToList(),
                }).ToList(),
            };

        var warnings = NullIfEmpty(CollectWarnings(r));
        var artifacts = r.Completed
            .Select(o => new { type = o.Task.Type, name = o.Task.Name, paths = ArtifactPaths.From(o.Result.Data) })
            .ToList();

        if (r.Ok)
            return new { ok = true, model = r.Model, stages = r.Stages.Count, elapsed_ms = r.ElapsedMs, artifacts, warnings };

        return new
        {
            ok = false,
            model = r.Model,
            failed = new
            {
                step = r.Failure!.Step,
                task = r.Failure.Task,
                code = r.Failure.Code,
                message = r.Failure.Message,
                hint = r.Failure.Hint,
            },
            rollback = r.Rollback is null
                ? null
                : new
                {
                    rolled_back = r.Rollback.RolledBack,
                    reverted = r.Rollback.Reverted,
                    left_behind = r.Rollback.LeftBehind.Count > 0 ? r.Rollback.LeftBehind : null,
                    reason = r.Rollback.Reason,
                },
            completed_before_failure = artifacts.Count,
            warnings,
        };
    }

    private static List<Warning> CollectWarnings(BatchResult r)
    {
        var list = r.Warnings.Select(w => new Warning(null, w)).ToList();
        var bridge = false;
        foreach (var outcome in r.Completed)
        {
            foreach (var message in outcome.Result.Warnings)
            {
                if (message.Contains(BridgeWarningMarker, StringComparison.OrdinalIgnoreCase))
                {
                    bridge = true;
                    continue;
                }
                list.Add(new Warning(outcome.Task.Label, message));
            }
        }
        if (bridge) list.Add(new Warning(null, BridgeNote));
        return list;
    }

    private static List<Warning>? NullIfEmpty(List<Warning> list) => list.Count > 0 ? list : null;
}
