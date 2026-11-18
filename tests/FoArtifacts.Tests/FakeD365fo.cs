using System.Text.Json;
using FoArtifacts.Core;

namespace FoArtifacts.Tests;

/// <summary>
/// An in-memory d365fo: generate pushes a journal entry, undo pops entries, journal list reports them
/// newest first. <see cref="Override"/> can replace any answer (return null to fall through; call
/// <see cref="Default"/> to do the normal work and then add to it). With <see cref="FilesRoot"/> set,
/// generate writes real files and undo deletes them; <see cref="JournalCap"/> prunes the oldest entries
/// the way d365fo's D365FO_JOURNAL_MAX_ENTRIES does.
/// </summary>
internal sealed class FakeD365fo : ID365fo
{
    public sealed record Entry(string Id, string Kind, string Name, string Model, string Target);

    public List<Entry> Journal { get; } = [];
    public List<string> Calls { get; } = [];
    /// <summary>Whether each call's token could be cancelled, in call order.</summary>
    public List<bool> Cancellable { get; } = [];
    public List<(string Name, bool IsCustom)> Models { get; } = [("ContosoFleet", true)];
    /// <summary>Tables d365fo get table knows, by name → model.</summary>
    public Dictionary<string, string> ExistingTables { get; } = new(StringComparer.OrdinalIgnoreCase) { ["CustTable"] = "ContosoFleet" };
    public Func<IReadOnlyList<string>, CliResult?>? Override { get; set; }
    public string? FilesRoot { get; set; }
    public int? JournalCap { get; set; }

    private int _nextId;

    public Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(string.Join(' ', args));
        Cancellable.Add(ct.CanBeCanceled);
        return Task.FromResult(Override?.Invoke(args) ?? Default(args));
    }

    public IEnumerable<string> Generates => Calls.Where(c => c.StartsWith("generate ", StringComparison.Ordinal));

    /// <summary>Adds a journal entry, pruning the oldest past <see cref="JournalCap"/>.</summary>
    public Entry Record(string kind, string name, string model, string target)
    {
        var entry = new Entry($"e{++_nextId:D4}", kind, name, model, target);
        Journal.Add(entry);
        while (JournalCap is { } cap && Journal.Count > cap) Journal.RemoveAt(0);
        return entry;
    }

    /// <summary>Where generate puts an object's file.</summary>
    public string TargetOf(string model, string type, string name) =>
        FilesRoot is null ? $"/pkg/{model}/{type}/{name}.xml" : Path.Combine(FilesRoot, model, type, name + ".xml");

    public CliResult Default(IReadOnlyList<string> a)
    {
        switch (a[0])
        {
            case "models":
                return Ok(new { count = Models.Count, items = Models.Select(m => new { name = m.Name, isCustom = m.IsCustom }) });
            case "journal":
                var limit = int.Parse(a[3]);
                var newest = Enumerable.Reverse(Journal).Take(limit)
                    .Select(e => new { id = e.Id, kind = e.Kind, name = e.Name, model = e.Model, target = e.Target });
                return Ok(new { count = Math.Min(limit, Journal.Count), totalCount = Journal.Count, entries = newest });
            case "undo":
                var steps = Math.Min(int.Parse(a[2]), Journal.Count);
                foreach (var popped in Journal.GetRange(Journal.Count - steps, steps))
                    if (FilesRoot is not null && File.Exists(popped.Target)) File.Delete(popped.Target);
                Journal.RemoveRange(Journal.Count - steps, steps);
                return Ok(new { reverted = steps });
            case "index":
                return Ok(new { modelsProcessed = 1, modelsSkipped = 0 });
            case "get" when a[1] == "table":
                return ExistingTables.TryGetValue(a[2], out var tableModel)
                    ? Ok(new { table = new { name = a[2], model = tableModel, sourcePath = $"/pkg/{tableModel}/AxTable/{a[2]}.xml" } })
                    : Fail("NOT_FOUND", $"Table '{a[2]}' not found.");
            case "generate":
                var type = a[1];
                var name = type == "extension" ? a[3] : a[2];
                var at = a.ToList().IndexOf("--install-to");
                var model = at >= 0 ? a[at + 1] : Models[0].Name;
                var target = TargetOf(model, type, name);
                if (FilesRoot is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, name);
                }
                Record(type, name, model, target);
                return Ok(new { path = target });
            case "build":
                return Ok(new
                {
                    buildSucceeded = true,
                    exitCode = 0,
                    elapsedMs = 5,
                    errorCount = 0,
                    warningCount = 0,
                    errors = Array.Empty<object>(),
                    warnings = Array.Empty<object>(),
                    models = new[] { new { model = a[2], xppc = new { log = $"/pkg/{a[2]}/BuildModelResult.log" } } },
                });
            default:
                return Fail("BAD_INPUT", $"Unknown command '{a[0]}'.");
        }
    }

    public static CliResult Ok(object data, params string[] warnings) =>
        Envelope.Parse(0, JsonSerializer.Serialize(new { ok = true, data, warnings }), "");

    public static CliResult Fail(string code, string message, string? hint = null) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new { ok = false, error = new { code, message, hint } }), "");
}
