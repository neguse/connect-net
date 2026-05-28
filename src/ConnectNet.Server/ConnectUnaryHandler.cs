using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ConnectNet.Pooling;

namespace ConnectNet.Server;

internal static class ConnectUnaryHandler
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
        if (contentType == null || !contentType.StartsWith($"application/{codec.Name}", StringComparison.OrdinalIgnoreCase))
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

        // Parse Connect-Timeout-Ms header
        CancellationTokenSource? timeoutCts = null;
        var ct = httpContext.RequestAborted;
        var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();
        var receiveLimit = serverOptions?.EffectiveReceiveLimit ?? Envelope.DefaultMaxMessageBytes;

        if (request.Headers.TryGetValue("Connect-Timeout-Ms", out var timeoutStr) &&
            long.TryParse(timeoutStr, out var timeoutMs) && timeoutMs > 0)
        {
            // Cap the client-provided timeout to prevent timer-queue exhaustion via huge values.
            var maxTimeout = serverOptions?.MaxTimeoutMs ?? 0;
            if (maxTimeout > 0 && timeoutMs > maxTimeout) timeoutMs = maxTimeout;
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            ct = timeoutCts.Token;
        }

        // Create context with request headers
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            requestHeaders[header.Key] = header.Value.ToString();
        }
        var context = new ConnectContext(requestHeaders: requestHeaders, cancellationToken: ct);

        ArrayPoolBufferWriter? rawWriter = null;
        ArrayPoolBufferWriter? decompressedWriter = null;
        try
        {
            // Read request body straight from Kestrel's PipeReader into a pooled buffer writer.
            // This skips the Stream wrapper and avoids the per-RPC 80 KB scratch allocation
            // that the prior MemoryStream-based path required.
            rawWriter = new ArrayPoolBufferWriter();
            await ReadBodyToWriterAsync(request.BodyReader, rawWriter, receiveLimit, ct);
            ReadOnlyMemory<byte> requestBytes = rawWriter.WrittenMemory;

            if (request.Headers.TryGetValue("Content-Encoding", out var requestEncoding))
            {
                var encodingName = requestEncoding.FirstOrDefault();
                if (!string.IsNullOrEmpty(encodingName) && encodingName != "identity")
                {
                    var decompressor = compressorRegistry?.Get(encodingName!);
                    if (decompressor == null)
                    {
                        throw new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {encodingName}");
                    }
                    decompressedWriter = new ArrayPoolBufferWriter();
                    decompressor.Decompress(requestBytes, decompressedWriter, receiveLimit);
                    requestBytes = decompressedWriter.WrittenMemory;
                }
            }

            if (serverOptions != null && serverOptions.MessageReceiveLimit > 0 && requestBytes.Length > serverOptions.MessageReceiveLimit)
            {
                throw new ConnectException(ConnectCode.ResourceExhausted, $"message size {requestBytes.Length} exceeds limit {serverOptions.MessageReceiveLimit}");
            }

            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Invoke service method (with interceptor chain if configured)
            IMessage responseMessage;

            if (serverOptions != null && serverOptions.Interceptors.Count > 0)
            {
                var serverContext = new UnaryServerContext(method.Procedure, requestMessage, context);

                Func<UnaryServerContext, Task<IMessage>> chain = async (ctx) =>
                    await method.Handler(service, ctx.Request, ctx.Context);

                for (int i = serverOptions.Interceptors.Count - 1; i >= 0; i--)
                {
                    var interceptor = serverOptions.Interceptors[i];
                    var next = chain;
                    chain = (ctx) => interceptor.InterceptUnaryAsync(ctx, next);
                }

                responseMessage = await chain(serverContext);
            }
            else
            {
                responseMessage = await method.Handler(service, requestMessage, context);
            }

            // Write response
            response.StatusCode = 200;
            response.ContentType = $"application/{codec.Name}";

            // Write response headers
            foreach (var header in context.ResponseHeaders)
            {
                response.Headers[header.Key] = header.Value;
            }

            // Write Trailer-* headers (Connect unary trailers)
            foreach (var trailer in context.ResponseTrailers)
            {
                response.Headers[$"Trailer-{trailer.Key}"] = trailer.Value;
            }

            // Serialize directly into a pooled buffer writer; the response is then either
            // written as-is or piped through a compressor that also writes into the pool.
            using var msgWriter = new ArrayPoolBufferWriter();
            codec.Serialize(responseMessage, msgWriter);

            ICompressor? selectedCompressor = null;
            if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) && compressorRegistry != null)
            {
                foreach (var name in compressorRegistry.SupportedNames)
                {
                    if (acceptEncoding.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedCompressor = compressorRegistry.Get(name);
                        response.Headers["Content-Encoding"] = name;
                        break;
                    }
                }
            }

            if (selectedCompressor != null)
            {
                using var compressedWriter = new ArrayPoolBufferWriter();
                selectedCompressor.Compress(msgWriter.WrittenMemory, compressedWriter);
                await response.Body.WriteAsync(compressedWriter.WrittenMemory, ct);
            }
            else
            {
                await response.Body.WriteAsync(msgWriter.WrittenMemory, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            WriteContextHeaders(response, context);
            response.StatusCode = 504;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            await response.WriteAsync(error.ToJson());
        }
        catch (ConnectException ex)
        {
            WriteContextHeaders(response, context);
            response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
            response.ContentType = "application/json";
            await response.WriteAsync(ex.ToJson());
        }
        catch (Exception)
        {
            WriteContextHeaders(response, context);
            if (!response.HasStarted)
            {
                response.StatusCode = 500;
                response.ContentType = "application/json";
            }
            var error = new ConnectException(ConnectCode.Internal, "internal error");
            await response.WriteAsync(error.ToJson());
        }
        finally
        {
            timeoutCts?.Dispose();
            rawWriter?.Dispose();
            decompressedWriter?.Dispose();
        }
    }

    public static async Task HandleGetAsync(
        HttpContext httpContext,
        ConnectMethodDescriptor method,
        object service,
        ICodec codec)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Validate connect=v1 query parameter
        if (!request.Query.TryGetValue("connect", out var connectVersion) || connectVersion != "v1")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, "missing or invalid connect query parameter");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Validate encoding query parameter
        if (!request.Query.TryGetValue("encoding", out var encoding) || encoding != codec.Name)
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, $"unsupported encoding: {encoding}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Parse message query parameter
        if (!request.Query.TryGetValue("message", out var messageParam) || string.IsNullOrEmpty(messageParam))
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, "missing message query parameter");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Resolve compressor registry from DI (optional)
        var compressorRegistry = httpContext.RequestServices.GetService<ConnectCompressorRegistry>();

        // Parse Connect-Timeout-Ms header
        CancellationTokenSource? timeoutCts = null;
        var ct = httpContext.RequestAborted;
        var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();
        var receiveLimit = serverOptions?.EffectiveReceiveLimit ?? Envelope.DefaultMaxMessageBytes;

        if (request.Headers.TryGetValue("Connect-Timeout-Ms", out var timeoutStr) &&
            long.TryParse(timeoutStr, out var timeoutMs) && timeoutMs > 0)
        {
            // Cap the client-provided timeout to prevent timer-queue exhaustion via huge values.
            var maxTimeout = serverOptions?.MaxTimeoutMs ?? 0;
            if (maxTimeout > 0 && timeoutMs > maxTimeout) timeoutMs = maxTimeout;
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            ct = timeoutCts.Token;
        }

        // Create context with request headers
        var getRequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            getRequestHeaders[header.Key] = header.Value.ToString();
        }
        var getContext = new ConnectContext(requestHeaders: getRequestHeaders, cancellationToken: ct);

        try
        {
            // Reject excessively large GET message params before decoding.
            if (messageParam.ToString().Length > receiveLimit)
            {
                throw new ConnectException(ConnectCode.ResourceExhausted,
                    $"message param exceeds limit {receiveLimit}");
            }

            // Decode message
            byte[] requestBytes;
            if (request.Query.TryGetValue("base64", out var base64Flag) && base64Flag == "1")
            {
                try
                {
                    requestBytes = Base64UrlDecode(messageParam!);
                }
                catch (FormatException)
                {
                    throw new ConnectException(ConnectCode.InvalidArgument, "invalid base64 message");
                }
            }
            else
            {
                requestBytes = System.Text.Encoding.UTF8.GetBytes(messageParam!);
            }

            // Decompress if compression query parameter is set
            if (request.Query.TryGetValue("compression", out var compressionName) &&
                !string.IsNullOrEmpty(compressionName))
            {
                var decompressor = compressorRegistry?.Get(compressionName!);
                if (decompressor != null)
                {
                    requestBytes = decompressor.DecompressToArray(requestBytes, receiveLimit);
                }
            }

            // Enforce message size limit
            if (serverOptions != null && serverOptions.MessageReceiveLimit > 0 && requestBytes.Length > serverOptions.MessageReceiveLimit)
            {
                throw new ConnectException(ConnectCode.ResourceExhausted, $"message size {requestBytes.Length} exceeds limit {serverOptions.MessageReceiveLimit}");
            }

            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Invoke service method (with interceptor chain if configured)
            IMessage responseMessage;

            if (serverOptions != null && serverOptions.Interceptors.Count > 0)
            {
                var serverContext = new UnaryServerContext(method.Procedure, requestMessage, getContext);

                Func<UnaryServerContext, Task<IMessage>> chain = async (ctx) =>
                    await method.Handler(service, ctx.Request, ctx.Context);

                for (int i = serverOptions.Interceptors.Count - 1; i >= 0; i--)
                {
                    var interceptor = serverOptions.Interceptors[i];
                    var next = chain;
                    chain = (ctx) => interceptor.InterceptUnaryAsync(ctx, next);
                }

                responseMessage = await chain(serverContext);
            }
            else
            {
                responseMessage = await method.Handler(service, requestMessage, getContext);
            }

            // Write response
            response.StatusCode = 200;
            response.ContentType = $"application/{codec.Name}";

            // Write response headers
            foreach (var header in getContext.ResponseHeaders)
            {
                response.Headers[header.Key] = header.Value;
            }

            // Write Trailer-* headers (Connect unary trailers)
            foreach (var trailer in getContext.ResponseTrailers)
            {
                response.Headers[$"Trailer-{trailer.Key}"] = trailer.Value;
            }

            // Serialize directly into a pooled buffer writer; the response is then either
            // written as-is or piped through a compressor that also writes into the pool.
            using var msgWriter = new ArrayPoolBufferWriter();
            codec.Serialize(responseMessage, msgWriter);

            ICompressor? selectedCompressor = null;
            if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) && compressorRegistry != null)
            {
                foreach (var name in compressorRegistry.SupportedNames)
                {
                    if (acceptEncoding.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        selectedCompressor = compressorRegistry.Get(name);
                        response.Headers["Content-Encoding"] = name;
                        break;
                    }
                }
            }

            if (selectedCompressor != null)
            {
                using var compressedWriter = new ArrayPoolBufferWriter();
                selectedCompressor.Compress(msgWriter.WrittenMemory, compressedWriter);
                await response.Body.WriteAsync(compressedWriter.WrittenMemory, ct);
            }
            else
            {
                await response.Body.WriteAsync(msgWriter.WrittenMemory, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
        {
            WriteContextHeaders(response, getContext);
            response.StatusCode = 504;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            await response.WriteAsync(error.ToJson());
        }
        catch (ConnectException ex)
        {
            WriteContextHeaders(response, getContext);
            response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
            response.ContentType = "application/json";
            await response.WriteAsync(ex.ToJson());
        }
        catch (Exception)
        {
            WriteContextHeaders(response, getContext);
            response.StatusCode = 500;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.Internal, "internal error");
            await response.WriteAsync(error.ToJson());
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    private static void WriteContextHeaders(Microsoft.AspNetCore.Http.HttpResponse response, ConnectContext context)
    {
        if (!response.HasStarted)
        {
            foreach (var header in context.ResponseHeaders)
            {
                response.Headers[header.Key] = header.Value;
            }
            foreach (var trailer in context.ResponseTrailers)
            {
                response.Headers[$"Trailer-{trailer.Key}"] = trailer.Value;
            }
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        // Reverse base64url encoding: replace -→+, _→/, add padding
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 1:
                // Length % 4 == 1 is invalid base64; reject explicitly so the caller can map
                // it to InvalidArgument instead of a generic 500.
                throw new FormatException("invalid base64 length");
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// Drains a PipeReader (typically <c>HttpRequest.BodyReader</c>) into the caller's buffer
    /// writer. Refuses oversize bodies before they reach memory; the scratch path lives in
    /// the pipeline's own pooled buffers, so this method does not allocate per call.
    /// </summary>
    private static async Task ReadBodyToWriterAsync(System.IO.Pipelines.PipeReader reader, ArrayPoolBufferWriter destination, int maxBytes, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            var result = await reader.ReadAsync(ct).ConfigureAwait(false);
            var buffer = result.Buffer;
            if (buffer.Length > 0)
            {
                total += buffer.Length;
                if (total > maxBytes)
                {
                    reader.AdvanceTo(buffer.End);
                    throw new ConnectException(
                        ConnectCode.ResourceExhausted,
                        $"request body exceeds limit {maxBytes}");
                }
                foreach (var segment in buffer)
                {
                    var dest = destination.GetSpan(segment.Length);
                    segment.Span.CopyTo(dest);
                    destination.Advance(segment.Length);
                }
            }
            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted) break;
        }
    }
}
