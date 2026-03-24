using System;
using System.Collections.Generic;
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

public class InterceptorTests
{
    // --- Client interceptor that adds a header ---
    private class AddHeaderInterceptor : IClientInterceptor
    {
        private readonly string _key;
        private readonly string _value;

        public AddHeaderInterceptor(string key, string value)
        {
            _key = key;
            _value = value;
        }

        public async Task<IMessage> InterceptUnaryAsync(
            UnaryRequestContext context,
            Func<UnaryRequestContext, Task<IMessage>> next,
            CancellationToken ct)
        {
            context.Headers[_key] = _value;
            return await next(context);
        }
    }

    // --- Server interceptor that modifies response ---
    private class PrefixResponseInterceptor : IServerInterceptor
    {
        private readonly string _prefix;

        public PrefixResponseInterceptor(string prefix)
        {
            _prefix = prefix;
        }

        public async Task<IMessage> InterceptUnaryAsync(
            UnaryServerContext context,
            Func<UnaryServerContext, Task<IMessage>> next)
        {
            var response = await next(context);
            if (response is HelloResponse hr)
            {
                return new HelloResponse { Message = _prefix + hr.Message };
            }
            return response;
        }
    }

    // --- Service that echoes headers ---
    private class HeaderEchoService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

            var message = $"Hello {request.Name}";
            if (context.RequestHeaders.TryGetValue("X-Custom-Header", out var val))
            {
                message = $"Hello {request.Name} [X-Custom-Header={val}]";
            }
            return Task.FromResult(new HelloResponse { Message = message });
        }
    }

    private class HeaderEchoServiceDefinition : IConnectServiceDefinition
    {
        public static HeaderEchoServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((HeaderEchoService)service)
                    .SayHello((HelloRequest)req, ctx))
        };
    }

    // --- Order-tracking interceptor ---
    private class OrderTrackingClientInterceptor : IClientInterceptor
    {
        private readonly List<string> _log;
        private readonly string _name;

        public OrderTrackingClientInterceptor(string name, List<string> log)
        {
            _name = name;
            _log = log;
        }

        public async Task<IMessage> InterceptUnaryAsync(
            UnaryRequestContext context,
            Func<UnaryRequestContext, Task<IMessage>> next,
            CancellationToken ct)
        {
            _log.Add($"{_name}-before");
            var result = await next(context);
            _log.Add($"{_name}-after");
            return result;
        }
    }

    private (TestServer server, ConnectChannel channel) CreateSetup(
        ConnectChannelOptions? clientOptions = null,
        Action<ConnectServerOptions>? configureServer = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices(configureServer);
        builder.Services.AddSingleton<HeaderEchoService>();
        var app = builder.Build();
        app.MapConnectService<HeaderEchoService>(HeaderEchoServiceDefinition.Instance);
        app.Start();

        var server = app.GetTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString(), channelOptions: clientOptions);
        return (server, channel);
    }

    [Fact]
    public async Task ClientInterceptor_AddsHeader()
    {
        var clientOptions = new ConnectChannelOptions();
        clientOptions.Interceptors.Add(new AddHeaderInterceptor("X-Custom-Header", "intercepted-value"));

        var (server, channel) = CreateSetup(clientOptions);
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "World" });

            Assert.Equal("Hello World [X-Custom-Header=intercepted-value]", response.Message);
        }
    }

    [Fact]
    public async Task ServerInterceptor_ModifiesResponse()
    {
        var (server, channel) = CreateSetup(
            configureServer: opts =>
            {
                opts.Interceptors.Add(new PrefixResponseInterceptor("[wrapped] "));
            });
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "World" });

            Assert.Equal("[wrapped] Hello World", response.Message);
        }
    }

    [Fact]
    public async Task MultipleInterceptors_ExecuteInOrder()
    {
        var log = new List<string>();
        var clientOptions = new ConnectChannelOptions();
        clientOptions.Interceptors.Add(new OrderTrackingClientInterceptor("first", log));
        clientOptions.Interceptors.Add(new OrderTrackingClientInterceptor("second", log));

        var (server, channel) = CreateSetup(clientOptions);
        using (server)
        {
            var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "Order" });

            Assert.Equal("Hello Order", response.Message);
            Assert.Equal(new List<string> { "first-before", "second-before", "second-after", "first-after" }, log);
        }
    }
}
