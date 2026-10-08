using System.Text.Json;

namespace Orchestrator.Domain.Common;

/// <summary>Serializer settings shared by the domain for the small JSON documents it produces itself.</summary>
internal static class DomainJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize(object value) => JsonSerializer.Serialize(value, value.GetType(), Options);

    public static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
