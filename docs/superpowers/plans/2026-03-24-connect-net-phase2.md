# connect-net Phase 2: Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect ProtocolのStreaming RPC（Server Streaming, Client Streaming, Bidirectional）をクライアント・サーバー両方に追加する。

**Architecture:** Phase 1のUnary実装に、5バイトエンベロープ処理（Envelope）を追加し、ストリーミング用のクライアントメソッド（ServerStreamAsync等）とサーバーハンドラ（ConnectServerStreamHandler等）を実装する。ストリーミングの終端はflag 0x02のEndStreamエンベロープ（JSON metadata/error）で表現する。

**Tech Stack:** C# / .NET Standard 2.1 / .NET 10 / ASP.NET Core / Google.Protobuf / xUnit

**Spec:** `docs/superpowers/specs/2026-03-24-connect-net-design.md`

---

## File Structure (Phase 2 additions)

```
src/
├── ConnectNet/
│   └── Envelope.cs                          # 5バイトエンベロープ読み書き (NEW)
│
├── ConnectNet.Client/
│   └── ConnectChannel.cs                    # ServerStreamAsync, ClientStreamAsync, BidiStreamAsync 追加 (MODIFY)
│
└── ConnectNet.Server/
    ├── ConnectServerStreamHandler.cs        # Server Streaming RPCハンドラ (NEW)
    ├── ConnectClientStreamHandler.cs        # Client Streaming RPCハンドラ (NEW)
    ├── ConnectBidiStreamHandler.cs          # Bidi Streaming RPCハンドラ (NEW)
    ├── ConnectServiceDescriptor.cs          # MethodType追加 (MODIFY)
    └── ConnectServiceExtensions.cs          # Streaming対応 (MODIFY)

tests/
└── ConnectNet.Tests/
    ├── EnvelopeTests.cs                     # エンベロープ単体テスト (NEW)
    ├── ServerStreamTests.cs                 # Server Streaming E2Eテスト (NEW)
    ├── ClientStreamTests.cs                 # Client Streaming E2Eテスト (NEW)
    └── BidiStreamTests.cs                   # Bidi Streaming E2Eテスト (NEW)
```

---

### Task 1: Envelope 読み書き

**Files:**
- Create: `src/ConnectNet/Envelope.cs`
- Create: `tests/ConnectNet.Tests/EnvelopeTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/EnvelopeTests.cs`:
```csharp
using System.IO;
using System.Threading.Tasks;
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class EnvelopeTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        using var stream = new MemoryStream();

        await Envelope.WriteAsync(stream, 0x00, data);
        stream.Position = 0;

        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Equal(0x00, result!.Value.flags);
        Assert.Equal(data, result.Value.data);
    }

    [Fact]
    public async Task WriteAndRead_CompressedFlag()
    {
        var data = new byte[] { 10, 20, 30 };
        using var stream = new MemoryStream();

        await Envelope.WriteAsync(stream, Envelope.FlagCompressed, data);
        stream.Position = 0;

        var result = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagCompressed, result!.Value.flags);
        Assert.Equal(data, result.Value.data);
    }

    [Fact]
    public async Task WriteAndRead_EndStreamFlag()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("{\"metadata\":{}}");
        using var stream = new MemoryStream();

        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, data);
        stream.Position = 0;

        var result = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, result!.Value.flags);
    }

    [Fact]
    public async Task WriteAndRead_EmptyData()
    {
        using var stream = new MemoryStream();

        await Envelope.WriteAsync(stream, 0x00, System.Array.Empty<byte>());
        stream.Position = 0;

        var result = await Envelope.ReadAsync(stream);
        Assert.NotNull(result);
        Assert.Empty(result!.Value.data);
    }

    [Fact]
    public async Task Read_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        var result = await Envelope.ReadAsync(stream);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteAndRead_MultipleMessages()
    {
        using var stream = new MemoryStream();

        await Envelope.WriteAsync(stream, 0x00, new byte[] { 1 });
        await Envelope.WriteAsync(stream, 0x00, new byte[] { 2 });
        await Envelope.WriteAsync(stream, Envelope.FlagEndStream, new byte[] { 3 });

        stream.Position = 0;

        var r1 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 1 }, r1!.Value.data);

        var r2 = await Envelope.ReadAsync(stream);
        Assert.Equal(new byte[] { 2 }, r2!.Value.data);

        var r3 = await Envelope.ReadAsync(stream);
        Assert.Equal(Envelope.FlagEndStream, r3!.Value.flags);

        var r4 = await Envelope.ReadAsync(stream);
        Assert.Null(r4);
    }

    [Fact]
    public void EnvelopeHeader_BigEndian()
    {
        // 5-byte header: [flags(1)][length(4 big-endian)]
        // For data length 256 (0x00000100), bytes should be 00 00 01 00
        var stream = new MemoryStream();
        Envelope.WriteAsync(stream, 0x00, new byte[256]).Wait();

        var bytes = stream.ToArray();
        Assert.Equal(0x00, bytes[0]); // flags
        Assert.Equal(0x00, bytes[1]); // length MSB
        Assert.Equal(0x00, bytes[2]);
        Assert.Equal(0x01, bytes[3]);
        Assert.Equal(0x00, bytes[4]); // length LSB
        Assert.Equal(261, bytes.Length); // 5 header + 256 data
    }
}
```

- [ ] **Step 2: テスト失敗を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter EnvelopeTests -v m`

- [ ] **Step 3: Envelope実装**

`src/ConnectNet/Envelope.cs`:
```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectNet;

public static class Envelope
{
    public const byte FlagCompressed = 0x01;
    public const byte FlagEndStream = 0x02;

    public static async Task WriteAsync(Stream stream, byte flags, byte[] data, CancellationToken ct = default)
    {
        var header = new byte[5];
        header[0] = flags;
        var length = data.Length;
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;

        await stream.WriteAsync(header, 0, 5, ct).ConfigureAwait(false);
        if (data.Length > 0)
        {
            await stream.WriteAsync(data, 0, data.Length, ct).ConfigureAwait(false);
        }
    }

    public static async Task<(byte flags, byte[] data)?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        var bytesRead = 0;
        while (bytesRead < 5)
        {
            var n = await stream.ReadAsync(header, bytesRead, 5 - bytesRead, ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (bytesRead == 0) return null; // clean EOF
                throw new ConnectException(ConnectCode.Internal, "incomplete envelope header");
            }
            bytesRead += n;
        }

        var flags = header[0];
        var length = (header[1] << 24) | (header[2] << 16) | (header[3] << 8) | header[4];

        var data = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            var n = await stream.ReadAsync(data, bytesRead, length - bytesRead, ct).ConfigureAwait(false);
            if (n == 0)
                throw new ConnectException(ConnectCode.Internal, "incomplete envelope data");
            bytesRead += n;
        }

        return (flags, data);
    }
}
```

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter EnvelopeTests -v m`
Expected: 7 tests passed

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet/Envelope.cs tests/ConnectNet.Tests/EnvelopeTests.cs
git commit -m "feat: add Envelope for 5-byte message framing (Connect streaming)"
```

---

### Task 2: ConnectMethodDescriptor に MethodType 追加

**Files:**
- Modify: `src/ConnectNet.Server/ConnectServiceDescriptor.cs`

- [ ] **Step 1: MethodType enum と Descriptor を更新**

`ConnectServiceDescriptor.cs` に追加:
```csharp
public enum ConnectMethodType
{
    Unary,
    ServerStreaming,
    ClientStreaming,
    BidiStreaming
}
```

`ConnectMethodDescriptor` に `MethodType` プロパティを追加:
```csharp
public ConnectMethodType MethodType { get; }
```

コンストラクタにデフォルト値 `ConnectMethodType.Unary` のパラメータを追加（既存コードの互換性維持）。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build connect-net.slnx`

- [ ] **Step 3: コミット**

```bash
git add src/ConnectNet.Server/ConnectServiceDescriptor.cs
git commit -m "feat: add ConnectMethodType enum to ConnectMethodDescriptor"
```

---

### Task 3: Server Streaming — サーバーハンドラ

**Files:**
- Create: `src/ConnectNet.Server/ConnectServerStreamHandler.cs`
- Modify: `src/ConnectNet.Server/ConnectServiceExtensions.cs`
- Modify: `src/ConnectNet.Server/ConnectServiceDescriptor.cs` — streaming用ハンドラデリゲート追加
- Create: `tests/ConnectNet.Tests/ServerStreamTests.cs`

- [ ] **Step 1: ConnectMethodDescriptor に StreamingHandler 追加**

`ConnectServiceDescriptor.cs` に streaming handler 用のデリゲートを追加:
```csharp
// 既存: Func<object, IMessage, ConnectContext, Task<IMessage>> Handler (unary)
// 追加:
public Func<object, IMessage, ConnectContext, IAsyncEnumerable<IMessage>>? ServerStreamHandler { get; }
```

コンストラクタにオプショナルパラメータとして追加。

- [ ] **Step 2: テストを書く**

`tests/ConnectNet.Tests/ServerStreamTests.cs`:
```csharp
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
using Xunit;

namespace ConnectNet.Tests;

public class ServerStreamTests
{
    private TestServer CreateTestServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<StreamGreeterService>();
        var app = builder.Build();
        app.MapConnectService<StreamGreeterService>(StreamGreeterServiceDefinition.Instance);
        app.Start();
        return app.GetTestServer();
    }

    [Fact]
    public async Task ServerStream_ReceivesMultipleMessages()
    {
        using var server = CreateTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress!.ToString());

        var messages = new List<HelloResponse>();
        await foreach (var msg in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
            "/example.GreeterService/SayHelloStream",
            new HelloRequest { Name = "Stream" }))
        {
            messages.Add(msg);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal("Hello Stream 1", messages[0].Message);
        Assert.Equal("Hello Stream 2", messages[1].Message);
        Assert.Equal("Hello Stream 3", messages[2].Message);
    }

    [Fact]
    public async Task ServerStream_RawHttp_CorrectFormat()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        // Send request with envelope
        var request = new HelloRequest { Name = "Raw" };
        var requestBytes = request.ToByteArray();
        var envelopeBody = new MemoryStream();
        await Envelope.WriteAsync(envelopeBody, 0x00, requestBytes);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHelloStream");
        httpRequest.Content = new ByteArrayContent(envelopeBody.ToArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/connect+proto");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/connect+proto", response.Content.Headers.ContentType!.MediaType);

        // Read response envelopes
        var responseStream = await response.Content.ReadAsStreamAsync();
        var messages = new List<byte[]>();
        byte lastFlags = 0;

        while (true)
        {
            var env = await Envelope.ReadAsync(responseStream);
            if (env == null) break;
            lastFlags = env.Value.flags;
            if (env.Value.flags == Envelope.FlagEndStream) break;
            messages.Add(env.Value.data);
        }

        Assert.Equal(3, messages.Count);
        Assert.Equal(Envelope.FlagEndStream, lastFlags);
    }

    [Fact]
    public async Task ServerStream_Error_InEndStream()
    {
        using var server = CreateTestServer();
        var httpClient = server.CreateClient();
        var channel = new ConnectChannel(httpClient, server.BaseAddress!.ToString());

        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                "/example.GreeterService/SayHelloStream",
                new HelloRequest { Name = "" }))
            {
            }
        });

        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
    }

    // --- Test service ---

    private class StreamGreeterService
    {
        public async IAsyncEnumerable<HelloResponse> SayHelloStream(
            HelloRequest request, ConnectContext context,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.InvalidArgument, "name required");

            for (int i = 1; i <= 3; i++)
            {
                yield return new HelloResponse { Message = $"Hello {request.Name} {i}" };
            }
        }
    }

    private class StreamGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static StreamGreeterServiceDefinition Instance { get; } = new();
        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHelloStream",
                HelloRequest.Parser,
                methodType: ConnectMethodType.ServerStreaming,
                serverStreamHandler: (service, req, ctx) => ((StreamGreeterService)service)
                    .SayHelloStream((HelloRequest)req, ctx))
        };
    }
}
```

- [ ] **Step 3: ConnectServerStreamHandler 実装**

`src/ConnectNet.Server/ConnectServerStreamHandler.cs`:
```csharp
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace ConnectNet.Server;

internal static class ConnectServerStreamHandler
{
    public static async Task HandleAsync(
        HttpContext httpContext,
        ConnectMethodDescriptor method,
        object service,
        ICodec codec)
    {
        var request = httpContext.Request;
        var response = httpContext.Response;

        // Validate Connect-Protocol-Version
        if (!request.Headers.TryGetValue("Connect-Protocol-Version", out var version) || version != "1")
        {
            response.StatusCode = 400;
            response.ContentType = "application/json";
            await response.WriteAsync(new ConnectException(ConnectCode.InvalidArgument,
                "missing or invalid Connect-Protocol-Version header").ToJson());
            return;
        }

        // Validate Content-Type for streaming: application/connect+{codec}
        var expectedContentType = $"application/connect+{codec.Name}";
        var contentType = request.ContentType;
        if (contentType == null || !contentType.StartsWith(expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            await response.WriteAsync(new ConnectException(ConnectCode.InvalidArgument,
                $"unsupported content type: {contentType}").ToJson());
            return;
        }

        try
        {
            // Read request envelope
            var env = await Envelope.ReadAsync(request.Body, httpContext.RequestAborted);
            if (env == null)
                throw new ConnectException(ConnectCode.InvalidArgument, "empty request body");

            var requestMessage = codec.Deserialize(env.Value.data, method.RequestParser);

            // Set response headers
            response.StatusCode = 200;
            response.ContentType = expectedContentType;

            var context = new ConnectContext(cancellationToken: httpContext.RequestAborted);

            // Stream response messages
            var stream = method.ServerStreamHandler!(service, requestMessage, context);
            await foreach (var msg in stream.WithCancellation(httpContext.RequestAborted))
            {
                var msgBytes = codec.Serialize(msg);
                await Envelope.WriteAsync(response.Body, 0x00, msgBytes, httpContext.RequestAborted);
                await response.Body.FlushAsync(httpContext.RequestAborted);
            }

            // Write EndStream with metadata
            var endStream = BuildEndStreamJson(null, context);
            await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream,
                Encoding.UTF8.GetBytes(endStream), httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
        }
        catch (ConnectException ex)
        {
            // If headers already sent, write error in EndStream envelope
            if (response.HasStarted)
            {
                var endStream = BuildEndStreamJson(ex, new ConnectContext());
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream,
                    Encoding.UTF8.GetBytes(endStream));
                await response.Body.FlushAsync();
            }
            else
            {
                response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
                response.ContentType = "application/json";
                await response.WriteAsync(ex.ToJson());
            }
        }
        catch (Exception)
        {
            var error = new ConnectException(ConnectCode.Internal, "internal error");
            if (response.HasStarted)
            {
                var endStream = BuildEndStreamJson(error, new ConnectContext());
                await Envelope.WriteAsync(response.Body, Envelope.FlagEndStream,
                    Encoding.UTF8.GetBytes(endStream));
                await response.Body.FlushAsync();
            }
            else
            {
                response.StatusCode = 500;
                response.ContentType = "application/json";
                await response.WriteAsync(error.ToJson());
            }
        }
    }

    internal static string BuildEndStreamJson(ConnectException? error, ConnectContext context)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        if (error != null)
        {
            writer.WritePropertyName("error");
            writer.WriteRawValue(error.ToJson());
        }
        writer.WriteStartObject("metadata");
        foreach (var trailer in context.ResponseTrailers)
        {
            writer.WriteStartArray(trailer.Key);
            writer.WriteStringValue(trailer.Value);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
```

- [ ] **Step 4: ConnectServiceExtensions を Streaming 対応に更新**

`MapConnectService` の `foreach` ループで `method.MethodType` を見て、適切なハンドラにルーティング:
```csharp
foreach (var method in definition.Methods)
{
    builder.MapPost(method.Procedure, async (HttpContext context) =>
    {
        var service = context.RequestServices.GetRequiredService<TService>();
        var codec = context.RequestServices.GetRequiredService<ICodec>();
        switch (method.MethodType)
        {
            case ConnectMethodType.ServerStreaming:
                await ConnectServerStreamHandler.HandleAsync(context, method, service, codec);
                break;
            default:
                await ConnectUnaryHandler.HandleAsync(context, method, service, codec);
                break;
        }
    });
}
```

- [ ] **Step 5: ConnectChannel に ServerStreamAsync を追加**

`src/ConnectNet.Client/ConnectChannel.cs` に追加:
```csharp
public async IAsyncEnumerable<TRes> ServerStreamAsync<TReq, TRes>(
    string procedure,
    TReq request,
    CallOptions? options = null,
    [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    where TReq : IMessage<TReq>
    where TRes : IMessage<TRes>, new()
{
    var body = _codec.Serialize(request);
    var uri = new Uri(_baseUri, procedure);

    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);

    // Wrap request in envelope for streaming
    using var envelopeStream = new MemoryStream();
    await Envelope.WriteAsync(envelopeStream, 0x00, body, ct);
    httpRequest.Content = new ByteArrayContent(envelopeStream.ToArray());
    httpRequest.Content.Headers.ContentType =
        new System.Net.Http.Headers.MediaTypeHeaderValue($"application/connect+{_codec.Name}");
    httpRequest.Headers.Add("Connect-Protocol-Version", "1");

    if (options?.Timeout is TimeSpan timeout)
        httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());

    var httpResponse = await _httpClient.SendAsync(httpRequest,
        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

    if (!httpResponse.IsSuccessStatusCode)
    {
        var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        throw ConnectException.FromJson(errorBody);
    }

    var responseStream = await httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

    while (true)
    {
        var env = await Envelope.ReadAsync(responseStream, ct);
        if (env == null) yield break;

        if (env.Value.flags == Envelope.FlagEndStream)
        {
            // Parse EndStream JSON for error
            var endStreamJson = System.Text.Encoding.UTF8.GetString(env.Value.data);
            using var doc = System.Text.Json.JsonDocument.Parse(endStreamJson);
            if (doc.RootElement.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                throw ConnectException.FromJson(errorProp.GetRawText());
            }
            yield break;
        }

        yield return _codec.Deserialize<TRes>(env.Value.data);
    }
}
```

- [ ] **Step 6: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ServerStreamTests -v m`
Expected: 3 tests passed

- [ ] **Step 7: コミット**

```bash
git add -A
git commit -m "feat: add Server Streaming RPC support (client + server)"
```

---

### Task 4: Client Streaming — サーバーハンドラ + クライアント

**Files:**
- Create: `src/ConnectNet.Server/ConnectClientStreamHandler.cs`
- Modify: `src/ConnectNet.Server/ConnectServiceDescriptor.cs` — ClientStreamHandler 追加
- Modify: `src/ConnectNet.Server/ConnectServiceExtensions.cs` — ClientStreaming ルーティング
- Modify: `src/ConnectNet.Client/ConnectChannel.cs` — ClientStreamAsync 追加
- Create: `src/ConnectNet.Client/ClientStreamCall.cs` — クライアント送信API
- Create: `tests/ConnectNet.Tests/ClientStreamTests.cs`

- [ ] **Step 1: ClientStreamCall 型を定義**

`src/ConnectNet.Client/ClientStreamCall.cs`:
```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ClientStreamCall<TReq, TRes> : IDisposable
    where TReq : IMessage<TReq>
    where TRes : IMessage<TRes>, new()
{
    private readonly Stream _requestStream;
    private readonly HttpResponseMessage _httpResponse;
    private readonly ICodec _codec;
    private bool _disposed;

    internal ClientStreamCall(Stream requestStream, HttpResponseMessage httpResponse, ICodec codec)
    {
        _requestStream = requestStream;
        _httpResponse = httpResponse;
        _codec = codec;
    }

    public async Task SendAsync(TReq message, CancellationToken ct = default)
    {
        var data = _codec.Serialize(message);
        await Envelope.WriteAsync(_requestStream, 0x00, data, ct);
        await _requestStream.FlushAsync(ct);
    }

    public async Task<TRes> CloseAndReceiveAsync(CancellationToken ct = default)
    {
        _requestStream.Close();

        var responseStream = await _httpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);

        // Read response: single message envelope + EndStream
        var env = await Envelope.ReadAsync(responseStream, ct);
        if (env == null)
            throw new ConnectException(ConnectCode.Internal, "empty response");

        if (env.Value.flags == Envelope.FlagEndStream)
        {
            var endJson = Encoding.UTF8.GetString(env.Value.data);
            using var doc = System.Text.Json.JsonDocument.Parse(endJson);
            if (doc.RootElement.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                throw ConnectException.FromJson(errorProp.GetRawText());
            throw new ConnectException(ConnectCode.Internal, "no response message before EndStream");
        }

        var result = _codec.Deserialize<TRes>(env.Value.data);

        // Read EndStream
        var end = await Envelope.ReadAsync(responseStream, ct);
        if (end != null && end.Value.flags == Envelope.FlagEndStream)
        {
            var endJson = Encoding.UTF8.GetString(end.Value.data);
            using var doc = System.Text.Json.JsonDocument.Parse(endJson);
            if (doc.RootElement.TryGetProperty("error", out var errorProp) &&
                errorProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                throw ConnectException.FromJson(errorProp.GetRawText());
        }

        return result;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _requestStream.Dispose();
            _httpResponse.Dispose();
            _disposed = true;
        }
    }
}
```

Note: Client Streamingの本実装はHTTP/2の双方向ストリームが必要で、HTTP/1.1上のHttpClientでは完全なclient streamingは困難。Phase 2ではリクエストボディを一括送信後にレスポンスを受け取るシンプルな実装とする（複数メッセージをバッファしてから送信）。真のclient streamingはHTTP/2 + YAHAで動作。

- [ ] **Step 2: ConnectMethodDescriptor に ClientStreamHandler 追加**

```csharp
public Func<object, IAsyncEnumerable<IMessage>, ConnectContext, Task<IMessage>>? ClientStreamHandler { get; }
```

- [ ] **Step 3: ConnectClientStreamHandler 実装**

`src/ConnectNet.Server/ConnectClientStreamHandler.cs`:
サーバー側はリクエストボディからエンベロープを繰り返し読み、IAsyncEnumerable<IMessage>としてサービスに渡す。レスポンスは単一メッセージ + EndStreamエンベロープ。

- [ ] **Step 4: テストを書く**

`tests/ConnectNet.Tests/ClientStreamTests.cs`:
複数のHelloRequestを送信し、単一のHelloResponse（全名前を連結）を受信するテスト。

- [ ] **Step 5: テスト通過を確認 + コミット**

```bash
git commit -m "feat: add Client Streaming RPC support"
```

---

### Task 5: Bidirectional Streaming

**Files:**
- Create: `src/ConnectNet.Server/ConnectBidiStreamHandler.cs`
- Create: `src/ConnectNet.Client/BidiStreamCall.cs`
- Modify: `src/ConnectNet.Server/ConnectServiceDescriptor.cs`
- Modify: `src/ConnectNet.Server/ConnectServiceExtensions.cs`
- Modify: `src/ConnectNet.Client/ConnectChannel.cs`
- Create: `tests/ConnectNet.Tests/BidiStreamTests.cs`

Note: Bidi StreamingはHTTP/2が必須。HTTP/1.1では動作しない。TestServerはHTTP/2をサポートするため、テストは可能。

- [ ] **Step 1: BidiStreamCall 型を定義**

サーバー・クライアント双方が同時にメッセージを送受信できるAPI。SendAsync/ReceiveAsync/CompleteAsync。

- [ ] **Step 2: ConnectBidiStreamHandler 実装**

- [ ] **Step 3: テスト + コミット**

```bash
git commit -m "feat: add Bidirectional Streaming RPC support"
```

---

### Task 6: 全テスト実行 + 最終確認

- [ ] **Step 1: 全テスト実行**

Run: `dotnet test connect-net.slnx -v m`
Expected: All tests passed (Phase 1 + Phase 2)

- [ ] **Step 2: コミット（修正があれば）**

```bash
git commit -m "chore: finalize Phase 2 streaming implementation"
```
