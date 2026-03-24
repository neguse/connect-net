using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

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
        if (contentType == null || !contentType.StartsWith($"application/{codec.Name}", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, $"unsupported content type: {contentType}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Resolve compressor registry from DI (optional)
        var compressorRegistry = httpContext.RequestServices.GetService<ConnectCompressorRegistry>();

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

        // Create context with request headers
        var requestHeaders = new Dictionary<string, string>();
        foreach (var header in request.Headers)
        {
            requestHeaders[header.Key] = header.Value.ToString();
        }
        var context = new ConnectContext(requestHeaders: requestHeaders, cancellationToken: ct);

        try
        {
            // Read and deserialize request
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms, ct);
            var requestBytes = ms.ToArray();

            // Decompress request if Content-Encoding is set
            if (request.Headers.TryGetValue("Content-Encoding", out var requestEncoding))
            {
                var encodingName = requestEncoding.FirstOrDefault();
                if (!string.IsNullOrEmpty(encodingName))
                {
                    var decompressor = compressorRegistry?.Get(encodingName!);
                    if (decompressor != null)
                    {
                        requestBytes = decompressor.Decompress(requestBytes);
                    }
                }
            }

            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Invoke service method (with interceptor chain if configured)
            var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();
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

            var responseBytes = codec.Serialize(responseMessage);

            // Compress response if client accepts a supported encoding
            if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) && compressorRegistry != null)
            {
                foreach (var name in compressorRegistry.SupportedNames)
                {
                    if (acceptEncoding.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        responseBytes = compressorRegistry.Get(name)!.Compress(responseBytes);
                        response.Headers["Content-Encoding"] = name;
                        break;
                    }
                }
            }

            await response.Body.WriteAsync(responseBytes, ct);
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

        if (request.Headers.TryGetValue("Connect-Timeout-Ms", out var timeoutStr) &&
            long.TryParse(timeoutStr, out var timeoutMs) && timeoutMs > 0)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(httpContext.RequestAborted);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            ct = timeoutCts.Token;
        }

        // Create context with request headers
        var getRequestHeaders = new Dictionary<string, string>();
        foreach (var header in request.Headers)
        {
            getRequestHeaders[header.Key] = header.Value.ToString();
        }
        var getContext = new ConnectContext(requestHeaders: getRequestHeaders, cancellationToken: ct);

        try
        {
            // Decode message
            byte[] requestBytes;
            if (request.Query.TryGetValue("base64", out var base64Flag) && base64Flag == "1")
            {
                requestBytes = Base64UrlDecode(messageParam!);
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
                    requestBytes = decompressor.Decompress(requestBytes);
                }
            }

            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Invoke service method (with interceptor chain if configured)
            var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();
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

            var responseBytes = codec.Serialize(responseMessage);

            // Compress response if client accepts a supported encoding
            if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) && compressorRegistry != null)
            {
                foreach (var name in compressorRegistry.SupportedNames)
                {
                    if (acceptEncoding.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        responseBytes = compressorRegistry.Get(name)!.Compress(responseBytes);
                        response.Headers["Content-Encoding"] = name;
                        break;
                    }
                }
            }

            await response.Body.WriteAsync(responseBytes, ct);
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
        catch (Exception ex)
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
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
