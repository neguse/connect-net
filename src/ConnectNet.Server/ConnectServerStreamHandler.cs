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
using ConnectNet.Pooling;

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
        var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();

        // Validate Content-Type first (415 takes priority per HTTP spec)
        var contentType = request.ContentType;
        if (!ConnectServerProtocol.MatchesContentType(contentType, $"application/connect+{codec.Name}"))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.Unknown, $"unsupported content type: {contentType}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Validate Connect-Protocol-Version (only when required by options)
        if (!ConnectServerProtocol.ProtocolVersionSatisfied(request, serverOptions))
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
                response.Headers["Connect-Accept-Encoding"] = ConnectServerProtocol.SupportedEncodings(compressorRegistry);
                var errContext = new ConnectContext();
                var errEndStream = BuildEndStreamJson(
                    new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {requestCompression}"), errContext);
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream,
                    Encoding.UTF8.GetBytes(errEndStream), httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
                return;
            }
        }

        // Find a response compressor that the client accepts (q=0 entries excluded)
        request.Headers.TryGetValue("Connect-Accept-Encoding", out var acceptEncodingValues);
        var responseCompressor = ConnectServerProtocol.NegotiateCompression(acceptEncodingValues, compressorRegistry);

        // Parse Connect-Timeout-Ms header (clamped to a safe upper bound)
        var ct = httpContext.RequestAborted;
        var timeoutCts = ConnectServerProtocol.StartTimeout(httpContext, serverOptions, ref ct);

        // Resolve the effective receive limit once; used both for envelope read and decompression.
        var receiveLimit = serverOptions?.EffectiveReceiveLimit ?? Envelope.DefaultMaxMessageBytes;

        try
        {
            // Read request envelope
            Google.Protobuf.IMessage requestMessage;
            try
            {
                var frameNullable = await Envelope.ReadFrameAsync(request.BodyReader, receiveLimit, ct);
                if (frameNullable == null)
                {
                    throw new ConnectException(ConnectCode.Unimplemented, "empty request body");
                }

                ArrayPoolBufferWriter? decompressedWriter = null;
                using (var frame = frameNullable.Value)
                {
                    try
                    {
                        var flags = frame.Flags;
                        ReadOnlyMemory<byte> data = frame.Data;

                        if ((flags & Envelope.FlagCompressed) != 0)
                        {
                            if (string.IsNullOrEmpty(requestCompression) || requestCompression == "identity")
                            {
                                throw new ConnectException(ConnectCode.Internal, "received compressed message but compression is identity or not specified");
                            }
                            var decompressor = compressorRegistry?.Get(requestCompression!);
                            if (decompressor == null)
                                throw new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {requestCompression}");
                            decompressedWriter = new ArrayPoolBufferWriter();
                            decompressor.Decompress(data, decompressedWriter, receiveLimit);
                            data = decompressedWriter.WrittenMemory;
                        }

                        if (serverOptions != null && serverOptions.MessageReceiveLimit > 0 && data.Length > serverOptions.MessageReceiveLimit)
                        {
                            throw new ConnectException(ConnectCode.ResourceExhausted, $"message size {data.Length} exceeds limit {serverOptions.MessageReceiveLimit}");
                        }

                        try
                        {
                            requestMessage = codec.Deserialize(data, method.RequestParser);
                        }
                        catch (Google.Protobuf.InvalidProtocolBufferException ex)
                        {
                            // Malformed payloads are the client's fault: InvalidArgument,
                            // delivered as an EndStream error envelope (HTTP 200) below.
                            throw new ConnectException(ConnectCode.InvalidArgument, $"invalid request message: {ex.Message}");
                        }
                        catch (Google.Protobuf.InvalidJsonException ex)
                        {
                            throw new ConnectException(ConnectCode.InvalidArgument, $"invalid request message: {ex.Message}");
                        }
                        catch (FormatException ex)
                        {
                            throw new ConnectException(ConnectCode.InvalidArgument, $"invalid request message: {ex.Message}");
                        }
                    }
                    finally
                    {
                        decompressedWriter?.Dispose();
                    }
                }

                // Check for unexpected additional envelopes (server stream expects exactly one request)
                var extraFrame = await Envelope.ReadFrameAsync(request.BodyReader, receiveLimit, ct);
                if (extraFrame != null)
                {
                    extraFrame.Value.Dispose();
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

                    var msgBytes = codec.SerializeToArray(msg);

                    // Compress response envelope if client accepts
                    if (responseCompressor != null)
                    {
                        msgBytes = responseCompressor.CompressToArray(msgBytes);
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
            catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
            {
                // Client disconnected: classify as Canceled, not DeadlineExceeded.
                streamError = new ConnectException(ConnectCode.Canceled, "client disconnected");
            }
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
            {
                streamError = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            }
            catch (Exception)
            {
                streamError = new ConnectException(ConnectCode.Internal, "internal error");
            }

            // The client is gone — no EndStream envelope can be delivered.
            if (httpContext.RequestAborted.IsCancellationRequested)
            {
                return;
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
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected (Canceled); nothing can be written.
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            // Timeout before streaming started
            if (!response.HasStarted)
            {
                response.StatusCode = 504;
                response.ContentType = "application/json";
                var error = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
                await response.WriteAsync(error.ToJson());
            }
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
