using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet.Pooling;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ConnectChannelOptions
{
    /// <summary>
    /// HttpClient to use for sending requests. If specified, the channel uses it as-is and
    /// the caller is responsible for its lifetime (unless <see cref="DisposeHttpClient"/> is true).
    /// When both <see cref="HttpClient"/> and <see cref="HttpHandler"/> are null, the channel
    /// constructs a default HttpClient with conservative security defaults
    /// (AllowAutoRedirect=false, UseCookies=false, AutomaticDecompression=None).
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    /// <summary>
    /// HttpMessageHandler to wrap in a new HttpClient. Use this to inject custom transports
    /// such as Cysharp.Net.Http.YetAnotherHttpHandler (Unity HTTP/2), UnityWebRequestHandler
    /// (Unity WebGL), DelegatingHandler chains (Polly/OpenTelemetry), or test mocks. The
    /// channel owns and disposes the HttpClient it creates.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>
    /// Codec used to serialize/deserialize messages. Defaults to <see cref="ProtobufCodec"/>.
    /// </summary>
    public ICodec? Codec { get; set; }

    /// <summary>
    /// Whether to dispose the user-provided <see cref="HttpClient"/> when the channel is
    /// disposed. Only consulted when <see cref="HttpClient"/> is set; channel-owned clients
    /// are always disposed.
    /// </summary>
    public bool DisposeHttpClient { get; set; } = false;

    public ICompressor? RequestCompressor { get; set; }
    public bool AcceptCompression { get; set; } = true;
    public List<ICompressor> Decompressors { get; set; } = new() { new GzipCompressor(), new DeflateCompressor() };
    public List<IClientInterceptor> Interceptors { get; set; } = new();

    /// <summary>
    /// Maximum bytes the client will read from a single response (compressed or decompressed,
    /// per envelope, or per Unary body). Defaults to 4 MiB. A malicious server cannot force
    /// the client to allocate more than this for one message.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class ConnectChannel : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;
    private readonly ConnectChannelOptions _channelOptions;
    // Cached on construction so the hot path never re-formats them.
    private readonly string _unaryContentType;
    private readonly string _streamingContentType;
    private readonly string? _acceptEncodingHeader;
    private bool _disposed;

    private ConnectChannel(HttpClient httpClient, bool ownsHttpClient, Uri baseUri, ICodec codec, ConnectChannelOptions options)
    {
        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
        _baseUri = baseUri;
        _codec = codec;
        _channelOptions = options;
        _unaryContentType = "application/" + codec.Name;
        _streamingContentType = "application/connect+" + codec.Name;
        _acceptEncodingHeader = options.AcceptCompression && options.Decompressors.Count > 0
            ? string.Join(", ", options.Decompressors.Select(d => d.Name))
            : null;
    }

    /// <summary>
    /// Creates a channel for the given <paramref name="baseUri"/>. When neither
    /// <see cref="ConnectChannelOptions.HttpClient"/> nor <see cref="ConnectChannelOptions.HttpHandler"/>
    /// is set, an internally constructed HttpClient is used with conservative security
    /// defaults (no auto-redirect, no cookies, no automatic decompression).
    /// </summary>
    public static ConnectChannel ForAddress(string baseUri, ConnectChannelOptions? options = null)
        => ForAddress(new Uri(baseUri.TrimEnd('/'), UriKind.Absolute), options);

    public static ConnectChannel ForAddress(Uri baseUri, ConnectChannelOptions? options = null)
    {
        if (baseUri == null) throw new ArgumentNullException(nameof(baseUri));
        if (!baseUri.IsAbsoluteUri) throw new ArgumentException("baseUri must be absolute", nameof(baseUri));

        options ??= new ConnectChannelOptions();
        var codec = options.Codec ?? new ProtobufCodec();
        var (client, owns) = ResolveHttpClient(options);
        return new ConnectChannel(client, owns, baseUri, codec, options);
    }

    private static (HttpClient client, bool owns) ResolveHttpClient(ConnectChannelOptions options)
    {
        if (options.HttpClient != null)
        {
            return (options.HttpClient, options.DisposeHttpClient);
        }
        if (options.HttpHandler != null)
        {
            // Wrap the user-supplied handler in an HttpClient that the channel owns. The
            // handler itself is not disposed by the channel because callers may share it
            // (e.g. a single SocketsHttpHandler reused across channels).
            return (new HttpClient(options.HttpHandler, disposeHandler: false), owns: true);
        }
        return (CreateDefaultHttpClient(), owns: true);
    }

    /// <summary>
    /// Builds an HttpClient whose underlying handler refuses redirects, cookies, and
    /// transparent content decompression. This prevents a malicious server from
    /// (a) redirecting the client to a different host (and leaking custom headers in the
    /// process), (b) populating a shared cookie jar, or (c) returning a compressed body
    /// that the runtime would silently expand without any size cap.
    /// </summary>
    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };
        return new HttpClient(handler, disposeHandler: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    internal HttpClient HttpClient => _httpClient;
    internal Uri BaseUri => _baseUri;
    internal ICodec Codec => _codec;
    internal ConnectChannelOptions ChannelOptions => _channelOptions;

    /// <summary>
    /// Resolves a procedure path against the channel's base URI, asserting that the result
    /// stays on the same scheme + authority. Defends against a malicious or buggy procedure
    /// string like <c>"//evil.com/x"</c> that would otherwise cause <see cref="Uri"/> to
    /// rewrite the authority via the "network-path reference" rule.
    /// </summary>
    internal static Uri BuildProcedureUri(Uri baseUri, string procedure)
    {
        if (procedure == null) throw new ArgumentNullException(nameof(procedure));
        var combined = new Uri(baseUri, procedure);
        if (!string.Equals(combined.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(combined.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"procedure '{procedure}' resolves to a different origin ({combined.Scheme}://{combined.Authority}) than the channel base ({baseUri.Scheme}://{baseUri.Authority})",
                nameof(procedure));
        }
        return combined;
    }

    public async Task<TRes> UnaryAsync<TReq, TRes>(
        string procedure,
        TReq request,
        CallOptions? options = null,
        CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        var interceptors = _channelOptions.Interceptors;
        if (interceptors.Count > 0)
        {
            var headers = options?.Headers != null
                ? new Dictionary<string, string>(options.Headers)
                : new Dictionary<string, string>();
            var context = new UnaryRequestContext(procedure, request, headers);

            // Build the chain: innermost is the actual HTTP call
            Func<UnaryRequestContext, Task<IMessage>> chain = async (ctx) =>
            {
                // Copy any headers modified by interceptors back to options
                var callOpts = options ?? new CallOptions();
                callOpts.Headers = ctx.Headers;
                return (IMessage)await SendUnaryAsync<TRes>(ctx.Procedure, ctx.Request, callOpts, ct).ConfigureAwait(false);
            };

            // Wrap interceptors in reverse order so first interceptor runs first
            for (int i = interceptors.Count - 1; i >= 0; i--)
            {
                var interceptor = interceptors[i];
                var next = chain;
                chain = (ctx) => interceptor.InterceptUnaryAsync(ctx, next, ct);
            }

            var result = await chain(context).ConfigureAwait(false);
            return (TRes)result;
        }

        return await SendUnaryAsync<TRes>(procedure, request, options, ct).ConfigureAwait(false);
    }

    private async Task<TRes> SendUnaryAsync<TRes>(
        string procedure,
        IMessage request,
        CallOptions? options,
        CancellationToken ct)
        where TRes : IMessage<TRes>, new()
    {
        if (options?.UseGet == true)
        {
            return await SendUnaryGetAsync<TRes>(procedure, request, options, ct).ConfigureAwait(false);
        }

        var uri = BuildProcedureUri(_baseUri, procedure);

        // Serialize directly into a pooled buffer; the HttpContent borrows it (no ToArray()).
        using var requestWriter = new ArrayPoolBufferWriter();
        _codec.Serialize(request, requestWriter);

        // Optional request compression. Compressed payload also lives in a pooled writer.
        ArrayPoolBufferWriter? compressedRequestWriter = null;
        ReadOnlyMemory<byte> requestBody;
        string requestContentEncoding = "";
        if (_channelOptions.RequestCompressor != null)
        {
            compressedRequestWriter = new ArrayPoolBufferWriter();
            _channelOptions.RequestCompressor.Compress(requestWriter.WrittenMemory, compressedRequestWriter);
            requestBody = compressedRequestWriter.WrittenMemory;
            requestContentEncoding = _channelOptions.RequestCompressor.Name;
        }
        else
        {
            requestBody = requestWriter.WrittenMemory;
        }

        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
            httpRequest.Content = new PooledMemoryHttpContent(
                requestBody,
                _unaryContentType,
                contentEncoding: requestContentEncoding.Length == 0 ? null : requestContentEncoding);
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");
            if (_acceptEncodingHeader != null)
            {
                httpRequest.Headers.Add("Accept-Encoding", _acceptEncodingHeader);
            }

            if (options?.Timeout is TimeSpan timeout)
            {
                httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
            }

            if (options?.Headers != null)
            {
                foreach (var header in options.Headers)
                {
                    httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            using var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

            // Extract response headers and Trailer-* headers only if the caller cares.
            if (options != null)
            {
                ExtractResponseHeaders(httpResponse, options);
                foreach (var header in httpResponse.Headers)
                {
                    if (header.Key.StartsWith("Trailer-", StringComparison.OrdinalIgnoreCase))
                    {
                        var trailerName = header.Key.Substring("Trailer-".Length);
                        options.ResponseTrailers[trailerName] = string.Join(",", header.Value);
                    }
                }
            }

            var maxResponseBytes = _channelOptions.MaxResponseBytes;

            if (!httpResponse.IsSuccessStatusCode)
            {
                using var errorWriter = new ArrayPoolBufferWriter();
                await ReadBoundedAsync(httpResponse.Content, errorWriter, maxResponseBytes, ct).ConfigureAwait(false);
                ReadOnlyMemory<byte> errorBytes = errorWriter.WrittenMemory;
                ArrayPoolBufferWriter? errorDecompressed = null;
                try
                {
                    var errorContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
                    if (errorContentEncoding != null)
                    {
                        var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                            string.Equals(d.Name, errorContentEncoding, StringComparison.OrdinalIgnoreCase));
                        if (decompressor != null)
                        {
                            try
                            {
                                errorDecompressed = new ArrayPoolBufferWriter();
                                decompressor.Decompress(errorBytes, errorDecompressed, maxResponseBytes);
                                errorBytes = errorDecompressed.WrittenMemory;
                            }
                            catch (ConnectException)
                            {
                                throw;
                            }
                            catch
                            {
                                // Body may not actually be compressed; fall back to raw bytes.
                            }
                        }
                    }
                    throw ParseErrorResponse(errorBytes, (int)httpResponse.StatusCode);
                }
                finally
                {
                    errorDecompressed?.Dispose();
                }
            }

            var contentType = httpResponse.Content.Headers.ContentType?.MediaType;
            if (contentType != null && !contentType.StartsWith(_unaryContentType, StringComparison.OrdinalIgnoreCase)
                && !contentType.StartsWith(_streamingContentType, StringComparison.OrdinalIgnoreCase))
            {
                var ctCode = contentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                    ? ConnectCode.Internal : ConnectCode.Unknown;
                throw new ConnectException(ctCode, $"unexpected content-type: {contentType}");
            }

            using var responseWriter = new ArrayPoolBufferWriter();
            await ReadBoundedAsync(httpResponse.Content, responseWriter, maxResponseBytes, ct).ConfigureAwait(false);
            ReadOnlyMemory<byte> responseBytes = responseWriter.WrittenMemory;

            ArrayPoolBufferWriter? responseDecompressed = null;
            try
            {
                var responseContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
                if (responseContentEncoding != null)
                {
                    var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                        string.Equals(d.Name, responseContentEncoding, StringComparison.OrdinalIgnoreCase));
                    if (decompressor != null)
                    {
                        responseDecompressed = new ArrayPoolBufferWriter();
                        decompressor.Decompress(responseBytes, responseDecompressed, maxResponseBytes);
                        responseBytes = responseDecompressed.WrittenMemory;
                    }
                    else if (!string.Equals(responseContentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ConnectException(ConnectCode.Internal, $"unknown content-encoding: {responseContentEncoding}");
                    }
                }

                if (responseBytes.Length == 0)
                    return new TRes();

                return _codec.Deserialize<TRes>(responseBytes);
            }
            finally
            {
                responseDecompressed?.Dispose();
            }
        }
        finally
        {
            compressedRequestWriter?.Dispose();
        }
    }

    private async Task<TRes> SendUnaryGetAsync<TRes>(
        string procedure,
        IMessage request,
        CallOptions? options,
        CancellationToken ct)
        where TRes : IMessage<TRes>, new()
    {
        var body = _codec.SerializeToArray(request);
        var messageEncoded = Base64UrlEncode(body);

        var uriBuilder = new UriBuilder(BuildProcedureUri(_baseUri, procedure));
        var query = $"encoding={Uri.EscapeDataString(_codec.Name)}&message={Uri.EscapeDataString(messageEncoded)}&base64=1&connect=v1";
        uriBuilder.Query = query;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uriBuilder.Uri);

        // Signal that we accept compressed responses
        if (_channelOptions.AcceptCompression && _channelOptions.Decompressors.Count > 0)
        {
            httpRequest.Headers.Add("Accept-Encoding", string.Join(", ", _channelOptions.Decompressors.Select(d => d.Name)));
        }

        if (options?.Timeout is TimeSpan timeout)
        {
            httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (options?.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

        // Extract response headers and Trailer-* headers early so they are available even on error
        if (options != null)
        {
            ExtractResponseHeaders(httpResponse, options);
            foreach (var header in httpResponse.Headers)
            {
                if (header.Key.StartsWith("Trailer-", StringComparison.OrdinalIgnoreCase))
                {
                    var trailerName = header.Key.Substring("Trailer-".Length);
                    options.ResponseTrailers[trailerName] = string.Join(",", header.Value);
                }
            }
        }

        var maxResponseBytes = _channelOptions.MaxResponseBytes;

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBytes = await ReadBoundedAsync(httpResponse.Content, maxResponseBytes, ct).ConfigureAwait(false);
            var errorContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
            if (errorContentEncoding != null)
            {
                var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                    string.Equals(d.Name, errorContentEncoding, StringComparison.OrdinalIgnoreCase));
                if (decompressor != null)
                {
                    try
                    {
                        errorBytes = decompressor.DecompressToArray(errorBytes, maxResponseBytes);
                    }
                    catch (ConnectException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Body may not actually be compressed; fall back to raw bytes
                    }
                }
            }
            throw ParseErrorResponse((ReadOnlyMemory<byte>)errorBytes, (int)httpResponse.StatusCode);
        }

        // Validate Content-Type
        var contentType = httpResponse.Content.Headers.ContentType?.MediaType;
        if (contentType != null && !contentType.StartsWith($"application/{_codec.Name}", StringComparison.OrdinalIgnoreCase)
            && !contentType.StartsWith($"application/connect+{_codec.Name}", StringComparison.OrdinalIgnoreCase))
        {
            var ctCode = contentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase)
                ? ConnectCode.Internal : ConnectCode.Unknown;
            throw new ConnectException(ctCode, $"unexpected content-type: {contentType}");
        }

        var responseBytes = await ReadBoundedAsync(httpResponse.Content, maxResponseBytes, ct).ConfigureAwait(false);

        // Decompress response if Content-Encoding matches a known decompressor
        var responseContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
        if (responseContentEncoding != null)
        {
            var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                string.Equals(d.Name, responseContentEncoding, StringComparison.OrdinalIgnoreCase));
            if (decompressor != null)
                responseBytes = decompressor.DecompressToArray(responseBytes, maxResponseBytes);
            else if (!string.Equals(responseContentEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                throw new ConnectException(ConnectCode.Internal, $"unknown content-encoding: {responseContentEncoding}");
        }

        if (responseBytes.Length == 0)
            return new TRes();

        var result = _codec.Deserialize<TRes>(responseBytes);

        return result;
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public ClientStreamCall<TReq, TRes> ClientStreamAsync<TReq, TRes>(
        string procedure, CallOptions? options = null, CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        return new ClientStreamCall<TReq, TRes>(_httpClient, _baseUri, procedure, _codec, _channelOptions, options, ct);
    }

    public BidiStreamCall<TReq, TRes> BidiStreamAsync<TReq, TRes>(
        string procedure, CallOptions? options = null, CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        return new BidiStreamCall<TReq, TRes>(_httpClient, _baseUri, procedure, _codec, _channelOptions, options, ct);
    }

    public async IAsyncEnumerable<TRes> ServerStreamAsync<TReq, TRes>(
        string procedure,
        TReq request,
        CallOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        var requestBytes = _codec.SerializeToArray(request);
        var uri = BuildProcedureUri(_baseUri, procedure);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);

        // Wrap request in envelope, compressing if configured
        byte envelopeFlags = 0x00;
        if (_channelOptions.RequestCompressor != null)
        {
            requestBytes = _channelOptions.RequestCompressor.CompressToArray(requestBytes);
            envelopeFlags = Envelope.FlagCompressed;
        }

        using var envelopeStream = new MemoryStream();
        await Envelope.WriteAsync(envelopeStream, envelopeFlags, requestBytes, ct).ConfigureAwait(false);
        var envelopeBytes = envelopeStream.ToArray();

        httpRequest.Content = new ByteArrayContent(envelopeBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/connect+{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        // Set streaming compression headers
        if (_channelOptions.RequestCompressor != null)
        {
            httpRequest.Headers.Add("Connect-Content-Encoding", _channelOptions.RequestCompressor.Name);
        }
        if (_channelOptions.AcceptCompression && _channelOptions.Decompressors.Count > 0)
        {
            httpRequest.Headers.Add("Connect-Accept-Encoding", string.Join(", ", _channelOptions.Decompressors.Select(d => d.Name)));
        }

        if (options?.Timeout is TimeSpan timeout)
        {
            httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (options?.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Extract response headers early so they are available even on error
        if (options != null)
        {
            ExtractResponseHeaders(httpResponse, options);
        }

        var maxResponseBytes = _channelOptions.MaxResponseBytes;

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBytes = await ReadBoundedAsync(httpResponse.Content, maxResponseBytes, ct).ConfigureAwait(false);
            var errorContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
            if (errorContentEncoding != null)
            {
                var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                    string.Equals(d.Name, errorContentEncoding, StringComparison.OrdinalIgnoreCase));
                if (decompressor != null)
                {
                    try
                    {
                        errorBytes = decompressor.DecompressToArray(errorBytes, maxResponseBytes);
                    }
                    catch (ConnectException)
                    {
                        throw;
                    }
                    catch
                    {
                        // Body may not actually be compressed; fall back to raw bytes
                    }
                }
            }
            throw ParseErrorResponse((ReadOnlyMemory<byte>)errorBytes, (int)httpResponse.StatusCode);
        }

        // Response headers already extracted above

        // Validate streaming Content-Type
        var streamContentType = httpResponse.Content.Headers.ContentType?.MediaType;
        var expectedStreamContentType = $"application/connect+{_codec.Name}";
        if (streamContentType != null && !string.Equals(streamContentType, expectedStreamContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectException(ConnectCode.Internal, $"unexpected content-type: {streamContentType}");
        }

        // Check if server is sending compressed envelopes
        httpResponse.Headers.TryGetValues("Connect-Content-Encoding", out var connectContentEncodings);
        var serverCompression = connectContentEncodings?.FirstOrDefault();

        // Validate that the compression encoding is known
        if (serverCompression != null && !string.Equals(serverCompression, "identity", StringComparison.OrdinalIgnoreCase))
        {
            var knownCompression = _channelOptions.Decompressors.Any(d =>
                string.Equals(d.Name, serverCompression, StringComparison.OrdinalIgnoreCase));
            if (!knownCompression)
                throw new ConnectException(ConnectCode.Internal, $"unknown compression: {serverCompression}");
        }

        using var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var responseReader = System.IO.Pipelines.PipeReader.Create(responseStream);

        while (true)
        {
            // Yield via a local function so the outer iterator method can hold the frame's
            // borrowed buffer for the lifetime of one envelope without leaking it past yield.
            var frameNullable = await Envelope.ReadFrameAsync(responseReader, maxResponseBytes, ct).ConfigureAwait(false);
            if (frameNullable == null)
            {
                throw new ConnectException(
                    ConnectCode.Internal,
                    "stream ended without EndStream envelope");
            }

            (TRes? msg, bool endOfStream) result;
            using (var frame = frameNullable.Value)
            {
                result = ProcessFrame(frame);
            }

            if (result.endOfStream) yield break;
            if (result.msg != null) yield return result.msg;
        }

        (TRes? msg, bool endOfStream) ProcessFrame(EnvelopeFrame frame)
        {
            var flags = frame.Flags;
            ReadOnlyMemory<byte> data = frame.Data;

            // Decompress if flag indicates compression
            ArrayPoolBufferWriter? decompressBuf = null;
            try
            {
                if ((flags & Envelope.FlagCompressed) != 0)
                {
                    if (serverCompression == null)
                        throw new ConnectException(ConnectCode.Internal, "received compressed message but no compression was negotiated");
                    var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                        string.Equals(d.Name, serverCompression, StringComparison.OrdinalIgnoreCase));
                    if (decompressor == null)
                        throw new ConnectException(ConnectCode.Internal, $"unknown compression: {serverCompression}");
                    decompressBuf = new ArrayPoolBufferWriter();
                    decompressor.Decompress(data, decompressBuf, maxResponseBytes);
                    data = decompressBuf.WrittenMemory;
                }

                if ((flags & Envelope.FlagEndStream) != 0)
                {
                    var endStreamJson = Encoding.UTF8.GetString(data.Span);
                    using var doc = JsonDocument.Parse(endStreamJson);
                    var root = doc.RootElement;

                    if (options != null && root.TryGetProperty("metadata", out var metadataElement))
                    {
                        ExtractEndStreamTrailers(metadataElement, options);
                    }

                    if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
                    {
                        var connectError = ConnectException.TryFromJsonElement(errorElement);
                        if (connectError != null)
                            throw connectError;
                    }

                    return (default, true);
                }

                var message = data.Length > 0 ? _codec.Deserialize<TRes>(data) : new TRes();
                return (message, false);
            }
            finally
            {
                decompressBuf?.Dispose();
            }
        }
    }

    internal static void ExtractResponseHeaders(HttpResponseMessage httpResponse, CallOptions options)
    {
        foreach (var header in httpResponse.Headers)
        {
            options.ResponseHeaders[header.Key] = string.Join(",", header.Value);
        }
        // Also include content headers
        if (httpResponse.Content?.Headers != null)
        {
            foreach (var header in httpResponse.Content.Headers)
            {
                options.ResponseHeaders[header.Key] = string.Join(",", header.Value);
            }
        }
    }

    internal static void ExtractEndStreamTrailers(JsonElement metadataElement, CallOptions options)
    {
        if (metadataElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in metadataElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    var values = new List<string>();
                    foreach (var v in prop.Value.EnumerateArray())
                    {
                        var s = v.GetString();
                        if (s != null) values.Add(s);
                    }
                    options.ResponseTrailers[prop.Name] = string.Join(",", values);
                }
            }
        }
    }

    internal static ConnectException ParseErrorResponse(string errorBody, int httpStatusCode)
    {
        var fallbackCode = ConnectException.CodeFromHttpStatus(httpStatusCode);
        if (!string.IsNullOrWhiteSpace(errorBody))
        {
            var parsed = ConnectException.TryFromJson(errorBody, fallbackCode);
            if (parsed != null)
                return parsed;
        }

        return new ConnectException(fallbackCode, $"HTTP {httpStatusCode}");
    }

    /// <summary>UTF-8 overload that skips the byte-to-string conversion.</summary>
    internal static ConnectException ParseErrorResponse(ReadOnlyMemory<byte> utf8ErrorBody, int httpStatusCode)
    {
        var fallbackCode = ConnectException.CodeFromHttpStatus(httpStatusCode);
        if (utf8ErrorBody.Length > 0)
        {
            var parsed = ConnectException.TryFromJson(utf8ErrorBody, fallbackCode);
            if (parsed != null)
                return parsed;
        }

        return new ConnectException(fallbackCode, $"HTTP {httpStatusCode}");
    }

    /// <summary>
    /// Reads an HTTP response body up to <paramref name="maxBytes"/>. Refuses oversized bodies
    /// pre-emptively if Content-Length advertises a value larger than the limit, and otherwise
    /// streams while counting bytes so a malicious server cannot force the client to allocate
    /// an arbitrary amount via chunked transfer.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maxBytes, CancellationToken ct)
    {
        using var destination = new ArrayPoolBufferWriter(initialCapacity: 1024);
        await ReadBoundedAsync(content, destination, maxBytes, ct).ConfigureAwait(false);
        return destination.ToArray();
    }

    /// <summary>
    /// Reads an HTTP response body into the caller's <see cref="ArrayPoolBufferWriter"/>,
    /// enforcing the same Content-Length pre-check and streaming byte cap as the byte[]
    /// overload. Avoids the per-call <c>.ToArray()</c> allocation; callers consume the
    /// payload via <see cref="ArrayPoolBufferWriter.WrittenMemory"/>.
    /// </summary>
    internal static async Task ReadBoundedAsync(HttpContent content, ArrayPoolBufferWriter destination, int maxBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long announced && announced > maxBytes)
            throw new ConnectException(
                ConnectCode.ResourceExhausted,
                $"response Content-Length {announced} exceeds limit {maxBytes}");

        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        var scratch = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long total = 0;
            int n;
            while ((n = await stream.ReadAsync(scratch, 0, scratch.Length, ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > maxBytes)
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"response body exceeds limit {maxBytes}");
                var dest = destination.GetSpan(n);
                scratch.AsSpan(0, n).CopyTo(dest);
                destination.Advance(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
