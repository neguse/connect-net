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

public class BidiStreamCall<TReq, TRes> : IDisposable
    where TReq : IMessage<TReq>
    where TRes : IMessage<TRes>, new()
{
    private readonly HttpClient _httpClient;
    private readonly Uri _uri;
    private readonly CallOptions? _options;
    private readonly CancellationToken _ct;
    private readonly MemoryStream _buffer = new();
    private readonly ICodec _codec;

    internal BidiStreamCall(
        HttpClient httpClient,
        Uri baseUri,
        string procedure,
        ICodec codec,
        CallOptions? options,
        CancellationToken ct)
    {
        _httpClient = httpClient;
        _uri = new Uri(baseUri, procedure);
        _codec = codec;
        _options = options;
        _ct = ct;
    }

    public async Task SendAsync(TReq message)
    {
        var data = _codec.Serialize(message);
        await Envelope.WriteAsync(_buffer, 0x00, data, _ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TRes> CompleteAndReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linkedCt = _ct;
        if (ct != default)
        {
            linkedCt = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct).Token;
        }

        _buffer.Position = 0;
        var bodyBytes = _buffer.ToArray();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _uri);
        httpRequest.Content = new ByteArrayContent(bodyBytes);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/connect+{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        if (_options?.Timeout is TimeSpan timeout)
        {
            httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (_options?.Headers != null)
        {
            foreach (var header in _options.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, linkedCt).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ConnectException.FromJson(errorBody);
        }

        using var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

        while (true)
        {
            var envelope = await Envelope.ReadAsync(responseStream, linkedCt).ConfigureAwait(false);
            if (envelope == null)
            {
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

                yield break;
            }

            var message = _codec.Deserialize<TRes>(data);
            yield return message;
        }
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }
}
