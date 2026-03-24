# Connect RPC Conformance テスト Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** connect-net の Connect プロトコル実装が公式 `connectrpc.com/conformance` テストスイート (v1.0.5) に適合することを検証するハーネスを構築する

**Architecture:** 3段階で実装する。(1) 圧縮レジストリの導入と Deflate 追加で既存コードを複数圧縮対応にする。(2) conformance proto を vendoring し C# コード生成する。(3) stdin/stdout で conformance ランナーと通信する C# コンソールアプリ（サーバー/クライアントハーネス）を実装する。

**Tech Stack:** C# / .NET 10 / ASP.NET Core (Kestrel) / Google.Protobuf / connectconformance v1.0.5

---

## File Structure

```
src/ConnectNet/
├── ConnectCompressorRegistry.cs         # Create: 圧縮レジストリ
├── DeflateCompressor.cs                 # Create: Deflate 圧縮
├── ConnectCodecRegistry.cs              # Existing
├── GzipCompressor.cs                    # Existing
├── ICompressor.cs                       # Existing

src/ConnectNet.Server/
├── ConnectServiceExtensions.cs          # Modify: レジストリ登録
├── ConnectUnaryHandler.cs               # Modify: レジストリベースの圧縮
├── ConnectServerStreamHandler.cs        # Modify: レジストリベースの圧縮
├── ConnectClientStreamHandler.cs        # Modify: レジストリベースの圧縮
├── ConnectBidiStreamHandler.cs          # Modify: レジストリベースの圧縮

src/ConnectNet.Client/
├── ConnectChannel.cs                    # Modify: レジストリベースの圧縮解凍

tests/ConnectNet.Conformance.Proto/
├── ConnectNet.Conformance.Proto.csproj  # Create: conformance proto 生成プロジェクト
└── proto/connectrpc/conformance/v1/     # Create: vendored proto files
    ├── service.proto
    ├── client_compat.proto
    ├── server_compat.proto
    ├── config.proto
    └── suite.proto

tests/ConnectNet.Conformance/
├── ConnectNet.Conformance.csproj        # Create: コンソールアプリ
├── Program.cs                           # Create: --mode server|client
├── StdioProtobuf.cs                     # Create: size-delimited Protobuf I/O
├── ServerHarness.cs                     # Create: サーバーハーネス
├── ClientHarness.cs                     # Create: クライアントハーネス
├── ConformanceServiceImpl.cs            # Create: ConformanceService 実装
└── config.yaml                          # Create: サポート機能宣言

connect-net.slnx                         # Modify: 新プロジェクト追加
```

---

### Task 1: ConnectCompressorRegistry と DeflateCompressor

**Files:**
- Create: `src/ConnectNet/ConnectCompressorRegistry.cs`
- Create: `src/ConnectNet/DeflateCompressor.cs`
- Create: `tests/ConnectNet.Tests/DeflateCompressorTests.cs`

- [ ] **Step 1: DeflateCompressorTests 作成**

```csharp
// tests/ConnectNet.Tests/DeflateCompressorTests.cs
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class DeflateCompressorTests
{
    [Fact]
    public void Name_ReturnsDeflate()
    {
        var compressor = new DeflateCompressor();
        Assert.Equal("deflate", compressor.Name);
    }

    [Fact]
    public void CompressDecompress_RoundTrips()
    {
        var compressor = new DeflateCompressor();
        var original = System.Text.Encoding.UTF8.GetBytes("Hello, Deflate compression test!");
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void CompressDecompress_EmptyData()
    {
        var compressor = new DeflateCompressor();
        var original = System.Array.Empty<byte>();
        var compressed = compressor.Compress(original);
        var decompressed = compressor.Decompress(compressed);
        Assert.Equal(original, decompressed);
    }
}
```

- [ ] **Step 2: ConnectCompressorRegistry 作成**

```csharp
// src/ConnectNet/ConnectCompressorRegistry.cs
using System.Collections.Generic;
using System.Linq;

namespace ConnectNet;

public class ConnectCompressorRegistry
{
    private readonly Dictionary<string, ICompressor> _compressors = new();
    private ICompressor? _default;

    public void Register(ICompressor compressor)
    {
        _compressors[compressor.Name] = compressor;
        if (_default == null)
            _default = compressor;
    }

    public ICompressor? Get(string name) => _compressors.TryGetValue(name, out var c) ? c : null;

    public ICompressor? Default => _default;

    public IEnumerable<string> SupportedNames => _compressors.Keys;
}
```

- [ ] **Step 3: DeflateCompressor 作成**

```csharp
// src/ConnectNet/DeflateCompressor.cs
using System.IO;
using System.IO.Compression;

namespace ConnectNet;

public class DeflateCompressor : ICompressor
{
    public string Name => "deflate";

    public byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest))
        {
            deflate.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    public byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
```

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~DeflateCompressorTests" -v n`
Expected: 3 tests PASS

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet/ConnectCompressorRegistry.cs src/ConnectNet/DeflateCompressor.cs tests/ConnectNet.Tests/DeflateCompressorTests.cs
git commit -m "feat: add ConnectCompressorRegistry and DeflateCompressor"
```

---

### Task 2: サーバー側ハンドラーを圧縮レジストリベースに移行

**Files:**
- Modify: `src/ConnectNet.Server/ConnectServiceExtensions.cs`
- Modify: `src/ConnectNet.Server/ConnectUnaryHandler.cs`
- Modify: `src/ConnectNet.Server/ConnectServerStreamHandler.cs`
- Modify: `src/ConnectNet.Server/ConnectClientStreamHandler.cs`
- Modify: `src/ConnectNet.Server/ConnectBidiStreamHandler.cs`

- [ ] **Step 1: ConnectServiceExtensions の DI 登録変更**

`AddConnectServices` メソッドを修正:

```csharp
// 既存の ICompressor 登録を削除し、ConnectCompressorRegistry に変更
var compressorRegistry = new ConnectCompressorRegistry();
compressorRegistry.Register(new GzipCompressor());
compressorRegistry.Register(new DeflateCompressor());
services.AddSingleton(compressorRegistry);
// 後方互換: ICompressor も残す（デフォルト=gzip）
services.AddSingleton<ICompressor, GzipCompressor>();
```

- [ ] **Step 2: ConnectUnaryHandler を修正**

`ConnectUnaryHandler.HandleAsync` と `HandleGetAsync` の圧縮処理を修正:

- `httpContext.RequestServices.GetService(typeof(ICompressor))` → `httpContext.RequestServices.GetService<ConnectCompressorRegistry>()`
- リクエスト解凍: `Content-Encoding` ヘッダーの値でレジストリから `ICompressor` を取得
- レスポンス圧縮: `Accept-Encoding` ヘッダーをパースし、サポートする最初の圧縮方式を選択

```csharp
// リクエスト解凍の変更例
var compressorRegistry = httpContext.RequestServices.GetService<ConnectCompressorRegistry>();
if (request.Headers.TryGetValue("Content-Encoding", out var requestEncoding))
{
    var encodingName = requestEncoding.FirstOrDefault();
    var decompressor = compressorRegistry?.Get(encodingName ?? "") ?? (encodingName == "gzip" ? new GzipCompressor() : null);
    if (decompressor != null)
        requestBytes = decompressor.Decompress(requestBytes);
}

// レスポンス圧縮の変更例
if (request.Headers.TryGetValue("Accept-Encoding", out var acceptEncoding) && compressorRegistry != null)
{
    foreach (var name in compressorRegistry.SupportedNames)
    {
        if (acceptEncoding.Any(v => v != null && v.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            responseBytes = compressorRegistry.Get(name)!.Compress(responseBytes);
            response.Headers["Content-Encoding"] = name;
            break;
        }
    }
}
```

**重要:** `HandleGetAsync` も同様に修正すること。GET リクエストの `compression` クエリパラメータの解凍処理もレジストリベースに変更する（現在は `"gzip"` のみハードコード）。

- [ ] **Step 3: ストリーミングハンドラー3つも同様に修正**

`ConnectServerStreamHandler`, `ConnectClientStreamHandler`, `ConnectBidiStreamHandler` の圧縮処理を同様にレジストリベースに変更:

- `GetService(typeof(ICompressor))` → `GetService<ConnectCompressorRegistry>()`
- `Connect-Content-Encoding` / `Connect-Accept-Encoding` ヘッダーの処理をレジストリベースに
- ハードコードされた `"gzip"` 文字列比較をレジストリ検索に変更

- [ ] **Step 4: 既存テスト実行**

Run: `dotnet test tests/ConnectNet.Tests -v n`
Expected: 全テスト PASS（既存の圧縮テストが壊れないこと）

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Server/
git commit -m "refactor: migrate server compression to registry-based approach"
```

---

### Task 3: クライアント側の圧縮レジストリ対応

**Files:**
- Modify: `src/ConnectNet.Client/ConnectChannel.cs`

- [ ] **Step 1: ConnectChannel の解凍処理修正**

`ConnectChannel` の `SendUnaryAsync`, `SendUnaryGetAsync`, ストリーミングメソッドの解凍処理を修正:

- `new GzipCompressor()` のハードコード → `Content-Encoding` / `Connect-Content-Encoding` の値に応じて適切な `ICompressor` を取得
- `ConnectChannelOptions` に `AcceptEncodings` リスト（またはレジストリ）を追加するか、既存の `AcceptCompression` bool をレジストリから available encodings を送信する方式に変更

最小限の変更（`AcceptCompression` は後方互換のため残す。`true` の場合は `Decompressors` リストから `Accept-Encoding` を生成、`false` の場合は何も送らない）:
```csharp
// ConnectChannelOptions に追加
public List<ICompressor> Decompressors { get; set; } = new() { new GzipCompressor(), new DeflateCompressor() };

// 解凍時
var encoding = responseContentEncoding;
var decompressor = _channelOptions.Decompressors.FirstOrDefault(d => string.Equals(d.Name, encoding, StringComparison.OrdinalIgnoreCase));
if (decompressor != null)
    responseBytes = decompressor.Decompress(responseBytes);

// Accept-Encoding ヘッダー
var acceptEncodings = string.Join(", ", _channelOptions.Decompressors.Select(d => d.Name));
httpRequest.Headers.Add("Accept-Encoding", acceptEncodings);
```

- [ ] **Step 2: 既存テスト実行**

Run: `dotnet test tests/ConnectNet.Tests -v n`
Expected: 全テスト PASS

- [ ] **Step 3: コミット**

```bash
git add src/ConnectNet.Client/ConnectChannel.cs
git commit -m "refactor: migrate client compression to support multiple decompressors"
```

---

### Task 4: Conformance Proto の vendoring とコード生成

**Files:**
- Create: `tests/ConnectNet.Conformance.Proto/ConnectNet.Conformance.Proto.csproj`
- Create: `tests/ConnectNet.Conformance.Proto/proto/connectrpc/conformance/v1/*.proto`
- Modify: `connect-net.slnx`

- [ ] **Step 1: conformance proto ファイルを取得**

```bash
cd /home/neguse/ghq/github.com/neguse/connect-net
mkdir -p tests/ConnectNet.Conformance.Proto/proto/connectrpc/conformance/v1
# connectrpc/conformance リポジトリの v1.0.5 タグから proto ファイルを取得
# https://github.com/connectrpc/conformance/tree/v1.0.5/proto/connectrpc/conformance/v1/
# 必要なファイル: service.proto, client_compat.proto, server_compat.proto, config.proto, suite.proto
```

各 proto ファイルをダウンロード。上記5ファイルに加え、`raw_request.proto`, `raw_response.proto` 等のファイルがある場合はそれも取得すること。import パスの依存関係（`google/protobuf/any.proto`, `google/protobuf/struct.proto` 等の well-known types、`buf/validate` 等）を確認し、`Grpc.Tools` が自動解決しないものは `AdditionalImportDirs` で設定する。`buf/validate` が必要な場合は `src/ConnectNet.Validation/Proto` をインポートパスに追加する。

- [ ] **Step 2: csproj 作成**

```xml
<!-- tests/ConnectNet.Conformance.Proto/ConnectNet.Conformance.Proto.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
    <Protobuf Include="proto/connectrpc/conformance/v1/*.proto" GrpcServices="None" AdditionalImportDirs="proto" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ConnectNet/ConnectNet.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Client/ConnectNet.Client.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Server/ConnectNet.Server.csproj" />
  </ItemGroup>
</Project>
```

注: `service.proto` のサービス定義は `protoc-gen-connect-csharp` でもコード生成する必要がある。ビルドが通らない場合は Grpc.Tools の Protobuf アイテムで `ProtoRoot` や `AdditionalImportDirs` を調整する。

- [ ] **Step 3: slnx にプロジェクト追加**

`connect-net.slnx` の `/tests/` フォルダに追加:
```xml
<Project Path="tests/ConnectNet.Conformance.Proto/ConnectNet.Conformance.Proto.csproj" />
```

- [ ] **Step 4: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance.Proto`
Expected: BUILD SUCCEEDED

proto ファイルに buf/validate などの外部依存がある場合は、そのインポートパスも設定する（`src/ConnectNet.Validation/Proto` を `AdditionalImportDirs` に追加するなど）。

- [ ] **Step 5: protoc-gen-connect-csharp でサービスコード生成**

`service.proto` の `ConformanceService` 定義から、connect-net のサービスベースクラスとクライアントコードを生成:

```bash
cd /home/neguse/ghq/github.com/neguse/connect-net
protoc --plugin=protoc-gen-connect-csharp=tools/protoc-gen-connect-csharp/protoc-gen-connect-csharp \
  --connect-csharp_out=tests/ConnectNet.Conformance.Proto \
  -I tests/ConnectNet.Conformance.Proto/proto \
  tests/ConnectNet.Conformance.Proto/proto/connectrpc/conformance/v1/service.proto
```

生成に失敗する場合は、proto ファイルの `csharp_namespace` オプションを確認し、必要に応じて設定する。

- [ ] **Step 6: コミット**

```bash
git add tests/ConnectNet.Conformance.Proto/ connect-net.slnx
git commit -m "feat(conformance): vendor conformance proto and generate C# code"
```

---

### Task 5: コンソールアプリのセットアップと StdioProtobuf

**Files:**
- Create: `tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj`
- Create: `tests/ConnectNet.Conformance/Program.cs`
- Create: `tests/ConnectNet.Conformance/StdioProtobuf.cs`
- Modify: `connect-net.slnx`

- [ ] **Step 1: csproj 作成**

```xml
<!-- tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ConnectNet/ConnectNet.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Client/ConnectNet.Client.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Server/ConnectNet.Server.csproj" />
    <ProjectReference Include="../ConnectNet.Conformance.Proto/ConnectNet.Conformance.Proto.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: StdioProtobuf 作成**

```csharp
// tests/ConnectNet.Conformance/StdioProtobuf.cs
using System;
using System.IO;
using Google.Protobuf;

namespace ConnectNet.Conformance;

internal static class StdioProtobuf
{
    /// <summary>
    /// Reads a size-delimited protobuf message from the stream.
    /// Returns null on EOF.
    /// </summary>
    public static T? Read<T>(Stream stream) where T : IMessage<T>, new()
    {
        // Read 4-byte big-endian length prefix
        var lengthBytes = new byte[4];
        var bytesRead = 0;
        while (bytesRead < 4)
        {
            var n = stream.Read(lengthBytes, bytesRead, 4 - bytesRead);
            if (n == 0)
                return default; // EOF
            bytesRead += n;
        }

        var length = (lengthBytes[0] << 24) | (lengthBytes[1] << 16) | (lengthBytes[2] << 8) | lengthBytes[3];

        // Read message bytes
        var messageBytes = new byte[length];
        bytesRead = 0;
        while (bytesRead < length)
        {
            var n = stream.Read(messageBytes, bytesRead, length - bytesRead);
            if (n == 0)
                throw new EndOfStreamException("Unexpected EOF while reading message body");
            bytesRead += n;
        }

        var message = new T();
        message.MergeFrom(messageBytes);
        return message;
    }

    /// <summary>
    /// Writes a size-delimited protobuf message to the stream.
    /// </summary>
    public static void Write(Stream stream, IMessage message)
    {
        var messageBytes = message.ToByteArray();
        var length = messageBytes.Length;

        // Write 4-byte big-endian length prefix
        var lengthBytes = new byte[4];
        lengthBytes[0] = (byte)(length >> 24);
        lengthBytes[1] = (byte)(length >> 16);
        lengthBytes[2] = (byte)(length >> 8);
        lengthBytes[3] = (byte)(length);

        stream.Write(lengthBytes, 0, 4);
        stream.Write(messageBytes, 0, messageBytes.Length);
        stream.Flush();
    }
}
```

- [ ] **Step 3: Program.cs 作成**

```csharp
// tests/ConnectNet.Conformance/Program.cs
using System;

namespace ConnectNet.Conformance;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 1 && args[0] == "--mode" ? args[1] : null;

        switch (mode)
        {
            case "server":
                await ServerHarness.RunAsync();
                return 0;
            case "client":
                await ClientHarness.RunAsync();
                return 0;
            default:
                Console.Error.WriteLine("Usage: --mode server|client");
                return 1;
        }
    }
}
```

- [ ] **Step 4: slnx にプロジェクト追加**

```xml
<Project Path="tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj" />
```

- [ ] **Step 5: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance`
Expected: BUILD SUCCEEDED（ServerHarness/ClientHarness はまだ存在しないのでスタブが必要。Program.cs で参照されるクラスのスタブを作成するか、ビルド確認はTask 6以降に延期）

- [ ] **Step 6: コミット**

```bash
git add tests/ConnectNet.Conformance/ connect-net.slnx
git commit -m "feat(conformance): add console app skeleton and StdioProtobuf"
```

---

### Task 6: ServerHarness 実装

**Files:**
- Create: `tests/ConnectNet.Conformance/ServerHarness.cs`
- Create: `tests/ConnectNet.Conformance/ConformanceServiceImpl.cs`

- [ ] **Step 1: ServerHarness 作成**

```csharp
// tests/ConnectNet.Conformance/ServerHarness.cs
using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using ConnectNet.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Conformance;

internal static class ServerHarness
{
    public static async Task RunAsync()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        // Read ServerCompatRequest from stdin
        var request = StdioProtobuf.Read<Connectrpc.Conformance.V1.ServerCompatRequest>(stdin);
        if (request == null)
            return;

        var builder = WebApplication.CreateBuilder();

        // Configure Kestrel
        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.Listen(IPAddress.Loopback, 0, listenOptions =>
            {
                // HTTP version
                if (request.HttpVersion == Connectrpc.Conformance.V1.HttpVersion.HttpVersion2)
                    listenOptions.Protocols = HttpProtocols.Http2;
                else
                    listenOptions.Protocols = HttpProtocols.Http1;

                // TLS
                if (request.ServerCreds != null && request.ServerCreds.Cert.Length > 0)
                {
                    var cert = X509Certificate2.CreateFromPem(
                        request.ServerCreds.Cert.ToStringUtf8(),
                        request.ServerCreds.Key.ToStringUtf8());
                    listenOptions.UseHttps(cert, httpsOptions =>
                    {
                        if (request.ClientTlsCert.Length > 0)
                        {
                            httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                            httpsOptions.ClientCertificateValidation = (cert, chain, errors) => true; // conformance runner handles validation
                        }
                    });
                }
            });
        });

        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ConformanceServiceImpl>();
        // suppress logging to avoid stdout pollution
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapConnectService<ConformanceServiceImpl>(ConformanceServiceDefinition.Instance);
        await app.StartAsync();

        // Get actual listening port via IServerAddressesFeature
        var server = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addressFeature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var address = addressFeature.Addresses.First();
        var uri = new Uri(address);
        var port = uri.Port;

        // Write ServerCompatResponse to stdout
        var response = new Connectrpc.Conformance.V1.ServerCompatResponse
        {
            Host = "127.0.0.1",
            Port = (uint)port,
        };
        // Include PEM cert if TLS was configured
        if (request.ServerCreds != null)
            response.PemCert = request.ServerCreds.Cert;

        StdioProtobuf.Write(stdout, response);

        // Wait until stdin is closed (runner terminates us)
        await Task.Run(() => { while (stdin.Read(new byte[1], 0, 1) > 0) { } });
        await app.StopAsync();
    }
}
```

注: `Connectrpc.Conformance.V1` の名前空間は proto の `csharp_namespace` に依存。生成コードの実際の名前空間に合わせて調整すること。`ConformanceServiceDefinition` は `protoc-gen-connect-csharp` の生成コードに依存。サーバーの listening ポート取得方法は `app.Urls` の代わりに Kestrel の `IServer.Features.Get<IServerAddressesFeature>()` を使う必要があるかもしれない。

- [ ] **Step 2: ConformanceServiceImpl の骨格作成**

```csharp
// tests/ConnectNet.Conformance/ConformanceServiceImpl.cs
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ConnectNet;
using Google.Protobuf;

namespace ConnectNet.Conformance;

internal class ConformanceServiceImpl
{
    // 各メソッドは response_definition に従ってレスポンスを返す
    // 具体的な実装は生成コードの型名に依存するため、ビルド確認後に完成させる

    public Task<IMessage> Unary(IMessage request, ConnectContext context)
    {
        // request から response_definition を取得
        // ヘッダー、トレーラーを context に設定
        // エラーコードが指定されていれば ConnectException を投げる
        // レスポンスペイロードを構築して返す
        throw new NotImplementedException("Implement after proto generation");
    }

    // IdempotentUnary, Unimplemented, ClientStream, ServerStream, BidiStream も同様
}
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance`
Expected: BUILD SUCCEEDED（NotImplementedException は OK）

- [ ] **Step 4: コミット**

```bash
git add tests/ConnectNet.Conformance/ServerHarness.cs tests/ConnectNet.Conformance/ConformanceServiceImpl.cs
git commit -m "feat(conformance): implement ServerHarness skeleton"
```

---

### Task 7: ClientHarness 実装

**Files:**
- Create: `tests/ConnectNet.Conformance/ClientHarness.cs`

- [ ] **Step 1: ClientHarness 作成**

```csharp
// tests/ConnectNet.Conformance/ClientHarness.cs
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ConnectNet.Client;

namespace ConnectNet.Conformance;

internal static class ClientHarness
{
    public static async Task RunAsync()
    {
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        while (true)
        {
            // Read ClientCompatRequest from stdin
            var request = StdioProtobuf.Read<Connectrpc.Conformance.V1.ClientCompatRequest>(stdin);
            if (request == null)
                break; // EOF

            var response = new Connectrpc.Conformance.V1.ClientCompatResponse
            {
                TestName = request.TestName,
            };

            try
            {
                var result = await ExecuteRpc(request);
                response.Response = result;
            }
            catch (Exception ex)
            {
                response.Error = new Connectrpc.Conformance.V1.ClientErrorResult
                {
                    Message = ex.ToString()
                };
            }

            StdioProtobuf.Write(stdout, response);
        }
    }

    private static async Task<Connectrpc.Conformance.V1.ClientResponseResult> ExecuteRpc(
        Connectrpc.Conformance.V1.ClientCompatRequest request)
    {
        // HttpClient を構築（TLS設定含む）
        var handler = new HttpClientHandler();
        if (request.ServerTlsCert.Length > 0)
        {
            handler.ServerCertificateCustomValidationCallback = (msg, cert, chain, errors) => true;
            // TLS client cert があれば設定
        }

        using var httpClient = new HttpClient(handler);
        var baseUrl = $"http{(request.ServerTlsCert.Length > 0 ? "s" : "")}://{request.Host}:{request.Port}";

        // ConnectChannel を構築
        var channelOptions = new ConnectChannelOptions();
        // 圧縮設定
        if (request.Compression == Connectrpc.Conformance.V1.Compression.Gzip)
            channelOptions.RequestCompressor = new GzipCompressor();
        else if (request.Compression == Connectrpc.Conformance.V1.Compression.Deflate)
            channelOptions.RequestCompressor = new DeflateCompressor();

        var channel = new ConnectChannel(httpClient, baseUrl, channelOptions: channelOptions);

        var result = new Connectrpc.Conformance.V1.ClientResponseResult();

        // StreamType に応じて RPC を実行
        switch (request.StreamType)
        {
            case Connectrpc.Conformance.V1.StreamType.Unary:
            case Connectrpc.Conformance.V1.StreamType.IdempotentUnary:
                // UnaryAsync で RPC 実行、結果を result に詰める
                break;
            case Connectrpc.Conformance.V1.StreamType.ServerStream:
                // ServerStreamAsync で RPC 実行
                break;
            case Connectrpc.Conformance.V1.StreamType.ClientStream:
                // ClientStreamAsync で RPC 実行
                break;
            case Connectrpc.Conformance.V1.StreamType.BidiStream:
            case Connectrpc.Conformance.V1.StreamType.HalfDuplexBidiStream:
                // BidiStreamAsync で RPC 実行
                break;
        }

        return result;
    }
}
```

注: 具体的なRPC実行ロジック（リクエストペイロードの構築、レスポンスの `ClientResponseResult` への変換、ヘッダー/トレーラーの収集）は、生成コードの型名に依存する。ビルド確認後に完成させる。

- [ ] **Step 2: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: コミット**

```bash
git add tests/ConnectNet.Conformance/ClientHarness.cs
git commit -m "feat(conformance): implement ClientHarness skeleton"
```

---

### Task 8: ConformanceService の完全実装

**Files:**
- Modify: `tests/ConnectNet.Conformance/ConformanceServiceImpl.cs`

- [ ] **Step 1: 生成コードのAPI確認**

`tests/ConnectNet.Conformance.Proto/` のビルド後、`obj/` 以下の生成コードを確認して以下を特定:
- `UnaryRequest`, `UnaryResponse` 等のメッセージ型名
- `ResponseDefinition` の構造（ヘッダー、トレーラー、エラーコード、ペイロード、遅延）
- `ConformanceServiceBase` （`protoc-gen-connect-csharp` 生成）のメソッドシグネチャ

- [ ] **Step 2: ConformanceServiceImpl を完全実装**

各メソッドの共通パターン:
```csharp
// 1. request から response_definition を取得
// 2. response_definition.ResponseHeaders を context.ResponseHeaders に設定
// 3. response_definition.ResponseTrailers を context.ResponseTrailers に設定
// 4. response_definition.Error が設定されていれば ConnectException を投げる
// 5. response_definition.ResponseData からペイロードを構築して返す
// 6. response_definition.ResponseDelayMs が設定されていれば await Task.Delay
```

`Unimplemented` は単に:
```csharp
throw new ConnectException(ConnectCode.Unimplemented, "unimplemented");
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: コミット**

```bash
git add tests/ConnectNet.Conformance/ConformanceServiceImpl.cs
git commit -m "feat(conformance): implement ConformanceService with all 6 RPC methods"
```

---

### Task 9: ClientHarness の完全実装

**Files:**
- Modify: `tests/ConnectNet.Conformance/ClientHarness.cs`

- [ ] **Step 1: ClientHarness の RPC 実行ロジック完成**

各 StreamType に対して:
- リクエストペイロードを `ClientCompatRequest.RequestMessages` から構築
- ヘッダーを `ClientCompatRequest.RequestHeaders` から設定
- RPC 実行
- レスポンスを `ClientResponseResult` に変換:
  - `ResponseHeaders`: サーバーからのレスポンスヘッダー
  - `Payloads`: 各レスポンスメッセージ
  - `ResponseTrailers`: トレーラー
  - `Error`: ConnectException が発生した場合のエラー情報
  - `HttpStatusCode`: HTTP ステータスコード

キャンセル処理: `ClientCompatRequest.Cancel` が設定されていれば、指定タイミングで CancellationToken をキャンセル

- [ ] **Step 2: ビルド確認**

Run: `dotnet build tests/ConnectNet.Conformance`
Expected: BUILD SUCCEEDED

- [ ] **Step 3: コミット**

```bash
git add tests/ConnectNet.Conformance/ClientHarness.cs
git commit -m "feat(conformance): complete ClientHarness RPC execution logic"
```

---

### Task 10: config.yaml と初回 conformance テスト実行

**Files:**
- Create: `tests/ConnectNet.Conformance/config.yaml`

- [ ] **Step 1: config.yaml 作成**

```yaml
# tests/ConnectNet.Conformance/config.yaml
features:
  versions:
    - HTTP_VERSION_1
    - HTTP_VERSION_2
  protocols:
    - PROTOCOL_CONNECT
  codecs:
    - CODEC_PROTO
    - CODEC_JSON
  compressions:
    - COMPRESSION_IDENTITY
    - COMPRESSION_GZIP
    - COMPRESSION_DEFLATE
  streamTypes:
    - STREAM_TYPE_UNARY
    - STREAM_TYPE_SERVER_STREAM
    - STREAM_TYPE_CLIENT_STREAM
    - STREAM_TYPE_BIDI_STREAM
    - STREAM_TYPE_HALF_DUPLEX_BIDI_STREAM
  supportsConnectGet: true
  supportsTlsClientCerts: true
```

- [ ] **Step 2: connectconformance バイナリをダウンロード**

```bash
# Linux x86_64 の場合
curl -sL "https://github.com/connectrpc/conformance/releases/download/v1.0.5/connectconformance-v1.0.5-linux-amd64.tar.gz" | tar xz
# バイナリを PATH の通った場所に配置
```

- [ ] **Step 3: サーバーテスト実行**

```bash
connectconformance --mode server \
  --conf tests/ConnectNet.Conformance/config.yaml \
  -v --trace \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode server
```

失敗するテストケースを記録する。

- [ ] **Step 4: クライアントテスト実行**

```bash
connectconformance --mode client \
  --conf tests/ConnectNet.Conformance/config.yaml \
  -v --trace \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode client
```

失敗するテストケースを記録する。

- [ ] **Step 5: 失敗テストの分析と修正**

失敗したテストのトレースを確認し、connect-net のバグを修正する。修正不可能な未サポート機能は `known-failing.txt` に記録。

- [ ] **Step 6: コミット**

```bash
git add tests/ConnectNet.Conformance/config.yaml
git commit -m "feat(conformance): add config.yaml and initial conformance test results"
```

---

### Task 11: バグ修正と conformance テスト通過

**Files:**
- Various (Task 10 で発見されたバグに依存)

- [ ] **Step 1: 失敗テストを1つずつ修正**

各失敗テストについて:
1. `--trace` 出力からリクエスト/レスポンスの期待値と実際の値を比較
2. connect-net のコードを修正
3. 該当テストのみ再実行して通過を確認

- [ ] **Step 2: known-failing.txt 作成（必要に応じて）**

```bash
# tests/ConnectNet.Conformance/known-failing.txt
# 未サポート機能による想定内の失敗をリスト
# 例:
# connect/server/tls_client_certs/...  (TLS client cert not yet supported on client side)
```

- [ ] **Step 3: 全 conformance テスト再実行**

```bash
connectconformance --mode server \
  --conf tests/ConnectNet.Conformance/config.yaml \
  --known-failing tests/ConnectNet.Conformance/known-failing.txt \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode server

connectconformance --mode client \
  --conf tests/ConnectNet.Conformance/config.yaml \
  --known-failing tests/ConnectNet.Conformance/known-failing.txt \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode client
```

Expected: known-failing 以外の全テスト PASS

- [ ] **Step 4: コミット**

```bash
git add -A
git commit -m "fix(conformance): fix protocol compliance issues found by conformance tests"
```

---

## Important Notes for Implementer

1. **Proto 生成コードの名前空間:** conformance proto の `csharp_namespace` は proto ファイルを確認すること。`Connectrpc.Conformance.V1` でない場合はコード内の参照を全て修正する。

2. **protoc-gen-connect-csharp:** conformance の `service.proto` から `ConformanceServiceBase` と `ConformanceServiceClient` を生成する。生成方法は既存の `greeter.proto` のパターン（`tests/ConnectNet.Tests.Proto/`）を参考にする。

3. **ServerHarness の listening ポート取得:** `app.Urls` は空の場合がある。`IServer.Features.Get<IServerAddressesFeature>().Addresses` を使うのが確実。

4. **stdout 汚染の防止:** ASP.NET Core のログが stdout に出るとランナーとの Protobuf 通信を壊す。`builder.Logging.ClearProviders()` または `builder.Logging.AddFilter(_ => false)` で抑制すること。

5. **Task 8-9 は生成コードに強く依存:** Task 4 のビルドが通ってから、生成コードの実際の型名・プロパティ名を確認して実装すること。プラン内のコードは推定ベース。

6. **レスポンスヘッダー伝搬:** 現在の `ConnectUnaryHandler` は `context.ResponseTrailers` を `Trailer-*` ヘッダーとして書くが、`context.ResponseHeaders` の書き込みロジックが無い。ConformanceService がレスポンスヘッダーを設定する必要があるため、ハンドラーに `context.ResponseHeaders` → `response.Headers` の書き込み処理を追加する必要がある。これは Task 11 のバグ修正で対応する。

7. **Task 10-11 は反復的:** conformance テストの初回実行で多数の失敗が出る可能性が高い。1つずつ修正し、修正のたびに再テストする。
