using FluxLimiter.Ipc;
using System.Text.Json;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

var request = new ServiceRequest(ServiceProtocol.Version, "abc", ServiceOperations.GetStatus);
var json = ServiceProtocol.Serialize(request);
var roundTrip = ServiceProtocol.Deserialize<ServiceRequest>(json);
Assert(roundTrip is not null, "Request deserializes");
Assert(roundTrip!.Version == ServiceProtocol.Version, "Protocol version round-trips");
Assert(roundTrip.RequestId == "abc", "Request id round-trips");
Assert(roundTrip.Operation == ServiceOperations.GetStatus, "Operation round-trips");

var status = new ServiceStatus("1.0", false, "unavailable", false);
var payload = JsonSerializer.SerializeToElement(status, ServiceProtocol.JsonOptions);
var response = new ServiceResponse(ServiceProtocol.Version, "abc", true, Payload: payload);
var responseRoundTrip = ServiceProtocol.Deserialize<ServiceResponse>(ServiceProtocol.Serialize(response));
Assert(responseRoundTrip is { Success: true }, "Response round-trips");
Assert(responseRoundTrip!.Payload?.Deserialize<ServiceStatus>(ServiceProtocol.JsonOptions)?.DriverConnected == false, "Status payload round-trips");
Assert(ServiceProtocol.PipeName.Contains("v1", StringComparison.Ordinal), "Pipe name is versioned");

using (var frames = new MemoryStream())
{
    await FrameCodec.WriteFrameAsync(frames, new byte[] { 1, 2, 3 }, CancellationToken.None);
    frames.Position = 0;
    var readBack = await FrameCodec.ReadFrameAsync(frames, CancellationToken.None);
    Assert(readBack is not null && readBack.SequenceEqual(new byte[] { 1, 2, 3 }), "Length-prefixed frame round-trips");
    Assert(await FrameCodec.ReadFrameAsync(frames, CancellationToken.None) is null, "Clean EOF between frames is accepted");
}

var oversizedHeader = new byte[4];
System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(oversizedHeader, ServiceProtocol.MaxMessageBytes + 1);
bool oversizedRejected = false;
try { await FrameCodec.ReadFrameAsync(new MemoryStream(oversizedHeader), CancellationToken.None); }
catch (InvalidDataException) { oversizedRejected = true; }
Assert(oversizedRejected, "Oversized IPC frame is rejected before allocation");

bool truncatedRejected = false;
try { await FrameCodec.ReadFrameAsync(new MemoryStream(new byte[] { 10, 0, 0, 0, 1, 2 }), CancellationToken.None); }
catch (EndOfStreamException) { truncatedRejected = true; }
Assert(truncatedRejected, "Truncated IPC frame is rejected");

Console.WriteLine("All IPC contract tests passed. No driver enforcement tested.");
