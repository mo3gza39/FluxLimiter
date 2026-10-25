using FluxLimiter.Ipc;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FluxLimiter.Service;

public sealed class PipeServerWorker(DriverBridge driver, ILogger<PipeServerWorker> logger) : BackgroundService
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);
    private bool firstInstance = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        driver.TryConnect();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try { pipe = CreatePipe(firstInstance); }
            catch (UnauthorizedAccessException ex) when (firstInstance)
            {
                logger.LogCritical(ex, "The IPC pipe name is already owned by another process. IPC is disabled.");
                return;
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not create an IPC pipe instance; retrying.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }
            firstInstance = false;

            try { await pipe.WaitForConnectionAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { await pipe.DisposeAsync(); break; }
            catch (IOException ex) { logger.LogWarning(ex, "IPC client connection failed."); await pipe.DisposeAsync(); continue; }
            _ = ServeClientAsync(pipe, stoppingToken);
        }
    }

    private async Task ServeClientAsync(NamedPipeServerStream pipe, CancellationToken stoppingToken)
    {
        await using (pipe)
        {
            try { await HandleClientAsync(pipe, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogWarning(ex, "IPC client failed."); }
        }
    }

    private static NamedPipeServerStream CreatePipe(bool isFirstInstance)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (isFirstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, options, 4096, 4096, security);
    }

    private async Task HandleClientAsync(Stream pipe, CancellationToken stoppingToken)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        requestCts.CancelAfter(RequestTimeout);
        var cancellationToken = requestCts.Token;
        var requestBytes = await FrameCodec.ReadFrameAsync(pipe, cancellationToken) ?? throw new EndOfStreamException("IPC client closed before sending a request.");
        ServiceResponse response;
        try
        {
            var request = ServiceProtocol.Deserialize<ServiceRequest>(Encoding.UTF8.GetString(requestBytes)) ?? throw new InvalidDataException("Invalid request.");
            response = Dispatch(request);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or DecoderFallbackException)
        {
            response = new ServiceResponse(ServiceProtocol.Version, "", false, "Invalid request.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IPC request handling failed.");
            response = new ServiceResponse(ServiceProtocol.Version, "", false, "Internal service error.");
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
