using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Client;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Reflection.V1;
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

    private static (Microsoft.AspNetCore.TestHost.TestServer server, ConnectChannel channel) CreateSetup(
        Action<WebApplication>? beforeMap = null, ICodec? codec = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ReflectionGreeterService>();
        var app = builder.Build();
        app.MapConnectService<ReflectionGreeterService>(GreeterServiceDefinition.Instance);
        beforeMap?.Invoke(app);
        app.MapConnectReflection();
        app.Start();

        var server = app.GetTestServer();
        // Bidi streaming requires HTTP/2; TestServer requests default to HTTP/1.1,
        // so upgrade the request version at the message-handler level.
        var httpClient = new HttpClient(new ServerHardeningTests.Http2VersionHandler(server.CreateHandler()))
        {
            BaseAddress = server.BaseAddress
        };
        var channel = ConnectChannel.ForAddress(server.BaseAddress.ToString(), new()
        {
            HttpClient = httpClient,
            Codec = codec
        });
        return (server, channel);
    }

    private static async Task<ServerReflectionResponse> RoundTripAsync(
        ConnectChannel channel, ServerReflectionRequest request,
        string procedure = "/grpc.reflection.v1.ServerReflection/ServerReflectionInfo")
    {
        using var call = channel.BidiStreamAsync<ServerReflectionRequest, ServerReflectionResponse>(procedure);
        await call.SendAsync(request);
        ServerReflectionResponse? response = null;
        await foreach (var msg in call.CompleteAndReadAsync())
        {
            response = msg;
        }
        Assert.NotNull(response);
        return response!;
    }

    // --- Standard gRPC Server Reflection over the Connect protocol ---

    [Fact]
    public async Task ServerReflection_ListServices()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                Host = "example.com",
                ListServices = ""
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ListServicesResponse,
                response.MessageResponseCase);
            var names = response.ListServicesResponse.Service.Select(s => s.Name).ToList();
            Assert.Contains("example.GreeterService", names);
            // Reflection lists itself (both the v1 service and the v1alpha alias).
            Assert.Contains("grpc.reflection.v1.ServerReflection", names);
            Assert.Contains("grpc.reflection.v1alpha.ServerReflection", names);

            // valid_host / original_request must echo the request per the spec.
            Assert.Equal("example.com", response.ValidHost);
            Assert.Equal(ServerReflectionRequest.MessageRequestOneofCase.ListServices,
                response.OriginalRequest.MessageRequestCase);
        }
    }

    [Fact]
    public async Task ServerReflection_FileContainingSymbol_Service()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileContainingSymbol = "example.GreeterService"
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse,
                response.MessageResponseCase);
            var files = response.FileDescriptorResponse.FileDescriptorProto
                .Select(b => FileDescriptorProto.Parser.ParseFrom(b))
                .ToList();
            Assert.Contains(files, f => f.Name == "greeter.proto");
            AssertDependencyClosure(files);
        }
    }

    [Fact]
    public async Task ServerReflection_FileContainingSymbol_Method()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileContainingSymbol = "example.GreeterService.SayHello"
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse,
                response.MessageResponseCase);
            var files = response.FileDescriptorResponse.FileDescriptorProto
                .Select(b => FileDescriptorProto.Parser.ParseFrom(b))
                .ToList();
            Assert.Contains(files, f => f.Name == "greeter.proto");
        }
    }

    [Fact]
    public async Task ServerReflection_FileContainingSymbol_IncludesTransitiveDependencies()
    {
        // validation_test.proto imports buf/validate/validate.proto and several
        // google.protobuf well-known types; the response must contain the full closure.
        var (server, channel) = CreateSetup(app =>
        {
            var registry = app.Services.GetRequiredService<ConnectReflectionService>();
            registry.AddService("validation_test.TestService", ValidationTestReflection.Descriptor);
        });
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileContainingSymbol = "validation_test.StringTestMessage"
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse,
                response.MessageResponseCase);
            var files = response.FileDescriptorResponse.FileDescriptorProto
                .Select(b => FileDescriptorProto.Parser.ParseFrom(b))
                .ToList();
            Assert.Contains(files, f => f.Name == "validation_test.proto");
            Assert.Contains(files, f => f.Name == "buf/validate/validate.proto");
            AssertDependencyClosure(files);
        }
    }

    [Fact]
    public async Task ServerReflection_FileByFilename()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileByFilename = "greeter.proto"
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse,
                response.MessageResponseCase);
            var files = response.FileDescriptorResponse.FileDescriptorProto
                .Select(b => FileDescriptorProto.Parser.ParseFrom(b))
                .ToList();
            Assert.Contains(files, f => f.Name == "greeter.proto");
            AssertDependencyClosure(files);
        }
    }

    [Fact]
    public async Task ServerReflection_UnknownSymbol_NotFound()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var request = new ServerReflectionRequest { FileContainingSymbol = "no.such.Symbol" };
            var response = await RoundTripAsync(channel, request);

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse,
                response.MessageResponseCase);
            // 5 = NOT_FOUND
            Assert.Equal(5, response.ErrorResponse.ErrorCode);
            Assert.Equal("no.such.Symbol", response.OriginalRequest.FileContainingSymbol);
        }
    }

    [Fact]
    public async Task ServerReflection_UnknownFile_NotFound()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileByFilename = "no_such_file.proto"
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse,
                response.MessageResponseCase);
            Assert.Equal(5, response.ErrorResponse.ErrorCode);
        }
    }

    [Fact]
    public async Task ServerReflection_Extensions_Unimplemented()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var byExtension = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                FileContainingExtension = new ExtensionRequest
                {
                    ContainingType = "example.HelloRequest",
                    ExtensionNumber = 100
                }
            });
            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse,
                byExtension.MessageResponseCase);
            // 12 = UNIMPLEMENTED
            Assert.Equal(12, byExtension.ErrorResponse.ErrorCode);

            var allNumbers = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                AllExtensionNumbersOfType = "example.HelloRequest"
            });
            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse,
                allNumbers.MessageResponseCase);
            Assert.Equal(12, allNumbers.ErrorResponse.ErrorCode);
        }
    }

    [Fact]
    public async Task ServerReflection_V1AlphaAlias_ListServices()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                ListServices = ""
            }, procedure: "/grpc.reflection.v1alpha.ServerReflection/ServerReflectionInfo");

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ListServicesResponse,
                response.MessageResponseCase);
            var names = response.ListServicesResponse.Service.Select(s => s.Name).ToList();
            Assert.Contains("example.GreeterService", names);
        }
    }

    [Fact]
    public async Task ServerReflection_MultipleRequestsOnOneStream()
    {
        var (server, channel) = CreateSetup();
        using (server)
        {
            using var call = channel.BidiStreamAsync<ServerReflectionRequest, ServerReflectionResponse>(
                "/grpc.reflection.v1.ServerReflection/ServerReflectionInfo");

            await call.SendAsync(new ServerReflectionRequest { ListServices = "" });
            await call.SendAsync(new ServerReflectionRequest { FileByFilename = "greeter.proto" });

            var responses = new List<ServerReflectionResponse>();
            await foreach (var msg in call.CompleteAndReadAsync())
            {
                responses.Add(msg);
            }

            Assert.Equal(2, responses.Count);
            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ListServicesResponse,
                responses[0].MessageResponseCase);
            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.FileDescriptorResponse,
                responses[1].MessageResponseCase);
        }
    }

    [Fact]
    public async Task ServerReflection_ConnectJsonCodec()
    {
        var (server, channel) = CreateSetup(codec: new JsonCodec());
        using (server)
        {
            var response = await RoundTripAsync(channel, new ServerReflectionRequest
            {
                ListServices = ""
            });

            Assert.Equal(ServerReflectionResponse.MessageResponseOneofCase.ListServicesResponse,
                response.MessageResponseCase);
            Assert.Contains(response.ListServicesResponse.Service, s => s.Name == "example.GreeterService");
        }
    }

    /// <summary>
    /// Every dependency named by a returned FileDescriptorProto must itself be present in
    /// the same response — clients rebuild the descriptor pool from this set alone.
    /// </summary>
    private static void AssertDependencyClosure(IReadOnlyList<FileDescriptorProto> files)
    {
        var names = files.Select(f => f.Name).ToHashSet();
        foreach (var file in files)
        {
            foreach (var dep in file.Dependency)
            {
                Assert.Contains(dep, names);
            }
        }
    }

    // --- Connect-native JSON discovery endpoint (unchanged) ---

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
