# Connect RPC Conformance テスト 設計書

## 概要

connect-net の Connect プロトコル実装が公式 `connectrpc.com/conformance` テストスイートに適合することを検証する。クライアント・サーバー両方のハーネスを C# コンソールアプリとして実装し、conformance テストランナーから自動テストできるようにする。

## スコープ

- **含む:** Connect プロトコルのクライアント・サーバーハーネス、ConformanceService 実装、Deflate 圧縮追加
- **含まない:** gRPC / gRPC-Web プロトコル、HTTP/3、Brotli / Zstd / Snappy 圧縮、CI 統合
- **対応機能:** HTTP/1.1 + HTTP/2、Proto + JSON コーデック、Gzip + Deflate 圧縮、Connect GET、TLS クライアント証明書（サーバー側）、全ストリームタイプ

## プロジェクト構成

```
tests/ConnectNet.Conformance/
├── ConnectNet.Conformance.csproj        # net10.0 コンソールアプリ
├── Program.cs                           # エントリポイント (--mode server|client)
├── ConformanceService.cs                # ConformanceService の6 RPCメソッド実装
├── ServerHarness.cs                     # サーバーモード
├── ClientHarness.cs                     # クライアントモード
├── StdioProtobuf.cs                     # stdin/stdout size-delimited Protobuf
└── config.yaml                          # サポート機能宣言

tests/ConnectNet.Conformance.Proto/
├── ConnectNet.Conformance.Proto.csproj  # conformance proto の C# コード生成
└── proto/connectrpc/conformance/v1/     # vendored from connectrpc/conformance v1.0.5
    ├── service.proto                    # ConformanceService 定義
    ├── client_compat.proto              # ClientCompatRequest/Response
    ├── server_compat.proto              # ServerCompatRequest/Response
    ├── config.proto                     # Features, ConfigCase
    └── suite.proto                      # TestSuite, TestCase

src/ConnectNet/
└── DeflateCompressor.cs                 # 新規: Deflate 圧縮
```

**依存関係:**
- `ConnectNet`, `ConnectNet.Client`, `ConnectNet.Server` — RPC クライアント・サーバー
- `ConnectNet.Conformance.Proto` — conformance proto 生成コード
- `Google.Protobuf` — Protobuf シリアライズ

## stdin/stdout プロトコル

conformance テストランナーとハーネスの通信は **size-delimited Protobuf** で行う：
- 4バイト BigEndian の長さプレフィックス
- 続く N バイトのシリアライズされた Protobuf メッセージ

```csharp
internal static class StdioProtobuf
{
    static T? Read<T>(Stream stdin) where T : IMessage<T>, new();
    static void Write(Stream stdout, IMessage message);
}
```

## サーバーハーネス

```
1. stdin から ServerCompatRequest を1回読む
2. リクエストに応じて ASP.NET Core (Kestrel) サーバーを起動
   - ポート0（OS自動割り当て）
   - TLS 設定: ServerCompatRequest に含まれる証明書素材を使用
     - server cert/key → Kestrel の ListenOptions.UseHttps(X509Certificate2) で設定
     - client CA cert → HttpsConnectionAdapterOptions.ClientCertificateMode = RequireCertificate
     - Microsoft.AspNetCore.Server.Kestrel.Https を使用
   - HTTP/1.1 or HTTP/2 は ServerCompatRequest.HttpVersion に応じて Kestrel の Protocol を設定
   - ConformanceService をマッピング
3. stdout に ServerCompatResponse (host, port, PEM証明書) を書く
4. 終了シグナルまで待機
```

## クライアントハーネス

```
1. stdin から ClientCompatRequest を繰り返し読む（EOFまで）
2. 各リクエストに対して:
   - ConnectChannel を作成
   - 指定されたRPC（Unary/Stream等）を実行
   - 結果を ClientCompatResponse にまとめる
3. stdout に ClientCompatResponse を書く
```

## ConformanceService

6つのRPCメソッドを実装：

| メソッド | 動作 |
|---------|------|
| `Unary` | リクエストの `response_definition` に従いレスポンスを返す |
| `IdempotentUnary` | 同上（HTTP GET 対応） |
| `Unimplemented` | 常に `ConnectCode.Unimplemented` を返す |
| `ClientStream` | クライアントストリームを全て読み、`response_definition` に従い応答 |
| `ServerStream` | `response_definition` に従い複数レスポンスをストリーム |
| `BidiStream` | `response_definition` に従い双方向ストリーム |

各メソッドはリクエストに含まれる `response_definition` の指示（ヘッダー、ペイロード、トレーラー、エラーコード、遅延等）を忠実に再現する。

**Half-Duplex Bidi について:** `STREAM_TYPE_HALF_DUPLEX_BIDI_STREAM` はクライアントが全メッセージを送信完了してからサーバーがレスポンスを返すパターン。`BidiStream` メソッド内で、リクエストストリームを全て読み切ってからレスポンスを返すことで対応する。既存の `ConnectBidiStreamHandler` の変更は不要（ハーネスのサービス実装側の制御）。

connect-net の `protoc-gen-connect-csharp` で生成したサービスベースクラスを使って実装する。これ自体がコード生成の互換性検証にもなる。

**Proto vendoring:** `connectrpc/conformance` リポジトリの v1.0.5 タグから `proto/connectrpc/conformance/v1/` 以下の `.proto` ファイルを取得。`Grpc.Tools` + `protoc-gen-connect-csharp` で C# コードを生成する。

## Deflate 圧縮

```csharp
// src/ConnectNet/DeflateCompressor.cs
public class DeflateCompressor : ICompressor
{
    public string Name => "deflate";
    public byte[] Compress(byte[] data);    // System.IO.Compression.DeflateStream
    public byte[] Decompress(byte[] data);
}
```

## 圧縮レジストリの追加

現在 `ICompressor` は DI で1つだけ登録されている（`GzipCompressor`）。複数の圧縮方式をサポートするため、`ConnectCodecRegistry` と同じパターンで `ConnectCompressorRegistry` を追加する。

```csharp
// src/ConnectNet/ConnectCompressorRegistry.cs
public class ConnectCompressorRegistry
{
    public ICompressor Default { get; }    // Gzip
    public void Register(ICompressor compressor);
    public ICompressor? Get(string name);  // "gzip", "deflate" 等
}
```

**変更箇所:**
- `ConnectServiceExtensions.AddConnectServices` で `ConnectCompressorRegistry` を DI 登録し、`GzipCompressor` と `DeflateCompressor` を両方登録
- `ConnectUnaryHandler`, `ConnectServerStreamHandler`, `ConnectClientStreamHandler`, `ConnectBidiStreamHandler` で `Content-Encoding` / `Connect-Content-Encoding` ヘッダーに応じて適切な `ICompressor` をレジストリから取得
- `ConnectChannel` のクライアント側も同様にレジストリベースに変更

## config.yaml

```yaml
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

## テスト実行

```bash
# connectconformance v1.0.5 をダウンロード
# https://github.com/connectrpc/conformance/releases

# サーバーテスト
connectconformance --mode server --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode server

# クライアントテスト
connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --project tests/ConnectNet.Conformance -- --mode client
```

**known-failing 管理:**
- 初回実行で失敗するテストケースを把握
- バグは修正、未サポート機能は `known-failing.txt` に記録
- 目標: known-failing ゼロ
