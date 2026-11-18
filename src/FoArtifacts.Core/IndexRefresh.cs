using System.Diagnostics;
using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>The outcome of <see cref="IndexRefresh"/>. <see cref="Refreshed"/> is false when d365fo skipped the model as unchanged.</summary>
public sealed record RefreshReport(bool Ok, string Model, bool Refreshed, long ElapsedMs, IReadOnlyList<string> Warnings, ToolError? Error);

/// <summary>
/// Re-reads one model into the index with <c>d365fo index refresh --model</c>, then checks the model is
/// there. The name is not checked against D365FO_CUSTOM_MODELS first: d365fo's patterns allow globs and
/// <c>!</c> exclusions, and a refresh only touches the index. It takes no lock; callers hold
/// <see cref="ModelLock"/>.
/// </summary>
public sealed class IndexRefresh(ID365fo cli, ModelCheck check)
{
    public static IReadOnlyList<string> Args(string model, bool force) =>
        force ? ["index", "refresh", "--model", model, "--force", "--output", "json"] : BatchExecutor.RefreshArgs(model);

    public async Task<RefreshReport> RunAsync(string model, bool force, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        // Like the batch's refreshes, a refresh is never cancelled once it has started.
        var result = await cli.RunAsync(Args(model, force), CancellationToken.None);
        if (!result.Ok)
            return Failed(model, result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", result.ErrorHint, clock);

        var (info, error) = await check.LookUpAsync(model, CancellationToken.None);
        if (error is not null) return Failed(model, "D365FO_UNUSABLE", error, null, clock);
        if (!info!.Found) return Failed(model, "MODEL_NOT_FOUND", ModelCheck.StillNotIndexed(model), null, clock);

        var warnings = result.Warnings.ToList();
        if (!info.IsCustom) warnings.Add(ModelCheck.NotCustom(model));
        var processed = result.Data.ValueKind == JsonValueKind.Object
            && result.Data.TryGetProperty("modelsProcessed", out var count)
            && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out var n)
            && n > 0;
        return new RefreshReport(true, model, processed, clock.ElapsedMilliseconds, warnings, null);
    }

    private static RefreshReport Failed(string model, string code, string message, string? hint, Stopwatch clock) =>
        new(false, model, false, clock.ElapsedMilliseconds, [], new ToolError(code, message, hint));
}
