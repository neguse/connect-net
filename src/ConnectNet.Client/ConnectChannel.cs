using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ConnectChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;

    public ConnectChannel(HttpClient httpClient, string baseUri, ICodec? codec = null)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.TrimEnd('/'));
        _codec = codec ?? new ProtobufCodec();
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
        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

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

        // Wrap request in envelope
        using var envelopeStream = new MemoryStream();
        await Envelope.WriteAsync(envelopeStream, 0x00, requestBytes, ct).ConfigureAwait(false);
        var envelopeBytes = envelopeStream.ToArray();

        httpRequest.Content = new ByteArrayContent(envelopeBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/connect+{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

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

            // Normal message envelope
            var message = _codec.Deserialize<TRes>(data);
            yield return message;
        }
    }
}
