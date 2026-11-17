using System.Text.Json;
using FoBatchGen.Core;

namespace FoBatchGen.Tests;

/// <summary>
/// An in-memory d365fo: generate pushes a journal entry, undo pops entries, journal list reports them
/// newest first. <see cref="Override"/> can replace any answer (return null to fall through).
/// </summary>
internal sealed class FakeD365fo : ID365fo
{
    public sealed record Entry(string Id, string Kind, string Name, string Model, string Target);

    public List<Entry> Journal { get; } = [];
    public List<string> Calls { get; } = [];
    public List<(string Name, bool IsCustom)> Models { get; } = [("ContosoFleet", true)];
    public HashSet<string> ExistingTables { get; } = new(StringComparer.OrdinalIgnoreCase) { "CustTable" };
    public Func<IReadOnlyList<string>, CliResult?>? Override { get; set; }

    private int _nextId;

    public Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(string.Join(' ', args));
        return Task.FromResult(Override?.Invoke(args) ?? Default(args));
    }

    public IEnumerable<string> Generates => Calls.Where(c => c.StartsWith("generate ", StringComparison.Ordinal));

    private CliResult Default(IReadOnlyList<string> a)
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
                Journal.RemoveRange(Journal.Count - steps, steps);
                return Ok(new { reverted = steps });
            case "index":
                return Ok(new { modelsProcessed = 1 });
            case "get" when a[1] == "table":
                return ExistingTables.Contains(a[2])
                    ? Ok(new { table = new { name = a[2], sourcePath = $"/pkg/ContosoFleet/AxTable/{a[2]}.xml" } })
                    : Fail("NOT_FOUND", $"Table '{a[2]}' not found.");
            case "generate":
                var type = a[1];
                var name = type == "extension" ? a[3] : a[2];
                var at = a.ToList().IndexOf("--install-to");
                var model = at >= 0 ? a[at + 1] : Models[0].Name;
                var target = $"/pkg/{model}/{type}/{name}.xml";
                Journal.Add(new Entry($"e{++_nextId:D4}", type, name, model, target));
                return Ok(new { path = target });
            default:
                return Fail("BAD_INPUT", $"Unknown command '{a[0]}'.");
        }
    }

    public static CliResult Ok(object data, params string[] warnings) =>
        Envelope.Parse(0, JsonSerializer.Serialize(new { ok = true, data, warnings }), "");

    public static CliResult Fail(string code, string message, string? hint = null) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new { ok = false, error = new { code, message, hint } }), "");
}
