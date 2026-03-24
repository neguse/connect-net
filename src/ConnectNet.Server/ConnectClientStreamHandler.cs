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

namespace ConnectNet.Server;

internal static class ConnectClientStreamHandler
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

        // Resolve compressor from DI (optional)
        var compressor = httpContext.RequestServices.GetService(typeof(ICompressor)) as ICompressor;

        // Check if client sends compressed envelopes
        request.Headers.TryGetValue("Connect-Content-Encoding", out var requestContentEncoding);
        var requestCompression = requestContentEncoding.FirstOrDefault();

        // Check if client accepts compressed response envelopes
        request.Headers.TryGetValue("Connect-Accept-Encoding", out var acceptEncodingValues);
        var clientAcceptsGzip = acceptEncodingValues.Any(v => v != null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase));

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
            var handler = method.ClientStreamHandler;
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
            if (clientAcceptsGzip && compressor != null)
            {
                response.Headers["Connect-Content-Encoding"] = "gzip";
            }

            var context = new ConnectContext(cancellationToken: ct);
            ConnectException? streamError = null;

            try
            {
                var requestStream = ReadRequestMessages(request.Body, method.RequestParser, codec, requestCompression, compressor, ct);
                var result = await handler(service, requestStream, context);

                // Write single response envelope
                var responseBytes = codec.Serialize(result);
                if (clientAcceptsGzip && compressor != null)
                {
                    responseBytes = compressor.Compress(responseBytes);
                    await Envelope.WriteAsync(response.Body, Envelope.FlagCompressed, responseBytes, ct);
                }
                else
                {
                    await Envelope.WriteAsync(response.Body, 0x00, responseBytes, ct);
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
        ICompressor? compressor,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            var envelope = await Envelope.ReadAsync(bodyStream, ct).ConfigureAwait(false);
            if (envelope == null)
            {
                // EOF — no more messages
                yield break;
            }

            var (flags, data) = envelope.Value;

            if ((flags & Envelope.FlagEndStream) != 0)
            {
                // EndStream envelope — stop reading
                yield break;
            }

            // Decompress if flag indicates compression
            if ((flags & Envelope.FlagCompressed) != 0 &&
                string.Equals(requestCompression, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                var gzip = compressor ?? (ICompressor)new GzipCompressor();
                data = gzip.Decompress(data);
            }

            var message = codec.Deserialize(data, parser);
            yield return message;
        }
    }
}
