using System.IO.Pipes;
using System.Text;

namespace FluxLimiter.Ipc;

public sealed class NamedPipeClient
{
    public async Task<ServiceResponse> SendAsync(ServiceRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await using var pipe = new NamedPipeClientStream(".", ServiceProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeoutCts.Token);
        await FrameCodec.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(ServiceProtocol.Serialize(request)), timeoutCts.Token);
        var bytes = await FrameCodec.ReadFrameAsync(pipe, timeoutCts.Token) ?? throw new EndOfStreamException("Service closed the IPC connection.");
        return ServiceProtocol.Deserialize<ServiceResponse>(Encoding.UTF8.GetString(bytes)) ?? throw new InvalidDataException("Service returned an invalid response.");
    }
}
