using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Client;
using ConnectNet.Tests.Proto;
using Xunit;
using Xunit.Abstractions;

namespace ConnectNet.IntegrationTests;

public class GoServerFixture : IAsyncLifetime
{
    private Process? _process;
    public int Port { get; } = 18080;
    public string BaseUrl => $"http://localhost:{Port}";

    public async Task InitializeAsync()
    {
        // Find the testserver directory relative to the test assembly
        var testServerDir = FindTestServerDir();

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "go",
                Arguments = "run .",
                WorkingDirectory = testServerDir,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["PORT"] = Port.ToString() }
            }
        };

        _process.Start();

        // Wait for the server to be ready by polling
        using var httpClient = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/example.GreeterService/SayHello");
                request.Content = new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
                request.Headers.Add("Connect-Protocol-Version", "1");
                var response = await httpClient.SendAsync(request);
                // Any response (even error) means server is up
                return;
            }
            catch
            {
                await Task.Delay(200);
            }
        }

        throw new Exception("Go test server failed to start within 30 seconds");
    }

    public Task DisposeAsync()
    {
        if (_process != null && !_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        _process?.Dispose();
        return Task.CompletedTask;
    }

    private static string FindTestServerDir()
    {
        // Walk up from the current directory to find the testserver
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "tests", "ConnectNet.IntegrationTests", "testserver");
            if (Directory.Exists(candidate))
                return candidate;
            // Also check if we're already in the test project output dir
            candidate = Path.Combine(dir, "testserver");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Exception("Could not find testserver directory");
    }
}

[Trait("Category", "Integration")]
[CollectionDefinition("GoServer")]
public class GoServerCollection : ICollectionFixture<GoServerFixture> { }

[Trait("Category", "Integration")]
[Collection("GoServer")]
public class InteropTests
{
    private readonly GoServerFixture _fixture;
    private readonly HttpClient _httpClient;

    public InteropTests(GoServerFixture fixture)
    {
        _fixture = fixture;
        _httpClient = new HttpClient();
    }

    [Fact]
    public async Task Interop_Unary_Proto()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello",
            new HelloRequest { Name = "World" });

        Assert.Equal("Hello World from connect-go", response.Message);
    }

    [Fact]
    public async Task Interop_Unary_Json()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl, codec: new JsonCodec());
        var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello",
            new HelloRequest { Name = "JSON" });

        Assert.Equal("Hello JSON from connect-go", response.Message);
    }

    [Fact]
    public async Task Interop_Unary_Error()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await channel.UnaryAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHello",
                new HelloRequest { Name = "" });
        });

        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
    }

    [Fact]
    public async Task Interop_ServerStream()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        var messages = new List<string>();

        await foreach (var response in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHelloStream",
            new HelloRequest { Name = "Stream" }))
        {
            messages.Add(response.Message);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal("Hello Stream 1", messages[0]);
        Assert.Equal("Hello Stream 2", messages[1]);
        Assert.Equal("Hello Stream 3", messages[2]);
    }

    [Fact]
    public async Task Interop_Unary_GetRequest()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        var response = await channel.UnaryAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHello",
            new HelloRequest { Name = "GetTest" },
            new CallOptions { UseGet = true });

        Assert.Equal("Hello GetTest from connect-go", response.Message);
    }

    [Fact]
    public async Task Interop_ClientStream()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/CollectHellos");

        await call.SendAsync(new HelloRequest { Name = "Alice" });
        await call.SendAsync(new HelloRequest { Name = "Bob" });
        await call.SendAsync(new HelloRequest { Name = "Charlie" });

        var response = await call.CloseAndReceiveAsync();
        Assert.Equal("Hello Alice, Bob, Charlie", response.Message);
    }

    [Fact]
    public async Task Interop_BidiStream()
    {
        var channel = new ConnectChannel(_httpClient, _fixture.BaseUrl);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/Chat");

        await call.SendAsync(new HelloRequest { Name = "alice" });
        await call.SendAsync(new HelloRequest { Name = "bob" });

        var messages = new List<string>();
        await foreach (var response in call.CompleteAndReadAsync())
        {
            messages.Add(response.Message);
        }

        Assert.Equal(2, messages.Count);
        Assert.Equal("Hello ALICE", messages[0]);
        Assert.Equal("Hello BOB", messages[1]);
    }
}
