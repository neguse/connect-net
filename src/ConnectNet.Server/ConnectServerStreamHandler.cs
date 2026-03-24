using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace ConnectNet.Server;

internal static class ConnectServerStreamHandler
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
            // Read request envelope
            Google.Protobuf.IMessage requestMessage;
            try
            {
                var envelope = await Envelope.ReadAsync(request.Body, ct);
                if (envelope == null)
                {
                    response.StatusCode = 400;
                    response.ContentType = "application/json";
                    var error = new ConnectException(ConnectCode.InvalidArgument, "empty request body");
                    await response.WriteAsync(error.ToJson());
                    return;
                }

                var (flags, data) = envelope.Value;

                // Decompress request envelope if compressed
                if ((flags & Envelope.FlagCompressed) != 0 &&
                    string.Equals(requestCompression, "gzip", StringComparison.OrdinalIgnoreCase))
                {
                    var gzip = compressor ?? (ICompressor)new GzipCompressor();
                    data = gzip.Decompress(data);
                }

                requestMessage = codec.Deserialize(data, method.RequestParser);
            }
            catch (ConnectException ex)
            {
                response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
                response.ContentType = "application/json";
                await response.WriteAsync(ex.ToJson());
                return;
            }

            // Set response headers before streaming
            response.StatusCode = 200;
            response.ContentType = $"application/connect+{codec.Name}";

            // Indicate response compression
            if (clientAcceptsGzip && compressor != null)
            {
                response.Headers["Connect-Content-Encoding"] = "gzip";
            }

            var context = new ConnectContext(cancellationToken: ct);
            var handler = method.ServerStreamHandler;
            if (handler == null)
            {
                var endStreamError = BuildEndStreamJson(null, context);
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamError), ct);
                await response.Body.FlushAsync(ct);
                return;
            }

            ConnectException? streamError = null;
            try
            {
                await foreach (var msg in handler(service, requestMessage, context).WithCancellation(ct))
                {
                    var msgBytes = codec.Serialize(msg);

                    // Compress response envelope if client accepts
                    if (clientAcceptsGzip && compressor != null)
                    {
                        msgBytes = compressor.Compress(msgBytes);
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
            var endStreamJson = BuildEndStreamJson(streamError, context);
            await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamJson), httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            // Timeout before streaming started
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

    internal static string BuildEndStreamJson(ConnectException? error, ConnectContext context)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();

        if (error != null)
        {
            // Write error as raw JSON
            writer.WritePropertyName("error");
            using var errorDoc = JsonDocument.Parse(error.ToJson());
            errorDoc.RootElement.WriteTo(writer);
        }

        writer.WriteStartObject("metadata");
        foreach (var trailer in context.ResponseTrailers)
        {
            writer.WriteStartArray(trailer.Key);
            writer.WriteStringValue(trailer.Value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
