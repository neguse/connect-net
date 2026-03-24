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
        if (_channelOptions.AcceptCompression)
        {
            httpRequest.Headers.Add("Accept-Encoding", "gzip");
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

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ConnectException.FromJson(errorBody);
        }

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

        // Decompress response if Content-Encoding is gzip
        var responseContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
        if (string.Equals(responseContentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
        {
            var decompressor = new GzipCompressor();
            responseBytes = decompressor.Decompress(responseBytes);
        }

        var result = _codec.Deserialize<TRes>(responseBytes);

        // Extract Trailer-* headers
        if (options != null)
        {
            foreach (var header in httpResponse.Headers)
            {
                if (header.Key.StartsWith("Trailer-", StringComparison.OrdinalIgnoreCase))
                {
                    var trailerName = header.Key.Substring("Trailer-".Length);
                    options.ResponseTrailers[trailerName] = string.Join(",", header.Value);
                }
            }
        }

        return result;
    }

    public ClientStreamCall<TReq, TRes> ClientStreamAsync<TReq, TRes>(
        string procedure, CallOptions? options = null, CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        return new ClientStreamCall<TReq, TRes>(_httpClient, _baseUri, procedure, _codec, options, ct);
    }

    public BidiStreamCall<TReq, TRes> BidiStreamAsync<TReq, TRes>(
        string procedure, CallOptions? options = null, CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        return new BidiStreamCall<TReq, TRes>(_httpClient, _baseUri, procedure, _codec, options, ct);
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
        if (_channelOptions.AcceptCompression)
        {
            httpRequest.Headers.Add("Connect-Accept-Encoding", "gzip");
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

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ConnectException.FromJson(errorBody);
        }

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

            if ((flags & Envelope.FlagEndStream) != 0)
            {
                // Parse EndStream JSON
                var endStreamJson = Encoding.UTF8.GetString(data);
                using var doc = JsonDocument.Parse(endStreamJson);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var errorElement))
                {
                    var errorJson = errorElement.GetRawText();
                    throw ConnectException.FromJson(errorJson);
                }

                // Success — done streaming
                yield break;
            }

            // Decompress if flag indicates compression
            if ((flags & Envelope.FlagCompressed) != 0 &&
                string.Equals(serverCompression, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                var decompressor = new GzipCompressor();
                data = decompressor.Decompress(data);
            }

            // Normal message envelope
            var message = _codec.Deserialize<TRes>(data);
            yield return message;
        }
    }
}
