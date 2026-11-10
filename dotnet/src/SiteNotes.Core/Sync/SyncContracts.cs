using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiteNotes.Core.Sync;

/// <summary>One row as it travels between device and server. The server stores it as-is,
/// so new fields never need a server change.</summary>
public record SyncRecord(
    string Id,
    string EntityType,
    string ProjectId,
    string Json,
    DateTime UpdatedAtUtc,
    bool IsDeleted,
    long Version = 0);

public record PushRequest(string DeviceId, List<SyncRecord> Records);

public record PushResponse(long ServerVersion, int Accepted, int Ignored);

public record PullResponse(List<SyncRecord> Records, long ServerVersion, bool HasMore);

public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public const string HubPath = "/hubs/sync";
    public const string ChangedMethod = "Changed";
    public const string ApiKeyHeader = "X-Api-Key";
}
