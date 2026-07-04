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

    /// <summary>
    /// True when the method is declared side-effect free (protobuf
    /// <c>option idempotency_level = NO_SIDE_EFFECTS</c>). Only such unary methods are
    /// exposed over HTTP GET; exposing arbitrary unary methods over GET would make
    /// state-changing RPCs CSRF-able.
    /// </summary>
    public bool IsNoSideEffects { get; }

    public Func<object, IMessage, ConnectContext, Task<IMessage>> Handler { get; }
    public Func<object, IMessage, ConnectContext, IAsyncEnumerable<IMessage>>? ServerStreamHandler { get; }
    public Func<object, IAsyncEnumerable<IMessage>, ConnectContext, Task<IMessage>>? ClientStreamHandler { get; }
    public Func<object, IAsyncEnumerable<IMessage>, ConnectContext, IAsyncEnumerable<IMessage>>? BidiStreamHandler { get; }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        Func<object, IMessage, ConnectContext, Task<IMessage>> handler,
        bool isNoSideEffects = false)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        MethodType = ConnectMethodType.Unary;
        IsNoSideEffects = isNoSideEffects;
        Handler = handler;
        ServerStreamHandler = null;
        ClientStreamHandler = null;
        BidiStreamHandler = null;
    }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        ConnectMethodType methodType,
        Func<object, IMessage, ConnectContext, Task<IMessage>>? handler = null,
        Func<object, IMessage, ConnectContext, IAsyncEnumerable<IMessage>>? serverStreamHandler = null,
        Func<object, IAsyncEnumerable<IMessage>, ConnectContext, Task<IMessage>>? clientStreamHandler = null,
        Func<object, IAsyncEnumerable<IMessage>, ConnectContext, IAsyncEnumerable<IMessage>>? bidiStreamHandler = null,
        bool isNoSideEffects = false)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        MethodType = methodType;
        IsNoSideEffects = isNoSideEffects;
        Handler = handler ?? ((_, _, _) => throw new ConnectException(ConnectCode.Unimplemented, "not implemented"));
        ServerStreamHandler = serverStreamHandler;
        ClientStreamHandler = clientStreamHandler;
        BidiStreamHandler = bidiStreamHandler;
    }
}
