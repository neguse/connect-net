# connect-net 設計ドキュメント

## 概要

connect-netは、Connect RPCプロトコルのC#ネイティブ実装である。Unityクライアント（.NET Standard 2.1）とASP.NETサーバー（.NET 10+）を提供し、protoc pluginによるコード生成を含む。

UnityにおけるgRPC利用の困難さ（HTTP/2ネイティブサポートの欠如）を解決し、HTTP/1.1上で動作するConnect Protocolにより、Unityからのtype-safe RPC通信を実現する。

## 設計判断の経緯

### gRPC生成コードラップ案の却下

grpc-dotnetのgRPC-Web対応（`GrpcWebHandler`/`GrpcWebMiddleware`）のように、gRPC生成コードを透過的にラップしてConnect Protocolに変換する案を検討した。

しかしgRPC-WebとgRPCのワイヤフォーマットはほぼ同一（trailersをボディに入れるだけ）であるのに対し、ConnectとgRPCには根本的な差異がある:

- Unary: Connectはエンベロープなし、gRPCは5バイトエンベロープ必須
- Streaming終端: Connect は flag 0x02 + JSON、gRPC は flag 0x80 + MIMEヘッダ
- エラー: Connect は JSON body + HTTPステータスコード、gRPC は trailers のみ + 常に HTTP 200

これらの差異により「透過レイヤー」ではなく「プロトコルブリッジ」が必要になり、ネイティブ実装と同等以上の複雑さになるため却下。ConnectRPCの他言語実装（Swift, Kotlin, Python, Dart）も全てネイティブ実装 + 専用protoc pluginの構成を採っている。

## プロジェクト構成

```
connect-net/
├── src/
│   ├── ConnectNet/                    # コアライブラリ (.NET Standard 2.1)
│   │   ├── ConnectNet.csproj
│   │   ├── ICodec.cs                  # Codec抽象
│   │   ├── ProtobufCodec.cs           # Google.Protobuf実装
│   │   ├── ConnectError.cs            # Connectエラー型 + コード定義
│   │   ├── Envelope.cs                # 5バイトエンベロープ読み書き
│   │   └── Interceptor.cs            # Interceptor抽象 (Phase 3)
│   │
│   ├── ConnectNet.Client/             # クライアント (.NET Standard 2.1)
│   │   ├── ConnectNet.Client.csproj
│   │   ├── ConnectChannel.cs          # HttpClient管理、RPC呼び出し
│   │   └── CallOptions.cs             # 呼び出しオプション
│   │
│   └── ConnectNet.Server/             # サーバー (.NET 10)
│       ├── ConnectNet.Server.csproj
│       ├── ConnectServiceExtensions.cs # MapConnectService<T>()
│       ├── ConnectUnaryHandler.cs      # Unary RPCハンドラ
│       └── ConnectServerStreamHandler.cs # Server Streaming RPCハンドラ
│
├── tools/
│   └── protoc-gen-connect-csharp/     # protoc plugin (Go)
│       ├── go.mod
│       └── main.go
│
├── tests/
│   ├── ConnectNet.Tests/              # ユニットテスト
│   └── ConnectNet.IntegrationTests/   # connect-goとの結合テスト
│
└── samples/
    ├── Sample.Server/
    └── Sample.Proto/                  # 共通.proto + 生成コード
```

### パッケージ依存関係

- **ConnectNet**: Google.Protobuf のみ。.NET Standard 2.1
- **ConnectNet.Client**: ConnectNet に依存。.NET Standard 2.1
- **ConnectNet.Server**: ConnectNet + Microsoft.AspNetCore.* に依存。.NET 10

## コード生成

### ツールチェーン

protoc pluginはGoで実装する（`protoc-gen-connect-csharp`）。connect-swift, connect-kotlin等と同じ判断で、GoのprotobufライブラリがpluginAPI操作に最適化されている。

2つのplugin併用:
- `protoc-gen-csharp`（Google公式、既存）→ メッセージ型（`HelloRequest`, `HelloResponse`等）
- `protoc-gen-connect-csharp`（本プロジェクト）→ サービススタブ

### 生成コードの構造

`.proto`のServiceごとに1つの`.connect.cs`ファイルを生成する。

```csharp
// サービスメタデータ
public static class GreeterService
{
    public const string FullName = "example.GreeterService";

    public static class Methods
    {
        public const string SayHello = "/example.GreeterService/SayHello";
        public const string ServerStreamHello = "/example.GreeterService/ServerStreamHello";
    }
}

// クライアントインターフェース
public interface IGreeterServiceClient
{
    Task<HelloResponse> SayHelloAsync(
        HelloRequest request, CallOptions? options = null, CancellationToken ct = default);
    IAsyncEnumerable<HelloResponse> ServerStreamHelloAsync(
        HelloRequest request, CallOptions? options = null, CancellationToken ct = default);
}

// クライアント実装
public class GreeterServiceClient : IGreeterServiceClient
{
    private readonly ConnectChannel _channel;
    public GreeterServiceClient(ConnectChannel channel) => _channel = channel;

    public Task<HelloResponse> SayHelloAsync(
        HelloRequest request, CallOptions? options, CancellationToken ct)
        => _channel.UnaryAsync<HelloRequest, HelloResponse>(
            GreeterService.Methods.SayHello, request, options, ct);

    public IAsyncEnumerable<HelloResponse> ServerStreamHelloAsync(
        HelloRequest request, CallOptions? options, CancellationToken ct)
        => _channel.ServerStreamAsync<HelloRequest, HelloResponse>(
            GreeterService.Methods.ServerStreamHello, request, options, ct);
}

// サーバー基底クラス
public abstract class GreeterServiceBase
{
    public virtual Task<HelloResponse> SayHello(
        HelloRequest request, ConnectContext context)
        => throw new ConnectException(ConnectCode.Unimplemented);

    public virtual IAsyncEnumerable<HelloResponse> ServerStreamHello(
        HelloRequest request, ConnectContext context)
        => throw new ConnectException(ConnectCode.Unimplemented);
}

// gRPCアダプタ（Grpc.AspNetCore連携用）
public class GreeterServiceGrpcAdapter : Greeter.GreeterBase
{
    private readonly GreeterServiceBase _impl;
    public GreeterServiceGrpcAdapter(GreeterServiceBase impl) => _impl = impl;

    public override Task<HelloResponse> SayHello(
        HelloRequest request, ServerCallContext context)
        => _impl.SayHello(request, ConnectContext.FromGrpc(context));
}
```

## ワイヤフォーマット

Connect Protocol仕様に準拠する。

### Unary RPC

**リクエスト:**
```
POST /example.GreeterService/SayHello
Content-Type: application/proto
Connect-Protocol-Version: 1
Connect-Timeout-Ms: 5000

[Protobufバイナリ — エンベロープなし]
```

**レスポンス（成功）:**
```
HTTP 200
Content-Type: application/proto
Trailer-Some-Key: some-value

[Protobufバイナリ — エンベロープなし]
```

**レスポンス（エラー）:**
```
HTTP 400
Content-Type: application/json

{"code":"invalid_argument","message":"name is required","details":[]}
```

### Streaming RPC

**リクエスト:**
```
POST /example.GreeterService/ServerStreamHello
Content-Type: application/connect+proto
Connect-Protocol-Version: 1

[0x00][4バイトBE長][Protobufバイナリ]
```

**レスポンス — メッセージ:**
```
HTTP 200
Content-Type: application/connect+proto

[0x00][4バイトBE長][Protobufバイナリ]   ← メッセージ1
[0x00][4バイトBE長][Protobufバイナリ]   ← メッセージ2
...
[0x02][4バイトBE長][JSON]               ← EndStream
```

EndStream JSONフォーマット:
```json
{"metadata":{"trailer-key":["value"]}}
```

エラー時:
```json
{"error":{"code":"internal","message":"..."},"metadata":{}}
```

### エンベロープ

5バイトプレフィックス + ペイロード:
- Byte 0: flags（0x00=通常, 0x01=圧縮, 0x02=EndStream）
- Bytes 1-4: big-endian uint32 ペイロード長
- 以降: ペイロードバイト列

```csharp
public static class Envelope
{
    public static async Task WriteAsync(
        Stream stream, byte flags, ReadOnlyMemory<byte> data, CancellationToken ct);
    public static async Task<(byte flags, byte[] data)?> ReadAsync(
        Stream stream, CancellationToken ct);
}
```

## クライアント アーキテクチャ

### ConnectChannel

ユーザーが触る唯一のエントリポイント。HttpClientをラップし、全RPC種別のHTTP処理を集約。

```csharp
public class ConnectChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly ICodec _codec;

    public ConnectChannel(HttpClient httpClient, string baseUri);

    public async Task<TRes> UnaryAsync<TReq, TRes>(...);
    public IAsyncEnumerable<TRes> ServerStreamAsync<TReq, TRes>(...);
    // Phase 2: ClientStreamAsync, BidiStreamAsync
}
```

### HTTPトランスポート

`System.Net.Http.HttpClient`ベース。Unity側のHTTPバックエンドはユーザーが`HttpMessageHandler`で差し込む:

- デフォルト: Unity標準HTTPスタック（HTTP/1.1、Unary + Server Streaming対応）
- YetAnotherHttpHandler: HTTP/2対応（Client/Bidi Streamingも可能に）

```csharp
// デフォルト
var channel = new ConnectChannel(new HttpClient(), "https://api.example.com");

// YAHA使用
var handler = new YetAnotherHttpHandler();
var channel = new ConnectChannel(new HttpClient(handler), "https://api.example.com");
```

### Unary 内部フロー

1. `_codec.Serialize(request)` → `byte[]`
2. `HttpRequestMessage` 構築（POST、Content-Type: application/proto、Connect-Protocol-Version: 1）
3. `_httpClient.SendAsync(request)`
4. HTTP 200 → `_codec.Deserialize<TRes>(responseBody)`、それ以外 → JSONパースして`ConnectException`をthrow
5. `Trailer-*`ヘッダがあれば`CallOptions`のtrailers dictに格納

### Server Streaming 内部フロー

1. `_codec.Serialize(request)` → `Envelope.Write(0x00, serialized)` → エンベロープ付きbody
2. `_httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)` ← ストリーム即時開始
3. レスポンスストリームを`IAsyncEnumerable<TRes>`として返す:
   - `Envelope.ReadAsync(stream)` → `(flags, data)`
   - flags == 0x02 → EndStream（エラーならthrow、metadataは格納、yield break）
   - それ以外 → `yield return _codec.Deserialize<TRes>(data)`

## サーバー アーキテクチャ

### ASP.NET Core統合

エンドポイントルーティング方式でハンドラを登録する。

```csharp
builder.Services.AddConnectServices();
app.MapConnectService<GreeterServiceImpl>();   // Connect Protocol
app.MapGrpcService<GreeterServiceGrpcAdapter>(); // gRPC（同じ実装を委譲）
```

`MapConnectService<T>()`はprotoc-gen-connect-csharpが生成するメタデータからプロシージャ一覧を取得し、各プロシージャに対応するハンドラをルート登録する。

### Unaryハンドラ

1. Content-Type検証（`application/proto`以外は415）
2. `Connect-Protocol-Version`検証（`"1"`以外は400）
3. `Request.Body` → `_codec.Deserialize<TReq>()`
4. `ConnectContext`作成（リクエストヘッダ、deadline等）
5. `service.Method(request, context)` 呼び出し
6. 成功: HTTP 200 + `_codec.Serialize(response)` + `Trailer-*`ヘッダ
7. `ConnectException` catch: HTTPステータスコード + JSONエラーボディ

### Server Streamingハンドラ

1. Content-Type検証（`application/connect+proto`）
2. `Request.Body` → `Envelope.Read` → `_codec.Deserialize<TReq>()`
3. `service.Method(request, context)` → `IAsyncEnumerable<TRes>`
4. HTTP 200, Content-Type: `application/connect+proto`
5. `await foreach`: `Envelope.Write(0x00, _codec.Serialize(item))` + Flush
6. 正常終了: `Envelope.Write(0x02, {"metadata":{...}})`
7. `ConnectException` catch: `Envelope.Write(0x02, {"error":{...},"metadata":{...}})`

### gRPC共用

protoc-gen-connect-csharpが`GreeterServiceGrpcAdapter`を生成する。このアダプタはgrpc-dotnet生成の`Greeter.GreeterBase`を継承し、内部で`GreeterServiceBase`の実装に委譲する。`ConnectContext.FromGrpc(ServerCallContext)`で文脈を変換する。

これにより同じサービス実装クラスをConnect ProtocolとgRPC両方で利用可能。

## エラーハンドリング

```csharp
public enum ConnectCode
{
    Canceled, Unknown, InvalidArgument, DeadlineExceeded,
    NotFound, AlreadyExists, PermissionDenied, ResourceExhausted,
    FailedPrecondition, Aborted, OutOfRange, Unimplemented,
    Internal, Unavailable, DataLoss, Unauthenticated
}

public class ConnectException : Exception
{
    public ConnectCode Code { get; }
    public IReadOnlyList<ConnectErrorDetail> Details { get; }

    internal string ToJson();
    internal static ConnectException FromJson(string json);
    internal static int ToHttpStatus(ConnectCode code);
    internal static int ToGrpcStatus(ConnectCode code);
    internal static ConnectCode FromGrpcStatus(int grpcStatus);
}

public class ConnectErrorDetail
{
    public string Type { get; }    // protobuf Any の type URL
    public byte[] Value { get; }   // バイナリ値
}
```

**エラーフロー:**
- クライアント: HTTP非200 → JSONパース → `ConnectException` throw
- サーバー（Connect）: `ConnectException` throw → JSON + HTTPステータスで返却
- サーバー（gRPCアダプタ）: `ConnectException` → `RpcException`に変換
- 予期しない例外: `ConnectCode.Internal`として処理

## テスト戦略

### ユニットテスト

`ConnectNet.Tests`プロジェクト。ASP.NET Coreの`WebApplicationFactory`/`TestServer`でインプロセスHTTPテスト。

- `EnvelopeTests.cs` — エンベロープ読み書き
- `ProtobufCodecTests.cs` — シリアライズ/デシリアライズ
- `ConnectErrorTests.cs` — エラーJSON変換、ステータスコードマッピング
- `UnaryHandlerTests.cs` — サーバーUnaryハンドラ
- `ServerStreamHandlerTests.cs` — サーバーStreamingハンドラ

### 結合テスト

`ConnectNet.IntegrationTests`プロジェクト。connect-goの参照サーバーをDockerで起動し、ConnectNet.Clientから通信してプロトコル互換性を検証。

## フェーズ計画

**Phase 1: Core — Unary RPC** (MVP)
- ConnectNet: ICodec, ProtobufCodec, ConnectError, ConnectException
- ConnectNet.Client: ConnectChannel.UnaryAsync
- ConnectNet.Server: MapConnectService, ConnectUnaryHandler
- protoc-gen-connect-csharp: Unary RPCのコード生成
- ユニットテスト + connect-go結合テスト

**Phase 2: Streaming**
- ConnectNet: Envelope読み書き
- ConnectNet.Client: ServerStreamAsync, ClientStreamAsync, BidiStreamAsync
- ConnectNet.Server: ConnectServerStreamHandler, ClientStream, BidiStream
- protoc-gen-connect-csharp: Streaming RPCのコード生成

**Phase 3: Production Readiness**
- gzip圧縮対応
- Interceptor/Middleware
- GET Unary対応
- gRPCアダプタ生成（Grpc.AspNetCore連携）
- タイムアウト・キャンセレーション完全対応
- connect-go相互運用テストスイート

**Phase 4: Ecosystem**
- JSON codec
- Server Reflection
- Health Check
- protovalidate C#実装
- NuGet/UPMパッケージ配布
- ドキュメント・サンプル充実
