using System.Text.Json;
using FoArtifacts.Core;
using Xunit;

namespace FoArtifacts.Tests;

internal static class TestData
{
    public static readonly ReferenceMap Map = ReferenceMap.LoadEmbedded();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A d365fo envelope saved under Fixtures, parsed as if d365fo had exited with <paramref name="exitCode"/>.</summary>
    public static CliResult Fixture(string name, int exitCode) =>
        Envelope.Parse(exitCode, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), "");

    public static TaskInput T(string type, string name, string? optionsJson = null) =>
        new(type, name, optionsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson));

    public static ValidBatch Valid(string prefix, params TaskInput[] tasks)
    {
        var (batch, errors) = BatchSpec.Validate(new BatchRequest("ContosoFleet", prefix, tasks), Map);
        Assert.Empty(errors);
        return batch!;
    }

    public static ExpandedBatch Expanded(string prefix, params TaskInput[] tasks)
    {
        var (batch, errors) = NameExpander.Expand(Valid(prefix, tasks), Map);
        Assert.Empty(errors);
        return batch!;
    }
}
