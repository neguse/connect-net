using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Client;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ConnectNet.Tests;

public class ServerHardeningTests
{
    private static TestServer CreateServer(
        Action<ConnectServerOptions>? configure = null,
        bool registerService = true,
        bool mapHealth = false,
        bool mapReflection = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices(configure);
        if (registerService)
            builder.Services.AddSingleton<HardeningService>();
        var app = builder.Build();
        app.MapConnectService<HardeningService>(HardeningServiceDefinition.Instance);
        if (mapHealth)
            app.MapConnectHealthCheck();
        if (mapReflection)
            app.MapConnectReflection();
        app.Start();
        return app.GetTestServer();
    }

    private static HttpRequestMessage BuildUnaryRequest(string path, byte[] body, string contentType = "application/proto")
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, path);
        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");
        return httpRequest;
    }

    private static async Task<byte[]> BuildEnvelopeBody(byte[] messageBytes, byte flags = 0x00)
    {
        using var ms = new MemoryStream();
        await Envelope.WriteAsync(ms, flags, messageBytes);
        return ms.ToArray();
    }

    private static async Task<(List<byte[]> messages, string? endStreamJson)> ReadEnvelopes(HttpResponseMessage response)
    {
        var messages = new List<byte[]>();
        string? endStreamJson = null;
        using var stream = await response.Content.ReadAsStreamAsync();
        while (true)
        {
            var envelope = await Envelope.ReadAsync(stream);
            if (envelope == null) break;
            var (flags, data) = envelope.Value;
            if ((flags & Envelope.FlagEndStream) != 0)
            {
                endStreamJson = Encoding.UTF8.GetString(data);
                break;
            }
            messages.Add(data);
        }
        return (messages, endStreamJson);
    }

    // --- 1. GET is only exposed for methods marked as having no side effects ---

    [Fact]
    public async Task Get_SideEffectMethod_IsNotRouted()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/hardening.Test/Mutate?connect=v1&encoding=proto&message=");
        // The POST route exists, so routing answers 405 (Method Not Allowed) for GET.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Get_NoSideEffectsMethod_IsRouted()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        // Empty message param must be accepted as a zero-byte message (item 16).
        var response = await client.GetAsync("/hardening.Test/Echo?connect=v1&encoding=proto&message=");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = HelloResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Echo ", result.Message);
    }

    [Fact]
    public async Task Get_GeneratedCode_NoSideEffectsMethod_IsRouted()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<GeneratedGreeter>();
        var app = builder.Build();
        app.MapConnectService<GeneratedGreeter>(GreeterServiceDefinition.Instance);
        app.Start();
        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        var message = new HelloRequest { Name = "Gen" }.ToByteArray();
        var encoded = Convert.ToBase64String(message).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var response = await client.GetAsync(
            $"/example.GreeterService/SayHello?connect=v1&encoding=proto&base64=1&message={Uri.EscapeDataString(encoded)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private class GeneratedGreeter : GreeterServiceBase
    {
        public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
    }

    // --- 2. Health check must not build JSON by string interpolation ---

    [Fact]
    public async Task Health_ContentTypeWithQuote_ProducesValidJson()
    {
        using var server = CreateServer(mapHealth: true);
        using var client = server.CreateClient();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
        httpRequest.Content = new ByteArrayContent(Array.Empty<byte>());
        httpRequest.Content.Headers.TryAddWithoutValidation("Content-Type", "application/x\"inject");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body); // must be valid JSON despite the quote
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("code").GetString());
    }

    // --- 3. Health / reflection body size limits ---

    [Fact]
    public async Task Health_BodyOverLimit_ReturnsResourceExhausted()
    {
        using var server = CreateServer(o => o.MessageReceiveLimit = 16, mapHealth: true);
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/grpc.health.v1.Health/Check", new byte[64]);
        var response = await client.SendAsync(httpRequest);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("resource_exhausted", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reflection_BodyOverLimit_ReturnsResourceExhausted()
    {
        using var server = CreateServer(o => o.MessageReceiveLimit = 16, mapReflection: true);
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest(
            "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo", new byte[64]);
        var response = await client.SendAsync(httpRequest);

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("resource_exhausted", doc.RootElement.GetProperty("code").GetString());
    }

    // --- 4. GET route has the same catch-all as POST ---

    [Fact]
    public async Task Get_UnregisteredService_Returns500Json()
    {
        using var server = CreateServer(registerService: false);
        using var client = server.CreateClient();

        var response = await client.GetAsync("/hardening.Test/Echo?connect=v1&encoding=proto&message=");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("internal", doc.RootElement.GetProperty("code").GetString());
    }

    // --- 5. Server-stream deserialization failure => EndStream invalid_argument ---

    [Fact]
    public async Task ServerStream_InvalidProtobuf_ReturnsEndStreamInvalidArgument()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var body = await BuildEnvelopeBody(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoStream", body, "application/connect+proto");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, endStreamJson) = await ReadEnvelopes(response);
        Assert.NotNull(endStreamJson);
        using var doc = JsonDocument.Parse(endStreamJson!);
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // --- 6. Unary deserialization failure => 400 invalid_argument ---

    [Fact]
    public async Task Unary_InvalidProtobuf_Returns400InvalidArgument()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unary_InvalidJson_Returns400InvalidArgument()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", Encoding.UTF8.GetBytes("{oops"), "application/json");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("invalid_argument", doc.RootElement.GetProperty("code").GetString());
    }

    // --- 7. Huge Connect-Timeout-Ms must not blow up CancelAfter ---

    [Fact]
    public async Task Unary_HugeTimeout_WithoutCap_Succeeds()
    {
        using var server = CreateServer(o => o.MaxTimeoutMs = 0);
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", new HelloRequest { Name = "big" }.ToByteArray());
        httpRequest.Headers.Add("Connect-Timeout-Ms", "9223372036854775806");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- 10. Accept-Encoding with q=0 must not select that encoding ---

    [Fact]
    public async Task Unary_AcceptEncodingQZero_NotCompressed()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", new HelloRequest { Name = "q0" }.ToByteArray());
        httpRequest.Headers.Add("Accept-Encoding", "gzip;q=0, deflate;q=0");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        var result = HelloResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Echo q0", result.Message);
    }

    [Fact]
    public async Task Unary_AcceptEncodingQPositive_Compressed()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", new HelloRequest { Name = "q5" }.ToByteArray());
        httpRequest.Headers.Add("Accept-Encoding", "gzip;q=0.5");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("gzip", response.Content.Headers.ContentEncoding);
    }

    [Fact]
    public async Task ServerStream_ConnectAcceptEncodingQZero_NotCompressed()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var body = await BuildEnvelopeBody(new HelloRequest { Name = "sq0" }.ToByteArray());
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoStream", body, "application/connect+proto");
        httpRequest.Headers.Add("Connect-Accept-Encoding", "gzip;q=0");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Connect-Content-Encoding"));
    }

    // --- 11 / 14. Unknown compression => Unimplemented with supported-encodings header ---

    [Fact]
    public async Task Get_UnknownCompression_Returns501WithAcceptEncoding()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var message = new HelloRequest { Name = "x" }.ToByteArray();
        var encoded = Convert.ToBase64String(message).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var response = await client.GetAsync(
            $"/hardening.Test/Echo?connect=v1&encoding=proto&base64=1&compression=br&message={Uri.EscapeDataString(encoded)}");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unimplemented", doc.RootElement.GetProperty("code").GetString());
        Assert.True(response.Headers.Contains("Accept-Encoding"), "Accept-Encoding header expected on 501 response");
    }

    [Fact]
    public async Task Unary_UnknownContentEncoding_Returns501WithAcceptEncoding()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo", new HelloRequest { Name = "x" }.ToByteArray());
        httpRequest.Content!.Headers.Add("Content-Encoding", "br");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.True(response.Headers.Contains("Accept-Encoding"), "Accept-Encoding header expected on 501 response");
    }

    [Fact]
    public async Task ServerStream_UnknownCompression_EndStreamUnimplementedWithConnectAcceptEncoding()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var body = await BuildEnvelopeBody(new HelloRequest { Name = "x" }.ToByteArray());
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoStream", body, "application/connect+proto");
        httpRequest.Headers.Add("Connect-Content-Encoding", "br");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Connect-Accept-Encoding"),
            "Connect-Accept-Encoding header expected on unimplemented-compression response");
        var (_, endStreamJson) = await ReadEnvelopes(response);
        Assert.NotNull(endStreamJson);
        using var doc = JsonDocument.Parse(endStreamJson!);
        Assert.Equal("unimplemented", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    // --- 13. Bidi requires HTTP/2 ---

    [Fact]
    public async Task Bidi_Http11_RejectedWithInternal()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var body = await BuildEnvelopeBody(new HelloRequest { Name = "h1" }.ToByteArray());
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoBidi", body, "application/connect+proto");
        // TestServer requests default to HTTP/1.1
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (_, endStreamJson) = await ReadEnvelopes(response);
        Assert.NotNull(endStreamJson);
        using var doc = JsonDocument.Parse(endStreamJson!);
        Assert.Equal("internal", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bidi_Http2_Accepted()
    {
        using var server = CreateServer();
        using var client = new HttpClient(new Http2VersionHandler(server.CreateHandler()))
        {
            BaseAddress = server.BaseAddress
        };

        var body = await BuildEnvelopeBody(new HelloRequest { Name = "h2" }.ToByteArray());
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoBidi", body, "application/connect+proto");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (messages, endStreamJson) = await ReadEnvelopes(response);
        Assert.Single(messages);
        Assert.NotNull(endStreamJson);
        using var doc = JsonDocument.Parse(endStreamJson!);
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    internal sealed class Http2VersionHandler : DelegatingHandler
    {
        public Http2VersionHandler(HttpMessageHandler inner) : base(inner) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Version = HttpVersion.Version20;
            return base.SendAsync(request, cancellationToken);
        }
    }

    // --- 14. Strict Content-Type parsing ---

    [Fact]
    public async Task Unary_ContentTypeProtobuf_Returns415WithAcceptPost()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        // "application/protobuf" is NOT a Connect content type; the old prefix match accepted it.
        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo",
            new HelloRequest { Name = "x" }.ToByteArray(), "application/protobuf");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.True(response.Headers.Contains("Accept-Post"), "Accept-Post header expected on 415 response");
        var acceptPost = string.Join(",", response.Headers.GetValues("Accept-Post"));
        Assert.Contains("application/proto", acceptPost);
    }

    [Fact]
    public async Task Unary_ContentTypeJsonWithCharset_Accepted()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/hardening.Test/Echo",
            Encoding.UTF8.GetBytes("{\"name\":\"cs\"}"), "application/json; charset=utf-8");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServerStream_UnknownConnectCodec_Returns415()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        var body = await BuildEnvelopeBody(new HelloRequest { Name = "x" }.ToByteArray());
        using var httpRequest = BuildUnaryRequest("/hardening.Test/EchoStream", body, "application/connect+msgpack");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.True(response.Headers.Contains("Accept-Post"), "Accept-Post header expected on 415 response");
        var acceptPost = string.Join(",", response.Headers.GetValues("Accept-Post"));
        Assert.Contains("application/connect+proto", acceptPost);
    }

    // --- 15. Connect-Protocol-Version is optional by default ---

    [Fact]
    public async Task Unary_MissingProtocolVersion_OptionalByDefault_Succeeds()
    {
        using var server = CreateServer();
        using var client = server.CreateClient();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/hardening.Test/Echo");
        httpRequest.Content = new ByteArrayContent(new HelloRequest { Name = "nover" }.ToByteArray());
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/proto");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unary_MissingProtocolVersion_Required_Returns400()
    {
        using var server = CreateServer(o => o.RequireConnectProtocolHeader = true);
        using var client = server.CreateClient();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/hardening.Test/Echo");
        httpRequest.Content = new ByteArrayContent(new HelloRequest { Name = "nover" }.ToByteArray());
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/proto");
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- 17. ConnectHealthService is registered by AddConnectServices ---

    [Fact]
    public async Task Health_WorksWithoutExplicitServiceRegistration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        var app = builder.Build();
        app.MapConnectHealthCheck();
        app.Start();
        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        using var httpRequest = BuildUnaryRequest("/grpc.health.v1.Health/Check", Array.Empty<byte>());
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- 18. Reflection: non-list_services proto request returns a proper error response ---

    [Fact]
    public async Task Reflection_NonListServicesProto_ReturnsErrorResponse()
    {
        using var server = CreateServer(mapReflection: true);
        using var client = server.CreateClient();

        // ServerReflectionRequest { file_by_filename: "foo.proto" } => field 3, wire type 2
        var name = Encoding.UTF8.GetBytes("foo.proto");
        var body = new byte[2 + name.Length];
        body[0] = 0x1A; // (3 << 3) | 2
        body[1] = (byte)name.Length;
        Array.Copy(name, 0, body, 2, name.Length);

        using var httpRequest = BuildUnaryRequest(
            "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo", body);
        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(bytes);
        // ServerReflectionResponse.error_response is field 7, wire type 2 => tag 0x3A
        Assert.Equal(0x3A, bytes[0]);
        // ErrorResponse.error_code (field 1, varint) must be UNIMPLEMENTED (12)
        Assert.Contains((byte)0x0C, bytes);
    }

    // --- Test service ---

    private class HardeningService
    {
        public Task<HelloResponse> Echo(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = $"Echo {request.Name}" });

        public Task<HelloResponse> Mutate(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = $"Mutated {request.Name}" });

        public async IAsyncEnumerable<HelloResponse> EchoStream(
            HelloRequest request, ConnectContext context, [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new HelloResponse { Message = $"Echo {request.Name}" };
            await Task.Yield();
        }

        public async IAsyncEnumerable<HelloResponse> EchoBidi(
            IAsyncEnumerable<HelloRequest> requests, ConnectContext context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var request in requests.WithCancellation(ct))
            {
                yield return new HelloResponse { Message = $"Echo {request.Name}" };
            }
        }
    }

    private class HardeningServiceDefinition : IConnectServiceDefinition
    {
        public static HardeningServiceDefinition Instance { get; } = new();
        public string ServiceName => "hardening.Test";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/hardening.Test/Echo",
                HelloRequest.Parser,
                handler: async (svc, req, ctx) => (IMessage)await ((HardeningService)svc).Echo((HelloRequest)req, ctx),
                isNoSideEffects: true),
            new ConnectMethodDescriptor(
                "/hardening.Test/Mutate",
                HelloRequest.Parser,
                handler: async (svc, req, ctx) => (IMessage)await ((HardeningService)svc).Mutate((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/hardening.Test/EchoStream",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (svc, req, ctx) => Convert(((HardeningService)svc).EchoStream((HelloRequest)req, ctx))),
            new ConnectMethodDescriptor(
                "/hardening.Test/EchoBidi",
                HelloRequest.Parser,
                ConnectMethodType.BidiStreaming,
                bidiStreamHandler: (svc, reqs, ctx) => Convert(((HardeningService)svc).EchoBidi(Cast<HelloRequest>(reqs), ctx))),
        };

        private static async IAsyncEnumerable<IMessage> Convert<T>(
            IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken ct = default) where T : IMessage
        {
            await foreach (var item in source.WithCancellation(ct))
                yield return item;
        }

        private static async IAsyncEnumerable<T> Cast<T>(
            IAsyncEnumerable<IMessage> source, [EnumeratorCancellation] CancellationToken ct = default) where T : IMessage
        {
            await foreach (var item in source.WithCancellation(ct))
                yield return (T)item;
        }
    }
}
