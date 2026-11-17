namespace FoArtifacts.Core;

/// <summary>A reference to an object that other task(s) in the batch define.</summary>
public sealed record ResolvedRef(string Option, string Name, IReadOnlyList<int> TargetTasks);

public sealed record ExpandedTask(
    int Index,
    string Type,
    string Name,
    string Install,
    IReadOnlyList<string> LeadingArgs,
    IReadOnlyList<KeyValuePair<string, string>> Options,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> DefinedNames,
    IReadOnlyList<ResolvedRef> References)
{
    public string Label => $"{Type} {Name}";
}

public sealed record ExpandedBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ExpandedTask> Tasks);

public static class NameExpander
{
    private sealed record Symbol(string Expanded, IReadOnlyList<string> Kinds, int Task);

    public static string ApplyPrefix(string prefix, string name) =>
        prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name : prefix + name;

    public static (ExpandedBatch? Batch, IReadOnlyList<string> Errors) Expand(ValidBatch batch, ReferenceMap map)
    {
        var errors = new List<string>();
        var symbols = new Dictionary<string, List<Symbol>>(StringComparer.OrdinalIgnoreCase);
        var labels = batch.Tasks.ToDictionary(t => t.Index, t => t.Label);

        void Define(string name, IReadOnlyList<string> kinds, ValidTask task)
        {
            var expanded = ApplyPrefix(batch.Prefix, name);
            if (symbols.TryGetValue(expanded, out var existing))
            {
                var clash = existing.FirstOrDefault(s =>
                    s.Task != task.Index
                    && string.Equals(s.Expanded, expanded, StringComparison.OrdinalIgnoreCase)
                    && s.Kinds.Intersect(kinds).Any());
                if (clash is not null)
                    errors.Add($"'{expanded}' is defined twice: {labels[clash.Task]} and {task.Label}.");
            }
            var symbol = new Symbol(expanded, kinds, task.Index);
            // A name given already prefixed ("CFltVehicle") must also catch bare references ("Vehicle").
            var bare = batch.Prefix.Length > 0 && expanded.Length > batch.Prefix.Length
                ? expanded[batch.Prefix.Length..]
                : expanded;
            foreach (var key in new[] { name, expanded, bare }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!symbols.TryGetValue(key, out var list)) symbols[key] = list = [];
                list.Add(symbol);
            }
        }

        // Pass 1: every name the batch defines.
        foreach (var task in batch.Tasks)
        {
            var rule = map.Commands[task.Type];
            if (rule.Name.Role == NameRoles.Define) Define(task.Name, rule.Name.Kinds, task);
            foreach (var definition in rule.Defines)
                foreach (var value in task.Values(definition.Option))
                    Define(value, definition.Kinds, task);
        }

        // Pass 2: prefix definitions, rewrite references.
        var expandedTasks = new List<ExpandedTask>();
        foreach (var task in batch.Tasks)
        {
            var rule = map.Commands[task.Type];
            var references = new List<ResolvedRef>();

            string Resolve(string option, string token, IReadOnlyList<string> kinds)
            {
                if (token.Length == 0 || !symbols.TryGetValue(token, out var candidates)) return token;
                var any = kinds.Contains(ReferenceMap.AnyKind);
                var matches = candidates
                    .Where(s => s.Task != task.Index && (any || s.Kinds.Intersect(kinds).Any()))
                    .ToList();
                if (matches.Count == 0) return token;
                references.Add(new ResolvedRef(option, matches[0].Expanded, matches.Select(m => m.Task).Distinct().ToList()));
                return matches[0].Expanded;
            }

            var name = rule.Name.Role switch
            {
                NameRoles.Define => ApplyPrefix(batch.Prefix, task.Name),
                NameRoles.Reference => Resolve("<name>", task.Name, rule.Name.Kinds),
                _ => task.Name,
            };

            var leading = rule.Leading.Select(key => task.Values(key)[0]).ToList();
            var defineOptions = rule.Defines.Select(d => d.Option).ToHashSet(StringComparer.Ordinal);
            var options = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in task.Options)
            {
                if (rule.Leading.Contains(key)) continue;
                if (defineOptions.Contains(key))
                {
                    options.Add(new(key, ApplyPrefix(batch.Prefix, value)));
                    continue;
                }
                var rewritten = value;
                foreach (var refRule in rule.Refs.Where(r => r.Option == key))
                    rewritten = RewriteValue(rewritten, refRule, (token, kinds) => Resolve(key, token, kinds));
                options.Add(new(key, rewritten));
            }

            var defined = new List<string>();
            if (rule.Name.Role == NameRoles.Define) defined.Add(name);
            foreach (var definition in rule.Defines)
                defined.AddRange(task.Values(definition.Option).Select(v => ApplyPrefix(batch.Prefix, v)));

            expandedTasks.Add(new ExpandedTask(
                task.Index, task.Type, name, rule.Install, leading, options, task.Flags,
                defined.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), references));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ExpandedBatch(batch.Model, batch.Prefix, batch.DryRun, expandedTasks), errors);
    }

    /// <summary>Applies one <see cref="RefRule"/> to an option value, resolving only the parts it names.</summary>
    internal static string RewriteValue(string value, RefRule rule, Func<string, IReadOnlyList<string>, string> resolve)
    {
        if (rule.Split is null) return Part(value);
        var parts = value.Split(rule.Split);
        if (rule.Segment >= parts.Length) return value;
        parts[rule.Segment] = Part(parts[rule.Segment]);
        return string.Join(rule.Split, parts);

        string Part(string segment)
        {
            if (rule.Subsplit is null) return resolve(segment, rule.Kinds);
            var sub = segment.Split(rule.Subsplit);
            for (var i = 0; i < sub.Length; i++)
            {
                var selected = rule.Subsegment switch
                {
                    null => true,
                    -1 => i == sub.Length - 1,
                    var n => i == n,
                };
                if (selected) sub[i] = resolve(sub[i], rule.Kinds);
            }
            return string.Join(rule.Subsplit, sub);
        }
    }
}
