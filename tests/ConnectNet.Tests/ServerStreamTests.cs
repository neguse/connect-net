using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

public class ServerStreamTests
{
    private (TestServer server, ConnectChannel channel, HttpClient rawClient) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<StreamGreeterService>();
        var app = builder.Build();
        app.MapConnectService<StreamGreeterService>(StreamGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString());
        return (server, channel, httpClient);
    }

    [Fact]
    public async Task ServerStream_ReceivesMultipleMessages()
    {
        var (server, channel, _) = CreateSetup();
        using (server)
        {
            var messages = new List<HelloResponse>();
            await foreach (var response in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloStream",
                new HelloRequest { Name = "Stream" }))
            {
                messages.Add(response);
            }

            Assert.Equal(3, messages.Count);
            Assert.Equal("Hello Stream #1", messages[0].Message);
            Assert.Equal("Hello Stream #2", messages[1].Message);
            Assert.Equal("Hello Stream #3", messages[2].Message);
        }
    }

    [Fact]
    public async Task ServerStream_RawHttp_CorrectFormat()
    {
        var (server, _, rawClient) = CreateSetup();
        using (server)
        {
            // Build envelope-wrapped request
            var requestMessage = new HelloRequest { Name = "RawTest" };
            var codec = new ProtobufCodec();
            var requestBytes = codec.Serialize(requestMessage);

            using var envelopeStream = new MemoryStream();
            await Envelope.WriteAsync(envelopeStream, 0x00, requestBytes);
            var envelopeBytes = envelopeStream.ToArray();

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post,
                "/example.GreeterService/SayHelloStream");
            httpRequest.Content = new ByteArrayContent(envelopeBytes);
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");

            var httpResponse = await rawClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(System.Net.HttpStatusCode.OK, httpResponse.StatusCode);
            Assert.Equal("application/connect+proto", httpResponse.Content.Headers.ContentType?.ToString());

            // Read response envelopes
            using var responseStream = await httpResponse.Content.ReadAsStreamAsync();
            var messages = new List<byte[]>();
            byte? endStreamFlags = null;
            string? endStreamJson = null;

            while (true)
            {
                var envelope = await Envelope.ReadAsync(responseStream);
                if (envelope == null) break;
                var (flags, data) = envelope.Value;

                if ((flags & Envelope.FlagEndStream) != 0)
                {
                    endStreamFlags = flags;
                    endStreamJson = Encoding.UTF8.GetString(data);
                    break;
                }

                messages.Add(data);
            }

            // Verify 3 message envelopes + EndStream
            Assert.Equal(3, messages.Count);
            Assert.NotNull(endStreamFlags);
            Assert.Equal(Envelope.FlagEndStream, endStreamFlags!.Value);
            Assert.NotNull(endStreamJson);

            // Verify EndStream JSON has metadata
            using var doc = JsonDocument.Parse(endStreamJson!);
            Assert.True(doc.RootElement.TryGetProperty("metadata", out _));
            // Should not have error
            Assert.False(doc.RootElement.TryGetProperty("error", out _));

            // Verify each message deserializes correctly
            for (int i = 0; i < 3; i++)
            {
                var msg = codec.Deserialize<HelloResponse>(messages[i]);
                Assert.Equal($"Hello RawTest #{i + 1}", msg.Message);
            }
        }
    }

    [Fact]
    public async Task ServerStream_Error_InEndStream()
    {
        var (server, channel, _) = CreateSetup();
        using (server)
        {
            var messages = new List<HelloResponse>();
            var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
            {
                await foreach (var response in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                    "/example.GreeterService/SayHelloStreamError",
                    new HelloRequest { Name = "Error" }))
                {
                    messages.Add(response);
                }
            });

            Assert.Equal(ConnectCode.Internal, ex.Code);
            Assert.Equal("stream error", ex.Message);
            // Should have received 1 message before the error
            Assert.Single(messages);
            Assert.Equal("Hello Error #1", messages[0].Message);
        }
    }

    // --- Test service ---

    private class StreamGreeterService
    {
        public async IAsyncEnumerable<HelloResponse> SayHelloStream(HelloRequest request, ConnectContext context)
        {
            for (int i = 1; i <= 3; i++)
            {
                yield return new HelloResponse { Message = $"Hello {request.Name} #{i}" };
                await Task.Yield();
            }
        }

        public async IAsyncEnumerable<HelloResponse> SayHelloStreamError(HelloRequest request, ConnectContext context)
        {
            yield return new HelloResponse { Message = $"Hello {request.Name} #1" };
            await Task.Yield();
            throw new ConnectException(ConnectCode.Internal, "stream error");
        }
    }

    private class StreamGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static StreamGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloStream",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) =>
                    ((StreamGreeterService)service).SayHelloStream((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloStreamError",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) =>
                    ((StreamGreeterService)service).SayHelloStreamError((HelloRequest)req, ctx)),
        };
    }
}
