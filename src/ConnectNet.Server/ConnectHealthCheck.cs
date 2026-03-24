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
    private readonly Dictionary<string, HealthStatus> _statuses = new();
    private HealthStatus _overallStatus = HealthStatus.Serving;

    public void SetStatus(string service, HealthStatus status)
        => _statuses[service] = status;

    public void SetOverallStatus(HealthStatus status)
        => _overallStatus = status;

    public HealthStatus GetStatus(string service)
        => string.IsNullOrEmpty(service)
            ? _overallStatus
            : _statuses.TryGetValue(service, out var s) ? s : HealthStatus.ServiceUnknown;
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

            // Validate Connect-Protocol-Version
            if (!request.Headers.TryGetValue("Connect-Protocol-Version", out var version) || version != "1")
            {
                response.StatusCode = 400;
                response.ContentType = "application/json";
                await response.WriteAsync("{\"code\":\"invalid_argument\",\"message\":\"missing or invalid Connect-Protocol-Version header\"}");
                return;
            }

            var contentType = request.ContentType ?? "";
            var isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);
            var isProto = contentType.StartsWith("application/proto", StringComparison.OrdinalIgnoreCase);

            if (!isJson && !isProto)
            {
                response.StatusCode = 415;
                response.ContentType = "application/json";
                await response.WriteAsync($"{{\"code\":\"invalid_argument\",\"message\":\"unsupported content type: {contentType}\"}}");
                return;
            }

            var healthService = httpContext.RequestServices.GetRequiredService<ConnectHealthService>();

            // Read request body
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var requestBytes = ms.ToArray();

            string serviceName;

            if (isJson)
            {
                serviceName = ParseJsonRequest(requestBytes);
            }
            else
            {
                serviceName = ParseProtobufRequest(requestBytes);
            }

            var status = healthService.GetStatus(serviceName);

            if (isJson)
            {
                response.StatusCode = 200;
                response.ContentType = "application/json";
                var statusName = StatusNames[(int)status];
                await response.WriteAsync($"{{\"status\":\"{statusName}\"}}");
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
    /// </summary>
    private static string ParseProtobufRequest(byte[] data)
    {
        if (data.Length == 0)
            return "";

        var offset = 0;
        while (offset < data.Length)
        {
            var tag = DecodeVarint(data, ref offset);
            var fieldNumber = tag >> 3;
            var wireType = tag & 0x7;

            if (fieldNumber == 1 && wireType == 2)
            {
                var length = (int)DecodeVarint(data, ref offset);
                var value = System.Text.Encoding.UTF8.GetString(data, offset, length);
                return value;
            }

            // Skip unknown fields
            SkipField(data, wireType, ref offset);
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
        while (offset < data.Length)
        {
            var b = data[offset++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        return result;
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
                offset += 8;
                break;
            case 2: // length-delimited
                var length = (int)DecodeVarint(data, ref offset);
                offset += length;
                break;
            case 5: // 32-bit
                offset += 4;
                break;
        }
    }
}
