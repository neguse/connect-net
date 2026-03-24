using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
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

public class BidiStreamTests
{
    private (TestServer server, ConnectChannel channel) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<BidiStreamGreeterService>();
        var app = builder.Build();
        app.MapConnectService<BidiStreamGreeterService>(BidiStreamGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString());
        return (server, channel);
    }

    [Fact]
    public async Task BidiStream_EchoMessages()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloBidiStream");

            await call.SendAsync(new HelloRequest { Name = "Alice" });
            await call.SendAsync(new HelloRequest { Name = "Bob" });
            await call.SendAsync(new HelloRequest { Name = "Charlie" });

            var responses = new List<string>();
            await foreach (var response in call.CompleteAndReadAsync())
            {
                responses.Add(response.Message);
            }

            Assert.Equal(3, responses.Count);
            Assert.Equal("ALICE", responses[0]);
            Assert.Equal("BOB", responses[1]);
            Assert.Equal("CHARLIE", responses[2]);
        }
    }

    [Fact]
    public async Task BidiStream_Error()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloBidiStreamError");

            await call.SendAsync(new HelloRequest { Name = "Alice" });

            var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
            {
                await foreach (var _ in call.CompleteAndReadAsync())
                {
                }
            });
            Assert.Equal(ConnectCode.Internal, ex.Code);
            Assert.Equal("bidi stream error", ex.Message);
        }
    }

    // --- Test service ---

    private class BidiStreamGreeterService
    {
        public async IAsyncEnumerable<HelloResponse> SayHelloBidiStream(
            IAsyncEnumerable<HelloRequest> requests,
            ConnectContext context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var request in requests.WithCancellation(ct))
            {
                yield return new HelloResponse { Message = request.Name.ToUpperInvariant() };
            }
        }

        public async IAsyncEnumerable<HelloResponse> SayHelloBidiStreamError(
            IAsyncEnumerable<HelloRequest> requests,
            ConnectContext context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var _ in requests.WithCancellation(ct))
            {
            }
            throw new ConnectException(ConnectCode.Internal, "bidi stream error");
            yield break; // unreachable, required for async iterator
        }
    }

    private class BidiStreamGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static BidiStreamGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloBidiStream",
                HelloRequest.Parser,
                ConnectMethodType.BidiStreaming,
                bidiStreamHandler: (service, requests, ctx) =>
                    CastAsyncEnumerable<HelloResponse>(
                        ((BidiStreamGreeterService)service).SayHelloBidiStream(
                            CastAsyncEnumerable<HelloRequest>(requests), ctx))),
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloBidiStreamError",
                HelloRequest.Parser,
                ConnectMethodType.BidiStreaming,
                bidiStreamHandler: (service, requests, ctx) =>
                    CastAsyncEnumerable<HelloResponse>(
                        ((BidiStreamGreeterService)service).SayHelloBidiStreamError(
                            CastAsyncEnumerable<HelloRequest>(requests), ctx))),
        };

        private static async IAsyncEnumerable<T> CastAsyncEnumerable<T>(IAsyncEnumerable<IMessage> source)
            where T : IMessage
        {
            await foreach (var item in source)
            {
                yield return (T)item;
            }
        }

        private static async IAsyncEnumerable<IMessage> CastAsyncEnumerable<T>(IAsyncEnumerable<T> source)
            where T : IMessage
        {
            await foreach (var item in source)
            {
                yield return item;
            }
        }
    }
}
