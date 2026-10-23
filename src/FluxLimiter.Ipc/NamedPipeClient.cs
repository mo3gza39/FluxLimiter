using System.IO.Pipes;
using System.Text;

namespace FluxLimiter.Ipc;

public sealed class NamedPipeClient
{
    public async Task<ServiceResponse> SendAsync(
        ServiceRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(
            ".",
            ServiceProtocol.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(timeoutCts.Token);
        await WriteLineAsync(pipe, ServiceProtocol.Serialize(request), timeoutCts.Token);
        var json = await ReadLineAsync(pipe, timeoutCts.Token);
        return ServiceProtocol.Deserialize<ServiceResponse>(json)
            ?? throw new InvalidDataException("Service returned an invalid response.");
    }

    public static async Task WriteLineAsync(Stream stream, string value, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\n");
        if (bytes.Length > ServiceProtocol.MaxMessageBytes)
            throw new InvalidDataException("IPC message exceeds the protocol limit.");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, cancellationToken);
            if (read == 0) throw new EndOfStreamException("IPC peer closed the connection.");
            if (one[0] == (byte)'\n') break;
            buffer.WriteByte(one[0]);
            if (buffer.Length > ServiceProtocol.MaxMessageBytes)
                throw new InvalidDataException("IPC message exceeds the protocol limit.");
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
