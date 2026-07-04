using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectNet.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace ConnectNet.Tests;

public class HealthCheckTests
{
    private (TestServer server, ConnectHealthService healthService) CreateTestServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ConnectHealthService>();
        var app = builder.Build();
        app.MapConnectHealthCheck();
        app.Start();

        var server = app.GetTestServer();
        var healthService = server.Services.GetRequiredService<ConnectHealthService>();
        return (server, healthService);
    }

    [Fact]
    public async Task HealthCheck_DefaultServing()
    {
        var (server, _) = CreateTestServer();
        using (server)
        using (var client = server.CreateClient())
        {
            // Empty protobuf request (no fields set = empty service name)
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            httpRequest.Content = new ByteArrayContent(System.Array.Empty<byte>());
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");

            var response = await client.SendAsync(httpRequest);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            // HealthCheckResponse with status=SERVING(1): tag 0x08, varint 1
            Assert.Equal(new byte[] { 0x08, 0x01 }, bytes);
        }
    }

    [Fact]
    public async Task HealthCheck_SpecificService()
    {
        var (server, healthService) = CreateTestServer();
        using (server)
        using (var client = server.CreateClient())
        {
            healthService.SetStatus("my.service", HealthStatus.NotServing);

            // Encode protobuf request: field 1 (tag=0x0A), length, "my.service"
            var serviceBytes = Encoding.UTF8.GetBytes("my.service");
            var requestBytes = new byte[2 + serviceBytes.Length];
            requestBytes[0] = 0x0A; // field 1, wire type 2
            requestBytes[1] = (byte)serviceBytes.Length;
            System.Array.Copy(serviceBytes, 0, requestBytes, 2, serviceBytes.Length);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            httpRequest.Content = new ByteArrayContent(requestBytes);
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");

            var response = await client.SendAsync(httpRequest);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            // HealthCheckResponse with status=NOT_SERVING(2): tag 0x08, varint 2
            Assert.Equal(new byte[] { 0x08, 0x02 }, bytes);
        }
    }

    [Fact]
    public async Task HealthCheck_UnknownService()
    {
        var (server, _) = CreateTestServer();
        using (server)
        using (var client = server.CreateClient())
        {
            // Encode "unknown.service"
            var serviceBytes = Encoding.UTF8.GetBytes("unknown.service");
            var requestBytes = new byte[2 + serviceBytes.Length];
            requestBytes[0] = 0x0A;
            requestBytes[1] = (byte)serviceBytes.Length;
            System.Array.Copy(serviceBytes, 0, requestBytes, 2, serviceBytes.Length);

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            httpRequest.Content = new ByteArrayContent(requestBytes);
            httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");

            var response = await client.SendAsync(httpRequest);

            // Per the gRPC Health protocol, Check must fail with NOT_FOUND for unknown
            // services (SERVICE_UNKNOWN is reserved for Watch).
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task HealthCheck_JsonFormat()
    {
        var (server, _) = CreateTestServer();
        using (server)
        using (var client = server.CreateClient())
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/grpc.health.v1.Health/Check");
            httpRequest.Content = new StringContent("{\"service\":\"\"}", Encoding.UTF8, "application/json");
            httpRequest.Headers.Add("Connect-Protocol-Version", "1");

            var response = await client.SendAsync(httpRequest);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("SERVING", doc.RootElement.GetProperty("status").GetString());
        }
    }
}
