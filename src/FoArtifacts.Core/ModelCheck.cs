using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>What d365fo's index says about one model.</summary>
public sealed record ModelInfo(bool Found, bool IsCustom, IReadOnlyList<string> Known);

/// <summary>Looks a model up with <c>d365fo models list</c>.</summary>
public sealed class ModelCheck(ID365fo cli)
{
    public static IReadOnlyList<string> ListArgs { get; } = ["models", "list", "--output", "json"];

    /// <summary>The model's entry, or an error message when d365fo itself is not usable.</summary>
    public async Task<(ModelInfo? Info, string? Error)> LookUpAsync(string model, CancellationToken ct)
    {
        var models = await cli.RunAsync(ListArgs, ct);
        if (!models.Ok)
            return (null, $"d365fo is not usable: {models.ErrorCode}: {models.ErrorMessage}"
                + (models.ErrorHint is null ? "" : $" Hint: {models.ErrorHint}"));

        var items = new List<JsonElement>();
        if (models.Data.ValueKind == JsonValueKind.Object
            && models.Data.TryGetProperty("items", out var array)
            && array.ValueKind == JsonValueKind.Array)
            items.AddRange(array.EnumerateArray());

        var known = items.Select(i => Str(i, "name")).OfType<string>().ToList();
        var match = items.FirstOrDefault(i => string.Equals(Str(i, "name"), model, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == JsonValueKind.Undefined) return (new ModelInfo(false, false, known), null);
        var custom = match.TryGetProperty("isCustom", out var flag) && flag.ValueKind == JsonValueKind.True;
        return (new ModelInfo(true, custom, known), null);
    }

    public static string NotIndexed(string model, IReadOnlyList<string> known) =>
        $"Model '{model}' is not in the d365fo index. Known models: {string.Join(", ", known.Take(20))}. "
        + $"Check the name. If the model is new, run `d365fo index refresh --model {model}`; "
        + "d365fo skips an empty model (one with no Ax* folder such as AxClass yet), so create one "
        + "empty Ax* folder in the model folder first.";

    public static string StillNotIndexed(string model) =>
        $"Model '{model}' is still not in the d365fo index after the refresh. d365fo skips an empty model "
        + "(one with no Ax* folder such as AxClass yet), so create one empty Ax* folder in the model folder, "
        + "and check that the model folder is under D365FO_PACKAGES_PATH.";

    public static string NotCustom(string model) =>
        $"Model '{model}' is not marked as custom in d365fo. Add it to D365FO_CUSTOM_MODELS.";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
