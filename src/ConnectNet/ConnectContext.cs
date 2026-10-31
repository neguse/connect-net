using System;
using System.Collections.Generic;
using System.Threading;

namespace ConnectNet;

public class ConnectContext
{
    private static readonly Dictionary<string, string> Empty = new();

    private readonly object? _requestHeaderSource;
    private readonly Func<object, IDictionary<string, string>>? _requestHeaderFactory;
    private IDictionary<string, string>? _requestHeaders;
    private Dictionary<string, string>? _responseHeaders;
    private Dictionary<string, string>? _responseTrailers;

    public IDictionary<string, string> RequestHeaders
        => _requestHeaders ?? LazyInitializer.EnsureInitialized(ref _requestHeaders, CreateRequestHeaders)!;
    public IDictionary<string, string> ResponseHeaders
        => _responseHeaders ?? LazyInitializer.EnsureInitialized(ref _responseHeaders, NewHeaders)!;
    public IDictionary<string, string> ResponseTrailers
        => _responseTrailers ?? LazyInitializer.EnsureInitialized(ref _responseTrailers, NewHeaders)!;
    public CancellationToken CancellationToken { get; }

    internal Dictionary<string, string> ResponseHeadersOrEmpty => _responseHeaders ?? Empty;
    internal Dictionary<string, string> ResponseTrailersOrEmpty => _responseTrailers ?? Empty;

    public ConnectContext(
        IDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        _requestHeaders = requestHeaders;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Defers copying the request headers until a handler reads them, which most never do.
    /// <paramref name="source"/> must stay valid for as long as the handler can read them.
    /// </summary>
    internal ConnectContext(
        object source,
        Func<object, IDictionary<string, string>> requestHeaderFactory,
        CancellationToken cancellationToken)
    {
        _requestHeaderSource = source;
        _requestHeaderFactory = requestHeaderFactory;
        CancellationToken = cancellationToken;
    }

    private IDictionary<string, string> CreateRequestHeaders()
        => _requestHeaderFactory != null ? _requestHeaderFactory(_requestHeaderSource!) : NewHeaders();

    private static Dictionary<string, string> NewHeaders() => new(StringComparer.OrdinalIgnoreCase);
}
