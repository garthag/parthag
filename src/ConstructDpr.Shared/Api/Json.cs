using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConstructDpr.Shared.Api;

/// <summary>One JSON format for the server and every app (camelCase, enums as text, readable by other software too).</summary>
public static class Json
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.PropertyNameCaseInsensitive = true;
        o.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        if (!o.Converters.OfType<JsonStringEnumConverter>().Any()) o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }

    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

/// <summary>Error body returned by the server; the apps turn it back into the same exception type.</summary>
public sealed record ApiError(string Kind, string Message);
