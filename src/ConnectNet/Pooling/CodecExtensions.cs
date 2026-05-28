using System;
using Google.Protobuf;

namespace ConnectNet.Pooling;

/// <summary>
/// Convenience helpers that adapt the buffer-writer-based <see cref="ICodec"/> and
/// <see cref="ICompressor"/> APIs back to <c>byte[]</c>-returning shapes for callers that
/// still pass byte arrays around. Internally each call rents a buffer from the pool and
/// only materialises the final array once, so this is the bridge we use while migrating
/// callers off byte[]. Once everyone speaks IBufferWriter, this file can be deleted.
/// </summary>
internal static class CodecExtensions
{
    public static byte[] SerializeToArray(this ICodec codec, IMessage message)
    {
        using var writer = new ArrayPoolBufferWriter();
        codec.Serialize(message, writer);
        return writer.ToArray();
    }

    public static byte[] DecompressToArray(this ICompressor compressor, ReadOnlyMemory<byte> source, int maxBytes)
    {
        using var writer = new ArrayPoolBufferWriter();
        compressor.Decompress(source, writer, maxBytes);
        return writer.ToArray();
    }

    public static byte[] CompressToArray(this ICompressor compressor, ReadOnlyMemory<byte> source)
    {
        using var writer = new ArrayPoolBufferWriter();
        compressor.Compress(source, writer);
        return writer.ToArray();
    }
}
