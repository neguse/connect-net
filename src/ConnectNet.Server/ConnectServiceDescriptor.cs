using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ConnectNet;
using Google.Protobuf;

namespace ConnectNet.Server;

public enum ConnectMethodType
{
    Unary,
    ServerStreaming,
    ClientStreaming,
    BidiStreaming
}

public class ConnectMethodDescriptor
{
    public string Procedure { get; }
    public MessageParser RequestParser { get; }
    public ConnectMethodType MethodType { get; }
    public Func<object, IMessage, ConnectContext, Task<IMessage>> Handler { get; }
    public Func<object, IMessage, ConnectContext, IAsyncEnumerable<IMessage>>? ServerStreamHandler { get; }
    public Func<object, IAsyncEnumerable<IMessage>, ConnectContext, Task<IMessage>>? ClientStreamHandler { get; }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        Func<object, IMessage, ConnectContext, Task<IMessage>> handler)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        MethodType = ConnectMethodType.Unary;
        Handler = handler;
        ServerStreamHandler = null;
        ClientStreamHandler = null;
    }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        ConnectMethodType methodType,
        Func<object, IMessage, ConnectContext, Task<IMessage>>? handler = null,
        Func<object, IMessage, ConnectContext, IAsyncEnumerable<IMessage>>? serverStreamHandler = null,
        Func<object, IAsyncEnumerable<IMessage>, ConnectContext, Task<IMessage>>? clientStreamHandler = null)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        MethodType = methodType;
        Handler = handler ?? ((_, _, _) => throw new ConnectException(ConnectCode.Unimplemented, "not implemented"));
        ServerStreamHandler = serverStreamHandler;
        ClientStreamHandler = clientStreamHandler;
    }
}
