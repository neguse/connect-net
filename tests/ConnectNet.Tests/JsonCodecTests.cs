using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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

public class JsonCodecTests
{
    private readonly JsonCodec _codec = new();

    [Fact]
    public void Name_ReturnsJson()
    {
        Assert.Equal("json", _codec.Name);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var request = new HelloRequest { Name = "test" };
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("test", deserialized.Name);
    }

    [Fact]
    public void Serialize_ProducesValidJson()
    {
        var request = new HelloRequest { Name = "world" };
        var bytes = _codec.Serialize(request);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"name\"", json);
        Assert.Contains("world", json);
    }

    [Fact]
    public void Serialize_EmptyMessage_RoundTrips()
    {
        var request = new HelloRequest();
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("", deserialized.Name);
    }

    [Fact]
    public void Deserialize_WithMessageParser_RoundTrips()
    {
        var request = new HelloRequest { Name = "parser-test" };
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize(bytes, HelloRequest.Parser);
        Assert.IsType<HelloRequest>(deserialized);
        Assert.Equal("parser-test", ((HelloRequest)deserialized).Name);
    }

    // --- E2E tests ---

    private (TestServer server, ConnectChannel channel) CreateSetup(ICodec? clientCodec = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<JsonTestGreeterService>();
        var app = builder.Build();
        app.MapConnectService<JsonTestGreeterService>(JsonTestGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString(), clientCodec);
        return (server, channel);
    }

    [Fact]
    public async Task Unary_JsonContentType_Success()
    {
        var (server, channel) = CreateSetup(new JsonCodec());
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "JSON" });
            Assert.Equal("Hello JSON", response.Message);
        }
    }

    [Fact]
    public async Task Unary_ProtoContentType_StillWorks()
    {
        var (server, channel) = CreateSetup(new ProtobufCodec());
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "Proto" });
            Assert.Equal("Hello Proto", response.Message);
        }
    }

    [Fact]
    public async Task Unary_DefaultCodec_UsesProto()
    {
        // No codec specified — should default to proto and still work
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "Default" });
            Assert.Equal("Hello Default", response.Message);
        }
    }

    // --- Test service ---

    private class JsonTestGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name is required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }
    }

    private class JsonTestGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static JsonTestGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((JsonTestGreeterService)service)
                    .SayHello((HelloRequest)req, ctx))
        };
    }
}
