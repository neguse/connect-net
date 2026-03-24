using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

internal static class ConnectBidiStreamHandler
{
    public static async Task HandleAsync(
        HttpContext httpContext,
        ConnectMethodDescriptor method,
        object service,
        ICodec codec)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Validate Connect-Protocol-Version
        if (!request.Headers.TryGetValue("Connect-Protocol-Version", out var version) || version != "1")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, "missing or invalid Connect-Protocol-Version header");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Validate Content-Type
        var contentType = request.ContentType;
        if (contentType == null || !contentType.StartsWith($"application/connect+{codec.Name}", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, $"unsupported content type: {contentType}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Resolve compressor registry from DI (optional)
        var compressorRegistry = httpContext.RequestServices.GetService<ConnectCompressorRegistry>();

        // Check if client sends compressed envelopes
        request.Headers.TryGetValue("Connect-Content-Encoding", out var requestContentEncoding);
        var requestCompression = requestContentEncoding.FirstOrDefault();

        // Find a response compressor that the client accepts
        ICompressor? responseCompressor = null;
        if (request.Headers.TryGetValue("Connect-Accept-Encoding", out var acceptEncodingValues) && compressorRegistry != null)
        {
            foreach (var name in compressorRegistry.SupportedNames)
            {
                if (acceptEncodingValues.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    responseCompressor = compressorRegistry.Get(name);
                    break;
                }
            }
        }

        // Parse Connect-Timeout-Ms header
        CancellationTokenSource? timeoutCts = null;
        var ct = httpContext.RequestAborted;

        if (request.Headers.TryGetValue("Connect-Timeout-Ms", out var timeoutStr) &&
            long.TryParse(timeoutStr, out var timeoutMs) && timeoutMs > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            ct = timeoutCts.Token;
        }

        try
        {
            var handler = method.BidiStreamHandler;
            if (handler == null)
            {
                response.StatusCode = 200;
                response.ContentType = $"application/connect+{codec.Name}";
                var context2 = new ConnectContext(cancellationToken: ct);
                var endStreamError = ConnectServerStreamHandler.BuildEndStreamJson(
                    new ConnectException(ConnectCode.Unimplemented, "not implemented"), context2);
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamError), ct);
                await response.Body.FlushAsync(ct);
                return;
            }

            response.StatusCode = 200;
            response.ContentType = $"application/connect+{codec.Name}";

            // Indicate response compression
            if (responseCompressor != null)
            {
                response.Headers["Connect-Content-Encoding"] = responseCompressor.Name;
            }

            var context = new ConnectContext(cancellationToken: ct);
            ConnectException? streamError = null;

            try
            {
                var requestStream = ReadRequestMessages(request.Body, method.RequestParser, codec, requestCompression, compressorRegistry, ct);
                var responseStream = handler(service, requestStream, context);

                await foreach (var msg in responseStream.WithCancellation(ct))
                {
                    var msgBytes = codec.Serialize(msg);
                    if (responseCompressor != null)
                    {
                        msgBytes = responseCompressor.Compress(msgBytes);
                        await Envelope.WriteAsync(response.Body, Envelope.FlagCompressed, msgBytes, ct);
                    }
                    else
                    {
                        await Envelope.WriteAsync(response.Body, 0x00, msgBytes, ct);
                    }
                    await response.Body.FlushAsync(ct);
                }
            }
            catch (ConnectException ex)
            {
                streamError = ex;
            }
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
            {
                streamError = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            }
            catch (Exception)
            {
                streamError = new ConnectException(ConnectCode.Internal, "internal error");
            }

            // Write EndStream envelope
            var endStreamJson = ConnectServerStreamHandler.BuildEndStreamJson(streamError, context);
            await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamJson), httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            response.StatusCode = 504;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            await response.WriteAsync(error.ToJson());
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static async IAsyncEnumerable<IMessage> ReadRequestMessages(
        Stream bodyStream,
        MessageParser parser,
        ICodec codec,
        string? requestCompression,
        ConnectCompressorRegistry? compressorRegistry,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            var envelope = await Envelope.ReadAsync(bodyStream, ct).ConfigureAwait(false);
            if (envelope == null)
            {
                yield break;
            }

            var (flags, data) = envelope.Value;

            if ((flags & Envelope.FlagEndStream) != 0)
            {
                yield break;
            }

            // Decompress if flag indicates compression
            if ((flags & Envelope.FlagCompressed) != 0 && !string.IsNullOrEmpty(requestCompression))
            {
                var decompressor = compressorRegistry?.Get(requestCompression!);
                if (decompressor != null)
                {
                    data = decompressor.Decompress(data);
                }
            }

            var message = codec.Deserialize(data, parser);
            yield return message;
        }
    }
}
