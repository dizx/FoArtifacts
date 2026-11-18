using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>A parsed d365fo result. <see cref="Data"/> is default (Undefined) when absent.</summary>
public sealed record CliResult(
    int ExitCode,
    bool Ok,
    JsonElement Data,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? ErrorMessage,
    string? ErrorHint)
{
    public static CliResult Failure(int exitCode, string code, string message, string? hint = null) =>
        new(exitCode, false, default, Array.Empty<string>(), code, message, hint);
}

public interface ID365fo
{
    /// <summary>Runs d365fo. Throws only when <paramref name="ct"/> is cancelled.</summary>
    Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

public static class Envelope
{
    /// <summary>Reads the envelope from stdout, or from stderr where d365fo prints argument-parsing errors.</summary>
    public static CliResult Parse(int exitCode, string stdout, string stderr) =>
        TryParse(exitCode, stdout) ?? TryParse(exitCode, stderr) ?? NoEnvelope(exitCode, stdout, stderr);

    private static CliResult? TryParse(int exitCode, string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var okElement)
                || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            var ok = okElement.GetBoolean();
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            var warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            string? code = null, message = null, hint = null;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                code = Str(e, "code");
                message = Str(e, "message");
                hint = Str(e, "hint");
            }

            if (ok && exitCode != 0)
                return new CliResult(exitCode, false, data, warnings, "EXIT_CODE", $"d365fo reported success but exited with code {exitCode}.", null);
            if (ok)
                return new CliResult(exitCode, true, data, warnings, null, null, null);
            return new CliResult(exitCode, false, data, warnings,
                code ?? "UNKNOWN", message ?? "d365fo reported a failure without a message.", hint);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static CliResult NoEnvelope(int exitCode, string stdout, string stderr)
    {
        var output = (stdout + " " + stderr).Trim();
        if (output.Length > 500) output = output[..500] + "…";
        return CliResult.Failure(exitCode, "NO_ENVELOPE", $"d365fo exited with code {exitCode} and printed no JSON result. Output: {output}");
    }
}

public sealed class D365foRunner(string executable, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) : ID365fo
{
    public async Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // The MCP server talks JSON-RPC over its own stdin/stdout: the child must not touch either.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["D365FO_FORCE_JSON"] = "1";
        if (environment is not null)
            foreach (var (key, value) in environment)
                info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return CliResult.Failure(-1, "CLI_NOT_FOUND", $"Could not start '{executable}': {ex.Message}",
                "Set D365FO_CLI to the full path of the d365fo executable, or put d365fo on PATH.");
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return CliResult.Failure(-1, "TIMEOUT",
                $"`d365fo {string.Join(' ', args.Take(2))}` did not finish within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s and was stopped.",
                "Raise FO_ARTIFACTS_TIMEOUT_SECONDS if the model is large.");
        }
        return Envelope.Parse(process.ExitCode, await stdout, await stderr);
    }
}

public static class CliLocator
{
    public static string Resolve(Func<string, string?> getEnv) =>
        getEnv("D365FO_CLI") is { } path && !string.IsNullOrWhiteSpace(path) ? path : "d365fo";

    public static TimeSpan Timeout(Func<string, string?> getEnv) => Seconds(getEnv("FO_ARTIFACTS_TIMEOUT_SECONDS"), 120);

    /// <summary>The limit for <c>d365fo build</c> alone: a large model compiles for minutes.</summary>
    public static TimeSpan BuildTimeout(Func<string, string?> getEnv) => Seconds(getEnv("FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS"), 900);

    private static TimeSpan Seconds(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(fallback);
}
