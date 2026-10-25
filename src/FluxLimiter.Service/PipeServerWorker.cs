using FluxLimiter.Ipc;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FluxLimiter.Service;

public sealed class PipeServerWorker(DriverBridge driver, ILogger<PipeServerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        driver.TryConnect();
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                await HandleClientAsync(pipe, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "IPC client failed."); }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var requestBytes = await FrameCodec.ReadFrameAsync(pipe, cancellationToken) ?? throw new EndOfStreamException("IPC client closed before sending a request.");
        ServiceResponse response;
        try
        {
            var request = ServiceProtocol.Deserialize<ServiceRequest>(Encoding.UTF8.GetString(requestBytes)) ?? throw new InvalidDataException("Invalid request.");
            response = Dispatch(request);
        }
        catch (Exception ex)
        {
            response = new ServiceResponse(ServiceProtocol.Version, "", false, ex.Message);
        }
        await FrameCodec.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(ServiceProtocol.Serialize(response)), cancellationToken);
    }

    private ServiceResponse Dispatch(ServiceRequest request)
    {
        if (request.Version != ServiceProtocol.Version) return new(ServiceProtocol.Version, request.RequestId, false, "Unsupported protocol version.");
        return request.Operation switch
        {
            ServiceOperations.Ping => new(ServiceProtocol.Version, request.RequestId, true),
            ServiceOperations.GetStatus or ServiceOperations.DriverGetStatus => new(ServiceProtocol.Version, request.RequestId, true, Payload: ToElement(driver.GetStatus())),
            _ => new(ServiceProtocol.Version, request.RequestId, false, "Unknown operation.")
        };
    }

    private static System.Text.Json.JsonElement ToElement<T>(T value) => System.Text.Json.JsonSerializer.SerializeToElement(value, ServiceProtocol.JsonOptions);
}
