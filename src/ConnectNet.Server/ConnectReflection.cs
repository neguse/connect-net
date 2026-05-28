using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

public class ConnectReflectionService
{
    // ConcurrentBag-style storage so AddService can be called during startup while
    // request threads enumerate Services concurrently without corruption.
    private readonly ConcurrentDictionary<string, byte> _services = new();

    public void AddService(string serviceName) => _services[serviceName] = 1;
    public IReadOnlyList<string> Services => _services.Keys.ToList().AsReadOnly();
}

public static class ConnectReflectionExtensions
{
    public static void MapConnectReflection(this IEndpointRouteBuilder builder)
    {
        // Connect-native JSON endpoint for easy service discovery
        builder.MapGet("/connect/v1/services", (HttpContext httpContext) =>
        {
            var reflection = httpContext.RequestServices.GetRequiredService<ConnectReflectionService>();
            httpContext.Response.ContentType = "application/json";
            return httpContext.Response.WriteAsync(
                JsonSerializer.Serialize(new { services = reflection.Services }));
        });

        // Standard gRPC reflection endpoint (list_services only)
        builder.MapPost("/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo", async (HttpContext httpContext) =>
        {
            var request = httpContext.Request;
            var response = httpContext.Response;

            var contentType = request.ContentType ?? "";
            var isJson = contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase);

            var reflection = httpContext.RequestServices.GetRequiredService<ConnectReflectionService>();

            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var requestBytes = ms.ToArray();

            if (isJson)
            {
                // Parse JSON request and check for list_services
                var isListServices = false;
                if (requestBytes.Length > 0)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(requestBytes);
                        isListServices = doc.RootElement.TryGetProperty("list_services", out _);
                    }
                    catch { }
                }

                if (isListServices)
                {
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    var services = new List<object>();
                    foreach (var svc in reflection.Services)
                    {
                        services.Add(new { name = svc });
                    }
                    var responseObj = new
                    {
                        list_services_response = new { service = services }
                    };
                    await response.WriteAsync(JsonSerializer.Serialize(responseObj));
                }
                else
                {
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    await response.WriteAsync("{\"error_response\":{\"error_code\":5,\"error_message\":\"only list_services is supported\"}}");
                }
            }
            else
            {
                // Protobuf: parse ServerReflectionRequest and handle list_services (field 7)
                var isListServices = false;
                if (requestBytes.Length > 0)
                {
                    isListServices = ParseReflectionRequestForListServices(requestBytes);
                }

                if (isListServices)
                {
                    response.StatusCode = 200;
                    response.ContentType = "application/proto";
                    var responseBytes = EncodeListServicesResponse(reflection.Services);
                    await response.Body.WriteAsync(responseBytes);
                }
                else
                {
                    response.StatusCode = 200;
                    response.ContentType = "application/proto";
                    // Return error_response (not implemented for other request types)
                    await response.Body.WriteAsync(Array.Empty<byte>());
                }
            }
        });
    }

    /// <summary>
    /// Check if the ServerReflectionRequest has list_services set (field 7, wire type 2).
    /// Returns false on malformed input rather than throwing — this endpoint is best-effort
    /// reflection and must not allow malformed protobuf to crash the server.
    /// </summary>
    private static bool ParseReflectionRequestForListServices(byte[] data)
    {
        try
        {
            var offset = 0;
            while (offset < data.Length)
            {
                var tag = DecodeVarint(data, ref offset);
                var fieldNumber = tag >> 3;
                var wireType = tag & 0x7;

                if (fieldNumber == 7 && wireType == 2)
                {
                    return true;
                }

                SkipField(data, wireType, ref offset);
            }
        }
        catch (InvalidDataException)
        {
            // Malformed varint or length-prefixed field — reject the request gracefully.
        }
        return false;
    }

    /// <summary>
    /// Encode ServerReflectionResponse with list_services_response.
    /// Response field 6 = ListServiceResponse, which has repeated ServiceResponse (field 1).
    /// Each ServiceResponse has name (field 1, string).
    /// </summary>
    private static byte[] EncodeListServicesResponse(IReadOnlyList<string> services)
    {
        using var innerMs = new MemoryStream();

        foreach (var svc in services)
        {
            // Encode ServiceResponse: field 1 (name), wire type 2
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(svc);
            using var serviceMs = new MemoryStream();
            serviceMs.WriteByte(0x0A); // field 1, wire type 2
            EncodeVarint(serviceMs, (ulong)nameBytes.Length);
            serviceMs.Write(nameBytes, 0, nameBytes.Length);
            var serviceBytes = serviceMs.ToArray();

            // Write as repeated field 1 in ListServiceResponse
            innerMs.WriteByte(0x0A); // field 1, wire type 2
            EncodeVarint(innerMs, (ulong)serviceBytes.Length);
            innerMs.Write(serviceBytes, 0, serviceBytes.Length);
        }

        var listServiceResponseBytes = innerMs.ToArray();

        // Wrap in ServerReflectionResponse field 6 (list_services_response)
        using var outerMs = new MemoryStream();
        outerMs.WriteByte(0x32); // field 6, wire type 2 = (6 << 3) | 2 = 50 = 0x32
        EncodeVarint(outerMs, (ulong)listServiceResponseBytes.Length);
        outerMs.Write(listServiceResponseBytes, 0, listServiceResponseBytes.Length);

        return outerMs.ToArray();
    }

    private static ulong DecodeVarint(byte[] data, ref int offset)
    {
        ulong result = 0;
        var shift = 0;
        // protobuf varint is at most 10 bytes (64-bit value). Reject longer encodings to
        // prevent a malicious payload from spinning DecodeVarint indefinitely.
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
        // Ran out of input mid-varint
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
            case 0:
                DecodeVarint(data, ref offset);
                break;
            case 1:
                if (offset + 8 > data.Length) throw new InvalidDataException("truncated fixed64");
                offset += 8;
                break;
            case 2:
                var rawLength = DecodeVarint(data, ref offset);
                if (rawLength > int.MaxValue) throw new InvalidDataException("length too large");
                var length = (int)rawLength;
                if (length < 0 || offset + length > data.Length)
                    throw new InvalidDataException("length-delimited field out of range");
                offset += length;
                break;
            case 5:
                if (offset + 4 > data.Length) throw new InvalidDataException("truncated fixed32");
                offset += 4;
                break;
            default:
                throw new InvalidDataException($"unknown wire type {wireType}");
        }
    }
}
