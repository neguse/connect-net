using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

public class ClientStreamTests
{
    private (TestServer server, ConnectChannel channel) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ClientStreamGreeterService>();
        var app = builder.Build();
        app.MapConnectService<ClientStreamGreeterService>(ClientStreamGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = ConnectChannel.ForAddress(server.BaseAddress.ToString(), new() { HttpClient = httpClient });
        return (server, channel);
    }

    [Fact]
    public async Task ClientStream_SendMultipleReceiveOne()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloClientStream");

            await call.SendAsync(new HelloRequest { Name = "Alice" });
            await call.SendAsync(new HelloRequest { Name = "Bob" });
            await call.SendAsync(new HelloRequest { Name = "Charlie" });

            var response = await call.CloseAndReceiveAsync();
            Assert.Equal("Hello Alice, Bob, Charlie", response.Message);
        }
    }

    [Fact]
    public async Task ClientStream_Error()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloClientStreamError");

            await call.SendAsync(new HelloRequest { Name = "Alice" });

            var ex = await Assert.ThrowsAsync<ConnectException>(() => call.CloseAndReceiveAsync());
            Assert.Equal(ConnectCode.Internal, ex.Code);
            Assert.Equal("client stream error", ex.Message);
        }
    }

    // --- Test service ---

    private class ClientStreamGreeterService
    {
        public async Task<HelloResponse> SayHelloClientStream(IAsyncEnumerable<HelloRequest> requests, ConnectContext context)
        {
            var names = new List<string>();
            await foreach (var request in requests.WithCancellation(context.CancellationToken))
            {
                names.Add(request.Name);
            }
            return new HelloResponse { Message = $"Hello {string.Join(", ", names)}" };
        }

        public async Task<HelloResponse> SayHelloClientStreamError(IAsyncEnumerable<HelloRequest> requests, ConnectContext context)
        {
            // Consume the stream
            await foreach (var _ in requests.WithCancellation(context.CancellationToken))
            {
            }
            throw new ConnectException(ConnectCode.Internal, "client stream error");
        }
    }

    private class ClientStreamGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static ClientStreamGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloClientStream",
                HelloRequest.Parser,
                ConnectMethodType.ClientStreaming,
                clientStreamHandler: async (service, requests, ctx) =>
                    (IMessage)await ((ClientStreamGreeterService)service).SayHelloClientStream(
                        CastAsyncEnumerable<HelloRequest>(requests), ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloClientStreamError",
                HelloRequest.Parser,
                ConnectMethodType.ClientStreaming,
                clientStreamHandler: async (service, requests, ctx) =>
                    (IMessage)await ((ClientStreamGreeterService)service).SayHelloClientStreamError(
                        CastAsyncEnumerable<HelloRequest>(requests), ctx)),
        };

        private static async IAsyncEnumerable<T> CastAsyncEnumerable<T>(IAsyncEnumerable<IMessage> source)
            where T : IMessage
        {
            await foreach (var item in source)
            {
                yield return (T)item;
            }
        }
    }
}
