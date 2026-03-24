using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet;

/// <summary>
/// Client-side unary interceptor.
/// </summary>
public interface IClientInterceptor
{
    Task<IMessage> InterceptUnaryAsync(
        UnaryRequestContext context,
        Func<UnaryRequestContext, Task<IMessage>> next,
        CancellationToken ct);
}

/// <summary>
/// Server-side unary interceptor.
/// </summary>
public interface IServerInterceptor
{
    Task<IMessage> InterceptUnaryAsync(
        UnaryServerContext context,
        Func<UnaryServerContext, Task<IMessage>> next);
}

public class UnaryRequestContext
{
    public string Procedure { get; set; }
    public IMessage Request { get; set; }
    public IDictionary<string, string> Headers { get; set; }

    public UnaryRequestContext(string procedure, IMessage request, IDictionary<string, string> headers)
    {
        Procedure = procedure;
        Request = request;
        Headers = headers;
    }
}

public class UnaryServerContext
{
    public string Procedure { get; }
    public IMessage Request { get; }
    public ConnectContext Context { get; }

    public UnaryServerContext(string procedure, IMessage request, ConnectContext context)
    {
        Procedure = procedure;
        Request = request;
        Context = context;
    }
}
