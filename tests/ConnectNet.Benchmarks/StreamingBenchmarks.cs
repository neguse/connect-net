using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ConnectNet.Client;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ConnectNet.Benchmarks;

[MemoryDiagnoser]
public class StreamingBenchmarks
{
    private TestServer _server = null!;
    private HttpClient _httpClient = null!;
    private ConnectChannel _channel = null!;
    private HelloRequest _request = null!;

    [Params(16, 256)]
    public int PayloadBytes;

    [Params(1, 8)]
    public int MessagesPerCall;

    [GlobalSetup]
    public void Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<StreamSvc>();
        var app = builder.Build();
        app.MapConnectService<StreamSvc>(StreamSvcDefinition.Instance);
        app.Start();

        _server = app.GetTestServer();
        _httpClient = _server.CreateClient();
        _channel = ConnectChannel.ForAddress(_server.BaseAddress.ToString(), new() { HttpClient = _httpClient });
        _request = new HelloRequest { Name = new string('a', PayloadBytes) };
        StreamSvc.MessageCount = MessagesPerCall;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _channel.Dispose();
        _httpClient.Dispose();
        _server.Dispose();
    }

    [Benchmark]
    public async Task<int> ServerStream()
    {
        int n = 0;
        await foreach (var _ in _channel.ServerStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHelloStream", _request))
        {
            n++;
        }
        return n;
    }

    [Benchmark]
    public async Task<HelloResponse> ClientStream()
    {
        using var call = _channel.ClientStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/CollectHellos");
        for (int i = 0; i < MessagesPerCall; i++)
        {
            await call.SendAsync(_request);
        }
        return await call.CloseAndReceiveAsync();
    }

    [Benchmark]
    public async Task<int> BidiStream()
    {
        using var call = _channel.BidiStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/Chat");
        for (int i = 0; i < MessagesPerCall; i++)
        {
            await call.SendAsync(_request);
        }
        int n = 0;
        await foreach (var _ in call.CompleteAndReadAsync())
        {
            n++;
        }
        return n;
    }

    private class StreamSvc
    {
        public static int MessageCount = 1;

        public async IAsyncEnumerable<HelloResponse> SayHelloStream(
            HelloRequest request, ConnectContext context,
            [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            for (int i = 0; i < MessageCount; i++)
            {
                yield return new HelloResponse { Message = "r" + i };
                await Task.Yield();
            }
        }

        public async Task<HelloResponse> CollectHellos(IAsyncEnumerable<HelloRequest> reqs, ConnectContext ctx)
        {
            int n = 0;
            await foreach (var _ in reqs) n++;
            return new HelloResponse { Message = "got " + n };
        }

        public async IAsyncEnumerable<HelloResponse> Chat(
            IAsyncEnumerable<HelloRequest> reqs, ConnectContext ctx,
            [EnumeratorCancellation] System.Threading.CancellationToken ct = default)
        {
            await foreach (var _ in reqs.WithCancellation(ct))
            {
                yield return new HelloResponse { Message = "echo" };
            }
        }
    }

    private class StreamSvcDefinition : IConnectServiceDefinition
    {
        public static StreamSvcDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloStream",
                HelloRequest.Parser,
                ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) => ((StreamSvc)service).SayHelloStream((HelloRequest)req, ctx)),
            new ConnectMethodDescriptor(
                "/example.GreeterService/CollectHellos",
                HelloRequest.Parser,
                ConnectMethodType.ClientStreaming,
                clientStreamHandler: async (service, reqs, ctx) =>
                {
                    async IAsyncEnumerable<HelloRequest> Cast(IAsyncEnumerable<IMessage> source,
                        [EnumeratorCancellation] System.Threading.CancellationToken c = default)
                    {
                        await foreach (var m in source.WithCancellation(c)) yield return (HelloRequest)m;
                    }
                    return (IMessage)await ((StreamSvc)service).CollectHellos(Cast(reqs, ctx.CancellationToken), ctx);
                }),
            new ConnectMethodDescriptor(
                "/example.GreeterService/Chat",
                HelloRequest.Parser,
                ConnectMethodType.BidiStreaming,
                bidiStreamHandler: (service, reqs, ctx) =>
                {
                    async IAsyncEnumerable<HelloRequest> Cast(IAsyncEnumerable<IMessage> source,
                        [EnumeratorCancellation] System.Threading.CancellationToken c = default)
                    {
                        await foreach (var m in source.WithCancellation(c)) yield return (HelloRequest)m;
                    }
                    async IAsyncEnumerable<IMessage> Wrap()
                    {
                        await foreach (var r in ((StreamSvc)service).Chat(Cast(reqs, ctx.CancellationToken), ctx))
                            yield return r;
                    }
                    return Wrap();
                }),
        };
    }
}
