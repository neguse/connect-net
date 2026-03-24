## Product Requirements Document: connect-net

**Author**: neguse
**Date**: 2026-03-24
**Status**: Draft

---

### 1. Executive Summary

connect-netは、Connect RPCプロトコルのC#実装であり、Unityクライアント（.NET Standard 2.1）とASP.NETサーバー（.NET 10+）の両方を提供する。UnityにおけるgRPCの利用が困難な現状（HTTP/2ネイティブサポートの欠如、ネイティブライブラリ依存の複雑さ）を解決し、HTTP/1.1上でも動作するConnect Protocolにより、Unityからのtype-safeなRPC通信を実現する。

### 2. Background & Context

**課題**: Unityでは標準的なgRPC C#クライアント（Grpc.Net.Client）の利用が難しい。
- UnityのHTTPスタックはHTTP/2を十分にサポートしていない
- grpc-dotnetはHTTP/2を前提としており、Unityでの利用にはYetAnotherHttpHandlerのようなネイティブ依存ライブラリが必要
- ネイティブ依存はプラットフォームごとのビルド管理を複雑にする

**Connect Protocolの利点**:
- HTTP/1.1上で動作可能（Unaryは標準的なPOST/GET、ストリーミングはchunked transfer）
- HTTP/2でも動作し、サーバー間通信でも使える
- gRPCと同じProtobuf IDLからコード生成でき、既存のgRPCサーバーとも互換性がある
- ブラウザフレンドリーで、curlでデバッグ可能なシンプルなワイヤフォーマット

**参照実装**:
- [connect-go](https://github.com/connectrpc/connect-go) — Go公式実装（本プロジェクトの主要リファレンス）
- [grpc-dotnet](https://github.com/grpc/grpc-dotnet) — .NET gRPC実装（C#パターンのリファレンス）
- [MagicOnion](https://github.com/Cysharp/MagicOnion) — Unity対応.NET RPCフレームワーク（Unity互換性のリファレンス）

### 3. Objectives & Success Metrics

**Goals**:
1. Unityアプリケーションから、Connect Protocolを使ったtype-safeなRPC通信ができる
2. ASP.NET CoreサーバーでConnect Protocol + gRPC両対応のサービスをホストできる
3. `.proto`ファイルからC#クライアント/サーバーコードを自動生成できる
4. 既存のconnect-go/grpc-goサーバーとの相互運用が可能

**Non-Goals**:
1. gRPC/gRPC-Webプロトコルのクライアント実装（クライアントはConnect Protocolのみ）
2. Unity Editor拡張やビジュアルツール
3. MessagePack等のProtobuf以外のシリアライゼーション対応（初期リリース時）
4. ロードバランシング、リトライポリシー等の高度なクライアント機能（初期リリース時）

**Success Metrics**:

| Metric | Target | Measurement |
|--------|--------|-------------|
| プロトコル互換性 | connect-goサーバーとの全Unary/Streaming RPC成功 | 結合テスト |
| Unity対応 | Unity 2021.3+ / .NET Standard 2.1で動作 | Unity統合テスト |
| サーバー互換性 | connect-go/grpc-goクライアントからのリクエスト処理可能 | 結合テスト |
| コード生成 | .protoからクライアント/サーバースタブの完全生成 | protoc pluginテスト |

### 4. Target Users & Segments

| Segment | Description | Primary Need |
|---------|-------------|-------------|
| Unityゲーム開発者 | マルチプレイヤーゲームやライブサービスのバックエンド通信を実装 | HTTP/1.1で動作するtype-safe RPC |
| .NETバックエンド開発者 | Unityクライアントと通信するAPIサーバーを構築 | Connect + gRPC両対応サーバー |
| フルスタック開発者 | クライアントとサーバーを同一IDLで管理したい | .protoからの一貫したコード生成 |

### 5. User Stories & Requirements

**P0 — Must Have**:

| # | User Story | Acceptance Criteria |
|---|-----------|-------------------|
| P0-1 | Unityクライアントから、Connect ProtocolでUnary RPCを実行できる | HTTP/1.1 POST、Content-Type: application/proto、Connect-Protocol-Version: 1ヘッダ付きリクエスト送信・レスポンス受信 |
| P0-2 | Unityクライアントから、Server Streaming RPCを実行できる | Content-Type: application/connect+proto、5バイトエンベロープフォーマットでメッセージを逐次受信 |
| P0-3 | `.proto`ファイルからC#クライアントスタブを生成できる | protoc plugin（protoc-gen-connect-csharp）がServiceごとにクライアントクラスを生成 |
| P0-4 | `.proto`ファイルからC#サーバースタブを生成できる | protoc pluginがServiceごとにサーバー基底クラスとエンドポイント登録コードを生成 |
| P0-5 | ASP.NET CoreでConnect Protocolサーバーをホストできる | エンドポイントルーティングでサービスをマッピング、Unary/Streaming対応 |
| P0-6 | Google.Protobufによるメッセージのシリアライズ/デシリアライズ | Marshaller実装がProtobufバイナリ形式で正しくエンコード/デコード |
| P0-7 | Connect Protocolのエラーハンドリング | Connectエラーコード（JSON形式）の送受信、ConnectException型への変換 |
| P0-8 | タイムアウト伝播 | Connect-Timeout-Msヘッダによるデッドライン伝播、CancellationTokenとの統合 |

**P1 — Should Have**:

| # | User Story | Acceptance Criteria |
|---|-----------|-------------------|
| P1-1 | Client Streaming RPCを実行できる | クライアントから複数メッセージ送信→サーバーが単一レスポンス返却 |
| P1-2 | Bidirectional Streaming RPCを実行できる（HTTP/2環境） | HTTP/2上で双方向ストリーミング動作 |
| P1-3 | サーバーがgRPCプロトコルでもリクエストを受け付けられる | grpc-dotnet（Grpc.AspNetCore）へのフォールバックまたは並行リスン |
| P1-4 | gzip圧縮に対応する | Connect-Content-Encoding/Content-Encodingヘッダでの圧縮ネゴシエーション |
| P1-5 | Interceptor/Middlewareパターン | クライアント/サーバー両方でリクエスト/レスポンスの前後にロジック挿入可能 |
| P1-6 | Unary RPCでのGETリクエスト対応 | Idempotent RPCがHTTP GETで実行可能（キャッシュ・CDN対応） |

**P2 — Nice to Have / Future**:

| # | User Story | Acceptance Criteria |
|---|-----------|-------------------|
| P2-1 | JSON codec対応 | Content-Type: application/jsonでのリクエスト/レスポンス |
| P2-2 | Server Reflection対応 | gRPC Server Reflection互換のサービスディスカバリ |
| P2-3 | Health Check対応 | 標準ヘルスチェックエンドポイント |
| P2-4 | Unity WebGL対応 | ブラウザ上のUnity WebGLからのRPC実行 |

### 6. Solution Overview

#### アーキテクチャ

```
connect-net/
├── src/
│   ├── ConnectNet.Core/              # 共有コア (.NET Standard 2.1)
│   │   ├── ICodec.cs                 # シリアライゼーション抽象
│   │   ├── ICompressor.cs            # 圧縮抽象
│   │   ├── ConnectError.cs           # エラー型
│   │   ├── Envelope.cs               # 5バイトエンベロープ読み書き
│   │   ├── ConnectProtocol.cs        # ワイヤフォーマット処理
│   │   └── Interceptor.cs            # Interceptor抽象
│   │
│   ├── ConnectNet.Client/            # クライアント (.NET Standard 2.1)
│   │   ├── ConnectChannel.cs         # 接続管理（HttpClientベース）
│   │   ├── ConnectClient<TReq,TRes>/ # 型付きRPCクライアント
│   │   ├── StreamingClientCall.cs    # ストリーミング呼び出し
│   │   └── CallOptions.cs            # 呼び出しオプション
│   │
│   ├── ConnectNet.Server/            # サーバー (.NET 10+)
│   │   ├── ConnectServiceEndpoint.cs # ASP.NET Coreエンドポイント
│   │   ├── ConnectMiddleware.cs      # Connect Protocolハンドラ
│   │   └── ServiceRegistration.cs    # DI/ルーティング登録
│   │
│   └── ConnectNet.Codecs.Protobuf/   # Protobuf codec (.NET Standard 2.1)
│       └── ProtobufCodec.cs
│
├── tools/
│   └── protoc-gen-connect-csharp/    # protoc plugin (コード生成)
│
├── tests/
│   ├── ConnectNet.Tests/             # ユニットテスト
│   └── ConnectNet.IntegrationTests/  # 結合テスト (connect-goサーバーとの相互運用)
│
└── samples/
    ├── Sample.Server/                # ASP.NET Coreサンプルサーバー
    └── Sample.UnityClient/           # Unityサンプルクライアント
```

#### 主要な設計判断

**ワイヤフォーマット** (connect-go準拠):
- Unary: `Content-Type: application/proto`、エンベロープなし、レスポンスボディに直接メッセージ
- Streaming: `Content-Type: application/connect+proto`、5バイトエンベロープ（1バイトフラグ + 4バイトBEサイズ）
- エラー: JSON形式の`ConnectError`（code, message, details）
- ストリーム終端: `flagEnvelopeEndStream`フラグ付きエンベロープでtrailer送信

**クライアントHTTPトランスポート**:
- `System.Net.Http.HttpClient`ベース（.NET Standard 2.1で利用可能）
- UnityではUnityWebRequestベースの`HttpMessageHandler`を提供することも検討
- HTTP/1.1でUnary + Server Streaming動作、HTTP/2利用可能時はClient/Bidi Streamingも対応

**サーバーgRPC互換**:
- ASP.NET CoreのエンドポイントルーティングでConnectハンドラを登録
- gRPCプロトコルはGrpc.AspNetCore（grpc-dotnet）にフォールバック
- Content-Typeヘッダで自動プロトコル判別

**コード生成**:
- `protoc-gen-connect-csharp` protoc pluginを実装
- 生成コード: サービスごとにクライアントインターフェース/実装、サーバー基底クラス
- connect-goの生成パターンをC#イディオムに適応（async/await、IAsyncEnumerable等）

### 7. Open Questions

| Question | Owner | Deadline |
|----------|-------|----------|
| protoc pluginの実装言語はC#（dotnet tool）かGoか？ | neguse | Phase 1開始前 |
| Unity側のHTTPトランスポートはHttpClient直接利用か、UnityWebRequestラッパーか？ | neguse | Phase 1開始前 |
| Client/Bidi StreamingのHTTP/1.1環境での挙動（非対応エラーを返す？Websocketフォールバック？） | neguse | Phase 1中 |
| NuGetパッケージとしての配布か、UPM（Unity Package Manager）としての配布か、両方か？ | neguse | Phase 2開始前 |
| サーバー側のgRPCフォールバックはGrpc.AspNetCoreをそのまま使うか、独自実装か？ | neguse | Phase 1中 |

### 8. Timeline & Phasing

**Phase 1: Core — Unary RPC** (MVP)
- ConnectNet.Core: Codec抽象、エラー型、ワイヤフォーマット
- ConnectNet.Codecs.Protobuf: Protobufシリアライズ
- ConnectNet.Client: Unary RPC（Connect Protocol over HTTP/1.1）
- ConnectNet.Server: Unary RPC（Connect Protocol on ASP.NET Core）
- protoc-gen-connect-csharp: Unary RPCのコード生成
- Unity統合テスト

**Phase 2: Streaming**
- Server Streaming（クライアント/サーバー）
- Client Streaming（クライアント/サーバー）
- Bidirectional Streaming（HTTP/2環境のみ）
- エンベロープ読み書き

**Phase 3: Production Readiness**
- gzip圧縮対応
- Interceptor/Middleware
- GET Unary対応
- サーバーgRPCフォールバック（Grpc.AspNetCore連携）
- タイムアウト・キャンセレーション完全対応
- connect-goとの相互運用テストスイート

**Phase 4: Ecosystem**
- JSON codec
- Server Reflection
- Health Check
- protovalidate C#実装（buf.build/bufbuild/protovalidate互換のバリデーションライブラリ）
- NuGet/UPMパッケージ配布
- ドキュメント・サンプル充実
