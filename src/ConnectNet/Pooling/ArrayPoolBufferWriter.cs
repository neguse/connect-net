using System;
using System.Buffers;

namespace ConnectNet.Pooling;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> backed by an <see cref="ArrayPool{T}"/> rental.
/// Caller is responsible for disposing to return the underlying array to the pool.
/// </summary>
/// <remarks>
/// Designed for short-lived RPC scratch buffers: serialize/deserialize, decompress, etc.
/// Not thread-safe. Each instance is single-use within one logical scope.
/// </remarks>
internal sealed class ArrayPoolBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultInitialCapacity = 256;

    private byte[] _buffer;
    private int _written;
    private bool _disposed;

    public ArrayPoolBufferWriter(int initialCapacity = DefaultInitialCapacity)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity));
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
        _written = 0;
    }

    public int WrittenCount => _written;
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>
    /// Returns a copy of the written bytes as a freshly allocated array. Use only when the
    /// caller really needs ownership of an independent buffer (e.g. legacy <c>byte[]</c> APIs).
    /// Prefer <see cref="WrittenSpan"/> or <see cref="WrittenMemory"/> to keep the pooled
    /// buffer in use.
    /// </summary>
    public byte[] ToArray()
    {
        EnsureNotDisposed();
        var result = new byte[_written];
        Buffer.BlockCopy(_buffer, 0, result, 0, _written);
        return result;
    }

    public void Advance(int count)
    {
        EnsureNotDisposed();
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (_written + count > _buffer.Length)
            throw new InvalidOperationException("cannot advance past end of buffer");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        EnsureNotDisposed();
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (sizeHint == 0) sizeHint = 1;
        var available = _buffer.Length - _written;
        if (available >= sizeHint) return;

        // Grow by at least doubling — matches List<T> / MemoryStream behavior, amortizes
        // expansion cost across many small writes.
        var required = _written + sizeHint;
        var newSize = _buffer.Length * 2;
        if (newSize < required) newSize = required;

        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _written);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(ArrayPoolBufferWriter));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = Array.Empty<byte>();
    }
}
