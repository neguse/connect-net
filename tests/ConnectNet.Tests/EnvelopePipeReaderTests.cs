using System;
using System.IO.Pipelines;
using System.Linq;
using System.Threading.Tasks;
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class EnvelopePipeReaderTests
{
    private static byte[] Frame(byte flags, params byte[] payload)
    {
        var length = payload.Length;
        return new[] { flags, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length }.Concat(payload).ToArray();
    }

    private static async Task<PipeReader> Deliver(int chunkSize, params byte[] bytes)
    {
        var pipe = new Pipe();
        for (var i = 0; i < bytes.Length; i += chunkSize)
        {
            await pipe.Writer.WriteAsync(bytes.AsMemory(i, Math.Min(chunkSize, bytes.Length - i)));
        }
        pipe.Writer.Complete();
        return pipe.Reader;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(1024)]
    public async Task ReadsConsecutiveFrames_HoweverTheBytesArrive(int chunkSize)
    {
        var bytes = Frame(0x01, 1, 2, 3).Concat(Frame(0x00)).Concat(Frame(0x02, 9)).ToArray();
        var reader = await Deliver(chunkSize, bytes);

        using (var first = (await Envelope.ReadFrameAsync(reader)).Value)
        {
            Assert.Equal(0x01, first.Flags);
            Assert.Equal(new byte[] { 1, 2, 3 }, first.Data.ToArray());
        }
        using (var empty = (await Envelope.ReadFrameAsync(reader)).Value)
        {
            Assert.Equal(0x00, empty.Flags);
            Assert.Equal(0, empty.Data.Length);
        }
        using (var last = (await Envelope.ReadFrameAsync(reader)).Value)
        {
            Assert.Equal(0x02, last.Flags);
            Assert.Equal(new byte[] { 9 }, last.Data.ToArray());
        }
        Assert.Null(await Envelope.ReadFrameAsync(reader));
    }

    [Theory]
    [InlineData(3, "incomplete envelope header")]
    [InlineData(5, "incomplete envelope data")]
    [InlineData(6, "incomplete envelope data")]
    public async Task TruncatedFrame_IsAnError(int length, string expected)
    {
        var reader = await Deliver(1024, Frame(0x00, 1, 2, 3).Take(length).ToArray());
        var ex = await Assert.ThrowsAsync<ConnectException>(() => Envelope.ReadFrameAsync(reader));
        Assert.Equal(ConnectCode.Internal, ex.Code);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public async Task OversizedHeader_IsRejectedBeforeThePayloadArrives()
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 0, 0x7F, 0, 0, 0 });
        var ex = await Assert.ThrowsAsync<ConnectException>(() => Envelope.ReadFrameAsync(pipe.Reader, 1024));
        Assert.Equal(ConnectCode.ResourceExhausted, ex.Code);
    }
}
