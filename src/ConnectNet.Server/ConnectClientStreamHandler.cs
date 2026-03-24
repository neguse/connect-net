using System;
using System.Collections.Generic;
using System.IO;
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

        var handler = method.ClientStreamHandler;
        if (handler == null)
        {
            response.StatusCode = 200;
            response.ContentType = $"application/connect+{codec.Name}";
            var context2 = new ConnectContext(cancellationToken: httpContext.RequestAborted);
            var endStreamError = ConnectServerStreamHandler.BuildEndStreamJson(
                new ConnectException(ConnectCode.Unimplemented, "not implemented"), context2);
            await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamError), httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
            return;
        }

        response.StatusCode = 200;
        response.ContentType = $"application/connect+{codec.Name}";

        var context = new ConnectContext(cancellationToken: httpContext.RequestAborted);
        ConnectException? streamError = null;

        try
        {
            var requestStream = ReadRequestMessages(request.Body, method.RequestParser, codec, httpContext.RequestAborted);
            var result = await handler(service, requestStream, context);

            // Write single response envelope
            var responseBytes = codec.Serialize(result);
            await Envelope.WriteAsync(response.Body, 0x00, responseBytes, httpContext.RequestAborted);
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
        var endStreamJson = ConnectServerStreamHandler.BuildEndStreamJson(streamError, context);
        await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamJson), httpContext.RequestAborted);
        await response.Body.FlushAsync(httpContext.RequestAborted);
    }

    private static async IAsyncEnumerable<IMessage> ReadRequestMessages(
        Stream bodyStream,
        MessageParser parser,
        ICodec codec,
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

            var message = codec.Deserialize(data, parser);
            yield return message;
        }
    }
}
