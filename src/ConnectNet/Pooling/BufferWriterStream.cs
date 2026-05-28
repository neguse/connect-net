using System;
using System.Buffers;
using System.IO;

namespace ConnectNet.Pooling;

/// <summary>
/// A write-only <see cref="Stream"/> adapter that funnels every write into an
/// <see cref="IBufferWriter{T}"/>. Used to bridge legacy Stream-based encoders
/// (e.g. <see cref="System.IO.Compression.GZipStream"/>) onto the modern buffer-writer API
/// without an intermediate MemoryStream + ToArray() allocation.
/// </summary>
internal sealed class BufferWriterStream : Stream
{
    private readonly IBufferWriter<byte> _writer;
    private bool _disposed;

    public BufferWriterStream(IBufferWriter<byte> writer)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureOpen();
        if (count <= 0) return;
        var dest = _writer.GetSpan(count);
        buffer.AsSpan(offset, count).CopyTo(dest);
        _writer.Advance(count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureOpen();
        if (buffer.Length == 0) return;
        var dest = _writer.GetSpan(buffer.Length);
        buffer.CopyTo(dest);
        _writer.Advance(buffer.Length);
    }

    public override void WriteByte(byte value)
    {
        EnsureOpen();
        var dest = _writer.GetSpan(1);
        dest[0] = value;
        _writer.Advance(1);
    }

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    private void EnsureOpen()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BufferWriterStream));
    }
}
