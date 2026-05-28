using System;
using System.Buffers;
using Google.Protobuf;

namespace ConnectNet;

public interface ICodec
{
    string Name { get; }

    /// <summary>Writes the encoded form of <paramref name="message"/> into <paramref name="destination"/>.</summary>
    void Serialize(IMessage message, IBufferWriter<byte> destination);

    /// <summary>Parses a message of type <typeparamref name="T"/> from the encoded bytes.</summary>
    T Deserialize<T>(ReadOnlyMemory<byte> data) where T : IMessage<T>, new();

    /// <summary>Parses a message via the supplied <see cref="MessageParser"/> from the encoded bytes.</summary>
    IMessage Deserialize(ReadOnlyMemory<byte> data, MessageParser parser);
}
