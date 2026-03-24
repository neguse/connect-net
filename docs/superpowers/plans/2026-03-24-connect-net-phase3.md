# connect-net Phase 3: Production Readiness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** connect-netをプロダクション利用可能な品質にする。圧縮、Interceptor、タイムアウト完全対応、GET Unary、gRPCアダプタ対応を追加。

**Architecture:** 既存のPhase 1/2実装に機能を追加。破壊的変更を最小化しつつ、Interceptorチェーン、圧縮ネゴシエーション、gRPCフォールバック用アダプタを導入。

**Tech Stack:** C# / .NET Standard 2.1 / .NET 10 / ASP.NET Core / Google.Protobuf / xUnit

---

### Task 1: gzip圧縮対応

**Files:**
- Create: `src/ConnectNet/ICompressor.cs`
- Create: `src/ConnectNet/GzipCompressor.cs`
- Modify: `src/ConnectNet.Client/ConnectChannel.cs` — 圧縮ネゴシエーション
- Modify: `src/ConnectNet.Server/ConnectUnaryHandler.cs` — 圧縮レスポンス
- Create: `tests/ConnectNet.Tests/CompressionTests.cs`

Connect Protocol圧縮仕様:
- Unary: `Content-Encoding: gzip` / `Accept-Encoding: gzip`
- Streaming: `Connect-Content-Encoding: gzip` / `Connect-Accept-Encoding: gzip` (per-message)
- サーバーはAccept-Encodingに応じてレスポンスを圧縮
- クライアントはCompressMinBytesを超えるメッセージを圧縮

実装:
```csharp
public interface ICompressor
{
    string Name { get; }
    byte[] Compress(byte[] data);
    byte[] Decompress(byte[] data);
}

public class GzipCompressor : ICompressor { ... }
```

ConnectChannelにオプション追加:
```csharp
public class ConnectChannelOptions
{
    public ICompressor? Compressor { get; set; }
    public int CompressMinBytes { get; set; } = 0; // 0 = always compress when compressor set
}
```

テスト: 圧縮リクエスト送信→サーバーで解凍→圧縮レスポンス返却→クライアントで解凍

---

### Task 2: Interceptor/Middleware

**Files:**
- Create: `src/ConnectNet/IInterceptor.cs`
- Modify: `src/ConnectNet.Client/ConnectChannel.cs` — Interceptorチェーン
- Modify: `src/ConnectNet.Server/ConnectUnaryHandler.cs` — サーバーInterceptor
- Create: `tests/ConnectNet.Tests/InterceptorTests.cs`

connect-goのInterceptorパターン:
```csharp
public interface IInterceptor
{
    Task<IMessage> InterceptUnary(UnaryRequest request, Func<UnaryRequest, Task<IMessage>> next);
}

public class UnaryRequest
{
    public string Procedure { get; }
    public IMessage Message { get; }
    public IDictionary<string, string> Headers { get; }
}
```

テスト: ヘッダ追加Interceptor、ログInterceptor

---

### Task 3: タイムアウト・キャンセレーション完全対応

**Files:**
- Modify: `src/ConnectNet.Server/ConnectUnaryHandler.cs` — Connect-Timeout-Ms読み取り→CancellationToken
- Modify: `src/ConnectNet.Server/ConnectServerStreamHandler.cs` — 同上
- Create: `tests/ConnectNet.Tests/TimeoutTests.cs`

サーバー側:
- `Connect-Timeout-Ms`ヘッダを読み取り
- `CancellationTokenSource`のタイムアウトを設定
- ConnectContextに渡す
- タイムアウト発生時は`ConnectCode.DeadlineExceeded`を返却

---

### Task 4: GET Unary対応

**Files:**
- Modify: `src/ConnectNet.Server/ConnectUnaryHandler.cs` — GET処理
- Modify: `src/ConnectNet.Server/ConnectServiceExtensions.cs` — GETルート登録
- Modify: `src/ConnectNet.Client/ConnectChannel.cs` — GET送信オプション
- Create: `tests/ConnectNet.Tests/GetUnaryTests.cs`

Connect Protocol GET仕様:
- URLクエリパラメータ: `?encoding=proto&message={base64}&base64=1`
- Content-Typeヘッダなし
- `Connect-Protocol-Version: 1`ヘッダは必須
- Idempotent RPCのみ（将来のconnectMethodDescriptorでマーク）

---

### Task 5: 全テスト + 最終確認

全テスト実行、ビルド確認。
