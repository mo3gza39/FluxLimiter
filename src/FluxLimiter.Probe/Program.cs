using FluxLimiter.Ipc;
using System.Text.Json;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "status";
var operation = command switch
{
    "ping" => ServiceOperations.Ping,
    "status" => ServiceOperations.GetStatus,
    "driver" => ServiceOperations.DriverGetStatus,
    _ => ""
};
if (operation.Length == 0)
{
    Console.Error.WriteLine("Usage: FluxLimiter.Probe ping | status | driver");
    return 2;
}
try
{
    var client = new NamedPipeClient();
    var response = await client.SendAsync(new ServiceRequest(ServiceProtocol.Version, Guid.NewGuid().ToString("N"), operation), TimeSpan.FromSeconds(5));
    if (!response.Success)
    {
        Console.Error.WriteLine(response.Error ?? "Service request failed.");
        return 1;
    }
    Console.WriteLine(response.Payload is { } payload ? JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }) : "OK");
    return 0;
}
catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
