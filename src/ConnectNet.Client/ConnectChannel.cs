using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ConnectChannelOptions
{
    public ICompressor? RequestCompressor { get; set; }
    public bool AcceptCompression { get; set; } = true;
    public List<ICompressor> Decompressors { get; set; } = new() { new GzipCompressor(), new DeflateCompressor() };
    public List<IClientInterceptor> Interceptors { get; set; } = new();
}

public class ConnectChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;
    private readonly ConnectChannelOptions _channelOptions;

    public ConnectChannel(HttpClient httpClient, string baseUri, ICodec? codec = null, ConnectChannelOptions? channelOptions = null)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.TrimEnd('/'));
        _codec = codec ?? new ProtobufCodec();
        _channelOptions = channelOptions ?? new ConnectChannelOptions();
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

        var body = _codec.Serialize(request);
        var uri = new Uri(_baseUri, procedure);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);

        // Compress request if compressor is configured
        if (_channelOptions.RequestCompressor != null)
        {
            body = _channelOptions.RequestCompressor.Compress(body);
            httpRequest.Content = new ByteArrayContent(body);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/{_codec.Name}");
            httpRequest.Content.Headers.Add("Content-Encoding", _channelOptions.RequestCompressor.Name);
        }
        else
        {
            httpRequest.Content = new ByteArrayContent(body);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/{_codec.Name}");
        }

        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

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

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

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

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ParseErrorResponse(errorBody, (int)httpResponse.StatusCode);
        }

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        // Decompress response if Content-Encoding matches a known decompressor
        var responseContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
        if (responseContentEncoding != null)
        {
            var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                string.Equals(d.Name, responseContentEncoding, StringComparison.OrdinalIgnoreCase));
            if (decompressor != null)
                responseBytes = decompressor.Decompress(responseBytes);
        }

        var result = _codec.Deserialize<TRes>(responseBytes);

        return result;
    }

    private async Task<TRes> SendUnaryGetAsync<TRes>(
        string procedure,
        IMessage request,
        CallOptions? options,
        CancellationToken ct)
        where TRes : IMessage<TRes>, new()
    {
        var body = _codec.Serialize(request);
        var messageEncoded = Base64UrlEncode(body);

        var uriBuilder = new UriBuilder(new Uri(_baseUri, procedure));
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

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

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

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ParseErrorResponse(errorBody, (int)httpResponse.StatusCode);
        }

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        // Decompress response if Content-Encoding matches a known decompressor
        var responseContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
        if (responseContentEncoding != null)
        {
            var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                string.Equals(d.Name, responseContentEncoding, StringComparison.OrdinalIgnoreCase));
            if (decompressor != null)
                responseBytes = decompressor.Decompress(responseBytes);
        }

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
        var requestBytes = _codec.Serialize(request);
        var uri = new Uri(_baseUri, procedure);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);

        // Wrap request in envelope, compressing if configured
        byte envelopeFlags = 0x00;
        if (_channelOptions.RequestCompressor != null)
        {
            requestBytes = _channelOptions.RequestCompressor.Compress(requestBytes);
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

        var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // Extract response headers early so they are available even on error
        if (options != null)
        {
            ExtractResponseHeaders(httpResponse, options);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ParseErrorResponse(errorBody, (int)httpResponse.StatusCode);
        }

        // Response headers already extracted above

        // Check if server is sending compressed envelopes
        httpResponse.Headers.TryGetValues("Connect-Content-Encoding", out var connectContentEncodings);
        var serverCompression = connectContentEncodings?.FirstOrDefault();

        using var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

        while (true)
        {
            var envelope = await Envelope.ReadAsync(responseStream, ct).ConfigureAwait(false);
            if (envelope == null)
            {
                // Stream ended without EndStream — treat as unexpected end
                break;
            }

            var (flags, data) = envelope.Value;

            // Decompress if flag indicates compression
            if ((flags & Envelope.FlagCompressed) != 0 && serverCompression != null)
            {
                var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                    string.Equals(d.Name, serverCompression, StringComparison.OrdinalIgnoreCase));
                if (decompressor != null)
                    data = decompressor.Decompress(data);
            }

            if ((flags & Envelope.FlagEndStream) != 0)
            {
                // Parse EndStream JSON and extract trailers
                var endStreamJson = Encoding.UTF8.GetString(data);
                using var doc = JsonDocument.Parse(endStreamJson);
                var root = doc.RootElement;

                // Extract trailers from EndStream metadata
                if (options != null && root.TryGetProperty("metadata", out var metadataElement))
                {
                    ExtractEndStreamTrailers(metadataElement, options);
                }

                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
                {
                    var errorJson = errorElement.GetRawText();
                    var connectError = ConnectException.TryFromJson(errorJson);
                    if (connectError != null)
                        throw connectError;
                }

                // Success — done streaming
                yield break;
            }

            // Normal message envelope
            var message = _codec.Deserialize<TRes>(data);
            yield return message;
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
        if (!string.IsNullOrWhiteSpace(errorBody))
        {
            try
            {
                return ConnectException.FromJson(errorBody);
            }
            catch
            {
                // Not valid Connect error JSON, fall through to HTTP status mapping
            }
        }

        // Map HTTP status code to Connect error code
        var code = ConnectException.CodeFromHttpStatus(httpStatusCode);
        return new ConnectException(code, $"HTTP {httpStatusCode}");
    }
}
