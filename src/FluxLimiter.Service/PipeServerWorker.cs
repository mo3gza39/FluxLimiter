using FluxLimiter.Ipc;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FluxLimiter.Service;

public sealed class PipeServerWorker(DriverBridge driver, ILogger<PipeServerWorker> logger) : BackgroundService
{
    private const int MaxConcurrentClients = 4;
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        driver.TryConnect();
        using var concurrencyGate = new SemaphoreSlim(MaxConcurrentClients, MaxConcurrentClients);
        var activeTasks = new List<Task>();

        while (!stoppingToken.IsCancellationRequested)
        {
            await concurrencyGate.WaitAsync(stoppingToken);
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                var capturedPipe = pipe;
                pipe = null;
                var clientTask = Task.Run(async () =>
                {
                    await using (capturedPipe)
                    {
                        try
                        {
                            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                            timeoutCts.CancelAfter(ClientRequestTimeout);
                            await HandleClientAsync(capturedPipe, timeoutCts.Token);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                        catch (Exception ex) { logger.LogWarning(ex, "IPC client failed or timed out."); }
                        finally { concurrencyGate.Release(); }
                    }
                }, stoppingToken);

                activeTasks.RemoveAll(t => t.IsCompleted);
                activeTasks.Add(clientTask);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                pipe?.Dispose();
                concurrencyGate.Release();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                concurrencyGate.Release();
                logger.LogWarning(ex, "IPC listener failed.");
            }
        }

        try { await Task.WhenAll(activeTasks); }
        catch { }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(ServiceProtocol.PipeName, PipeDirection.InOut, MaxConcurrentClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
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
