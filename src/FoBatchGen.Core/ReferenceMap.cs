using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FoBatchGen.Core;

/// <summary>What a task's <c>name</c> means for a subcommand.</summary>
public static class NameRoles
{
    /// <summary>The name of the new object; it gets the prefix.</summary>
    public const string Define = "define";
    /// <summary>An existing object the command changes; rewritten only if the batch defines it.</summary>
    public const string Reference = "reference";
    /// <summary>Passed through unchanged (e.g. a report-extension pattern).</summary>
    public const string Literal = "literal";
}

/// <summary>How the server tells d365fo where to write.</summary>
public static class InstallModes
{
    public const string InstallTo = "install-to";
    public const string ApplyTo = "apply-to";
}

public sealed class NameRule
{
    public string Role { get; init; } = NameRoles.Define;
    public List<string> Kinds { get; init; } = [];
}

/// <summary>An option whose value is the name of a further new object (prefixed like a name).</summary>
public sealed class DefineRule
{
    public string Option { get; init; } = "";
    public List<string> Kinds { get; init; } = [];
}

/// <summary>
/// An option, or one part of its value, that names another object. With <see cref="Split"/> the
/// value is split and only <see cref="Segment"/> is considered; with <see cref="Subsplit"/> that
/// segment is split again and <see cref="Subsegment"/> picks the part (null = every part, -1 = last).
/// </summary>
public sealed class RefRule
{
    public string Option { get; init; } = "";
    public string? Split { get; init; }
    public int Segment { get; init; }
    public string? Subsplit { get; init; }
    public int? Subsegment { get; init; }
    public List<string> Kinds { get; init; } = [];
}

public sealed class CommandRule
{
    /// <summary>Tie-break order inside a stage: lower runs first.</summary>
    public int Rank { get; init; }
    public NameRule Name { get; init; } = new();
    public string Install { get; init; } = InstallModes.InstallTo;
    /// <summary>Option keys whose single value is passed as a positional argument before the name.</summary>
    public List<string> Leading { get; init; } = [];
    public List<string> Produces { get; init; } = [];
    public List<DefineRule> Defines { get; init; } = [];
    public List<RefRule> Refs { get; init; } = [];
}

/// <summary>Per-subcommand rules loaded from <c>reference-map.json</c>.</summary>
public sealed class ReferenceMap
{
    public const string AnyKind = "*";

    public static readonly IReadOnlySet<string> KnownKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "enum", "edt", "table", "view", "map", "query", "class", "form", "form-part", "menu-item",
        "menu", "tile", "privilege", "duty", "role", "entity", "configuration-key", "label-file",
        "resource", "report", "service", "service-group", "security-policy", "workflow",
        "workflow-approval", "workflow-task", "workflow-category", "kpi", "measurement",
        "number-sequence",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string D365foCommit { get; init; } = "";

    public Dictionary<string, CommandRule> Commands { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Types => Commands.Keys.Order(StringComparer.Ordinal).ToList();

    public bool TryGet(string type, [NotNullWhen(true)] out CommandRule? rule) => Commands.TryGetValue(type, out rule);

    public static ReferenceMap Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<ReferenceMap>(json, JsonOptions)
            ?? throw new InvalidDataException("reference-map.json is empty.");
        return new ReferenceMap
        {
            D365foCommit = parsed.D365foCommit,
            Commands = new Dictionary<string, CommandRule>(parsed.Commands, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static ReferenceMap LoadEmbedded()
    {
        using var stream = typeof(ReferenceMap).Assembly.GetManifestResourceStream("FoBatchGen.Core.reference-map.json")
            ?? throw new InvalidOperationException("Embedded resource reference-map.json is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
