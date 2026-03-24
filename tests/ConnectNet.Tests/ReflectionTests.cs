using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectNet;
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

public class ReflectionTests
{
    private class ReflectionGreeterService : GreeterServiceBase
    {
        public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
            => Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
    }

    [Fact]
    public async Task ListServices_Json()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ReflectionGreeterService>();
        var app = builder.Build();
        app.MapConnectService<ReflectionGreeterService>(GreeterServiceDefinition.Instance);
        app.MapConnectReflection();
        app.Start();

        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/connect/v1/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var services = doc.RootElement.GetProperty("services");
        Assert.Equal(JsonValueKind.Array, services.ValueKind);
        Assert.Contains(services.EnumerateArray(), s => s.GetString() == "example.GreeterService");
    }

    [Fact]
    public async Task ListServices_IncludesAllRegistered()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ReflectionGreeterService>();
        var app = builder.Build();
        app.MapConnectService<ReflectionGreeterService>(GreeterServiceDefinition.Instance);

        // Manually add another service name to the reflection service
        var reflection = app.Services.GetRequiredService<ConnectReflectionService>();
        reflection.AddService("another.TestService");

        app.MapConnectReflection();
        app.Start();

        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/connect/v1/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var services = doc.RootElement.GetProperty("services");

        var serviceNames = new List<string>();
        foreach (var s in services.EnumerateArray())
        {
            serviceNames.Add(s.GetString()!);
        }

        Assert.Contains("example.GreeterService", serviceNames);
        Assert.Contains("another.TestService", serviceNames);
    }

    [Fact]
    public async Task ListServices_IncludesHealthCheck()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ConnectHealthService>();
        var app = builder.Build();

        // Register health check service name via reflection
        var reflection = app.Services.GetRequiredService<ConnectReflectionService>();
        reflection.AddService("grpc.health.v1.Health");

        app.MapConnectHealthCheck();
        app.MapConnectReflection();
        app.Start();

        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/connect/v1/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var services = doc.RootElement.GetProperty("services");
        Assert.Contains(services.EnumerateArray(), s => s.GetString() == "grpc.health.v1.Health");
    }
}
