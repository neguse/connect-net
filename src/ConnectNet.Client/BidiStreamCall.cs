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
    private readonly ConnectChannelOptions _channelOptions;

    internal BidiStreamCall(
        HttpClient httpClient,
        Uri baseUri,
        string procedure,
        ICodec codec,
        ConnectChannelOptions channelOptions,
        CallOptions? options,
        CancellationToken ct)
    {
        _httpClient = httpClient;
        _uri = new Uri(baseUri, procedure);
        _codec = codec;
        _channelOptions = channelOptions;
        _options = options;
        _ct = ct;
    }

    public async Task SendAsync(TReq message)
    {
        var data = _codec.Serialize(message);
        byte flags = 0x00;
        if (_channelOptions.RequestCompressor != null)
        {
            data = _channelOptions.RequestCompressor.Compress(data);
            flags = Envelope.FlagCompressed;
        }
        await Envelope.WriteAsync(_buffer, flags, data, _ct).ConfigureAwait(false);
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

        if (_channelOptions.RequestCompressor != null)
        {
            httpRequest.Headers.Add("Connect-Content-Encoding", _channelOptions.RequestCompressor.Name);
        }
        if (_channelOptions.AcceptCompression && _channelOptions.Decompressors.Count > 0)
        {
            httpRequest.Headers.Add("Connect-Accept-Encoding", string.Join(", ", _channelOptions.Decompressors.Select(d => d.Name)));
        }

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

        // Extract response headers early so they are available even on error
        if (_options != null)
        {
            ConnectChannel.ExtractResponseHeaders(httpResponse, _options);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var errorContentEncoding = httpResponse.Content.Headers.ContentEncoding.FirstOrDefault();
            if (errorContentEncoding != null)
            {
                var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                    string.Equals(d.Name, errorContentEncoding, StringComparison.OrdinalIgnoreCase));
                if (decompressor != null)
                    errorBytes = decompressor.Decompress(errorBytes);
            }
            var errorBody = Encoding.UTF8.GetString(errorBytes);
            throw ConnectChannel.ParseErrorResponse(errorBody, (int)httpResponse.StatusCode);
        }

        // Check if server is sending compressed envelopes
        httpResponse.Headers.TryGetValues("Connect-Content-Encoding", out var connectContentEncodings);
        var serverCompression = connectContentEncodings?.FirstOrDefault();

        using var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

        while (true)
        {
            var envelope = await Envelope.ReadAsync(responseStream, linkedCt).ConfigureAwait(false);
            if (envelope == null)
            {
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
                // Parse EndStream JSON
                var endStreamJson = Encoding.UTF8.GetString(data);
                using var doc = JsonDocument.Parse(endStreamJson);
                var root = doc.RootElement;

                // Extract trailers from EndStream metadata
                if (_options != null && root.TryGetProperty("metadata", out var metadataElement))
                {
                    ConnectChannel.ExtractEndStreamTrailers(metadataElement, _options);
                }

                if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
                {
                    var errorJson = errorElement.GetRawText();
                    var connectError = ConnectException.TryFromJson(errorJson);
                    if (connectError != null)
                        throw connectError;
                }

                yield break;
            }

            var message = data.Length > 0 ? _codec.Deserialize<TRes>(data) : new TRes();
            yield return message;
        }
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }
}
