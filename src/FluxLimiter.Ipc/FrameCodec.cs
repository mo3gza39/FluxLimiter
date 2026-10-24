using System.Buffers.Binary;

namespace FluxLimiter.Ipc;

public static class FrameCodec
{
    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > ServiceProtocol.MaxMessageBytes) throw new InvalidDataException("IPC frame exceeds the protocol limit.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, cancellationToken)) return null;
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > ServiceProtocol.MaxMessageBytes) throw new InvalidDataException("IPC frame length is outside the protocol limit.");
        var payload = new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, cancellationToken)) throw new EndOfStreamException("IPC peer closed in the middle of a frame.");
        return payload;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("IPC peer closed in the middle of a frame.");
            }
            offset += read;
        }
        return true;
    }
}
