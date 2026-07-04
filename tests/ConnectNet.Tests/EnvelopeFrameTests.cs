using System.Buffers;
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class EnvelopeFrameTests
{
    [Fact]
    public void Dispose_ExposesEmptyData()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16);
        var frame = new EnvelopeFrame(0x01, buffer, 4);
        Assert.Equal(4, frame.Data.Length);

        frame.Dispose();

        Assert.True(frame.Data.IsEmpty);
    }

    [Fact]
    public void Dispose_Twice_ReturnsBufferToPoolOnlyOnce()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(60000);
        var frame = new EnvelopeFrame(0x00, buffer, 8);

        frame.Dispose();
        frame.Dispose();

        // A double return would let the pool hand the same array to two
        // consecutive callers.
        var first = ArrayPool<byte>.Shared.Rent(60000);
        var second = ArrayPool<byte>.Shared.Rent(60000);
        try
        {
            Assert.NotSame(first, second);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(first);
            ArrayPool<byte>.Shared.Return(second);
        }
    }

    [Fact]
    public void Dispose_EmptyFrame_IsNoOp()
    {
        var frame = new EnvelopeFrame(0x02, null, 0);
        frame.Dispose();
        frame.Dispose();
        Assert.True(frame.Data.IsEmpty);
    }
}
