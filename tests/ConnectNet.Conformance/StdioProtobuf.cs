using System;
using System.IO;
using Google.Protobuf;

namespace ConnectNet.Conformance;

internal static class StdioProtobuf
{
    public static T? Read<T>(Stream stream) where T : IMessage<T>, new()
    {
        var lengthBytes = new byte[4];
        var bytesRead = 0;
        while (bytesRead < 4)
        {
            var n = stream.Read(lengthBytes, bytesRead, 4 - bytesRead);
            if (n == 0) return default;
            bytesRead += n;
        }
        var length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];
        var messageBytes = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            var n = stream.Read(messageBytes, bytesRead, length - bytesRead);
            if (n == 0) throw new EndOfStreamException();
            bytesRead += n;
        }
        var message = new T();
        message.MergeFrom(messageBytes);
        return message;
    }

    public static void Write(Stream stream, IMessage message)
    {
        var messageBytes = message.ToByteArray();
        var length = messageBytes.Length;
        var lengthBytes = new byte[] {
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length
        };
        stream.Write(lengthBytes, 0, 4);
        stream.Write(messageBytes, 0, messageBytes.Length);
        stream.Flush();
    }
}
