using System;
using System.Threading.Tasks;
using ConnectNet;
using Google.Protobuf;

namespace ConnectNet.Server;

public class ConnectMethodDescriptor
{
    public string Procedure { get; }
    public MessageParser RequestParser { get; }
    public Func<object, IMessage, ConnectContext, Task<IMessage>> Handler { get; }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        Func<object, IMessage, ConnectContext, Task<IMessage>> handler)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        Handler = handler;
    }
}
