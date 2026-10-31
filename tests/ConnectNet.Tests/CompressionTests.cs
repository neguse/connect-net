using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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
    private (TestServer server, ConnectChannel channel) CreateSetup(ConnectChannelOptions? channelOptions = null, Action<ConnectServerOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices(configure);
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

    private static HttpRequestMessage UnaryRequest(string name, string acceptEncoding = "gzip")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello")
        {
            Content = new ByteArrayContent(new HelloRequest { Name = name }.ToByteArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return request;
    }

    private static async Task<byte[]> StreamFlags(TestServer server, string name)
    {
        var body = new MemoryStream();
        await Envelope.WriteAsync(body, 0, new HelloRequest { Name = name }.ToByteArray());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHelloStream")
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
        request.Headers.TryAddWithoutValidation("Connect-Accept-Encoding", "gzip");
        using var response = await server.CreateClient().SendAsync(request);
        using var stream = await response.Content.ReadAsStreamAsync();

        var flags = new List<byte>();
        while (await Envelope.ReadFrameAsync(stream) is { } frame)
        {
            using (frame) flags.Add(frame.Flags);
        }
        return flags.ToArray();
    }

    private static string Compressible(int responseSize)
    {
        // "Hello " plus the name, framed as one length-delimited string field
        var name = new string('a', responseSize - 3 - "Hello ".Length);
        Assert.Equal(responseSize, new HelloResponse { Message = "Hello " + name }.CalculateSize());
        return name;
    }

    [Fact]
    public async Task Unary_ByDefault_NotCompressed()
    {
        var (server, _) = CreateSetup();
        using (server)
        {
            using var request = UnaryRequest(Compressible(8192));
            using var response = await server.CreateClient().SendAsync(request);
            Assert.Empty(response.Content.Headers.ContentEncoding);
        }
    }

    [Fact]
    public async Task Unary_ConfiguredMinBytes_IsHonored()
    {
        var (server, _) = CreateSetup(configure: o => o.CompressMinBytes = 4096);
        using (server)
        {
            using var below = UnaryRequest(Compressible(4095));
            using var belowResponse = await server.CreateClient().SendAsync(below);
            Assert.Empty(belowResponse.Content.Headers.ContentEncoding);

            using var at = UnaryRequest(Compressible(4096));
            using var atResponse = await server.CreateClient().SendAsync(at);
            Assert.Contains("gzip", atResponse.Content.Headers.ContentEncoding);
        }
    }

    [Fact]
    public async Task Unary_CompressionThatGrowsTheBody_SentPlain()
    {
        var (server, _) = CreateSetup(configure: o => o.CompressMinBytes = 0);
        using (server)
        {
            using var request = UnaryRequest("x");
            using var response = await server.CreateClient().SendAsync(request);
            Assert.Empty(response.Content.Headers.ContentEncoding);
            var reply = HelloResponse.Parser.ParseFrom(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("Hello x", reply.Message);
        }
    }

    [Fact]
    public async Task ServerStream_BelowMinBytes_FlagClear()
    {
        var (server, _) = CreateSetup();
        using (server)
        {
            var flags = await StreamFlags(server, "small");
            Assert.Equal(new byte[] { 0, 0, 0, Envelope.FlagEndStream }, flags);
        }
    }

    [Fact]
    public async Task ServerStream_AtMinBytes_FlagSet()
    {
        var (server, _) = CreateSetup(configure: o => o.CompressMinBytes = 64);
        using (server)
        {
            var flags = await StreamFlags(server, new string('a', 256));
            Assert.Equal(new byte[] { Envelope.FlagCompressed, Envelope.FlagCompressed, Envelope.FlagCompressed, Envelope.FlagEndStream }, flags);
        }
    }

    [Fact]
    public async Task ServerStream_CompressionThatGrowsTheMessage_FlagClear()
    {
        var (server, _) = CreateSetup(configure: o => o.CompressMinBytes = 0);
        using (server)
        {
            var flags = await StreamFlags(server, "x");
            Assert.Equal(new byte[] { 0, 0, 0, Envelope.FlagEndStream }, flags);
        }
    }

    [Fact]
    public async Task ServerStream_LargeMessage_ClientDecompresses()
    {
        var (server, channel) = CreateSetup(configure: o => o.CompressMinBytes = 1024);
        using (server)
        {
            var name = new string('a', 1024);
            var messages = new List<HelloResponse>();
            await foreach (var response in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloStream", new HelloRequest { Name = name }))
            {
                messages.Add(response);
            }
            Assert.Equal("Hello " + name + " #3", messages[2].Message);
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
