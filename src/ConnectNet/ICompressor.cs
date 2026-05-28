using System;
using System.Buffers;

namespace ConnectNet;

public interface ICompressor
{
    string Name { get; }

    /// <summary>
    /// Compresses <paramref name="source"/> into <paramml name="destination"/>. The
    /// destination grows as needed via the caller-provided <see cref="IBufferWriter{T}"/>.
    /// </summary>
    void Compress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination);

    /// <summary>
    /// Decompresses <paramref name="source"/> into <paramref name="destination"/>, refusing
    /// payloads whose decompressed size exceeds <paramref name="maxBytes"/>. Implementations
    /// must throw <see cref="ConnectException"/> with <see cref="ConnectCode.ResourceExhausted"/>
    /// on overrun to defend against zip bombs.
    /// </summary>
    void Decompress(ReadOnlyMemory<byte> source, IBufferWriter<byte> destination, int maxBytes);
}
