using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Google.Protobuf;

namespace ConnectNet;

public class ProtobufCodec : ICodec
{
    public string Name => "proto";

    public void Serialize(IMessage message, IBufferWriter<byte> destination)
    {
        if (destination == null) throw new ArgumentNullException(nameof(destination));
        // Google.Protobuf supports IBufferWriter<byte> via WriteTo as of recent versions.
        message.WriteTo(destination);
    }

    public T Deserialize<T>(ReadOnlyMemory<byte> data) where T : IMessage<T>, new()
    {
        var message = new T();
        if (data.Length == 0) return message;
        // Fast path: if the memory is array-backed (always the case for ArrayPool /
        // byte[] origins in this codebase), parse without copying.
        if (MemoryMarshal.TryGetArray(data, out ArraySegment<byte> seg) && seg.Array != null)
        {
            ((IMessage)message).MergeFrom(seg.Array.AsSpan(seg.Offset, seg.Count));
            return message;
        }
        ((IMessage)message).MergeFrom(data.Span);
        return message;
    }

    public IMessage Deserialize(ReadOnlyMemory<byte> data, MessageParser parser)
    {
        if (data.Length == 0) return parser.ParseFrom(Array.Empty<byte>());
        return parser.ParseFrom(data.Span);
    }
}
