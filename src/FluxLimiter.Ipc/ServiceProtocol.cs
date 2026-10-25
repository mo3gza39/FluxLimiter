using System.Text.Json;

namespace FluxLimiter.Ipc;

public static class ServiceProtocol
{
    public const string PipeName = "FluxLimiter.Service.v1";
    public const int Version = 1;
    public const int MaxMessageBytes = 64 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);
}

public sealed record ServiceRequest(
    int Version,
    string RequestId,
    string Operation,
    JsonElement? Payload = null);

public sealed record ServiceResponse(
    int Version,
    string RequestId,
    bool Success,
    string? Error = null,
    JsonElement? Payload = null);

public sealed record ServiceStatus(
    string ServiceVersion,
    bool DriverConnected,
    string CounterSource,
    bool EnforcementAvailable);

public static class ServiceOperations
{
    public const string Ping = "ping";
    public const string GetStatus = "status.get";
    public const string DriverGetStatus = "driver.status";
}
