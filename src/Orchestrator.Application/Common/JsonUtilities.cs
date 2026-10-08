using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orchestrator.Application.Common;

/// <summary>Dotted-path navigation over <see cref="JsonNode"/> trees (no expressions, no wildcards).</summary>
public static class JsonPath
{
    public static IReadOnlyList<string> Split(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? Array.Empty<string>()
            : path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Follows object properties and numeric array indices. Returns false when a segment is missing.</summary>
    public static bool TryGet(JsonNode? root, IReadOnlyList<string> path, out JsonNode? value)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var child))
            {
                current = child;
                continue;
            }

            if (current is JsonArray array
                && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index < array.Count)
            {
                current = array[index];
                continue;
            }

            value = null;
            return false;
        }

        value = current;
        return true;
    }

    /// <summary>Sets <paramref name="value"/> at <paramref name="path"/>, creating intermediate objects.</summary>
    public static void Set(JsonObject root, IReadOnlyList<string> path, JsonNode? value)
    {
        if (path.Count == 0)
        {
            throw new ArgumentException("Target path must not be empty.", nameof(path));
        }

        var current = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            if (current[path[i]] is JsonObject next)
            {
                current = next;
            }
            else
            {
                var created = new JsonObject();
                current[path[i]] = created;
                current = created;
            }
        }

        current[path[^1]] = value;
    }
}

/// <summary>Canonical JSON + SHA-256 hashing used to compare idempotent requests.</summary>
public static class RequestHasher
{
    /// <summary>Re-serialises JSON with object keys sorted (ordinal) and no insignificant whitespace.</summary>
    public static string Canonicalize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "null";
        }

        var node = JsonNode.Parse(json);
        return Canonical(node)?.ToJsonString() ?? "null";
    }

    public static string Hash(params string?[] parts)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Join("\n", parts.Select(p => p ?? string.Empty)));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonNode? Canonical(JsonNode? node) =>
        node switch
        {
            null => null,
            JsonObject obj => new JsonObject(obj
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))
                .ToList()),
            JsonArray array => new JsonArray(array.Select(Canonical).ToArray()),
            _ => node.DeepClone(),
        };
}

public static class JsonText
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>Raw JSON of an optional element, or <paramref name="fallback"/> when absent/null.</summary>
    public static string? FromElement(JsonElement? element, string? fallback = null) =>
        element is { } value && value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? value.GetRawText()
            : fallback;

    /// <summary>Parses stored JSON into a detached element for API responses.</summary>
    public static JsonElement? ToElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(json);
        }
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Web);
}

public static class UtcTime
{
    /// <summary>Normalises client-supplied timestamps to UTC (Unspecified is treated as UTC).</summary>
    public static DateTime? Normalize(DateTime? value) =>
        value switch
        {
            null => null,
            { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
            { Kind: DateTimeKind.Unspecified } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
            _ => value,
        };
}
