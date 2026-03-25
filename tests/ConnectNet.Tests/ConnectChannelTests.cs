using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Client;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Xunit;

namespace ConnectNet.Tests;

public class ConnectChannelTests
{
    [Fact]
    public async Task UnaryAsync_Success_DeserializesResponse()
    {
        var expectedResponse = new HelloResponse { Message = "Hello test" };
        var handler = new MockHttpHandler((request) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/example.GreeterService/SayHello", request.RequestUri!.AbsolutePath);
            Assert.Equal("application/proto", request.Content!.Headers.ContentType!.MediaType);
            Assert.True(request.Headers.Contains("Connect-Protocol-Version"));
            Assert.Equal("1", request.Headers.GetValues("Connect-Protocol-Version").First());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedResponse.ToByteArray())
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto") }
                }
            };
        });

        var channel = new ConnectChannel(new HttpClient(handler), "https://example.com");
        var result = await channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello",
            new HelloRequest { Name = "test" });

        Assert.Equal("Hello test", result.Message);
    }

    [Fact]
    public async Task UnaryAsync_ErrorResponse_ThrowsConnectException()
    {
        var handler = new MockHttpHandler((_) =>
        {
            var errorJson = new ConnectException(ConnectCode.NotFound, "not found").ToJson();
            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(errorJson, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var channel = new ConnectChannel(new HttpClient(handler), "https://example.com");
        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "test" }));

        Assert.Equal(ConnectCode.NotFound, ex.Code);
    }

    [Fact]
    public async Task UnaryAsync_WithTimeout_SetsHeader()
    {
        TimeSpan? capturedTimeout = null;
        var handler = new MockHttpHandler((request) =>
        {
            if (request.Headers.TryGetValues("Connect-Timeout-Ms", out var values))
            {
                capturedTimeout = TimeSpan.FromMilliseconds(long.Parse(values.First()));
            }
            var response = new HelloResponse { Message = "ok" };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(response.ToByteArray())
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto") }
                }
            };
        });

        var channel = new ConnectChannel(new HttpClient(handler), "https://example.com");
        await channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello",
            new HelloRequest { Name = "test" },
            new CallOptions { Timeout = TimeSpan.FromSeconds(5) });

        Assert.NotNull(capturedTimeout);
        Assert.Equal(5000, capturedTimeout!.Value.TotalMilliseconds);
    }

    [Fact]
    public void ParseErrorResponse_ValidConnectJson_UsesJsonCode()
    {
        var error = ConnectChannel.ParseErrorResponse(
            "{\"code\":\"not_found\",\"message\":\"gone\"}", 500);
        Assert.Equal(ConnectCode.NotFound, error.Code);
        Assert.Equal("gone", error.Message);
    }

    [Fact]
    public void ParseErrorResponse_InvalidJson_UsesHttpStatus()
    {
        var error = ConnectChannel.ParseErrorResponse("not json", 401);
        Assert.Equal(ConnectCode.Unauthenticated, error.Code);
    }

    [Fact]
    public void ParseErrorResponse_EmptyBody_UsesHttpStatus()
    {
        var error = ConnectChannel.ParseErrorResponse("", 503);
        Assert.Equal(ConnectCode.Unavailable, error.Code);
    }

    [Fact]
    public void ParseErrorResponse_NullJsonValue_UsesHttpStatus()
    {
        var error = ConnectChannel.ParseErrorResponse("null", 404);
        Assert.Equal(ConnectCode.Unimplemented, error.Code);
    }

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
