using System.Net.Http;
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
public class ChannelBenchmarks
{
    private TestServer _server = null!;
    private HttpClient _httpClient = null!;
    private ConnectChannel _channel = null!;
    private HelloRequest _request = null!;

    [Params(16, 256, 4096)]
    public int PayloadBytes;

    [GlobalSetup]
    public void Setup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<BenchGreeterService>();
        var app = builder.Build();
        app.MapConnectService<BenchGreeterService>(BenchGreeterServiceDefinition.Instance);
        app.Start();

        _server = app.GetTestServer();
        _httpClient = _server.CreateClient();
        _channel = ConnectChannel.ForAddress(_server.BaseAddress.ToString(), new() { HttpClient = _httpClient });
        _request = new HelloRequest { Name = new string('a', PayloadBytes) };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _channel.Dispose();
        _httpClient.Dispose();
        _server.Dispose();
    }

    [Benchmark]
    public async Task<HelloResponse> UnaryAsync()
    {
        return await _channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello", _request);
    }

    // Pure server-side handler invocation (no HTTP) — measures per-RPC alloc baseline.
    private static readonly BenchGreeterService _direct = new();

    [Benchmark]
    public Task<HelloResponse> DirectHandler() => _direct.SayHello(_request, new ConnectContext());

    private class BenchGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = "Hello " + request.Name });
    }

    private class BenchGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static BenchGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((BenchGreeterService)service).SayHello((HelloRequest)req, ctx))
        };
    }
}
