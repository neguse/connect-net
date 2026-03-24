# connect-net Phase 1: Unary RPC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Unary RPCが動作するconnect-netのMVP。Unityクライアント（.NET Standard 2.1）からASP.NET Coreサーバー（.NET 10）にConnect ProtocolでUnary RPCを実行できる状態にする。

**Architecture:** ConnectNet（コア）、ConnectNet.Client、ConnectNet.Serverの3プロジェクト構成。コアにCodec抽象・エラー型を置き、クライアントはHttpClientベース、サーバーはASP.NET Coreエンドポイントルーティング方式。Phase 1ではprotoc pluginによるコード生成は行わず、手書きのサンプルスタブで動作検証する。

**Tech Stack:** C# / .NET Standard 2.1 / .NET 10 / ASP.NET Core / Google.Protobuf / xUnit

**Spec:** `docs/superpowers/specs/2026-03-24-connect-net-design.md`

---

## File Structure

```
connect-net/
├── connect-net.sln
├── src/
│   ├── ConnectNet/                         # コアライブラリ (.NET Standard 2.1)
│   │   ├── ConnectNet.csproj
│   │   ├── ICodec.cs                       # Codec抽象インターフェース
│   │   ├── ProtobufCodec.cs                # Google.Protobuf実装
│   │   ├── ConnectCode.cs                  # エラーコードenum
│   │   ├── ConnectException.cs             # Connect例外型 + JSON変換
│   │   ├── ConnectErrorDetail.cs           # エラー詳細型
│   │   └── ConnectContext.cs               # RPCコンテキスト（ヘッダ、deadline）
│   │
│   ├── ConnectNet.Client/                  # クライアント (.NET Standard 2.1)
│   │   ├── ConnectNet.Client.csproj
│   │   ├── ConnectChannel.cs               # HttpClient管理 + UnaryAsync
│   │   └── CallOptions.cs                  # 呼び出しオプション
│   │
│   └── ConnectNet.Server/                  # サーバー (.NET 10)
│       ├── ConnectNet.Server.csproj
│       ├── ConnectServiceExtensions.cs     # AddConnectServices / MapConnectService<T>
│       ├── ConnectUnaryHandler.cs          # Unary RPCハンドラ
│       ├── IConnectService.cs              # サービスメタデータインターフェース
│       └── ConnectServiceDescriptor.cs     # プロシージャ記述子
│
├── tests/
│   ├── ConnectNet.Tests/                   # ユニットテスト (.NET 10)
│   │   ├── ConnectNet.Tests.csproj
│   │   ├── ProtobufCodecTests.cs
│   │   ├── ConnectExceptionTests.cs
│   │   └── UnaryHandlerTests.cs            # TestServerでの結合テスト
│   │
│   └── ConnectNet.Tests.Proto/             # テスト用.proto生成コード
│       ├── ConnectNet.Tests.Proto.csproj
│       ├── greeter.proto
│       └── Generated/                      # protoc生成ファイル
│
└── samples/
    └── Sample.Server/                      # サンプルサーバー (.NET 10)
        ├── Sample.Server.csproj
        ├── Program.cs
        └── GreeterServiceImpl.cs           # 手書きサービス実装
```

---

### Task 1: ソリューションとプロジェクト骨格

**Files:**
- Create: `connect-net.sln`
- Create: `src/ConnectNet/ConnectNet.csproj`
- Create: `src/ConnectNet.Client/ConnectNet.Client.csproj`
- Create: `src/ConnectNet.Server/ConnectNet.Server.csproj`
- Create: `tests/ConnectNet.Tests/ConnectNet.Tests.csproj`
- Create: `tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj`

- [ ] **Step 1: ConnectNet.csprojを作成**

```bash
cd /home/neguse/ghq/github.com/neguse/connect-net
mkdir -p src/ConnectNet
```

`src/ConnectNet/ConnectNet.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="System.Text.Json" Version="8.*" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: ConnectNet.Client.csprojを作成**

```bash
mkdir -p src/ConnectNet.Client
```

`src/ConnectNet.Client/ConnectNet.Client.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../ConnectNet/ConnectNet.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: ConnectNet.Server.csprojを作成**

```bash
mkdir -p src/ConnectNet.Server
```

`src/ConnectNet.Server/ConnectNet.Server.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../ConnectNet/ConnectNet.csproj" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: テスト用Protoプロジェクトを作成**

```bash
mkdir -p tests/ConnectNet.Tests.Proto/Generated
```

`tests/ConnectNet.Tests.Proto/greeter.proto`:
```protobuf
syntax = "proto3";
package example;
option csharp_namespace = "ConnectNet.Tests.Proto";

message HelloRequest {
  string name = 1;
}

message HelloResponse {
  string message = 1;
}

service GreeterService {
  rpc SayHello (HelloRequest) returns (HelloResponse);
}
```

`tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
    <Protobuf Include="greeter.proto" GrpcServices="None" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: テストプロジェクトを作成**

```bash
mkdir -p tests/ConnectNet.Tests
```

`tests/ConnectNet.Tests/ConnectNet.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.*" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ConnectNet/ConnectNet.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Client/ConnectNet.Client.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Server/ConnectNet.Server.csproj" />
    <ProjectReference Include="../ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 6: ソリューションファイルを作成**

```bash
cd /home/neguse/ghq/github.com/neguse/connect-net
dotnet new sln -n connect-net
dotnet sln add src/ConnectNet/ConnectNet.csproj
dotnet sln add src/ConnectNet.Client/ConnectNet.Client.csproj
dotnet sln add src/ConnectNet.Server/ConnectNet.Server.csproj
dotnet sln add tests/ConnectNet.Tests/ConnectNet.Tests.csproj
dotnet sln add tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj
```

- [ ] **Step 7: ビルド確認**

Run: `dotnet build connect-net.sln`
Expected: BUILD SUCCEEDED（警告は許容）

- [ ] **Step 8: コミット**

```bash
git add -A
git commit -m "feat: scaffold solution with ConnectNet, Client, Server, and test projects"
```

---

### Task 2: ICodec + ProtobufCodec

**Files:**
- Create: `src/ConnectNet/ICodec.cs`
- Create: `src/ConnectNet/ProtobufCodec.cs`
- Create: `tests/ConnectNet.Tests/ProtobufCodecTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/ProtobufCodecTests.cs`:
```csharp
using ConnectNet;
using ConnectNet.Tests.Proto;
using Xunit;

namespace ConnectNet.Tests;

public class ProtobufCodecTests
{
    private readonly ProtobufCodec _codec = new();

    [Fact]
    public void Name_ReturnsProto()
    {
        Assert.Equal("proto", _codec.Name);
    }

    [Fact]
    public void Serialize_RoundTrips()
    {
        var request = new HelloRequest { Name = "test" };
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("test", deserialized.Name);
    }

    [Fact]
    public void Serialize_EmptyMessage_ReturnsEmptyBytes()
    {
        var request = new HelloRequest();
        var bytes = _codec.Serialize(request);
        var deserialized = _codec.Deserialize<HelloRequest>(bytes);
        Assert.Equal("", deserialized.Name);
    }

    [Fact]
    public void Deserialize_InvalidBytes_Throws()
    {
        var badBytes = new byte[] { 0xFF, 0xFF, 0xFF };
        Assert.Throws<Google.Protobuf.InvalidProtocolBufferException>(
            () => _codec.Deserialize<HelloRequest>(badBytes));
    }
}
```

- [ ] **Step 2: テスト失敗を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ProtobufCodecTests -v m`
Expected: FAIL（ConnectNet名前空間にICodec/ProtobufCodecが存在しない）

- [ ] **Step 3: ICodecを実装**

`src/ConnectNet/ICodec.cs`:
```csharp
using Google.Protobuf;

namespace ConnectNet;

public interface ICodec
{
    string Name { get; }
    byte[] Serialize(IMessage message);
    T Deserialize<T>(byte[] data) where T : IMessage<T>, new();
    IMessage Deserialize(byte[] data, MessageParser parser);
}
```

- [ ] **Step 4: ProtobufCodecを実装**

`src/ConnectNet/ProtobufCodec.cs`:
```csharp
using Google.Protobuf;

namespace ConnectNet;

public class ProtobufCodec : ICodec
{
    public string Name => "proto";

    public byte[] Serialize(IMessage message)
    {
        return message.ToByteArray();
    }

    public T Deserialize<T>(byte[] data) where T : IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());
        return parser.ParseFrom(data);
    }

    public IMessage Deserialize(byte[] data, MessageParser parser)
    {
        return parser.ParseFrom(data);
    }
}
```

- [ ] **Step 5: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ProtobufCodecTests -v m`
Expected: 4 tests passed

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet/ICodec.cs src/ConnectNet/ProtobufCodec.cs tests/ConnectNet.Tests/ProtobufCodecTests.cs
git commit -m "feat: add ICodec interface and ProtobufCodec implementation"
```

---

### Task 3: ConnectCode + ConnectException + ConnectErrorDetail

**Files:**
- Create: `src/ConnectNet/ConnectCode.cs`
- Create: `src/ConnectNet/ConnectException.cs`
- Create: `src/ConnectNet/ConnectErrorDetail.cs`
- Create: `tests/ConnectNet.Tests/ConnectExceptionTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/ConnectExceptionTests.cs`:
```csharp
using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class ConnectExceptionTests
{
    [Fact]
    public void ToJson_BasicError()
    {
        var ex = new ConnectException(ConnectCode.InvalidArgument, "bad input");
        var json = ex.ToJson();
        Assert.Contains("\"code\":\"invalid_argument\"", json);
        Assert.Contains("\"message\":\"bad input\"", json);
    }

    [Fact]
    public void FromJson_BasicError()
    {
        var json = "{\"code\":\"not_found\",\"message\":\"missing\"}";
        var ex = ConnectException.FromJson(json);
        Assert.Equal(ConnectCode.NotFound, ex.Code);
        Assert.Equal("missing", ex.Message);
    }

    [Fact]
    public void FromJson_WithDetails()
    {
        var json = "{\"code\":\"internal\",\"message\":\"err\",\"details\":[{\"type\":\"type.googleapis.com/example.Foo\",\"value\":\"AQID\"}]}";
        var ex = ConnectException.FromJson(json);
        Assert.Single(ex.Details);
        Assert.Equal("type.googleapis.com/example.Foo", ex.Details[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, ex.Details[0].Value);
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var original = new ConnectException(
            ConnectCode.PermissionDenied, "denied",
            new[] { new ConnectErrorDetail("type.googleapis.com/x", new byte[] { 42 }) });
        var restored = ConnectException.FromJson(original.ToJson());
        Assert.Equal(original.Code, restored.Code);
        Assert.Equal(original.Message, restored.Message);
        Assert.Equal(original.Details.Count, restored.Details.Count);
    }

    [Theory]
    [InlineData(ConnectCode.InvalidArgument, 400)]
    [InlineData(ConnectCode.Unauthenticated, 401)]
    [InlineData(ConnectCode.PermissionDenied, 403)]
    [InlineData(ConnectCode.NotFound, 404)]
    [InlineData(ConnectCode.AlreadyExists, 409)]
    [InlineData(ConnectCode.ResourceExhausted, 429)]
    [InlineData(ConnectCode.FailedPrecondition, 400)]
    [InlineData(ConnectCode.Aborted, 409)]
    [InlineData(ConnectCode.OutOfRange, 400)]
    [InlineData(ConnectCode.Unimplemented, 501)]
    [InlineData(ConnectCode.Internal, 500)]
    [InlineData(ConnectCode.Unavailable, 503)]
    [InlineData(ConnectCode.DataLoss, 500)]
    [InlineData(ConnectCode.DeadlineExceeded, 504)]
    [InlineData(ConnectCode.Canceled, 408)]
    [InlineData(ConnectCode.Unknown, 500)]
    public void ToHttpStatus_MapsCorrectly(ConnectCode code, int expectedStatus)
    {
        Assert.Equal(expectedStatus, ConnectException.ToHttpStatus(code));
    }

    [Fact]
    public void CodeToString_UsesSnakeCase()
    {
        Assert.Equal("invalid_argument", ConnectException.CodeToString(ConnectCode.InvalidArgument));
        Assert.Equal("not_found", ConnectException.CodeToString(ConnectCode.NotFound));
        Assert.Equal("deadline_exceeded", ConnectException.CodeToString(ConnectCode.DeadlineExceeded));
    }

    [Fact]
    public void CodeFromString_ParsesSnakeCase()
    {
        Assert.Equal(ConnectCode.InvalidArgument, ConnectException.CodeFromString("invalid_argument"));
        Assert.Equal(ConnectCode.NotFound, ConnectException.CodeFromString("not_found"));
    }

    [Fact]
    public void CodeFromString_Unknown_ForInvalidInput()
    {
        Assert.Equal(ConnectCode.Unknown, ConnectException.CodeFromString("bogus"));
    }
}
```

- [ ] **Step 2: テスト失敗を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ConnectExceptionTests -v m`
Expected: FAIL

- [ ] **Step 3: ConnectCodeを実装**

`src/ConnectNet/ConnectCode.cs`:
```csharp
namespace ConnectNet;

public enum ConnectCode
{
    Canceled,
    Unknown,
    InvalidArgument,
    DeadlineExceeded,
    NotFound,
    AlreadyExists,
    PermissionDenied,
    ResourceExhausted,
    FailedPrecondition,
    Aborted,
    OutOfRange,
    Unimplemented,
    Internal,
    Unavailable,
    DataLoss,
    Unauthenticated
}
```

- [ ] **Step 4: ConnectErrorDetailを実装**

`src/ConnectNet/ConnectErrorDetail.cs`:
```csharp
namespace ConnectNet;

public class ConnectErrorDetail
{
    public string Type { get; }
    public byte[] Value { get; }

    public ConnectErrorDetail(string type, byte[] value)
    {
        Type = type;
        Value = value;
    }
}
```

- [ ] **Step 5: ConnectExceptionを実装**

`src/ConnectNet/ConnectException.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ConnectNet;

public class ConnectException : Exception
{
    public ConnectCode Code { get; }
    public IReadOnlyList<ConnectErrorDetail> Details { get; }

    public ConnectException(ConnectCode code, string? message = null, IEnumerable<ConnectErrorDetail>? details = null)
        : base(message ?? code.ToString())
    {
        Code = code;
        Details = details?.ToList().AsReadOnly() ?? (IReadOnlyList<ConnectErrorDetail>)Array.Empty<ConnectErrorDetail>();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("code", CodeToString(Code));
        writer.WriteString("message", Message);
        if (Details.Count > 0)
        {
            writer.WriteStartArray("details");
            foreach (var detail in Details)
            {
                writer.WriteStartObject();
                writer.WriteString("type", detail.Type);
                writer.WriteString("value", Convert.ToBase64String(detail.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ConnectException FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeProp)
            ? CodeFromString(codeProp.GetString() ?? "")
            : ConnectCode.Unknown;

        var message = root.TryGetProperty("message", out var msgProp)
            ? msgProp.GetString() ?? ""
            : "";

        var details = new List<ConnectErrorDetail>();
        if (root.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in detailsProp.EnumerateArray())
            {
                var type = d.GetProperty("type").GetString() ?? "";
                var value = Convert.FromBase64String(d.GetProperty("value").GetString() ?? "");
                details.Add(new ConnectErrorDetail(type, value));
            }
        }

        return new ConnectException(code, message, details);
    }

    public static int ToHttpStatus(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => 408,
        ConnectCode.Unknown => 500,
        ConnectCode.InvalidArgument => 400,
        ConnectCode.DeadlineExceeded => 504,
        ConnectCode.NotFound => 404,
        ConnectCode.AlreadyExists => 409,
        ConnectCode.PermissionDenied => 403,
        ConnectCode.ResourceExhausted => 429,
        ConnectCode.FailedPrecondition => 400,
        ConnectCode.Aborted => 409,
        ConnectCode.OutOfRange => 400,
        ConnectCode.Unimplemented => 501,
        ConnectCode.Internal => 500,
        ConnectCode.Unavailable => 503,
        ConnectCode.DataLoss => 500,
        ConnectCode.Unauthenticated => 401,
        _ => 500,
    };

    public static string CodeToString(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => "canceled",
        ConnectCode.Unknown => "unknown",
        ConnectCode.InvalidArgument => "invalid_argument",
        ConnectCode.DeadlineExceeded => "deadline_exceeded",
        ConnectCode.NotFound => "not_found",
        ConnectCode.AlreadyExists => "already_exists",
        ConnectCode.PermissionDenied => "permission_denied",
        ConnectCode.ResourceExhausted => "resource_exhausted",
        ConnectCode.FailedPrecondition => "failed_precondition",
        ConnectCode.Aborted => "aborted",
        ConnectCode.OutOfRange => "out_of_range",
        ConnectCode.Unimplemented => "unimplemented",
        ConnectCode.Internal => "internal",
        ConnectCode.Unavailable => "unavailable",
        ConnectCode.DataLoss => "data_loss",
        ConnectCode.Unauthenticated => "unauthenticated",
        _ => "unknown",
    };

    public static ConnectCode CodeFromString(string s) => s switch
    {
        "canceled" => ConnectCode.Canceled,
        "unknown" => ConnectCode.Unknown,
        "invalid_argument" => ConnectCode.InvalidArgument,
        "deadline_exceeded" => ConnectCode.DeadlineExceeded,
        "not_found" => ConnectCode.NotFound,
        "already_exists" => ConnectCode.AlreadyExists,
        "permission_denied" => ConnectCode.PermissionDenied,
        "resource_exhausted" => ConnectCode.ResourceExhausted,
        "failed_precondition" => ConnectCode.FailedPrecondition,
        "aborted" => ConnectCode.Aborted,
        "out_of_range" => ConnectCode.OutOfRange,
        "unimplemented" => ConnectCode.Unimplemented,
        "internal" => ConnectCode.Internal,
        "unavailable" => ConnectCode.Unavailable,
        "data_loss" => ConnectCode.DataLoss,
        "unauthenticated" => ConnectCode.Unauthenticated,
        _ => ConnectCode.Unknown,
    };
}
```

- [ ] **Step 6: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ConnectExceptionTests -v m`
Expected: All tests passed

- [ ] **Step 7: コミット**

```bash
git add src/ConnectNet/ConnectCode.cs src/ConnectNet/ConnectException.cs src/ConnectNet/ConnectErrorDetail.cs tests/ConnectNet.Tests/ConnectExceptionTests.cs
git commit -m "feat: add ConnectCode, ConnectException with JSON serialization, and HTTP status mapping"
```

---

### Task 4: ConnectContext + CallOptions

**Files:**
- Create: `src/ConnectNet/ConnectContext.cs`
- Create: `src/ConnectNet.Client/CallOptions.cs`

- [ ] **Step 1: ConnectContextを実装**

`src/ConnectNet/ConnectContext.cs`:
```csharp
using System.Collections.Generic;
using System.Threading;

namespace ConnectNet;

public class ConnectContext
{
    public IDictionary<string, string> RequestHeaders { get; }
    public IDictionary<string, string> ResponseHeaders { get; }
    public IDictionary<string, string> ResponseTrailers { get; }
    public CancellationToken CancellationToken { get; }

    public ConnectContext(
        IDictionary<string, string>? requestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        RequestHeaders = requestHeaders ?? new Dictionary<string, string>();
        ResponseHeaders = new Dictionary<string, string>();
        ResponseTrailers = new Dictionary<string, string>();
        CancellationToken = cancellationToken;
    }
}
```

- [ ] **Step 2: CallOptionsを実装**

`src/ConnectNet.Client/CallOptions.cs`:
```csharp
using System;
using System.Collections.Generic;

namespace ConnectNet.Client;

public class CallOptions
{
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
    public TimeSpan? Timeout { get; set; }
    public IDictionary<string, string> ResponseTrailers { get; } = new Dictionary<string, string>();
}
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build connect-net.sln`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: コミット**

```bash
git add src/ConnectNet/ConnectContext.cs src/ConnectNet.Client/CallOptions.cs
git commit -m "feat: add ConnectContext and CallOptions"
```

---

### Task 5: ConnectChannel（クライアント Unary RPC）

**Files:**
- Create: `src/ConnectNet.Client/ConnectChannel.cs`
- Create: `tests/ConnectNet.Tests/ConnectChannelTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/ConnectChannelTests.cs`:
```csharp
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

    private class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
```

- [ ] **Step 2: テスト失敗を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ConnectChannelTests -v m`
Expected: FAIL（ConnectChannelが存在しない）

- [ ] **Step 3: ConnectChannelを実装**

`src/ConnectNet.Client/ConnectChannel.cs`:
```csharp
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Client;

public class ConnectChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;

    public ConnectChannel(HttpClient httpClient, string baseUri, ICodec? codec = null)
    {
        _httpClient = httpClient;
        _baseUri = new Uri(baseUri.TrimEnd('/'));
        _codec = codec ?? new ProtobufCodec();
    }

    public async Task<TRes> UnaryAsync<TReq, TRes>(
        string procedure,
        TReq request,
        CallOptions? options = null,
        CancellationToken ct = default)
        where TReq : IMessage<TReq>
        where TRes : IMessage<TRes>, new()
    {
        var body = _codec.Serialize(request);
        var uri = new Uri(_baseUri, procedure);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Content = new ByteArrayContent(body);
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue($"application/{_codec.Name}");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        if (options?.Timeout is TimeSpan timeout)
        {
            httpRequest.Headers.Add("Connect-Timeout-Ms", ((long)timeout.TotalMilliseconds).ToString());
        }

        if (options?.Headers != null)
        {
            foreach (var header in options.Headers)
            {
                httpRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        var httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var errorBody = await httpResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw ConnectException.FromJson(errorBody);
        }

        var responseBytes = await httpResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var result = _codec.Deserialize<TRes>(responseBytes);

        // Extract Trailer-* headers
        if (options != null)
        {
            foreach (var header in httpResponse.Headers)
            {
                if (header.Key.StartsWith("Trailer-", StringComparison.OrdinalIgnoreCase))
                {
                    var trailerName = header.Key.Substring("Trailer-".Length);
                    options.ResponseTrailers[trailerName] = string.Join(",", header.Value);
                }
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter ConnectChannelTests -v m`
Expected: 3 tests passed

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Client/ConnectChannel.cs tests/ConnectNet.Tests/ConnectChannelTests.cs
git commit -m "feat: add ConnectChannel with UnaryAsync"
```

---

### Task 6: サーバー — IConnectService + ConnectServiceDescriptor

**Files:**
- Create: `src/ConnectNet.Server/IConnectService.cs`
- Create: `src/ConnectNet.Server/ConnectServiceDescriptor.cs`

- [ ] **Step 1: IConnectServiceを実装**

サービスメタデータをランタイムに提供するインターフェース。protoc-gen-connect-csharpが将来自動生成するが、Phase 1では手書きで実装する。

`src/ConnectNet.Server/IConnectService.cs`:
```csharp
using System.Collections.Generic;

namespace ConnectNet.Server;

public interface IConnectServiceDefinition
{
    string ServiceName { get; }
    IReadOnlyList<ConnectMethodDescriptor> Methods { get; }
}
```

- [ ] **Step 2: ConnectServiceDescriptorを実装**

`src/ConnectNet.Server/ConnectServiceDescriptor.cs`:
```csharp
using System;
using System.Threading.Tasks;
using ConnectNet;
using Google.Protobuf;

namespace ConnectNet.Server;

public class ConnectMethodDescriptor
{
    public string Procedure { get; }
    public MessageParser RequestParser { get; }
    public Func<object, IMessage, ConnectContext, Task<IMessage>> Handler { get; }

    public ConnectMethodDescriptor(
        string procedure,
        MessageParser requestParser,
        Func<object, IMessage, ConnectContext, Task<IMessage>> handler)
    {
        Procedure = procedure;
        RequestParser = requestParser;
        Handler = handler;
    }
}
```

- [ ] **Step 3: ビルド確認**

Run: `dotnet build src/ConnectNet.Server/ConnectNet.Server.csproj`
Expected: BUILD SUCCEEDED

- [ ] **Step 4: コミット**

```bash
git add src/ConnectNet.Server/IConnectService.cs src/ConnectNet.Server/ConnectServiceDescriptor.cs
git commit -m "feat: add IConnectServiceDefinition and ConnectMethodDescriptor"
```

---

### Task 7: サーバー — ConnectUnaryHandler + ConnectServiceExtensions

**Files:**
- Create: `src/ConnectNet.Server/ConnectUnaryHandler.cs`
- Create: `src/ConnectNet.Server/ConnectServiceExtensions.cs`
- Create: `tests/ConnectNet.Tests/UnaryHandlerTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/UnaryHandlerTests.cs`:
```csharp
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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

public class UnaryHandlerTests
{
    private TestServer CreateTestServer()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<TestGreeterService>();
        var app = builder.Build();
        app.MapConnectService<TestGreeterService>(TestGreeterServiceDefinition.Instance);
        app.Start();
        return app.GetTestServer();
    }

    [Fact]
    public async Task Unary_Success()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "World" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/proto", response.Content.Headers.ContentType!.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        var result = HelloResponse.Parser.ParseFrom(bytes);
        Assert.Equal("Hello World", result.Message);
    }

    [Fact]
    public async Task Unary_ServiceThrowsConnectException_ReturnsJsonError()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "" }; // triggers not_found in test service
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(body);
        Assert.Equal("not_found", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unary_MissingProtocolVersion_Returns400()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        var request = new HelloRequest { Name = "test" };
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new ByteArrayContent(request.ToByteArray());
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/proto");
        // No Connect-Protocol-Version header

        var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unary_WrongContentType_Returns415()
    {
        using var server = CreateTestServer();
        using var client = server.CreateClient();

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/example.GreeterService/SayHello");
        httpRequest.Content = new StringContent("{}");
        httpRequest.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        httpRequest.Headers.Add("Connect-Protocol-Version", "1");

        var response = await client.SendAsync(httpRequest);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // --- Test service and definition (hand-written, future: generated by protoc plugin) ---

    private class TestGreeterService
    {
        public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
        {
            if (string.IsNullOrEmpty(request.Name))
                throw new ConnectException(ConnectCode.NotFound, "name required");
            return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}" });
        }
    }

    private class TestGreeterServiceDefinition : IConnectServiceDefinition
    {
        public static TestGreeterServiceDefinition Instance { get; } = new();

        public string ServiceName => "example.GreeterService";
        public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
        {
            new ConnectMethodDescriptor(
                "/example.GreeterService/SayHello",
                HelloRequest.Parser,
                async (service, req, ctx) => (IMessage)await ((TestGreeterService)service)
                    .SayHello((HelloRequest)req, ctx))
        };
    }
}
```

Note: 括弧の対応 — `new ConnectMethodDescriptor(` を `)` で閉じ、`new[]{ }` で配列を閉じる。`((TestGreeterService)service)` のダブル括弧はキャスト式。

- [ ] **Step 2: テスト失敗を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter UnaryHandlerTests -v m`
Expected: FAIL（AddConnectServices、MapConnectServiceが存在しない）

- [ ] **Step 3: ConnectUnaryHandlerを実装**

`src/ConnectNet.Server/ConnectUnaryHandler.cs`:
```csharp
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;

namespace ConnectNet.Server;

internal static class ConnectUnaryHandler
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
            var error = new ConnectException(ConnectCode.InvalidArgument, "missing or invalid Connect-Protocol-Version header");
            await response.WriteAsync(error.ToJson());
            return;
        }

        // Validate Content-Type
        var contentType = request.ContentType;
        if (contentType == null || !contentType.StartsWith($"application/{codec.Name}", StringComparison.OrdinalIgnoreCase))
        {
            response.StatusCode = 415;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.InvalidArgument, $"unsupported content type: {contentType}");
            await response.WriteAsync(error.ToJson());
            return;
        }

        try
        {
            // Read and deserialize request
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            var requestBytes = ms.ToArray();
            var requestMessage = codec.Deserialize(requestBytes, method.RequestParser);

            // Create context
            var context = new ConnectContext(cancellationToken: httpContext.RequestAborted);

            // Invoke service method
            var responseMessage = await method.Handler(service, requestMessage, context);

            // Write response
            response.StatusCode = 200;
            response.ContentType = $"application/{codec.Name}";

            // Write Trailer-* headers
            foreach (var trailer in context.ResponseTrailers)
            {
                response.Headers[$"Trailer-{trailer.Key}"] = trailer.Value;
            }

            var responseBytes = codec.Serialize(responseMessage);
            await response.Body.WriteAsync(responseBytes);
        }
        catch (ConnectException ex)
        {
            response.StatusCode = ConnectException.ToHttpStatus(ex.Code);
            response.ContentType = "application/json";
            await response.WriteAsync(ex.ToJson());
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            response.ContentType = "application/json";
            var error = new ConnectException(ConnectCode.Internal, "internal error");
            await response.WriteAsync(error.ToJson());
        }
    }
}
```

- [ ] **Step 4: ConnectServiceExtensionsを実装**

`src/ConnectNet.Server/ConnectServiceExtensions.cs`:
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

public static class ConnectServiceExtensions
{
    public static IServiceCollection AddConnectServices(this IServiceCollection services)
    {
        services.AddSingleton<ICodec, ProtobufCodec>();
        return services;
    }

    public static void MapConnectService<TService>(
        this IEndpointRouteBuilder builder,
        IConnectServiceDefinition definition)
        where TService : class
    {
        foreach (var method in definition.Methods)
        {
            builder.MapPost(method.Procedure, async (HttpContext context) =>
            {
                var service = context.RequestServices.GetRequiredService<TService>();
                var codec = context.RequestServices.GetRequiredService<ICodec>();
                await ConnectUnaryHandler.HandleAsync(context, method, service, codec);
            });
        }
    }
}
```

- [ ] **Step 5: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter UnaryHandlerTests -v m`
Expected: 4 tests passed

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet.Server/ConnectUnaryHandler.cs src/ConnectNet.Server/ConnectServiceExtensions.cs tests/ConnectNet.Tests/UnaryHandlerTests.cs
git commit -m "feat: add ConnectUnaryHandler and MapConnectService for ASP.NET Core"
```

---

### Task 8: End-to-End テスト — クライアント → サーバー

**Files:**
- Create: `tests/ConnectNet.Tests/EndToEndTests.cs`

- [ ] **Step 1: テストを書く**

`tests/ConnectNet.Tests/EndToEndTests.cs`:
```csharp
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
        var channel = new ConnectChannel(httpClient, server.BaseAddress.ToString());
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
```

- [ ] **Step 2: テスト通過を確認**

Run: `dotnet test tests/ConnectNet.Tests --filter EndToEndTests -v m`
Expected: 3 tests passed

- [ ] **Step 3: コミット**

```bash
git add tests/ConnectNet.Tests/EndToEndTests.cs
git commit -m "test: add end-to-end unary RPC tests (ConnectChannel → ConnectUnaryHandler)"
```

---

### Task 9: サンプルサーバー

**Files:**
- Create: `samples/Sample.Server/Sample.Server.csproj`
- Create: `samples/Sample.Server/Program.cs`
- Create: `samples/Sample.Server/GreeterServiceImpl.cs`

- [ ] **Step 1: プロジェクトを作成**

`samples/Sample.Server/Sample.Server.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ConnectNet/ConnectNet.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Server/ConnectNet.Server.csproj" />
    <ProjectReference Include="../../tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: サービス実装を作成**

`samples/Sample.Server/GreeterServiceImpl.cs`:
```csharp
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;

namespace Sample.Server;

public class GreeterServiceImpl
{
    public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
    {
        if (string.IsNullOrEmpty(request.Name))
            throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

        return Task.FromResult(new HelloResponse
        {
            Message = $"Hello {request.Name} from connect-net!"
        });
    }
}

public class GreeterServiceDefinition : IConnectServiceDefinition
{
    public static GreeterServiceDefinition Instance { get; } = new();
    public string ServiceName => "example.GreeterService";
    public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
    {
        new ConnectMethodDescriptor(
            "/example.GreeterService/SayHello",
            HelloRequest.Parser,
            async (service, req, ctx) => (IMessage)await ((GreeterServiceImpl)service)
                .SayHello((HelloRequest)req, ctx))
    };
}
```

- [ ] **Step 3: Program.csを作成**

`samples/Sample.Server/Program.cs`:
```csharp
using ConnectNet.Server;
using Sample.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConnectServices();
builder.Services.AddSingleton<GreeterServiceImpl>();

var app = builder.Build();
app.MapConnectService<GreeterServiceImpl>(GreeterServiceDefinition.Instance);

app.Run();
```

- [ ] **Step 4: ソリューションに追加してビルド確認**

```bash
dotnet sln connect-net.sln add samples/Sample.Server/Sample.Server.csproj
dotnet build samples/Sample.Server/Sample.Server.csproj
```

Expected: BUILD SUCCEEDED

- [ ] **Step 5: curlで動作確認**

```bash
# 別ターミナルでサーバー起動
# dotnet run --project samples/Sample.Server

# curlで確認（バイナリprotobufなので、成功すればHTTP 200が返る）
# echo -n '0a05576f726c64' | xxd -r -p | curl -s -o /dev/null -w '%{http_code}' \
#   -X POST http://localhost:5000/example.GreeterService/SayHello \
#   -H 'Content-Type: application/proto' \
#   -H 'Connect-Protocol-Version: 1' \
#   --data-binary @-
# Expected: 200
```

- [ ] **Step 6: コミット**

```bash
git add samples/Sample.Server/
git commit -m "feat: add sample server with hand-written GreeterService"
```

---

### Task 10: 全テスト実行 + 最終確認

- [ ] **Step 1: 全テスト実行**

Run: `dotnet test connect-net.sln -v m`
Expected: All tests passed (ProtobufCodecTests + ConnectExceptionTests + ConnectChannelTests + UnaryHandlerTests + EndToEndTests)

- [ ] **Step 2: ビルド警告の確認**

Run: `dotnet build connect-net.sln -warnaserror`
Expected: BUILD SUCCEEDED（または許容可能な警告のみ）

- [ ] **Step 3: コミット（必要に応じて修正後）**

```bash
git add -A
git commit -m "chore: fix any remaining warnings and finalize Phase 1 MVP"
```
