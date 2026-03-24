using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
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

public class TimeoutTests
{
    private (TestServer server, ConnectChannel channel, HttpClient rawClient) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<TimeoutGreeterService>();
        var app = builder.Build();
        app.MapConnectService<TimeoutGreeterService>(TimeoutGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString());
        return (server, channel, httpClient);
    }

    [Fact]
    public async Task Unary_Timeout_ReturnsDeadlineExceeded()
    {
        var (server, _, rawClient) = CreateSetup();
        using (server)
        {
            var requestMessage = new HelloRequest { Name = "slow" };
            var codec = new ProtobufCodec();
            var requestBytes = codec.Serialize(requestMessage);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
                "/example.GreeterService/SlowSayHello");
            httpRequest.Content = new ByteArrayContent(requestBytes);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");
            httpRequest.Headers.Add("Connect-Timeout-Ms", "100");

            var httpResponse = await rawClient.SendAsync(httpRequest);
            Assert.Equal(System.Net.HttpStatusCode.GatewayTimeout, httpResponse.StatusCode);

            var body = await httpResponse.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("deadline_exceeded", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task Unary_NoTimeout_Succeeds()
    {
        var (server, channel, _) = CreateSetup();
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/FastSayHello",
                new HelloRequest { Name = "fast" });
            Assert.Equal("Hello fast", response.Message);
        }
    }

    [Fact]
    public async Task ServerStream_Timeout_EndsStream()
    {
        var (server, _, rawClient) = CreateSetup();
        using (server)
        {
            var requestMessage = new HelloRequest { Name = "slowstream" };
            var codec = new ProtobufCodec();
            var requestBytes = codec.Serialize(requestMessage);

            // Build envelope-wrapped request
            using var envelopeStream = new MemoryStream();
            await Envelope.WriteAsync(envelopeStream, 0x00, requestBytes);
            var envelopeBytes = envelopeStream.ToArray();

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
                "/example.GreeterService/SlowSayHelloStream");
            httpRequest.Content = new ByteArrayContent(envelopeBytes);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");
            httpRequest.Headers.Add("Connect-Timeout-Ms", "100");

            var httpResponse = await rawClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(System.Net.HttpStatusCode.OK, httpResponse.StatusCode);

            // Read response envelopes
            using var responseStream = await httpResponse.Content.ReadAsStreamAsync();
            var messages = new List<byte[]>();
            string? endStreamJson = null;

            while (true)
            {
                var envelope = await Envelope.ReadAsync(responseStream);
                if (envelope == null) break;
                var (flags, data) = envelope.Value;

                if ((flags & Envelope.FlagEndStream) != 0)
                {
                    endStreamJson = Encoding.UTF8.GetString(data);
                    break;
                }

                messages.Add(data);
            }

            // Should have an EndStream with deadline_exceeded error
            Assert.NotNull(endStreamJson);
            using var doc = JsonDocument.Parse(endStreamJson!);
            Assert.True(doc.RootElement.TryGetProperty("error", out var errorElement));
            Assert.Equal("deadline_exceeded", errorElement.GetProperty("code").GetString());
        }
    }

    // --- Test service ---

    private class TimeoutGreeterService
    {
        public async Task<HelloResponse> SlowSayHello(HelloRequest request, ConnectContext context)
        {
            await Task.Delay(5000, context.CancellationToken);
            return new HelloResponse { Message = $"Hello {request.Name}" };
        }

        public Task<HelloResponse> FastSayHello(HelloRequest request, ConnectContext context)
        {
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }

        public async IAsyncEnumerable<HelloResponse> SlowSayHelloStream(
            HelloRequest request,
            ConnectContext context)
        {
            for (int i = 1; i <= 10; i++)
            {
                await Task.Delay(5000, context.CancellationToken);
                yield return new HelloResponse { Message = $"Hello {request.Name} #{i}" };
            }
        }
    }

    private class TimeoutGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static TimeoutGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SlowSayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((TimeoutGreeterService)service)
                    .SlowSayHello((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/FastSayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((TimeoutGreeterService)service)
                    .FastSayHello((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/SlowSayHelloStream",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) =>
                    ((TimeoutGreeterService)service).SlowSayHelloStream((HelloRequest)req, ctx)),
        };
    }
}
