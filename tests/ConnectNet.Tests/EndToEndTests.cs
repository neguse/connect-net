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

public class EndToEndTests
{
    private (TestServer server, ConnectChannel channel) CreateSetup()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<E2EGreeterService>();
        var app = builder.Build();
        app.MapConnectService<E2EGreeterService>(E2EGreeterServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = ConnectChannel.ForAddress(server.BaseAddress.ToString(), new() { HttpClient = httpClient });
        return (server, channel);
    }

    [Fact]
    public async Task UnaryRpc_ClientToServer_Success()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "E2E" });
            Assert.Equal("Hello E2E", response.Message);
        }
    }

    [Fact]
    public async Task UnaryRpc_ClientToServer_Error()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var ex = await Assert.ThrowsAsync<ConnectException>(() =>
                channel.UnaryAsync<HelloRequest, HelloResponse>(
                    "/example.GreeterService/SayHello",
                    new HelloRequest { Name = "" }));
            Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
        }
    }

    [Fact]
    public async Task UnaryRpc_ClientToServer_WithTimeout()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var options = new CallOptions { Timeout = System.TimeSpan.FromSeconds(10) };
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "timeout-test" },
                options);
            Assert.Equal("Hello timeout-test", response.Message);
        }
    }

    // --- Test service ---

    private class E2EGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name is required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }
    }

    private class E2EGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static E2EGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((E2EGreeterService)service)
                    .SayHello((HelloRequest)req, ctx))
        };
    }
}
