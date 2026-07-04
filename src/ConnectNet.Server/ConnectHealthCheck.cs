using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

public enum HealthStatus
{
    Unknown = 0,
    Serving = 1,
    NotServing = 2,
    ServiceUnknown = 3
}

public class ConnectHealthService
{
    // ConcurrentDictionary so SetStatus during runtime cannot corrupt Dictionary internals
    // for concurrent GetStatus readers.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, HealthStatus> _statuses = new();
    private volatile HealthStatus _overallStatus = HealthStatus.Serving;

    public void SetStatus(string service, HealthStatus status)
        => _statuses[service] = status;

    public void SetOverallStatus(HealthStatus status)
        => _overallStatus = status;

    public HealthStatus GetStatus(string service)
        => string.IsNullOrEmpty(service)
            ? _overallStatus
            : _statuses.TryGetValue(service, out var s) ? s : HealthStatus.ServiceUnknown;

    /// <summary>
    /// Looks up the status for a service. Returns false when the service has never been
    /// registered — per the gRPC Health protocol, Check must then fail with NotFound
    /// (SERVICE_UNKNOWN is reserved for the Watch streaming variant).
    /// </summary>
    public bool TryGetStatus(string service, out HealthStatus status)
    {
        if (string.IsNullOrEmpty(service))
        {
            status = _overallStatus;
            return true;
        }
        return _statuses.TryGetValue(service, out status);
    }
}

public static class ConnectHealthCheckExtensions
{
    private static readonly string[] StatusNames = { "UNKNOWN", "SERVING", "NOT_SERVING", "SERVICE_UNKNOWN" };

    public static void MapConnectHealthCheck(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/grpc.health.v1.Health/Check", async (HttpContext httpContext) =>
        {
            var request = httpContext.Request;
            var response = httpContext.Response;
            var serverOptions = httpContext.RequestServices.GetService<ConnectServerOptions>();

            // Validate Connect-Protocol-Version (only when required by options)
            if (!ConnectServerProtocol.ProtocolVersionSatisfied(request, serverOptions))
            {
                await WriteErrorAsync(response,
                    new ConnectException(ConnectCode.InvalidArgument, "missing or invalid Connect-Protocol-Version header"),
                    overrideStatus: 400);
                return;
            }

            var contentType = request.ContentType;
            var isJson = ConnectServerProtocol.MatchesContentType(contentType, "application/json");
            var isProto = ConnectServerProtocol.MatchesContentType(contentType, "application/proto");

            if (!isJson && !isProto)
            {
                // Serialize through ConnectException.ToJson so a hostile Content-Type value
                // cannot inject content into the JSON error body.
                await WriteErrorAsync(response,
                    new ConnectException(ConnectCode.InvalidArgument, $"unsupported content type: {contentType}"),
                    overrideStatus: 415);
                return;
            }

            var healthService = httpContext.RequestServices.GetRequiredService<ConnectHealthService>();

            // Read request body with the configured receive limit
            var receiveLimit = serverOptions?.EffectiveReceiveLimit ?? Envelope.DefaultMaxMessageBytes;
            var requestBytes = await ConnectServerProtocol.ReadBodyWithLimitAsync(request, receiveLimit, httpContext.RequestAborted);
            if (requestBytes == null)
            {
                await WriteErrorAsync(response,
                    new ConnectException(ConnectCode.ResourceExhausted, $"request body exceeds limit {receiveLimit}"));
                return;
            }

            var serviceName = isJson ? ParseJsonRequest(requestBytes) : ParseProtobufRequest(requestBytes);

            // Per the gRPC Health protocol, Check responds NOT_FOUND for services that
            // were never registered; SERVICE_UNKNOWN is only used by Watch.
            if (!healthService.TryGetStatus(serviceName, out var status))
            {
                await WriteErrorAsync(response,
                    new ConnectException(ConnectCode.NotFound, $"unknown service: {serviceName}"));
                return;
            }

            if (isJson)
            {
                response.StatusCode = 200;
                response.ContentType = "application/json";
                var statusName = StatusNames[(int)status];
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteString("status", statusName);
                    writer.WriteEndObject();
                }
                await response.Body.WriteAsync(stream.ToArray());
            }
            else
            {
                response.StatusCode = 200;
                response.ContentType = "application/proto";
                var responseBytes = EncodeProtobufResponse(status);
                await response.Body.WriteAsync(responseBytes);
            }
        });
    }

    private static async Task WriteErrorAsync(HttpResponse response, ConnectException error, int? overrideStatus = null)
    {
        response.StatusCode = overrideStatus ?? ConnectException.ToHttpStatus(error.Code);
        response.ContentType = "application/json";
        await response.WriteAsync(error.ToJson());
    }

    private static string ParseJsonRequest(byte[] data)
    {
        if (data.Length == 0)
            return "";

        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.TryGetProperty("service", out var serviceElem))
                return serviceElem.GetString() ?? "";
            return "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Parse HealthCheckRequest protobuf: field 1, wire type 2 (length-delimited string).
    /// Returns empty string on malformed input — clients cannot crash the health endpoint
    /// with bogus varints or out-of-range lengths.
    /// </summary>
    private static string ParseProtobufRequest(byte[] data)
    {
        if (data.Length == 0)
            return "";

        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var tag = DecodeVarint(data, ref offset);
                var fieldNumber = tag >> 3;
                var wireType = tag & 0x7;

                if (fieldNumber == 1 && wireType == 2)
                {
                    var rawLength = DecodeVarint(data, ref offset);
                    if (rawLength > int.MaxValue) return "";
                    var length = (int)rawLength;
                    if (length > data.Length - offset) return "";
                    return System.Text.Encoding.UTF8.GetString(data, offset, length);
                }

                // Skip unknown fields
                SkipField(data, wireType, ref offset);
            }
        }
        catch (InvalidDataException)
        {
            return "";
        }

        return "";
    }

    /// <summary>
    /// Encode HealthCheckResponse protobuf: field 1, wire type 0 (varint enum).
    /// </summary>
    private static byte[] EncodeProtobufResponse(HealthStatus status)
    {
        var value = (int)status;
        if (value == 0)
            return Array.Empty<byte>(); // default value, omit

        // Tag: field 1, wire type 0 = (1 << 3) | 0 = 0x08
        using var ms = new MemoryStream();
        ms.WriteByte(0x08);
        EncodeVarint(ms, (ulong)value);
        return ms.ToArray();
    }

    private static ulong DecodeVarint(byte[] data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        var consumed = 0;
        while (offset < data.Length)
        {
            if (consumed >= 10)
                throw new InvalidDataException("varint too long");
            var b = data[offset++];
            consumed++;
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
        throw new InvalidDataException("truncated varint");
    }

    private static void EncodeVarint(MemoryStream ms, ulong value)
    {
        while (value > 0x7F)
        {
            ms.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }

    private static void SkipField(byte[] data, ulong wireType, ref int offset)
    {
        switch (wireType)
        {
            case 0: // varint
                DecodeVarint(data, ref offset);
                break;
            case 1: // 64-bit
                if (offset + 8 > data.Length) throw new InvalidDataException("truncated fixed64");
                offset += 8;
                break;
            case 2: // length-delimited
                var rawLength = DecodeVarint(data, ref offset);
                if (rawLength > int.MaxValue) throw new InvalidDataException("length too large");
                var length = (int)rawLength;
                if (length > data.Length - offset)
                    throw new InvalidDataException("length-delimited field out of range");
                offset += length;
                break;
            case 5: // 32-bit
                if (offset + 4 > data.Length) throw new InvalidDataException("truncated fixed32");
                offset += 4;
                break;
            default:
                throw new InvalidDataException($"unknown wire type {wireType}");
        }
    }
}
