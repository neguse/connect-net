using System;
using ConnectNet.Pooling;
using Xunit;

namespace ConnectNet.Tests.Pooling;

public class ArrayPoolBufferWriterTests
{
    [Fact]
    public void Write_AccumulatesAndExposesWrittenSpan()
    {
        using var w = new ArrayPoolBufferWriter(16);
        var span = w.GetSpan(4);
        span[0] = 1; span[1] = 2; span[2] = 3; span[3] = 4;
        w.Advance(4);

        Assert.Equal(4, w.WrittenCount);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, w.WrittenSpan.ToArray());
    }

    [Fact]
    public void GrowsWhenSizeHintExceedsCapacity()
    {
        using var w = new ArrayPoolBufferWriter(8);
        var span = w.GetSpan(1024);
        Assert.True(span.Length >= 1024);
        for (int i = 0; i < 1024; i++) span[i] = (byte)(i & 0xff);
        w.Advance(1024);
        Assert.Equal(1024, w.WrittenCount);
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var w = new ArrayPoolBufferWriter(16);
        w.Dispose();
        w.Dispose(); // should not throw
    }

    [Fact]
    public void OperationsAfterDisposeThrow()
    {
        var w = new ArrayPoolBufferWriter(16);
        w.Dispose();
        Assert.Throws<ObjectDisposedException>(() => w.GetSpan(1));
    }

    [Fact]
    public void AdvancePastBufferEndThrows()
    {
        using var w = new ArrayPoolBufferWriter(4);
        Assert.Throws<InvalidOperationException>(() => w.Advance(1024));
    }

    [Fact]
    public void ToArrayReturnsIndependentCopy()
    {
        using var w = new ArrayPoolBufferWriter(8);
        var span = w.GetSpan(3);
        span[0] = 7; span[1] = 8; span[2] = 9;
        w.Advance(3);
        var arr = w.ToArray();
        Assert.Equal(new byte[] { 7, 8, 9 }, arr);

        // Mutating returned array must not affect the writer's internal state.
        arr[0] = 0;
        Assert.Equal(7, w.WrittenSpan[0]);
    }
}
