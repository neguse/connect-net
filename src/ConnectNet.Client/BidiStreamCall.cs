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
    private readonly ICodec _codec;
    private readonly ConnectChannelOptions _channelOptions;

    private StreamingContent? _streamingContent;
    private Stream? _requestStream;
    private Task<HttpResponseMessage>? _responseTask;
    private HttpRequestMessage? _httpRequest;

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
        await EnsureRequestStartedAsync().ConfigureAwait(false);

        var data = _codec.Serialize(message);
        byte flags = 0x00;
        if (_channelOptions.RequestCompressor != null)
        {
            data = _channelOptions.RequestCompressor.Compress(data);
            flags = Envelope.FlagCompressed;
        }
        await Envelope.WriteAsync(_requestStream!, flags, data, _ct).ConfigureAwait(false);
        await _requestStream!.FlushAsync(_ct).ConfigureAwait(false);
    }

    private async Task EnsureRequestStartedAsync()
    {
        if (_responseTask != null) return;

        _streamingContent = new StreamingContent();
        _streamingContent.Headers.ContentType = new MediaTypeHeaderValue($"application/connect+{_codec.Name}");

        _httpRequest = new HttpRequestMessage(HttpMethod.Post, _uri);
        _httpRequest.Content = _streamingContent;
        _httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        if (_channelOptions.RequestCompressor != null)
        {
            _httpRequest.Headers.Add("Connect-Content-Encoding", _channelOptions.RequestCompressor.Name);
        }
        if (_channelOptions.AcceptCompression && _channelOptions.Decompressors.Count > 0)
        {
            _httpRequest.Headers.Add("Connect-Accept-Encoding", string.Join(", ", _channelOptions.Decompressors.Select(d => d.Name)));
        }

        if (_options?.Timeout is TimeSpan timeout)
        {
            _httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (_options?.Headers != null)
        {
            foreach (var header in _options.Headers)
            {
                _httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        _responseTask = _httpClient.SendAsync(_httpRequest, HttpCompletionOption.ResponseHeadersRead, _ct);
        _requestStream = await _streamingContent.GetStreamAsync().ConfigureAwait(false);
    }

    /// <summary>Close the send side without reading responses.</summary>
    public void CloseSend()
    {
        _streamingContent?.Complete();
    }

    /// <summary>Read responses from the server. Does NOT close the send side.</summary>
    public async IAsyncEnumerable<TRes> ReadResponsesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var linkedCt = _ct;
        if (ct != default)
        {
            linkedCt = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct).Token;
        }

        // If no messages were sent, start the request now
        await EnsureRequestStartedAsync().ConfigureAwait(false);

        // Wait for response
        var httpResponse = await _responseTask!.ConfigureAwait(false);

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
                {
                    try
                    {
                        errorBytes = decompressor.Decompress(errorBytes);
                    }
                    catch
                    {
                        // Body may not actually be compressed; fall back to raw bytes
                    }
                }
            }
            var errorBody = Encoding.UTF8.GetString(errorBytes);
            throw ConnectChannel.ParseErrorResponse(errorBody, (int)httpResponse.StatusCode);
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

        while (true)
        {
            var envelope = await Envelope.ReadAsync(responseStream, linkedCt).ConfigureAwait(false);
            if (envelope == null)
            {
                break;
            }

            var (flags, data) = envelope.Value;

            // Decompress if flag indicates compression
            if ((flags & Envelope.FlagCompressed) != 0)
            {
                if (serverCompression == null)
                    throw new ConnectException(ConnectCode.Internal, "received compressed message but no compression was negotiated");
                var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
                    string.Equals(d.Name, serverCompression, StringComparison.OrdinalIgnoreCase));
                if (decompressor != null)
                    data = decompressor.Decompress(data);
                else
                    throw new ConnectException(ConnectCode.Internal, $"unknown compression: {serverCompression}");
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

    /// <summary>Wait for the HTTP response to be available (blocks until server sends headers).</summary>
    public async Task WaitForResponseAsync()
    {
        await EnsureRequestStartedAsync().ConfigureAwait(false);
        await _responseTask!.ConfigureAwait(false);
    }

    /// <summary>Close send side and read all responses. Convenience for CloseSend + ReadResponsesAsync.</summary>
    public async IAsyncEnumerable<TRes> CompleteAndReadAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        CloseSend();
        await foreach (var msg in ReadResponsesAsync(ct).ConfigureAwait(false))
            yield return msg;
    }

    public void Dispose()
    {
        _streamingContent?.Complete(); // ensure we don't leave the request hanging
        _httpRequest?.Dispose();
    }
}
