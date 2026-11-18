using System.Diagnostics;

namespace FoArtifacts.Core;

/// <summary>
/// Compiles one custom model with <c>d365fo build --model</c>. A build writes no source files, only
/// <c>bin</c> output the next build overwrites, so unlike generate, refresh and undo calls it may be
/// cancelled while it runs. It takes no lock; callers hold <see cref="ModelLock"/>.
/// </summary>
public sealed class ModelBuild(ID365fo buildCli, ModelCheck check)
{
    public static IReadOnlyList<string> Args(string model) => ["build", "--model", model, "--output", "json"];

    public async Task<BuildReport> RunAsync(string model, IReadOnlySet<string>? batchObjects, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        if (!BatchSpec.IsModelName(model)) return BuildReport.Failed("BAD_INPUT", BatchSpec.BadModelName(model), null);
        try
        {
            var (info, error) = await check.LookUpAsync(model, ct);
            if (error is not null)
                return BuildReport.Failed("D365FO_UNUSABLE", error, null, clock.ElapsedMilliseconds);
            if (!info!.Found)
                return BuildReport.Failed("MODEL_NOT_FOUND", ModelCheck.NotIndexed(model, info.Known), null, clock.ElapsedMilliseconds);
            if (!info.IsCustom)
                return BuildReport.Failed("MODEL_NOT_CUSTOM",
                    $"Model '{model}' is not marked as custom in d365fo, so fo-artifacts does not build it.",
                    "If the model is yours, add it to D365FO_CUSTOM_MODELS, restart the MCP server, then run refresh_index with force.",
                    clock.ElapsedMilliseconds);

            var result = await buildCli.RunAsync(Args(model), ct);
            return BuildReport.From(result, batchObjects, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return BuildReport.Failed("CANCELLED", "The build was cancelled.", null, clock.ElapsedMilliseconds);
        }
    }
}
