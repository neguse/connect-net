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
using ConnectNet.Pooling;

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
        var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();

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

            var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                requestHeaders[header.Key] = header.Value.ToString();
            }
            var context = new ConnectContext(requestHeaders: requestHeaders, cancellationToken: ct);
            ConnectException? streamError = null;

            try
            {
                var msgLimit = serverOptions?.MessageReceiveLimit ?? 0;
                var receiveLimit = serverOptions?.EffectiveReceiveLimit ?? Envelope.DefaultMaxMessageBytes;
                var idleTimeoutMs = serverOptions?.StreamIdleTimeoutMs ?? 0;
                var requestStream = ReadRequestMessages(request.BodyReader, method.RequestParser, codec, requestCompression, compressorRegistry, msgLimit, receiveLimit, idleTimeoutMs, ct);
                var responseStream = handler(service, requestStream, context);

                bool headersFlushed = false;
                await foreach (var msg in responseStream.WithCancellation(ct))
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
            catch (OperationCanceledException) when (timeoutCts?.IsCancellationRequested == true)
            {
                streamError = new ConnectException(ConnectCode.DeadlineExceeded, "deadline exceeded");
            }
            catch (Exception ex)
            {
                // Check if a ConnectException is wrapped inside
                var inner = ex;
                while (inner != null)
                {
                    if (inner is ConnectException connectEx)
                    {
                        streamError = connectEx;
                        break;
                    }
                    inner = inner.InnerException;
                }
                streamError ??= new ConnectException(ConnectCode.Internal, "internal error");
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
        System.IO.Pipelines.PipeReader bodyReader,
        MessageParser parser,
        ICodec codec,
        string? requestCompression,
        ConnectCompressorRegistry? compressorRegistry,
        uint messageReceiveLimit,
        int receiveLimit,
        long idleTimeoutMs,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        while (true)
        {
            // Per-envelope idle deadline (see ConnectClientStreamHandler for rationale).
            using var idleCts = idleTimeoutMs > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (idleCts != null)
                idleCts.CancelAfter(TimeSpan.FromMilliseconds(idleTimeoutMs));
            var readCt = idleCts?.Token ?? ct;

            EnvelopeFrame? frameNullable;
            try
            {
                frameNullable = await Envelope.ReadFrameAsync(bodyReader, receiveLimit, readCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (idleCts != null && idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new ConnectException(
                    ConnectCode.DeadlineExceeded,
                    $"stream idle timeout exceeded ({idleTimeoutMs} ms)");
            }
            if (frameNullable == null)
            {
                yield break;
            }

            (IMessage? msg, bool endOfStream) result;
            using (var frame = frameNullable.Value)
            {
                result = ProcessFrame(frame);
            }
            if (result.endOfStream) yield break;
            if (result.msg != null) yield return result.msg;
        }

        (IMessage? msg, bool endOfStream) ProcessFrame(EnvelopeFrame frame)
        {
            var flags = frame.Flags;
            ReadOnlyMemory<byte> data = frame.Data;

            if ((flags & Envelope.FlagEndStream) != 0)
            {
                return (null, true);
            }

            ArrayPoolBufferWriter? decompressBuf = null;
            try
            {
                if ((flags & Envelope.FlagCompressed) != 0)
                {
                    if (string.IsNullOrEmpty(requestCompression) || requestCompression == "identity")
                    {
                        throw new ConnectException(ConnectCode.Internal, "received compressed message but compression is identity or not specified");
                    }
                    var decompressor = compressorRegistry?.Get(requestCompression!);
                    if (decompressor == null)
                        throw new ConnectException(ConnectCode.Unimplemented, $"unknown compression: {requestCompression}");
                    decompressBuf = new ArrayPoolBufferWriter();
                    decompressor.Decompress(data, decompressBuf, receiveLimit);
                    data = decompressBuf.WrittenMemory;
                }

                if (messageReceiveLimit > 0 && data.Length > messageReceiveLimit)
                {
                    throw new ConnectException(ConnectCode.ResourceExhausted, $"message size {data.Length} exceeds limit {messageReceiveLimit}");
                }

                return (codec.Deserialize(data, parser), false);
            }
            finally
            {
                decompressBuf?.Dispose();
            }
        }
    }
}
