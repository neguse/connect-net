using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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

        // Validate Content-Type first (415 takes priority per HTTP spec)
        var contentType = request.ContentType;
        if (contentType == null || !contentType.StartsWith($"application/connect+{codec.Name}", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.Unknown, $"unsupported content type: {contentType}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Validate Connect-Protocol-Version
        if (!request.Headers.TryGetValue("Connect-Protocol-Version", out var version) || version != "1")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, "missing or invalid Connect-Protocol-Version header");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Resolve compressor registry from DI (optional)
        var compressorRegistry = httpContext.RequestServices.GetService<ConnectCompressorRegistry>();

        // Check if client sends compressed envelopes
        request.Headers.TryGetValue("Connect-Content-Encoding", out var requestContentEncoding);
        var requestCompression = requestContentEncoding.FirstOrDefault();

        // Validate that the requested compression is supported
        if (!string.IsNullOrEmpty(requestCompression) && requestCompression != "identity")
        {
            if (compressorRegistry?.Get(requestCompression!) == null)
            {
                response.StatusCode = 200;
                response.ContentType = $"application/connect+{codec.Name}";
                var errContext = new ConnectContext();
                var errEndStream = BuildEndStreamJson(
                    new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {requestCompression}"), errContext);
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream,
                    Encoding.UTF8.GetBytes(errEndStream), httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
                return;
            }
        }

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
            // Read request envelope
            Google.Protobuf.IMessage requestMessage;
            try
            {
                var envelope = await Envelope.ReadAsync(request.Body, ct);
                if (envelope == null)
                {
                    throw new ConnectException(ConnectCode.Unimplemented, "empty request body");
                }

                var (flags, data) = envelope.Value;

                // Decompress request envelope if compressed
                if ((flags & Envelope.FlagCompressed) != 0)
                {
                    if (string.IsNullOrEmpty(requestCompression) || requestCompression == "identity")
                    {
                        throw new ConnectException(ConnectCode.Internal, "received compressed message but compression is identity or not specified");
                    }
                    var decompressor = compressorRegistry?.Get(requestCompression!);
                    if (decompressor != null)
                    {
                        data = decompressor.Decompress(data);
                    }
                    else
                    {
                        throw new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {requestCompression}");
                    }
                }

                // Enforce message size limit
                var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();
                if (serverOptions != null && serverOptions.MessageReceiveLimit > 0 && data.Length > serverOptions.MessageReceiveLimit)
                {
                    throw new ConnectException(ConnectCode.ResourceExhausted, $"message size {data.Length} exceeds limit {serverOptions.MessageReceiveLimit}");
                }

                requestMessage = codec.Deserialize(data, method.RequestParser);

                // Check for unexpected additional envelopes (server stream expects exactly one request)
                var extraEnvelope = await Envelope.ReadAsync(request.Body, ct);
                if (extraEnvelope != null)
                {
                    throw new ConnectException(ConnectCode.Unimplemented, "server stream received multiple request messages");
                }
            }
            catch (ConnectException ex)
            {
                // For streaming, return error as EndStream envelope with HTTP 200
                response.StatusCode = 200;
                response.ContentType = $"application/connect+{codec.Name}";
                var errContext = new ConnectContext(cancellationToken: ct);
                var errEndStream = BuildEndStreamJson(ex, errContext);
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(errEndStream), httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
                return;
            }

            // Set response headers before streaming
            response.StatusCode = 200;
            response.ContentType = $"application/connect+{codec.Name}";

            // Indicate response compression
            if (responseCompressor != null)
            {
                response.Headers["Connect-Content-Encoding"] = responseCompressor.Name;
            }

            var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                requestHeaders[header.Key] = header.Value.ToString();
            }
            var context = new ConnectContext(requestHeaders: requestHeaders, cancellationToken: ct);
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
                bool headersFlushed = false;
                await foreach (var msg in handler(service, requestMessage, context).WithCancellation(ct))
                {
                    // Write response headers from context before the first envelope
                    if (!headersFlushed)
                    {
                        foreach (var header in context.ResponseHeaders)
                        {
                            response.Headers[header.Key] = header.Value;
                        }
                        headersFlushed = true;
                    }

                    var msgBytes = codec.Serialize(msg);

                    // Compress response envelope if client accepts
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
                // If no messages were yielded, still write headers
                if (!headersFlushed)
                {
                    foreach (var header in context.ResponseHeaders)
                    {
                        response.Headers[header.Key] = header.Value;
                    }
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

            // Write response headers on error path if not already sent
            if (!response.HasStarted)
            {
                foreach (var header in context.ResponseHeaders)
                {
                    response.Headers[header.Key] = header.Value;
                }
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
