using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Parses_a_success()
    {
        var r = Envelope.Parse(0, """{"ok":true,"data":{"path":"/p/A.xml"},"warnings":["w1"]}""", "");
        Assert.True(r.Ok);
        Assert.Equal("/p/A.xml", r.Data.GetProperty("path").GetString());
        Assert.Equal(new[] { "w1" }, r.Warnings);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public void Parses_a_failure_with_code_message_and_hint()
    {
        var r = Envelope.Parse(2,
            """{"ok":false,"error":{"code":"BAD_INPUT","message":"Unknown option 'bogus'.","hint":"Run the command with --help to see the options it actually accepts."}}""",
            "");
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("BAD_INPUT", r.ErrorCode);
        Assert.Equal("Unknown option 'bogus'.", r.ErrorMessage);
        Assert.StartsWith("Run the command", r.ErrorHint);
    }

    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException")]
    [InlineData("{not json}")]
    [InlineData("""{"data":{}}""")]
    public void Output_without_an_envelope_is_a_failure(string stdout)
    {
        var r = Envelope.Parse(1, stdout, "boom");
        Assert.False(r.Ok);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
        Assert.Contains("exited with code 1", r.ErrorMessage);
    }

    [Fact]
    public void Success_with_a_nonzero_exit_code_is_a_failure()
    {
        var r = Envelope.Parse(3, """{"ok":true,"data":{}}""", "");
        Assert.False(r.Ok);
        Assert.Equal("EXIT_CODE", r.ErrorCode);
    }

    [Fact]
    public void An_envelope_on_stderr_is_used_when_stdout_has_none()
    {
        // d365fo prints argument-parsing errors (e.g. an unknown option) on stderr.
        var r = Envelope.Parse(2, "", """{"ok":false,"error":{"code":"BAD_INPUT","message":"Unknown option 'bogus'."}}""");
        Assert.Equal("BAD_INPUT", r.ErrorCode);
    }

    [Fact]
    public void Text_before_the_envelope_is_ignored() =>
        Assert.True(Envelope.Parse(0, "note: warming up\n{\"ok\":true,\"data\":{}}", "").Ok);

    [Fact]
    public void Failure_without_error_details_gets_a_generic_code()
    {
        var r = Envelope.Parse(1, """{"ok":false}""", "");
        Assert.Equal("UNKNOWN", r.ErrorCode);
        Assert.NotNull(r.ErrorMessage);
    }
}
