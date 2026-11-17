using System.Security.Cryptography;
using System.Text.Json;
using FoBatchGen.Core;

namespace FoBatchGen.IntegrationTests;

/// <summary>A throw-away copy of d365fo's sample model with its own index, journal and config.</summary>
public sealed class D365foSandbox : IAsyncDisposable
{
    public const string Model = "TestModel";

    public static string? CliPath =>
        Environment.GetEnvironmentVariable("D365FO_CLI") is { Length: > 0 } path ? path : null;

    /// <summary>The d365fo-cli checkout made by scripts/build-d365fo.*, found by walking up to FoBatchGen.slnx.</summary>
    public static string? CheckoutPath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "FoBatchGen.slnx"))) continue;
                var checkout = Path.Combine(dir.FullName, ".d365fo-cli");
                return Directory.Exists(checkout) ? checkout : null;
            }
            return null;
        }
    }

    public static string? SkipReason =>
        CliPath is null ? "Set D365FO_CLI to the d365fo executable (scripts/build-d365fo.sh prints it) to run integration tests."
        : CheckoutPath is null ? "Run scripts/build-d365fo.sh first: the tests use the sample model in .d365fo-cli."
        : null;

    public required string Root { get; init; }
    public required string Packages { get; init; }
    public required D365foRunner Cli { get; init; }
    public required BatchExecutor Executor { get; init; }

    private Dictionary<string, string> _snapshot = new();

    public string ModelDir => Path.Combine(Packages, Model, Model);

    public static async Task<D365foSandbox> CreateAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "fo-batchgen-it-" + Guid.NewGuid().ToString("N")[..8]);
        var packages = Path.Combine(root, "Packages");
        CopyDirectory(Path.Combine(CheckoutPath!, "tests", "Samples", "MiniAot"), packages);
        var environment = new Dictionary<string, string>
        {
            ["D365FO_PACKAGES_PATH"] = packages,
            ["D365FO_CUSTOM_MODELS"] = Model,
            ["D365FO_CONFIG_DIR"] = Path.Combine(root, "cfg"),
            ["D365FO_INDEX_DB"] = Path.Combine(root, "cfg", "index.sqlite"),
            ["D365FO_HOME"] = Path.Combine(root, "home"),
            ["D365FO_BRIDGE_ENABLED"] = "0",
        };
        var cli = new D365foRunner(CliPath!, TimeSpan.FromMinutes(2), environment);
        await MustAsync(cli, ["index", "build", "--output", "json"], ct);
        await MustAsync(cli, ["index", "extract", "--output", "json"], ct);
        var sandbox = new D365foSandbox
        {
            Root = root,
            Packages = packages,
            Cli = cli,
            Executor = new BatchExecutor(cli, ReferenceMap.LoadEmbedded()),
        };
        sandbox.TakeSnapshot();
        return sandbox;
    }

    public static async Task<CliResult> MustAsync(ID365fo cli, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await cli.RunAsync(args, ct);
        if (!result.Ok)
            throw new InvalidOperationException($"d365fo {string.Join(' ', args)} failed: {result.ErrorCode}: {result.ErrorMessage}");
        return result;
    }

    public static TaskInput T(string type, string name, string? optionsJson = null) =>
        new(type, name, optionsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson));

    /// <summary>Remembers the content of every file under Packages.</summary>
    public void TakeSnapshot() => _snapshot = Hashes();

    /// <summary>Files added, changed or removed since <see cref="TakeSnapshot"/>.</summary>
    public IReadOnlyList<string> ChangesSinceSnapshot()
    {
        var now = Hashes();
        var changes = new List<string>();
        changes.AddRange(now.Keys.Except(_snapshot.Keys).Select(f => "added " + f));
        changes.AddRange(_snapshot.Keys.Except(now.Keys).Select(f => "removed " + f));
        changes.AddRange(now.Keys.Intersect(_snapshot.Keys).Where(f => now[f] != _snapshot[f]).Select(f => "changed " + f));
        return changes;
    }

    private Dictionary<string, string> Hashes() =>
        Directory.EnumerateFiles(Packages, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Packages, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}
