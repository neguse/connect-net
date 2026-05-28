using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using ConnectNet.Client;
using ConnectNet.Tests.Proto;
using Google.Protobuf;

namespace ConnectNet.Benchmarks;

/// <summary>
/// Client-only allocation benchmarks. Uses a mock HttpMessageHandler so HttpClient /
/// Kestrel / TestServer don't pollute the numbers; what we see here is the allocation
/// the ConnectNet client itself produces per unary call.
/// </summary>
[MemoryDiagnoser]
public class ClientOnlyBenchmarks
{
    private static byte[] _responseBytes = null!;
    private ConnectChannel _channel = null!;
    private HelloRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        _responseBytes = new HelloResponse { Message = "ok" }.ToByteArray();
        var handler = new InstantHandler();
        _channel = ConnectChannel.ForAddress("http://example.com", new() { HttpHandler = handler });
        _request = new HelloRequest { Name = "a" };
    }

    [GlobalCleanup]
    public void Cleanup() => _channel.Dispose();

    /// <summary>The minimum-feature unary call: no CallOptions, no headers, no timeout.</summary>
    [Benchmark]
    public async Task<HelloResponse> UnaryAsync_Minimal()
    {
        return await _channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello", _request);
    }

    /// <summary>
    /// Theoretical floor: codec round-trip with no HTTP layer at all. What's left here is
    /// just the response-message object plus the codec's own pooled scratch buffers.
    /// </summary>
    [Benchmark]
    public HelloResponse PureCodec_RoundTrip()
    {
        var codec = new ProtobufCodec();
        using var requestWriter = new ConnectNet.Pooling.ArrayPoolBufferWriter();
        codec.Serialize(_request, requestWriter);
        return codec.Deserialize<HelloResponse>(_responseBytes);
    }

    private sealed class InstantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_responseBytes)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/proto") }
                }
            };
            return Task.FromResult(response);
        }
    }
}
