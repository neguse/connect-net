using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectNet;
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

public class UnaryHandlerTests
{
    private TestServer CreateTestServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<TestGreeterService>();
        var app = builder.Build();
        app.MapConnectService<TestGreeterService>(TestGreeterServiceDefinition.Instance);
        app.Start();
        return app.GetTestServer();
    }

    [Fact]
    public async Task Unary_Success()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "World" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/proto", response.Content.Headers.ContentType!.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var result = HelloResponse.Parser.ParseFrom(bytes);
        Assert.Equal("Hello World", result.Message);
    }

    [Fact]
    public async Task Unary_ServiceThrowsConnectException_ReturnsJsonError()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unary_MissingProtocolVersion_Returns400()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "test" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");

        var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unary_WrongContentType_Returns415()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new StringContent("{}");
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // --- Test service and definition ---

    private class TestGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.NotFound, "name required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }
    }

    private class TestGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static TestGreeterServiceDefinition Instance { get; } = new();

        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((TestGreeterService)service)
                    .SayHello((HelloRequest)req, ctx))
        };
    }
}
