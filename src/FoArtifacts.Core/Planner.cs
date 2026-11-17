namespace FoArtifacts.Core;

public sealed record Stage(int Number, IReadOnlyList<ExpandedTask> Tasks);

public static class Planner
{
    /// <summary>
    /// Puts each task one stage after its deepest dependency (longest path), so everything a task
    /// references is written — and indexed — before it runs. Within a stage, tasks run by rank, then
    /// by their position in the request.
    /// </summary>
    public static (IReadOnlyList<Stage>? Stages, IReadOnlyList<string> Errors) Plan(ExpandedBatch batch, ReferenceMap map)
    {
        var tasks = batch.Tasks.ToDictionary(t => t.Index);
        var dependsOn = tasks.Values.ToDictionary(
            t => t.Index,
            t => t.References.SelectMany(r => r.TargetTasks).Where(i => i != t.Index && tasks.ContainsKey(i)).ToHashSet());
        var dependents = tasks.Keys.ToDictionary(i => i, _ => new List<int>());
        foreach (var (task, deps) in dependsOn)
            foreach (var dep in deps)
                dependents[dep].Add(task);

        var waiting = dependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var depth = tasks.Keys.ToDictionary(i => i, _ => 0);
        var ready = new Queue<int>(waiting.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order());
        var placed = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            placed++;
            foreach (var next in dependents[current])
            {
                depth[next] = Math.Max(depth[next], depth[current] + 1);
                if (--waiting[next] == 0) ready.Enqueue(next);
            }
        }

        if (placed < tasks.Count)
        {
            var stuck = waiting.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order()
                .Select(i => $"tasks[{i}] ({tasks[i].Label})");
            return (null, new List<string>
            {
                $"Dependency cycle: these tasks are part of, or wait on, a cycle of references, so no order works: {string.Join(", ", stuck)}.",
            });
        }

        var stages = depth
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => new Stage(
                g.Key + 1,
                g.Select(kv => tasks[kv.Key]).OrderBy(t => map.Commands[t.Type].Rank).ThenBy(t => t.Index).ToList()))
            .ToList();
        return (stages, Array.Empty<string>());
    }
}
