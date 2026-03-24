using System;
using System.IO;
using System.Text;
using System.Text.Json;
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

        // Read request envelope
        Google.Protobuf.IMessage requestMessage;
        try
        {
            var envelope = await Envelope.ReadAsync(request.Body, httpContext.RequestAborted);
            if (envelope == null)
            {
                response.StatusCode = 400;
                response.ContentType = "application/json";
                var error = new ConnectException(ConnectCode.InvalidArgument, "empty request body");
                await response.WriteAsync(error.ToJson());
                return;
            }

            var (flags, data) = envelope.Value;
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

        var context = new ConnectContext(cancellationToken: httpContext.RequestAborted);
        var handler = method.ServerStreamHandler;
        if (handler == null)
        {
            var endStreamError = BuildEndStreamJson(null, context);
            await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamError), httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
            return;
        }

        ConnectException? streamError = null;
        try
        {
            await foreach (var msg in handler(service, requestMessage, context).WithCancellation(httpContext.RequestAborted))
            {
                var msgBytes = codec.Serialize(msg);
                await Envelope.WriteAsync(response.Body, 0x00, msgBytes, httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }
        }
        catch (ConnectException ex)
        {
            streamError = ex;
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
