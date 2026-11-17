using System.Globalization;
using System.Text.Json;

namespace FoBatchGen.Core;

public sealed record JournalEntry(string Id, string Kind, string Name, string Model, string Target);

public static class Journal
{
    public static IReadOnlyList<string> ListArgs(int limit) =>
        ["journal", "list", "-n", limit.ToString(CultureInfo.InvariantCulture), "--output", "json"];

    public static IReadOnlyList<string> UndoArgs(int steps) =>
        ["undo", "--steps", steps.ToString(CultureInfo.InvariantCulture), "--output", "json"];

    /// <summary>Entries from <c>journal list</c> data, most recent first.</summary>
    public static IReadOnlyList<JournalEntry> Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<JournalEntry>();
        return entries.EnumerateArray()
            .Select(e => new JournalEntry(Str(e, "id"), Str(e, "kind"), Str(e, "name"), Str(e, "model"), Str(e, "target")))
            .ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

public sealed record RollbackDecision(bool CanUndo, IReadOnlyList<JournalEntry> Entries, string? Reason)
{
    public int Steps => Entries.Count;
}

public static class RollbackPlanner
{
    /// <summary>
    /// Decides whether the entries written since <paramref name="anchorId"/> can be undone as a unit.
    /// They can only when every one is this batch's: same model, and a name containing one of the
    /// batch's names (commands journal derived names such as CustTable_Extension) or a target a task
    /// reported writing.
    /// </summary>
    public static RollbackDecision Decide(
        string? anchorId,
        IReadOnlyList<JournalEntry> newestFirst,
        int limit,
        string model,
        IReadOnlyCollection<string> touchNames,
        IReadOnlyCollection<string> reportedPaths)
    {
        var newer = new List<JournalEntry>();
        var anchorFound = anchorId is null;
        foreach (var entry in newestFirst)
        {
            if (anchorId is not null && entry.Id == anchorId)
            {
                anchorFound = true;
                break;
            }
            newer.Add(entry);
        }

        if (!anchorFound)
            return new RollbackDecision(false, newer,
                $"The journal entry recorded before the batch is not among the newest {limit} entries, so it is unclear which writes belong to this batch.");
        if (anchorId is null && newestFirst.Count >= limit)
            return new RollbackDecision(false, newer,
                $"The journal was empty before the batch but now has at least {limit} entries, so it is unclear which writes belong to this batch.");

        var paths = new HashSet<string>(reportedPaths, StringComparer.OrdinalIgnoreCase);
        var foreign = newer.Where(e => !Owned(e, model, touchNames, paths)).ToList();
        if (foreign.Count > 0)
            return new RollbackDecision(false, newer,
                "The journal contains writes that are not from this batch: "
                + string.Join(", ", foreign.Select(e => $"{e.Kind} {e.Name} ({e.Model})"))
                + ". Nothing was undone, so those writes are kept.");

        return new RollbackDecision(true, newer, null);
    }

    private static bool Owned(JournalEntry e, string model, IReadOnlyCollection<string> touchNames, HashSet<string> paths) =>
        string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase)
        && (touchNames.Any(t => t.Length > 0 && e.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            || (e.Target.Length > 0 && paths.Contains(e.Target)));
}
