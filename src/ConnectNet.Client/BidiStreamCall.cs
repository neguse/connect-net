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
using ConnectNet.Pooling;

namespace ConnectNet.Client;

public class BidiStreamCall<TReq, TRes> : IDisposable
    where TReq : IMessage<TReq>
    where TRes : IMessage<TRes>, new()
{
    private readonly HttpClient _httpClient;
    private readonly Uri _uri;
    private readonly CallOptions? _options;
    private readonly ClientCallScope _scope;
    private readonly CancellationToken _ct;
    private readonly ICodec _codec;
    private readonly ConnectChannelOptions _channelOptions;
    private readonly object _startLock = new object();

    private StreamingContent? _streamingContent;
    private Stream? _requestStream;
    private Task? _startTask;
    private Task<HttpResponseMessage>? _responseTask;
    private HttpRequestMessage? _httpRequest;
    private bool _disposed;

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
        _uri = ConnectChannel.BuildProcedureUri(baseUri, procedure);
        _codec = codec;
        _channelOptions = channelOptions;
        _options = options;
        _scope = new ClientCallScope(options?.Timeout, ct);
        _ct = _scope.Token;
    }

    public async Task SendAsync(TReq message)
    {
        try
        {
            await EnsureRequestStartedAsync().ConfigureAwait(false);

            var requestStream = _requestStream;
            if (requestStream == null)
            {
                // The server completed the response before the request body stream became
                // available (it never read the request). The real status is on the response;
                // read it via ReadResponsesAsync.
                throw new ConnectException(
                    ConnectCode.Unavailable,
                    "the server completed the response before the request stream was established");
            }

            var data = _codec.SerializeToArray(message);
            byte flags = 0x00;
            if (_channelOptions.RequestCompressor != null)
            {
                data = _channelOptions.RequestCompressor.CompressToArray(data);
                flags = Envelope.FlagCompressed;
            }
            await Envelope.WriteAsync(requestStream, flags, data, _ct).ConfigureAwait(false);
            await requestStream.FlushAsync(_ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ClientCallScope.ShouldNormalize(ex))
        {
            throw _scope.Normalize(ex);
        }
    }

    /// <summary>
    /// Starts the HTTP request exactly once, no matter how many callers race on the first
    /// SendAsync/ReadResponsesAsync/WaitForResponseAsync.
    /// </summary>
    private Task EnsureRequestStartedAsync()
    {
        lock (_startLock)
        {
            _startTask ??= StartRequestAsync();
            return _startTask;
        }
    }

    private async Task StartRequestAsync()
    {
        _streamingContent = new StreamingContent();
        _streamingContent.Headers.ContentType = new MediaTypeHeaderValue($"application/connect+{_codec.Name}");

        _httpRequest = new HttpRequestMessage(HttpMethod.Post, _uri)
        {
            Version = ConnectChannel.Http2,
        };
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

        var responseTask = _httpClient.SendAsync(_httpRequest, HttpCompletionOption.ResponseHeadersRead, _ct);
        _responseTask = responseTask;

        // Wait for whichever happens first: the transport opens the request body stream, or
        // the response task completes. Waiting only on the stream would hang forever on DNS
        // failure / connection refused, because the transport never serializes the content.
        // Cancellation is covered too: _ct faults the response task.
        var streamTask = _streamingContent.GetStreamAsync();
        var completed = await Task.WhenAny(responseTask, streamTask).ConfigureAwait(false);
        if (ReferenceEquals(completed, streamTask))
        {
            _requestStream = await streamTask.ConfigureAwait(false);
            return;
        }

        if (!responseTask.IsCompletedSuccessfully)
        {
            // Propagate connection failures / cancellation to every caller of
            // EnsureRequestStartedAsync (the start task is cached, so they all observe it).
            await responseTask.ConfigureAwait(false);
        }

        // The server responded before the request stream opened. If the stream materialized
        // in the meantime, use it; otherwise leave it null so senders fail fast while
        // readers can still consume the response.
        if (streamTask.IsCompletedSuccessfully)
        {
            _requestStream = streamTask.Result;
        }
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
        await using var enumerator = ReadResponsesCoreAsync(ct).GetAsyncEnumerator();
        while (true)
        {
            TRes current;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    yield break;
                current = enumerator.Current;
            }
            catch (Exception ex) when (ClientCallScope.ShouldNormalize(ex))
            {
                throw _scope.Normalize(ex);
            }
            yield return current;
        }
    }

    private async IAsyncEnumerable<TRes> ReadResponsesCoreAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Hold the linked CTS in a using-scope so callbacks registered on _ct aren't leaked
        // for the lifetime of _ct (which can be the channel's long-lived token).
        using var linkedCts = ct == default
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
        var linkedCt = linkedCts?.Token ?? _ct;

        // If no messages were sent, start the request now
        await EnsureRequestStartedAsync().ConfigureAwait(false);

        // Wait for response
        using var httpResponse = await _responseTask!.ConfigureAwait(false);

        // Extract response headers early so they are available even on error
        if (_options != null)
        {
            ConnectChannel.ExtractResponseHeaders(httpResponse, _options);
        }

        var maxResponseBytes = _channelOptions.MaxResponseBytes;

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBytes = await ConnectChannel.ReadBoundedAsync(httpResponse.Content, maxResponseBytes, linkedCt).ConfigureAwait(false);
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
            throw ConnectChannel.ParseErrorResponse((ReadOnlyMemory<byte>)errorBytes, (int)httpResponse.StatusCode);
        }

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
            var frameNullable = await Envelope.ReadFrameAsync(responseReader, maxResponseBytes, linkedCt).ConfigureAwait(false);
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
                    var jsonOptions = new JsonDocumentOptions { MaxDepth = ConnectException.MaxJsonDepth };
                    using var doc = JsonDocument.Parse(endStreamJson, jsonOptions);
                    var root = doc.RootElement;

                    if (_options != null && root.TryGetProperty("metadata", out var metadataElement))
                    {
                        ConnectChannel.ExtractEndStreamTrailers(metadataElement, _options);
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

    /// <summary>Wait for the HTTP response to be available (blocks until server sends headers).</summary>
    public async Task WaitForResponseAsync()
    {
        try
        {
            await EnsureRequestStartedAsync().ConfigureAwait(false);
            await _responseTask!.ConfigureAwait(false);
        }
        catch (Exception ex) when (ClientCallScope.ShouldNormalize(ex))
        {
            throw _scope.Normalize(ex);
        }
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
        if (_disposed) return;
        _disposed = true;

        _streamingContent?.Complete(); // ensure we don't leave the request hanging

        // Release the response (and its connection) without blocking Dispose, and observe
        // any fault so an abandoned call cannot surface as UnobservedTaskException.
        var responseTask = _responseTask;
        if (responseTask != null)
        {
            _ = responseTask.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
                else
                {
                    _ = t.Exception; // observe
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        _httpRequest?.Dispose();
        _scope.Dispose();
    }
}
