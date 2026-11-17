using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

internal static class TestData
{
    public static readonly ReferenceMap Map = ReferenceMap.LoadEmbedded();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TaskInput T(string type, string name, string? optionsJson = null) =>
        new(type, name, optionsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson));

    public static ValidBatch Valid(string prefix, params TaskInput[] tasks)
    {
        var (batch, errors) = BatchSpec.Validate(new BatchRequest("ContosoFleet", prefix, tasks), Map);
        Assert.Empty(errors);
        return batch!;
    }
}
