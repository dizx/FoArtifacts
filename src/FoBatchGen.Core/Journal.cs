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
    /// They can only when every one of them is a write this batch made — a journal id that appeared while
    /// one of its commands ran, in the batch's model — and every write the batch made is still in the journal.
    /// </summary>
    public static RollbackDecision Decide(
        string? anchorId,
        IReadOnlyList<JournalEntry> newestFirst,
        int limit,
        string model,
        IReadOnlyCollection<string> ownedIds)
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

        var owned = new HashSet<string>(ownedIds, StringComparer.Ordinal);
        var foreign = newer
            .Where(e => !owned.Contains(e.Id) || !string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (foreign.Count > 0)
            return new RollbackDecision(false, newer,
                "The journal contains writes that are not from this batch: "
                + string.Join(", ", foreign.Select(e => $"{e.Kind} {e.Name} ({e.Model})"))
                + ". Undoing would remove them too, so nothing was undone.");

        var missing = owned.Count(id => newer.All(e => e.Id != id));
        if (missing > 0)
            return new RollbackDecision(false, newer,
                $"{missing} write(s) of this batch are no longer in the d365fo journal (it keeps at most D365FO_JOURNAL_MAX_ENTRIES entries), "
                + "so they cannot be undone. Nothing was undone.");

        return new RollbackDecision(true, newer, null);
    }
}
