using System;
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
using ConnectNet.Pooling;

namespace ConnectNet.Tests;

public class GetUnaryTests
{
    private (TestServer server, ConnectChannel channel) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<GetGreeterService>();
        var app = builder.Build();
        app.MapConnectService<GetGreeterService>(GetGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = ConnectChannel.ForAddress(server.BaseAddress.ToString(), new() { HttpClient = httpClient });
        return (server, channel);
    }

    [Fact]
    public async Task GetUnary_Success()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var options = new CallOptions { UseGet = true };
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "GET-Test" },
                options);
            Assert.Equal("Hello GET-Test", response.Message);
        }
    }

    [Fact]
    public async Task GetUnary_RawHttp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<GetGreeterService>();
        var app = builder.Build();
        app.MapConnectService<GetGreeterService>(GetGreeterServiceDefinition.Instance);
        app.Start();

        using var server = app.GetTestServer();
        var httpClient = server.CreateClient();

        // Manually build a GET request with query params
        var requestMsg = new HelloRequest { Name = "RawGet" };
        var codec = new ProtobufCodec();
        var body = codec.SerializeToArray(requestMsg);
        var messageEncoded = Convert.ToBase64String(body)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var url = $"/example.GreeterService/SayHello?encoding=proto&message={Uri.EscapeDataString(messageEncoded)}&base64=1&connect=v1";
        var httpResponse = await httpClient.GetAsync(url);

        Assert.True(httpResponse.IsSuccessStatusCode, $"Status: {httpResponse.StatusCode}");
        Assert.Equal("application/proto", httpResponse.Content.Headers.ContentType?.MediaType);

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync();
        var response = HelloResponse.Parser.ParseFrom(responseBytes);
        Assert.Equal("Hello RawGet", response.Message);
    }

    [Fact]
    public async Task GetUnary_Error()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var options = new CallOptions { UseGet = true };
            var ex = await Assert.ThrowsAsync<ConnectException>(() =>
                channel.UnaryAsync<HelloRequest, HelloResponse>(
                    "/example.GreeterService/SayHello",
                    new HelloRequest { Name = "" },
                    options));
            Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
        }
    }

    // --- Test service ---

    private class GetGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name is required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }
    }

    private class GetGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static GetGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((GetGreeterService)service)
                    .SayHello((HelloRequest)req, ctx),
                isNoSideEffects: true)
        };
    }
}
