# Client Conformance Test 全パス設計

## 背景

Client conformanceテスト（connectrpc/conformance v1.0.5）の結果:
- 全2422テスト中、941 passed / 1481 failed
- HTTP/2テスト1326件は全て「expected HTTP version 2; instead got 1」
- HTTP/1テスト156件は複数の実装バグに起因

## 制約

- Unity対応: .NET Standard 2.1ターゲット
- HTTP/2: YetAnotherHttpHandler (YAHA) を使用
- ライブラリ本体はHTTPハンドラ非依存（ユーザーがHttpClientを渡す設計）

## アプローチ

ボトムアップ: HTTP/1のバグを先に全て潰してからHTTP/2（YAHA）を追加。

## Step 1: ConnectException.FromJson の堅牢化（~24件）

### 問題
- `"error": null` でJsonElementWrongTypeException
- `"code": null` や不明コード文字列の処理が不適切

### 修正
- FromJson: 入力がnullまたは空の場合はnullを返す
- JSON rootがnull型の場合はnullを返す
- `"code"` プロパティがnullの場合は `Unknown` にフォールバック
- 不明なコード文字列は `Unknown`

### 影響ファイル
- `src/ConnectNet/ConnectException.cs`

## Step 2: HTTP Status → Connect Code マッピング修正（~60件）

### 問題
CodeFromHttpStatusのマッピングが仕様と不一致。
エラーボディ不在時のHTTPステータスフォールバックが不正確。

### 修正
Connect仕様の正確なマッピング:
- 400 → InvalidArgument
- 401 → Unauthenticated
- 403 → PermissionDenied
- 404 → Unimplemented
- 408 → DeadlineExceeded
- 409 → Aborted（※仕様確認要）
- 429 → Unavailable
- 502 → Unavailable
- 503 → Unavailable
- 504 → Unavailable
- 上記以外 → Unknown

ParseErrorResponseのフォールバックロジック:
1. レスポンスボディをConnect error JSONとしてパース試行
2. 成功 → JSONのコードを使用
3. 失敗 → HTTPステータスからマッピング

### 影響ファイル
- `src/ConnectNet/ConnectException.cs`
- `src/ConnectNet.Client/ConnectChannel.cs`

## Step 3: Timeout伝播（~96件）

### 問題
`Connect-Timeout-Ms` ヘッダー未送信。タイムアウト時のエラーコードが不正確。

### 修正
- `CallOptions.Timeout` を `Connect-Timeout-Ms` リクエストヘッダーに変換
- タイムアウト発生時に `DeadlineExceeded` エラーを返す
- 全RPC種別（unary, server-stream, client-stream, bidi-stream）で対応

### 影響ファイル
- `src/ConnectNet.Client/ConnectChannel.cs`
- `src/ConnectNet.Client/ClientStreamCall.cs`
- `src/ConnectNet.Client/BidiStreamCall.cs`

## Step 4: Client Cancellation修正（~100件）

### 問題
ストリームキャンセル時にエラーが伝播しない。
ClientHarnessでcancelAfterResponses実行後にエラーレスポンスが返らない。

### 修正
- `OperationCanceledException` → ConnectException(Canceled) に変換
- ClientHarnessでキャンセル後のエラー報告を追加
- server-stream cancel-after-responsesの処理修正

### 影響ファイル
- `src/ConnectNet.Client/ConnectChannel.cs`
- `tests/ConnectNet.Conformance/ClientHarness.cs`

## Step 5: Unexpected Responses バリデーション（~14件）

### 問題
content-type、codec、圧縮のミスマッチを検出していない。

### 修正
- レスポンスContent-Typeの検証（期待するcodecと一致するか）
- 未知の圧縮エンコーディングのエラーハンドリング
- client-streamで複数レスポンスが返った場合のエラー
- unary 200 OKだがレスポンスなしのエラー

### 影響ファイル
- `src/ConnectNet.Client/ConnectChannel.cs`

## Step 6: Empty Responses対応（~6件）

### 問題
空レスポンスボディのデシリアライズで失敗。

### 修正
- 空バイト配列に対してデフォルトメッセージインスタンスを生成

### 影響ファイル
- `src/ConnectNet.Client/ConnectChannel.cs`
- `src/ConnectNet/ICodec.cs`（必要に応じて）

## Step 7: YAHA統合（~1326件）

### 問題
HTTP/2テストが全て失敗（クライアントが常にHTTP/1.1を使用）。

### 修正
- `ConnectNet.Conformance.csproj` に `YetAnotherHttpHandler` NuGetパッケージ追加
- `ClientHarness.cs`: HTTP/2要求時に `YetAnotherHttpHandler` をHttpMessageHandlerとして使用
- TLS証明書設定の引き渡し
- HTTP/2 cleartext (h2c) のサポート確認

### 影響ファイル
- `tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj`
- `tests/ConnectNet.Conformance/ClientHarness.cs`

## 検証方法

各Stepの修正後に `connectconformance --mode client` を実行し、失敗数の減少を確認。
