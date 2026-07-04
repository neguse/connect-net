using System;
using System.Buffers;

namespace ConnectNet;

/// <summary>
/// Represents a single envelope read from the wire. The payload is held in a buffer
/// borrowed from <see cref="ArrayPool{T}.Shared"/>. Callers must dispose the frame
/// to return the buffer; failing to do so does not corrupt anything but defeats the
/// pooling. Disposing the same variable twice is harmless, but copies of a frame share
/// the pooled buffer: never dispose a copy, or the buffer is returned to the pool twice.
/// </summary>
public struct EnvelopeFrame : IDisposable
{
    private byte[]? _pooledBuffer;
    private readonly int _length;

    public byte Flags { get; }
    public ReadOnlyMemory<byte> Data
        => _pooledBuffer == null ? ReadOnlyMemory<byte>.Empty : _pooledBuffer.AsMemory(0, _length);

    internal EnvelopeFrame(byte flags, byte[]? pooledBuffer, int length)
    {
        Flags = flags;
        _pooledBuffer = pooledBuffer;
        _length = length;
    }

    public void Dispose()
    {
        var buffer = _pooledBuffer;
        _pooledBuffer = null;
        if (buffer != null)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
