using System.Diagnostics;
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class D365foRunnerTests
{
    private static (string Exe, string[] Args) Sleep(int seconds) => OperatingSystem.IsWindows()
        ? ("powershell", new[] { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" })
        : ("sleep", new[] { seconds.ToString() });

    [Fact]
    public async Task Missing_executable_is_reported()
    {
        var r = await new D365foRunner("/nonexistent/d365fo-missing", TimeSpan.FromSeconds(5)).RunAsync(["models", "list"], Ct);
        Assert.Equal("CLI_NOT_FOUND", r.ErrorCode);
        Assert.Contains("D365FO_CLI", r.ErrorHint);
    }

    [Fact]
    public async Task Program_without_json_output_is_a_failure()
    {
        var r = await new D365foRunner("dotnet", TimeSpan.FromSeconds(30)).RunAsync(["--version"], Ct);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
    }

    [Fact]
    public async Task Slow_program_is_stopped()
    {
        var (exe, args) = Sleep(30);
        var clock = Stopwatch.StartNew();
        var r = await new D365foRunner(exe, TimeSpan.FromSeconds(1)).RunAsync(args, Ct);
        Assert.Equal("TIMEOUT", r.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        var (exe, args) = Sleep(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new D365foRunner(exe, TimeSpan.FromSeconds(60)).RunAsync(args, cts.Token));
    }

    [Fact]
    public async Task Child_gets_force_json_extra_environment_and_utf8()
    {
        var env = new Dictionary<string, string> { ["FO_TEST_VALUE"] = "Kjøretøy" };
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command",
                "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); " +
                "[Console]::Out.Write('{\"ok\":true,\"data\":{\"force\":\"' + $env:D365FO_FORCE_JSON + '\",\"extra\":\"' + $env:FO_TEST_VALUE + '\"}}')" })
            : ("sh", new[] { "-c", "printf '{\"ok\":true,\"data\":{\"force\":\"%s\",\"extra\":\"%s\"}}' \"$D365FO_FORCE_JSON\" \"$FO_TEST_VALUE\"" });
        var r = await new D365foRunner(exe, TimeSpan.FromSeconds(30), env).RunAsync(args, Ct);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("1", r.Data.GetProperty("force").GetString());
        Assert.Equal("Kjøretøy", r.Data.GetProperty("extra").GetString());
    }

    [Fact]
    public async Task Child_does_not_inherit_the_servers_stdin()
    {
        // The child reads stdin until EOF; with stdin closed it ends at once instead of hanging until the timeout.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "[Console]::In.ReadToEnd()" })
            : ("cat", Array.Empty<string>());
        var clock = Stopwatch.StartNew();
        var r = await new D365foRunner(exe, TimeSpan.FromSeconds(30)).RunAsync(args, Ct);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"took {clock.Elapsed}");
    }

    [Theory]
    [InlineData("/opt/d365fo", "/opt/d365fo")]
    [InlineData(null, "d365fo")]
    [InlineData("  ", "d365fo")]
    public void Locator_prefers_D365FO_CLI(string? value, string expected) =>
        Assert.Equal(expected, CliLocator.Resolve(name => name == "D365FO_CLI" ? value : null));

    [Theory]
    [InlineData("30", 30)]
    [InlineData("abc", 120)]
    [InlineData("0", 120)]
    [InlineData(null, 120)]
    public void Timeout_comes_from_FO_ARTIFACTS_TIMEOUT_SECONDS(string? value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CliLocator.Timeout(name => name == "FO_ARTIFACTS_TIMEOUT_SECONDS" ? value : null));
}
