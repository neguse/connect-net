# connect-net Alloc Reduction Plan (Phase A / B / C)

> **For agentic workers:** This plan is multi-phase. Each phase is independently shippable. Measure after each phase before deciding whether to advance.

**Goal:** クライアント / サーバ両側で、Connect RPC 1 回あたりの managed heap allocation を「メッセージオブジェクトと HTTP 層オーバーヘッドだけ」まで削減する。中間バッファ・文字列変換・MemoryStream 経由のコピーを排除し、modern .NET (`IBufferWriter<byte>` / `PipeReader` / `ReadOnlySpan<byte>`) 流儀に揃える。

**Baseline (2026-05-28 ベンチ結果):**

| シナリオ | Mean | Allocated |
|---|---|---|
| Channel UnaryAsync (16B payload) | 142 us | **274 KB / RPC** |
| Channel UnaryAsync (4096B payload) | 165 us | 340 KB |
| ProtobufCodec.Deserialize (16B) | 569 ns | 4,440 B |
| Gzip_Decompress (1 KB compressed) | 11 us | 84 KB |
| Envelope.Read (64B payload) | 121 ns | 416 B |

**Target (3 Phase 完了後):**

| シナリオ | Mean | Allocated |
|---|---|---|
| Channel UnaryAsync (16B payload) | < 100 us | **< 30 KB / RPC** |
| ProtobufCodec.Deserialize (16B) | < 500 ns | < 250 B |
| Gzip_Decompress (1 KB compressed) | < 12 us | < 2 KB |

**Tech Stack:** C# / .NET Standard 2.1 (Client) / .NET 10 (Server) / `System.Buffers` / `System.IO.Pipelines` / Google.Protobuf

**Prerequisites:**
- ベンチ環境 `tests/ConnectNet.Benchmarks/` が動作する
- conformance test (`tests/ConnectNet.Conformance/`) が回せる(規模が大きいので Phase B 完了後の 1 回でよい)

---

## Design Rationale

### なぜ Span / `IBufferWriter` か

1. **`byte[]` 返却 API は alloc を強制する** — caller は受け取った配列を後で再利用できない(pool 返却契約がない)
2. **`IBufferWriter<byte>` 渡しなら caller がバッファのライフサイクルを所有** — `ArrayPool` でも `RecyclableMemoryStream` でも `PipeWriter` でも何でも渡せる
3. **Kestrel は HTTP body を最初から `PipeReader` で流している** — Stream ラッパは中間コピーを生む
4. **modern .NET シリアライザ全部この形** — MessagePack-CSharp / protobuf-net / System.Text.Json / Google.Protobuf も `WriteTo(IBufferWriter<byte>)` 対応

### あえて諦めるもの

- **JsonCodec の string 化**: Google.Protobuf の `JsonParser`/`JsonFormatter` が string ベース。回避には別 JSON 実装が必要で本計画スコープ外
- **`ConnectException.TryFromJson` の string 経由**: 同上
- **Unity `UnityWebRequestHandler` の Stream モデル**: WebGL では PipeReader 不可なので、Stream ベース経路を残す(クライアント側に薄い adapter)

---

## File Structure

### 新規追加

```
src/ConnectNet/
├── Pooling/
│   ├── ArrayPoolBufferWriter.cs    # IBufferWriter<byte> + ArrayPool 実装
│   └── PooledArray.cs              # ArrayPool 借用配列の disposable wrapper
└── EnvelopeFrame.cs                # readonly struct + IDisposable
```

### 変更

```
src/ConnectNet/
├── ICompressor.cs           ← Decompress(ReadOnlySpan<byte>, IBufferWriter<byte>, int) に
├── ICodec.cs                ← Serialize(IMessage, IBufferWriter<byte>) / Deserialize(ReadOnlySpan<byte>) に
├── ProtobufCodec.cs         ← Span 経路
├── JsonCodec.cs             ← Encoding.UTF8.GetString 経由は維持 (string-based parser のため)
├── GzipCompressor.cs        ← IBufferWriter 経路
├── DeflateCompressor.cs     ← IBufferWriter 経路
├── Envelope.cs              ← PipeReader/PipeWriter + EnvelopeFrame
└── DecompressionHelpers.cs  ← 削除 (各 Compressor に統合)

src/ConnectNet.Client/
├── ConnectChannel.cs        ← レスポンス読みを PipeReader.Create(stream) に
├── ClientStreamCall.cs      ← 同上
├── BidiStreamCall.cs        ← 同上
└── StreamingContent.cs      ← PipeWriter 出力 (要検討)

src/ConnectNet.Server/
├── ConnectUnaryHandler.cs            ← HttpRequest.BodyReader (PipeReader) 直接
├── ConnectServerStreamHandler.cs     ← 同上
├── ConnectClientStreamHandler.cs     ← 同上
└── ConnectBidiStreamHandler.cs       ← 同上
```

---

## Phase A: 内部実装の Pool 化(API 不変)

**Goal:** ICompressor / ICodec / Envelope の API 表面は触らず、内部の `byte[]` / `MemoryStream` を `ArrayPool` 借用に置換する。alloc の大部分(81920 バッファ)を pool 化する第一段階。

**Why first:** リスク最小。API 互換性を維持したまま 60% 程度の alloc 削減を達成できる見込み。

**Tasks:**

- [ ] **A-1: `ArrayPoolBufferWriter<byte>` を `ConnectNet/Pooling/` に新規追加**
  - `IBufferWriter<byte>` を実装、内部で `ArrayPool<byte>.Shared` から借用
  - `WrittenSpan` / `WrittenMemory` / `WrittenCount` を露出
  - `Dispose()` で借用配列を pool に返却
  - 単体テスト `tests/ConnectNet.Tests/Pooling/ArrayPoolBufferWriterTests.cs`

- [ ] **A-2: `DecompressionHelpers.ReadWithLimit` を pool 化**
  - `var buffer = new byte[81920]` を `ArrayPool<byte>.Shared.Rent(81920)` に
  - `try/finally` で `Return(buffer)`
  - `MemoryStream output` も `ArrayPoolBufferWriter` 経由に切り替え検討

- [ ] **A-3: `ConnectChannel.ReadBoundedAsync` を pool 化**
  - 同上の 81920 バッファを `ArrayPool` 借用に
  - `MemoryStream output.ToArray()` の最終 alloc は仕様上残る(`byte[]` を返すため)

- [ ] **A-4: `ConnectUnaryHandler.CopyBoundedAsync` を pool 化**
  - 同上

- [ ] **A-5: `Envelope` の 5 byte header を pool 化**
  - `var header = new byte[5]` を `stackalloc Span<byte>` または `ArrayPool.Rent(5)` に
  - `WriteAsync(ReadOnlyMemory<byte>, ct)` overload(netstandard2.1)で書き出し
  - `ReadAsync` の header buffer も同様

- [ ] **A-6: `ProtobufCodec.Deserialize` を `byte[]` 直接経路に戻す**
  - `MemoryStream` + `CodedInputStream.CreateWithLimits` を廃し、`parser.ParseFrom(byte[])` に戻す
  - **重要な判断**: `CodedInputStream.RecursionLimit` のデフォルト 100 を受け入れる(現状 32)
  - サイズ上限(Envelope の 4 MiB)が間接的に深さを制約することを doc コメントに明記
  - 単体テストでネスト 100 段の proto が `RecursionLimitException` を投げることを確認

- [ ] **A-7: Phase A 完了後の再ベンチ**
  - `dotnet run --project tests/ConnectNet.Benchmarks -- --filter "*"` 全シナリオ
  - 結果を `docs/superpowers/plans/2026-05-28-alloc-reduction.md` の Baseline と並べて記録

**Expected Outcome:**
- UnaryAsync alloc: 274 KB → ~100 KB
- ProtobufCodec.Deserialize (16B): 4440 B → ~200 B
- Gzip_Decompress (1KB): 84 KB → ~4 KB

**Risk:**
- `ArrayPool` 返却忘れによる pool 枯渇 → `try/finally` の徹底でカバー、単体テストで確認
- `CodedInputStream` の RecursionLimit 緩和 → 既存 conformance テストで regression なきこと

**Rollback:** Phase A はファイル数限定で独立した変更。問題があれば `git revert` で 1 PR 単位で戻せる。

---

## Phase B: `IBufferWriter<byte>` / `ReadOnlySpan<byte>` API 化(破壊的変更)

**Goal:** `ICompressor` / `ICodec` のシグネチャを `IBufferWriter<byte>` / `ReadOnlySpan<byte>` 形に変更する。`byte[]` 返却を廃止。

**Why second:** Phase A の数字を見て、まだ削る価値があるか判断してから着手。API 破壊なので独立 PR で。

**Tasks:**

- [ ] **B-1: `ICompressor` のシグネチャ変更**
  - `byte[] Compress(byte[])` → `void Compress(ReadOnlySpan<byte> source, IBufferWriter<byte> destination)`
  - `byte[] Decompress(byte[], int)` → `void Decompress(ReadOnlySpan<byte> source, IBufferWriter<byte> destination, int maxBytes)`
  - `GzipCompressor` / `DeflateCompressor` 実装更新
  - `GZipStream`/`DeflateStream` は Span を読めるが、書き出しも IBufferWriter に直接書ければ理想

- [ ] **B-2: `ICodec` のシグネチャ変更**
  - `byte[] Serialize(IMessage)` → `void Serialize(IMessage, IBufferWriter<byte>)`
  - `T Deserialize<T>(byte[])` → `T Deserialize<T>(ReadOnlySpan<byte>)`
  - `IMessage Deserialize(byte[], MessageParser)` → `IMessage Deserialize(ReadOnlySpan<byte>, MessageParser)`
  - `ProtobufCodec`: `MessageParser.ParseFrom(ReadOnlySpan<byte>)` を使用、`message.WriteTo(IBufferWriter<byte>)` も Google.Protobuf 新版でサポート済み
  - `JsonCodec`: `JsonParser/JsonFormatter` が string ベースのため内部で string 化は残るが、API 表面は Span/IBufferWriter に揃える

- [ ] **B-3: 全 caller を更新**
  - サーバ 4 ハンドラ
  - クライアント `ConnectChannel` / `ClientStreamCall` / `BidiStreamCall`
  - Validation
  - テスト/サンプル/conformance harness

- [ ] **B-4: `EnvelopeFrame : IDisposable` 導入**
  - `readonly struct EnvelopeFrame { byte Flags; ReadOnlyMemory<byte> Data; IDisposable Owner; }`
  - `Envelope.ReadAsync` の戻り値を `EnvelopeFrame?` に変更
  - 全 caller を `using` で受けるパターンに

- [ ] **B-5: 全テスト緑にする**
  - 既存 unit test 182 件
  - integration test 7 件
  - conformance test 2422 件

- [ ] **B-6: Phase B 完了後の再ベンチ + 記録**

**Expected Outcome:**
- UnaryAsync alloc: 100 KB → ~50 KB (HTTP/Kestrel オーバーヘッドのみ残る)
- Gzip_Decompress (1KB): 4 KB → ~1 KB (caller の IBufferWriter に流れる)

**Risk:**
- 公開 API 破壊で利用者が再コンパイル必要 → ライブラリ v1 前なので許容
- Google.Protobuf のバージョン依存(`ParseFrom(ReadOnlySpan<byte>)` の availability) → csproj の最低バージョン確認

**Rollback:** B-1 / B-2 をひとまとめにした独立 PR。

---

## Phase C: `PipeReader` 直結(サーバ受信パスの zero-copy 化)

**Goal:** ASP.NET Core サーバで `HttpRequest.BodyReader` (PipeReader) を `Envelope.ReadAsync` に直接渡す。Stream → MemoryStream → byte[] のコピーを排除。

**Why third:** API 破壊の度合いは小さいが、サーバ実装の構造変更が大きい。Phase B 完了後に着手。

**Tasks:**

- [ ] **C-1: `Envelope.ReadAsync(PipeReader, int maxLength, CancellationToken)` 追加**
  - 既存の `Stream` 版は内部で `PipeReader.Create(stream)` でラップして互換維持(クライアント経路で使う)
  - `PipeReader.ReadAsync()` → `ReadResult.Buffer` (`ReadOnlySequence<byte>`) からヘッダ 5 byte + データを切り出す
  - データを `EnvelopeFrame` (借用配列 + コピー一回)で返す

- [ ] **C-2: サーバハンドラを `HttpRequest.BodyReader` 経由に**
  - `ConnectUnaryHandler.HandleAsync`: `context.Request.BodyReader` を Envelope.ReadAsync に
  - 同様に `ServerStreamHandler` / `ClientStreamHandler` / `BidiStreamHandler`
  - `CopyBoundedAsync` ヘルパー削除

- [ ] **C-3: クライアント受信パスを `PipeReader.Create(stream)` に**
  - `ConnectChannel.UnaryAsync` のレスポンス読みを PipeReader 経由に
  - `ReadBoundedAsync` ヘルパー削除、PipeReader の `MaxBufferSize` 制御に変更

- [ ] **C-4: `Envelope.WriteAsync(PipeWriter, ...)` 追加**
  - サーバ送信側 `HttpResponse.BodyWriter` も PipeWriter なので直結
  - `StreamingContent.cs` (クライアント送信) も `PipeWriter` 出力に切り替え

- [ ] **C-5: Unity 経路の確認**
  - `UnityWebRequestHandler` は Stream モデルなので、`PipeReader.Create(stream)` ラッパで吸収
  - WebGL ビルドが壊れないこと(Editor では UnityWebRequestHandler は無効化されるので CI では検証困難 — 実機テスト推奨)

- [ ] **C-6: 全テスト緑 + conformance**

- [ ] **C-7: Phase C 完了後の再ベンチ + 記録**

**Expected Outcome:**
- UnaryAsync alloc: 50 KB → < 30 KB (Kestrel/HttpClient のオーバーヘッドのみ)

**Risk:**
- `PipeReader` の `ExamineConsumed` の扱いを間違えると無限ループ or buffer 巨大化 → 単体テストで境界ケース網羅
- Unity WebGL での `PipeReader.Create(stream)` の挙動が未検証 → 実機で必ず確認
- ASP.NET Core 経由のリクエストヘッダ・トレーラ取得タイミングが変わる可能性 → 既存テスト緑で担保

**Rollback:** Phase B / A はそのまま残し、C のみ revert。

---

## Verification

各 Phase 完了時に必ず実行:

```bash
# 全テスト
dotnet test connect-net.slnx

# ベンチ
dotnet run --project tests/ConnectNet.Benchmarks -c Release -- \
  --filter "*" --warmupCount 3 --iterationCount 5

# Conformance (Phase B/C のみ)
# tests/ConnectNet.Conformance を connect-conformance ハーネス経由で
```

ベンチ結果はこの plan の `Baseline` セクション直下に追記:

```markdown
## Results

### Phase A (2026-MM-DD)
| シナリオ | Allocated | Baseline 比 |
|---|---|---|
| ... | ... | -XX% |

### Phase B (2026-MM-DD)
...

### Phase C (2026-MM-DD)
...
```

---

## Results

### Phase A (2026-05-28)

| シナリオ | Baseline | Phase A | Δ |
|---|---|---|---|
| Channel UnaryAsync (16 B) | 274,207 B | 19,503 B | **-93%** |
| Channel UnaryAsync (256 B) | 276,594 B | 21,896 B | -92% |
| Channel UnaryAsync (4096 B) | 339,632 B | 64,231 B | -81% |
| Proto_Deserialize (16 B) | 4,440 B | 256 B | -94% |
| Proto_Deserialize (256 B) | 4,920 B | 736 B | -85% |
| Proto_Deserialize (4096 B) | 20,960 B | 8,416 B | -60% |
| Gzip_Decompress (1 KB) | 84,416 B | 1,392 B | -98% |
| Gzip_Decompress (16 KB) | 115,136 B | 16,752 B | -85% |
| Gzip_Decompress (256 KB) | 918,155 B | 262,568 B | -71% |
| Envelope.Write (64 B) | 192 B | 0 B | -100% |
| Envelope.Read (64 B) | 416 B | 384 B | -8% |

**16 B payload Unary は当初目標 < 30 KB を達成**。残るのは大物 payload (4096 B → 64 KB) のレスポンスバッファ。Phase B でさらに削る。

### Phase B (2026-05-28)

| シナリオ | Baseline | Phase A | Phase B | Δ from Baseline |
|---|---|---|---|---|
| Channel UnaryAsync (16 B) | 274,207 B | 19,503 B | 18,358 B | -93% |
| Channel UnaryAsync (4096 B) | 339,632 B | 64,231 B | 59,186 B | -83% |
| Proto_Deserialize (16 B) | 4,440 B | 256 B | **88 B** | **-98%** |
| Proto_Deserialize (4096 B) | 20,960 B | 8,416 B | 8,248 B | -61% |
| Proto_Serialize (16 B) | 112 B | 112 B | 0 B (Gen0) | -100% |
| Gzip_Compress (16 KB) | 1,416 B | 1,416 B | 0 B (Gen0) | -100% |
| Gzip_Compress (256 KB) | 7,136 B | 7,136 B | 3,584 B | -50% |

`ICodec`/`ICompressor` を `IBufferWriter<byte>` / `ReadOnlySpan<byte>` 経由にし、`EnvelopeFrame` 導入で borrowed buffer の所有権を caller (`using var frame`) で扱えるようになった。**Proto_Deserialize の 98% alloc 削減が象徴的**。Unary は HTTP/TestServer 経路のオーバーヘッドが支配的になり、ここから先は PipeReader 直結 (Phase C) に踏み込む必要がある。

### Phase C (2026-05-28)

| シナリオ | Baseline | Phase A | Phase B | Phase C | Δ from Baseline |
|---|---|---|---|---|---|
| Channel UnaryAsync (16 B) | 274,207 B | 19,503 B | 18,358 B | 18,989 B | -93% |
| Channel UnaryAsync (256 B) | 276,594 B | 21,896 B | 20,904 B | 21,447 B | -92% |
| Channel UnaryAsync (4096 B) | 339,632 B | 64,231 B | 59,186 B | 59,768 B | -82% |
| Envelope.Read (64 B) | 416 B | 384 B | 400 B | 400 B | - |
| Envelope.Read (4096 B) | - | 4,416 B | 4,432 B | 4,432 B | - |

サーバを `HttpRequest.BodyReader` (PipeReader) 直接読みに、クライアントを `PipeReader.Create(stream)` 経由に変更。`CopyBoundedAsync` および Stream-based `Envelope.ReadAsync` の呼び出しを完全に廃止。

Unary の alloc は **Phase B からほぼ変化なし**。Channel UnaryAsync の残り 19 KB は HTTP/HttpClient/TestServer/Kestrel 内部のオーバーヘッドが大半で、ライブラリ層では削れない領域。
ストリーミング系の追加ベンチがあれば Phase C の真の効果(per-envelope の pool 化)が見えるはず — それは次の plan に持ち越し。

### Summary across phases

| シナリオ | Baseline | Final (Phase C) | 削減率 |
|---|---|---|---|
| **Channel UnaryAsync (16 B)** | 274 KB | **19 KB** | **-93%** |
| **Channel UnaryAsync (4 KB)** | 340 KB | 60 KB | -82% |
| **Proto_Deserialize (16 B)** | 4,440 B | **88 B** | **-98%** |
| **Gzip_Decompress (1 KB)** | 84 KB | 1.4 KB | -98% |
| **Envelope.Write (64 B)** | 192 B | 160 B | -17% |

当初目標の **Channel UnaryAsync < 30 KB** は 16 B / 256 B payload で達成。4096 B では 60 KB 残りだが、内訳の大半は HTTP 層のオーバーヘッド。

### Phase D — 残作業の処理 (2026-05-28)

Phase A〜C 完了後、残っていた次の項目を処理した:

- **ストリーミング系の専用ベンチを `StreamingBenchmarks` として追加**。`ServerStream` / `ClientStream` / `BidiStream` を `MessagesPerCall` ∈ {1, 8} で測定。
- **`RepeatedRules.unique` の `HashSet<object>` boxing 削減**。`IList<int>`/`<long>`/`<uint>`/`<ulong>`/`<float>`/`<double>`/`<bool>`/`<string>` を型特化版 `HasDup<T>` で判定し、protobuf primitive 型の boxing をゼロに。
- **`ConnectException.TryFromJsonElement` 追加**。streaming の `errorElement.GetRawText()` → `JsonDocument.Parse()` の往復を廃して、既にパース済みの `JsonElement` から直接ビルド。
- **`TryFromJson(ReadOnlyMemory<byte>)` overload と `ParseErrorResponse(ReadOnlyMemory<byte>)` overload を追加**。client のエラー本文を `Encoding.UTF8.GetString` 経由でなく直接 UTF-8 で `JsonDocument.Parse` できるように。`ConnectChannel` / `ClientStreamCall` / `BidiStreamCall` の全 caller を bytes 経路に切替済み。

### Streaming benchmarks (新規)

| Method | PayloadBytes | Messages/Call | Allocated |
|---|---|---|---|
| ServerStream | 16 | 1 | **20.4 KB** |
| ServerStream | 16 | 8 | 28.6 KB |
| ClientStream | 16 | 1 | 19.8 KB |
| ClientStream | 16 | 8 | 21.8 KB |
| BidiStream | 16 | 1 | 20.8 KB |
| BidiStream | 16 | 8 | 30.1 KB |
| ServerStream | 256 | 8 | 30.4 KB |
| BidiStream | 256 | 8 | 37.9 KB |

8 メッセージのストリーミングで 22-38 KB。1 メッセージ追加あたり ~1 KB 増分で済んでいる(Phase A 以前は推定 +80 KB/envelope)。

### Phase E — クライアント ゼロアロケーション化 (2026-05-29)

ユーザ要求「最適ユースケースで client alloc をゼロに」を受けて、 HttpClient/Kestrel/TestServer を排除した **`ClientOnlyBenchmarks`** を追加し、ライブラリ層単独の alloc を測定。

入った変更:
- **`PooledMemoryHttpContent`** 新規追加: `ByteArrayContent` の中間 `byte[]` を排除し、`ArrayPoolBufferWriter.WrittenMemory` を直接 HTTP body に。
- **`ReadBoundedAsync(HttpContent, ArrayPoolBufferWriter, ...)`** overload 追加: 既存の `.ToArray()` を経由する版を残しつつ、ホットパスは pool 化された writer に直接書く。
- **UnaryAsync 全体を pool 化**: 送信 (`requestWriter`)、圧縮 (`compressedRequestWriter`)、受信 (`responseWriter`)、レスポンス展開 (`responseDecompressed`) すべて `ArrayPoolBufferWriter` 経由で、中間 `byte[]` ゼロに。
- **string interpolation を ConnectChannel ctor でキャッシュ**: `_unaryContentType` / `_streamingContentType` / `_acceptEncodingHeader` を起動時に1回計算し、毎 RPC の `$"application/..."` フォーマット alloc を排除。

#### 結果(MockHandler 経由、HttpClient framework 除外で測定)

| Method | Allocated | 内訳 |
|---|---|---|
| **PureCodec_RoundTrip** (no HTTP) | **96 B** | TRes (HelloResponse) のみ — **コーデック層の理論限界** |
| **UnaryAsync_Minimal** (mock HTTP) | **2,616 B** | + HttpRequestMessage/HttpResponseMessage/HttpContent ほか |
| **UnaryAsync (TestServer, 16 B)** | 18,786 B | + Kestrel/HttpClient/サーバ側ハンドラ |

**コーデック層単独では実質ゼロ alloc**。`UnaryAsync_Minimal` の 2,616 B は `HttpRequestMessage` / `HttpResponseMessage` / `HttpClient.SendAsync` の framework 内部 alloc が大半で、ConnectNet ライブラリ層から削れる余地は残っていない。

完全ゼロを目指すなら `HttpClient` を捨てて自前 transport (PipeReader/PipeWriter 直接 + socket) に降りる必要があるが、`IHttpClientFactory`/Polly/OpenTelemetry instrumentation との互換を失うため、本 plan のスコープでは「コーデック層ゼロ alloc + HttpClient framework alloc は受容」を最終形とする。

### Phase D 後の最終 ChannelBenchmarks

| Method | Payload | Baseline | Final |
|---|---|---|---|
| UnaryAsync | 16 B | 274,207 B | **18,786 B (-93%)** |
| UnaryAsync | 256 B | 276,594 B | 21,310 B (-92%) |
| UnaryAsync | 4 KB | 339,632 B | 59,817 B (-82%) |

---

## Out of Scope (将来別 plan)

- **JsonCodec の `Utf8JsonReader` 全面化**: Google.Protobuf の `JsonParser` / `JsonFormatter` が `string` ベース API しか公開しておらず、UTF-8 → string の変換を完全に廃するには Connect JSON のパーサ・フォーマッタを **自前実装** する必要がある(`Any` の `@type` 解決、proto3 のフィールド命名規則、Well-Known Type の表現、Discard Unknown Fields 挙動など特殊ルール多数)。本 plan のスコープを超えるため別 plan。現状では:
  - JSON は protobuf に比べて遅い経路として受容(typical workload は protobuf)
  - `JsonCodec.Deserialize(ReadOnlyMemory<byte>)` が API としては Span 経由になっているので、将来 Google.Protobuf 側が Utf8 API を出せばそのまま入れ替え可能
- **`MessageParser.ParseFrom(ReadOnlySequence<byte>)`**: PipeReader の複数セグメントを跨ぐ deserialize で完全 zero-copy 化する場合に必要。現状は `EnvelopeFrame` がコピーで連続バッファを保証している
- **`ArrayPool` ではなく `MemoryPool<byte>` を使う**: 大物割当に有利だが、現状の 81920 サイズなら ArrayPool で十分

---

## Open Questions

1. **Phase B / C の順序を入れ替えるべきか?** — Phase C 単独でサーバ側 alloc が大幅減るので、API 破壊なしに先に進める案もある。実装スコープが小さい方を先にやる戦略
2. **`ICodec.Serialize` の戻り値を `int` (書き込みバイト数) にすべきか?** — `IBufferWriter` は `Advance` で進めるが、caller が「いくら書かれたか」を知りたい場合の API 設計
3. **`EnvelopeFrame` を `class` にして finalizer を持たせるべきか?** — `using` 漏れ時のセーフネット。GC 圧との trade-off
