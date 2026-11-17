namespace FoArtifacts.Core;

public static class CommandBuilder
{
    /// <summary>The argument list for one task. Arguments are passed as a list, never through a shell.</summary>
    public static IReadOnlyList<string> Build(ExpandedTask task, string model, string? applyToPath = null)
    {
        var args = new List<string> { "generate", task.Type };
        args.AddRange(task.LeadingArgs);
        args.Add(task.Name);
        // "--key=value" in one token: d365fo's parser would read a separate value that starts with "-" as an option.
        foreach (var (key, value) in task.Options) args.Add($"--{key}={value}");
        foreach (var flag in task.Flags) args.Add("--" + flag);
        if (task.Install == InstallModes.ApplyTo)
        {
            args.Add("--apply-to");
            args.Add(applyToPath ?? $"<file of table {task.Name}>");
        }
        else
        {
            args.Add("--install-to");
            args.Add(model);
        }
        args.AddRange(["--no-refresh", "--output", "json"]);
        return args;
    }

    /// <summary>A readable command line for dry runs and messages.</summary>
    public static string Render(IReadOnlyList<string> args) => "d365fo " + string.Join(' ', args.Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_.:/\\=,@".Contains(c))
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";
}
