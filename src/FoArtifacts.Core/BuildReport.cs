using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>One compiler diagnostic. <see cref="InBatch"/> is null when the build was not part of a batch.</summary>
public sealed record BuildDiagnostic(string? Object, string? Kind, string? Member, string Message, bool? InBatch);

/// <summary>
/// The outcome of <c>d365fo build --model</c>: either the build ran (<see cref="Succeeded"/> is set) or it
/// could not run (<see cref="Error"/> is set).
/// </summary>
public sealed record BuildReport(
    bool? Succeeded,
    int ErrorCount,
    int WarningCount,
    long ElapsedMs,
    IReadOnlyList<BuildDiagnostic> Errors,
    IReadOnlyList<BuildDiagnostic> Warnings,
    bool Truncated,
    string? Log,
    ToolError? Error)
{
    public const int MaxErrors = 50;
    public const int MaxWarnings = 20;

    public const string TimeoutHint = "Raise FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS (default 900) if the model is large.";

    public static BuildReport Failed(string code, string message, string? hint, long elapsedMs = 0) =>
        new(null, 0, 0, elapsedMs, [], [], false, null, new ToolError(code, message, hint));

    /// <summary>
    /// Reads d365fo's build result. A failed build exits non-zero with <c>ok: true</c>, which
    /// <see cref="Envelope"/> reports as an EXIT_CODE failure, so the data is read whenever it holds
    /// <c>buildSucceeded</c>, whatever <see cref="CliResult.Ok"/> says. Errors in
    /// <paramref name="batchObjects"/> come first; with a batch only its warnings are listed.
    /// </summary>
    public static BuildReport From(CliResult result, IReadOnlySet<string>? batchObjects, long elapsedMs)
    {
        var data = result.Data;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("buildSucceeded", out var succeeded)
            || succeeded.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            if (result.Ok)
                return Failed("NO_BUILD_RESULT", "d365fo build returned no buildSucceeded field.", null, elapsedMs);
            var hint = result.ErrorCode == "TIMEOUT" ? TimeoutHint : result.ErrorHint;
            return Failed(result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", hint, elapsedMs);
        }

        var inBatch = batchObjects is null ? null : new HashSet<string>(batchObjects, StringComparer.OrdinalIgnoreCase);
        var errors = Diagnostics(data, "errors", inBatch);
        var warnings = Diagnostics(data, "warnings", inBatch);
        // OrderByDescending is stable: within each group d365fo's order is kept.
        var shownErrors = errors.OrderByDescending(d => d.InBatch == true).Take(MaxErrors).ToList();
        var candidates = inBatch is null ? warnings : warnings.Where(d => d.InBatch == true).ToList();
        var shownWarnings = candidates.Take(MaxWarnings).ToList();

        return new BuildReport(
            succeeded.GetBoolean(),
            Int(data, "errorCount") ?? errors.Count,
            Int(data, "warningCount") ?? warnings.Count,
            data.TryGetProperty("elapsedMs", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var d365foMs)
                ? d365foMs
                : elapsedMs,
            shownErrors,
            shownWarnings,
            errors.Count > MaxErrors || candidates.Count > MaxWarnings,
            LogPath(data),
            null);
    }

    private static List<BuildDiagnostic> Diagnostics(JsonElement data, string name, HashSet<string>? inBatch)
    {
        var list = new List<BuildDiagnostic>();
        if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(new BuildDiagnostic(null, null, null, item.GetString()!, inBatch is null ? null : false));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var obj = Str(item, "object");
            list.Add(new BuildDiagnostic(
                obj, Str(item, "kind"), Str(item, "member"), Str(item, "message") ?? item.GetRawText(),
                inBatch is null ? null : obj is not null && inBatch.Contains(obj)));
        }
        return list;
    }

    private static string? LogPath(JsonElement data)
    {
        if (!data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var model in models.EnumerateArray())
            if (model.ValueKind == JsonValueKind.Object && model.TryGetProperty("xppc", out var xppc) && Str(xppc, "log") is { } log)
                return log;
        return null;
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
