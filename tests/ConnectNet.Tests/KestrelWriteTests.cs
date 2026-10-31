using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// The response write path over real Kestrel sockets: TestServer's body pipe does not enforce
/// Kestrel's GetSpan/Advance ordering or show when bytes actually reach the client.
/// </summary>
public class KestrelWriteTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private int _http1Port;
    private int _http2Port;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1);
            o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddConnectServices(o => o.CompressMinBytes = 1024);
        builder.Services.AddSingleton<WriteGreeter>();
        _app = builder.Build();
        _app.MapConnectService<WriteGreeter>(GreeterServiceDefinition.Instance);
        await _app.StartAsync();

        var ports = _app.Urls.Select(u => new Uri(u).Port).ToArray();
        (_http1Port, _http2Port) = (ports[0], ports[1]);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private (GreeterServiceClient client, IDisposable lifetime) Client(bool http2, ICodec? codec = null)
    {
        HttpMessageHandler handler = new SocketsHttpHandler();
        if (http2) handler = new PriorKnowledgeHandler(handler);
        var channel = ConnectChannel.ForAddress(
            $"http://127.0.0.1:{(http2 ? _http2Port : _http1Port)}",
            new() { HttpHandler = handler, Codec = codec });
        return (new GreeterServiceClient(channel), channel);
    }

    public static IEnumerable<object[]> Transports => new[] { new object[] { false }, new object[] { true } };

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ServerStream_EachMessageReachesTheClientBeforeTheNextIsProduced(bool http2)
    {
        var (client, lifetime) = Client(http2);
        using (lifetime)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            WriteGreeter.Gates[nameof(http2) + http2] = gate;

            await using var stream = client.SayHelloStreamAsync(new HelloRequest { Name = nameof(http2) + http2 })
                .GetAsyncEnumerator();

            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            gate.SetResult();
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("second", stream.Current.Message);
            Assert.False(await stream.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ServerStream_LargeAndConcurrentMessagesSurvive(bool http2)
    {
        var (client, lifetime) = Client(http2);
        using (lifetime)
        {
            var name = "size:" + new string('a', 200_000);
            await Task.WhenAll(Enumerable.Range(0, 16).Select(async _ =>
            {
                var seen = 0;
                await foreach (var reply in client.SayHelloStreamAsync(new HelloRequest { Name = name }))
                {
                    Assert.Equal(name, reply.Message);
                    seen++;
                }
                Assert.Equal(3, seen);
            }));
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task ServerStream_Json_RoundTrips(bool http2)
    {
        var (client, lifetime) = Client(http2, new JsonCodec());
        using (lifetime)
        {
            var seen = 0;
            await foreach (var reply in client.SayHelloStreamAsync(new HelloRequest { Name = "size:json" }))
            {
                Assert.Equal("size:json", reply.Message);
                seen++;
            }
            Assert.Equal(3, seen);
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Unary_SmallAndLargeMessagesSurviveConcurrency(bool http2)
    {
        var (client, lifetime) = Client(http2);
        using (lifetime)
        {
            await Task.WhenAll(Enumerable.Range(0, 64).Select(async i =>
            {
                var name = i % 2 == 0 ? "small" : new string('a', 200_000);
                var reply = await client.SayHelloAsync(new HelloRequest { Name = name });
                Assert.Equal(name, reply.Message);
            }));
        }
    }

    [Theory]
    [MemberData(nameof(Transports))]
    public async Task Unary_EmptyMessage_Succeeds(bool http2)
    {
        var (client, lifetime) = Client(http2);
        using (lifetime)
        {
            var reply = await client.SayHelloAsync(new HelloRequest());
            Assert.Equal("", reply.Message);
        }
    }

    [Theory]
    [InlineData("small", null)]
    [InlineData("small", "gzip")]
    [InlineData("large", null)]
    [InlineData("large", "gzip")]
    public async Task Unary_Http1_StatesContentLength(string size, string? acceptEncoding)
    {
        var name = size == "small" ? "x" : new string('a', 8192);
        using var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.None });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_http1Port}/example.GreeterService/SayHello")
        {
            Content = new ByteArrayContent(new HelloRequest { Name = name }.ToByteArray()),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        if (acceptEncoding != null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.TransferEncodingChunked);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        Assert.Equal(size == "large" && acceptEncoding != null, response.Content.Headers.ContentEncoding.Contains("gzip"));
    }

    [Fact]
    public async Task Unary_ErrorAfterChoosingLength_IsNotTruncated()
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_http1Port}/example.GreeterService/SayHello")
        {
            Content = new ByteArrayContent(new byte[] { 0xFF }),
        };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_argument", body);
    }

    private sealed class PriorKnowledgeHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            return base.SendAsync(request, ct);
        }
    }

    private sealed class WriteGreeter : GreeterServiceBase
    {
        public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> Gates = new();

        public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = request.Name });

        public override async IAsyncEnumerable<HelloResponse> SayHelloStream(HelloRequest request, ConnectContext context, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Gates.TryGetValue(request.Name, out var gate))
            {
                yield return new HelloResponse { Message = "first" };
                await gate.Task.WaitAsync(ct);
                yield return new HelloResponse { Message = "second" };
                yield break;
            }
            for (var i = 0; i < 3; i++)
            {
                yield return new HelloResponse { Message = request.Name };
            }
        }
    }
}
