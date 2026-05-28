using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using ConnectNet.Pooling;

namespace ConnectNet.Client;

public class ClientStreamCall<TReq, TRes> : IDisposable
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

    internal ClientStreamCall(
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
        _ct = ct;
    }

    public async Task SendAsync(TReq message)
    {
        var data = _codec.SerializeToArray(message);
        byte flags = 0x00;
        if (_channelOptions.RequestCompressor != null)
        {
            data = _channelOptions.RequestCompressor.CompressToArray(data);
            flags = Envelope.FlagCompressed;
        }
        await Envelope.WriteAsync(_buffer, flags, data, _ct).ConfigureAwait(false);
    }

    public async Task<TRes> CloseAndReceiveAsync()
    {
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

        using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, _ct).ConfigureAwait(false);

        // Extract response headers early so they are available even on error
        if (_options != null)
        {
            ConnectChannel.ExtractResponseHeaders(httpResponse, _options);
        }

        var maxResponseBytes = _channelOptions.MaxResponseBytes;

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBytes = await ConnectChannel.ReadBoundedAsync(httpResponse.Content, maxResponseBytes, _ct).ConfigureAwait(false);
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

        TRes? result = default;

        while (true)
        {
            var frameNullable = await Envelope.ReadFrameAsync(responseReader, maxResponseBytes, _ct).ConfigureAwait(false);
            if (frameNullable == null)
            {
                throw new ConnectException(
                    ConnectCode.Internal,
                    "stream ended without EndStream envelope");
            }

            using var frame = frameNullable.Value;
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

                    break;
                }

                if (result != null)
                    throw new ConnectException(ConnectCode.Unimplemented, "unexpected extra response in client stream");
                result = data.Length > 0 ? _codec.Deserialize<TRes>(data) : new TRes();
            }
            finally
            {
                decompressBuf?.Dispose();
            }
        }

        if (result == null)
        {
            throw new ConnectException(ConnectCode.Unimplemented, "no response message received");
        }

        return result;
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }
}
