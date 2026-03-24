using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectNet;

public static class Envelope
{
    public const byte FlagCompressed = 0x01;
    public const byte FlagEndStream = 0x02;

    public static async Task WriteAsync(Stream stream, byte flags, byte[] data, CancellationToken ct = default)
    {
        var header = new byte[5];
        header[0] = flags;
        var length = data.Length;
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;
        await stream.WriteAsync(header, 0, 5, ct).ConfigureAwait(false);
        if (data.Length > 0)
            await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
    }

    public static async Task<(byte flags, byte[] data)?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        var bytesRead = 0;
        while (bytesRead < 5)
        {
            var n = await stream.ReadAsync(header, bytesRead, 5 - bytesRead, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (bytesRead == 0) return null;
                throw new ConnectException(ConnectCode.Internal, "incomplete envelope header");
            }
            bytesRead += n;
        }
        var flags = header[0];
        var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];
        var data = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            var n = await stream.ReadAsync(data, bytesRead, length - bytesRead, ct).ConfigureAwait(false);
            if (n == 0)
                throw new ConnectException(ConnectCode.Internal, "incomplete envelope data");
            bytesRead += n;
        }
        return (flags, data);
    }
}
