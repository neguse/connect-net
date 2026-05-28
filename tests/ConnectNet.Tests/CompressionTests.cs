using System.Collections.Generic;
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

public class CompressionTests
{
    private (TestServer server, ConnectChannel channel) CreateSetup(ConnectChannelOptions? channelOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<CompGreeterService>();
        var app = builder.Build();
        app.MapConnectService<CompGreeterService>(CompGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        channelOptions ??= new ConnectChannelOptions();
        channelOptions.HttpClient = httpClient;
        var channel = ConnectChannel.ForAddress(server.BaseAddress.ToString(), channelOptions);
        return (server, channel);
    }

    [Fact]
    public async Task Unary_CompressedRequest_ServerDecompresses()
    {
        var options = new ConnectChannelOptions
        {
            RequestCompressor = new GzipCompressor(),
            AcceptCompression = false
        };
        var (server, channel) = CreateSetup(options);
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "Compressed" });
            Assert.Equal("Hello Compressed", response.Message);
        }
    }

    [Fact]
    public async Task Unary_ServerCompressesResponse()
    {
        var options = new ConnectChannelOptions
        {
            RequestCompressor = null,
            AcceptCompression = true
        };
        var (server, channel) = CreateSetup(options);
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "AcceptGzip" });
            Assert.Equal("Hello AcceptGzip", response.Message);
        }
    }

    [Fact]
    public async Task Unary_RoundTrip_Compressed()
    {
        var options = new ConnectChannelOptions
        {
            RequestCompressor = new GzipCompressor(),
            AcceptCompression = true
        };
        var (server, channel) = CreateSetup(options);
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "RoundTrip" });
            Assert.Equal("Hello RoundTrip", response.Message);
        }
    }

    [Fact]
    public async Task ServerStream_CompressedMessages()
    {
        var options = new ConnectChannelOptions
        {
            RequestCompressor = new GzipCompressor(),
            AcceptCompression = true
        };
        var (server, channel) = CreateSetup(options);
        using (server)
        {
            var messages = new List<HelloResponse>();
            await foreach (var response in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloStream",
                new HelloRequest { Name = "CompStream" }))
            {
                messages.Add(response);
            }

            Assert.Equal(3, messages.Count);
            Assert.Equal("Hello CompStream #1", messages[0].Message);
            Assert.Equal("Hello CompStream #2", messages[1].Message);
            Assert.Equal("Hello CompStream #3", messages[2].Message);
        }
    }

    // --- Test service ---

    private class CompGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name is required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }

        public async IAsyncEnumerable<HelloResponse> SayHelloStream(HelloRequest request, ConnectContext context)
        {
            for (int i = 1; i <= 3; i++)
            {
                yield return new HelloResponse { Message = $"Hello {request.Name} #{i}" };
                await Task.Yield();
            }
        }
    }

    private class CompGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static CompGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((CompGreeterService)service)
                    .SayHello((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloStream",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) =>
                    ((CompGreeterService)service).SayHelloStream((HelloRequest)req, ctx)),
        };
    }
}
