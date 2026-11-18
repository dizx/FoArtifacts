using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoArtifacts.Core;

/// <summary>One artifact as the client sends it.</summary>
public sealed record TaskInput(string Type, string Name, Dictionary<string, JsonElement>? Options = null);

/// <summary>A whole tool call.</summary>
public sealed record BatchRequest(string Model, string Prefix, IReadOnlyList<TaskInput> Tasks, bool DryRun = false, bool Build = false);

/// <summary>A task that passed validation. Options keep their order; a repeated option appears once per value.</summary>
public sealed record ValidTask(
    int Index,
    string Type,
    string Name,
    IReadOnlyList<KeyValuePair<string, string>> Options,
    IReadOnlyList<string> Flags)
{
    public string Label => $"tasks[{Index}] ({Type} {Name})";

    public IReadOnlyList<string> Values(string option) =>
        Options.Where(o => o.Key == option).Select(o => o.Value).ToList();
}

public sealed record ValidBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ValidTask> Tasks, bool Build = false);

public static partial class BatchSpec
{
    public const int MaxTasks = 500;

    /// <summary>
    /// Options fo-artifacts sets itself, plus options that make d365fo write to a path of the caller's
    /// choosing — d365fo does not journal those writes (<c>add-to</c>, <c>into-role</c>) or they land
    /// outside the model (<c>out-*</c>), so a rollback could not undo them.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "out", "install-to", "apply-to", "output", "overwrite", "no-refresh", "add-to", "into-role",
    };

    public static bool IsReserved(string option) =>
        ReservedOptions.Contains(option) || option.StartsWith("out-", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex AotName();

    /// <summary>True for a valid model name. d365fo reads <c>--model ""</c> as no filter, so a blank name must never reach it.</summary>
    public static bool IsModelName(string? name) => !string.IsNullOrWhiteSpace(name) && AotName().IsMatch(name);

    public static string BadModelName(string? name) =>
        $"model: '{name}' is not a valid model name (letters, digits and _ only).";

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex PrefixName();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex OptionName();

    public static (ValidBatch? Batch, IReadOnlyList<string> Errors) Validate(BatchRequest request, ReferenceMap map)
    {
        var errors = new List<string>();

        if (!IsModelName(request.Model))
            errors.Add(BadModelName(request.Model));
        if (request.Prefix is null)
            errors.Add("prefix: required; use \"\" for no prefix.");
        else if (request.Prefix.Length > 0 && !PrefixName().IsMatch(request.Prefix))
            errors.Add($"prefix: '{request.Prefix}' must start with a letter and contain only letters, digits and _.");

        if (request.Tasks is null || request.Tasks.Count == 0)
        {
            errors.Add("tasks: at least one task is required.");
            return (null, errors);
        }
        if (request.Tasks.Count > MaxTasks)
            errors.Add($"tasks: {request.Tasks.Count} tasks given; the maximum is {MaxTasks}.");

        var valid = new List<ValidTask>();
        for (var i = 0; i < request.Tasks.Count; i++)
        {
            var input = request.Tasks[i];
            if (input is null)
            {
                errors.Add($"tasks[{i}]: task is null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(input.Type) || !map.TryGet(input.Type, out var rule))
            {
                errors.Add($"tasks[{i}]: unknown type '{input.Type}'. Supported: {string.Join(", ", map.Types)}.");
                continue;
            }
            var type = input.Type.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                errors.Add($"tasks[{i}] ({type}): name is required.");
                continue;
            }
            var where = $"tasks[{i}] ({type} {input.Name})";
            if (rule.Name.Role != NameRoles.Literal && !AotName().IsMatch(input.Name))
                errors.Add($"{where}: name '{input.Name}' is not a valid AOT name (letters, digits and _ only).");

            var options = new List<KeyValuePair<string, string>>();
            var flags = new List<string>();
            foreach (var (rawKey, value) in input.Options ?? new Dictionary<string, JsonElement>())
            {
                var key = rawKey.StartsWith("--", StringComparison.Ordinal) ? rawKey[2..] : rawKey;
                if (!OptionName().IsMatch(key))
                {
                    errors.Add($"{where}: option '{rawKey}' is not a valid option name (lowercase, e.g. \"field\" or \"lines-table\").");
                    continue;
                }
                if (IsReserved(key))
                {
                    errors.Add($"{where}: option '{key}' is set by fo-artifacts and cannot be given.");
                    continue;
                }
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                        flags.Add(key);
                        break;
                    case JsonValueKind.False:
                        break;
                    case JsonValueKind.String:
                        options.Add(new(key, value.GetString()!));
                        break;
                    case JsonValueKind.Number:
                        options.Add(new(key, value.GetRawText()));
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String) options.Add(new(key, item.GetString()!));
                            else if (item.ValueKind == JsonValueKind.Number) options.Add(new(key, item.GetRawText()));
                            else errors.Add($"{where}: option '{key}': array items must be strings or numbers.");
                        }
                        break;
                    default:
                        errors.Add($"{where}: option '{key}' must be a string, number, boolean or array.");
                        break;
                }
            }

            foreach (var lead in rule.Leading)
            {
                if (options.Count(o => o.Key == lead) != 1)
                    errors.Add($"{where}: option '{lead}' is required exactly once for {type} (it is passed as the first argument).");
            }

            valid.Add(new ValidTask(i, type, input.Name, options, flags));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ValidBatch(request.Model, request.Prefix ?? "", request.DryRun, valid, request.Build), errors);
    }
}
