using System.Text.Json;

namespace FoBatchGen.Core;

public static class ArtifactPaths
{
    /// <summary>Every string under a property named <c>path</c>, at any depth of a d365fo result.</summary>
    public static IReadOnlyList<string> From(JsonElement data)
    {
        var found = new List<string>();
        Walk(data, found);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(JsonElement element, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("path") && property.Value.ValueKind == JsonValueKind.String)
                        found.Add(property.Value.GetString()!);
                    else
                        Walk(property.Value, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, found);
                break;
        }
    }
}
