using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

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

        // Resolve compressor from DI (optional)
        var compressor = httpContext.RequestServices.GetService(typeof(ICompressor)) as ICompressor;

        try
        {
            // Read and deserialize request
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var requestBytes = ms.ToArray();

            // Decompress request if Content-Encoding is gzip
            if (request.Headers.TryGetValue("Content-Encoding", out var requestEncoding) &&
                string.Equals(requestEncoding.FirstOrDefault(), "gzip", StringComparison.OrdinalIgnoreCase))
            {
                if (compressor != null)
                {
                    requestBytes = compressor.Decompress(requestBytes);
                }
                else
                {
                    var gzip = new GzipCompressor();
                    requestBytes = gzip.Decompress(requestBytes);
                }
            }

            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Create context
            var context = new ConnectContext(cancellationToken: httpContext.RequestAborted);

            // Invoke service method
            var responseMessage = await method.Handler(service, requestMessage, context);

            // Write response
            response.StatusCode = 200;
            response.ContentType = $"application/{codec.Name}";

            // Write Trailer-* headers
            foreach (var trailer in context.ResponseTrailers)
            {
                response.Headers[$"Trailer-{trailer.Key}"] = trailer.Value;
            }

            var responseBytes = codec.Serialize(responseMessage);

            // Compress response if client accepts gzip
            if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) &&
                acceptEncoding.Any(v => v != null && v.Contains("gzip", StringComparison.OrdinalIgnoreCase)))
            {
                var gzip = compressor ?? (ICompressor)new GzipCompressor();
                responseBytes = gzip.Compress(responseBytes);
                response.Headers["Content-Encoding"] = "gzip";
            }

            await response.Body.WriteAsync(responseBytes);
        }
        catch (ConnectException ex)
        {
            response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
            response.ContentType = "application/json";
            await response.WriteAsync(ex.ToJson());
        }
        catch (Exception)
        {
            response.StatusCode = 500;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.Internal, "internal error");
            await response.WriteAsync(error.ToJson());
        }
    }
}
