# Client Conformance Test 全パス 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Client conformanceテスト (connectrpc/conformance v1.0.5) の全2422テストをパスさせる

**Architecture:** HTTP/1のバグ修正（JSON堅牢化、エラーマッピング、タイムアウト、キャンセル、バリデーション）を段階的に行い、最後にHTTP/2対応（YAHA）を追加。各修正後にconformanceテストで回帰確認。

**Tech Stack:** C# (.NET Standard 2.1 / net10.0), System.Text.Json, YetAnotherHttpHandler, xUnit

**検証コマンド:**
```bash
# conformanceテスト（クライアントモード）
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client

# ユニットテスト
dotnet test tests/ConnectNet.Tests
```

---

### Task 1: ConnectException.FromJson の堅牢化

**Files:**
- Modify: `src/ConnectNet/ConnectException.cs:47-72`
- Modify: `tests/ConnectNet.Tests/ConnectExceptionTests.cs`

- [ ] **Step 1: null/不正JSON入力のテストを追加**

`tests/ConnectNet.Tests/ConnectExceptionTests.cs` に以下を追加:

```csharp
[Fact]
public void FromJson_NullJsonElement_ReturnsNull()
{
    // "null" is valid JSON but represents a null value
    var result = ConnectException.TryFromJson("null");
    Assert.Null(result);
}

[Fact]
public void FromJson_MissingCode_DefaultsToUnknown()
{
    var ex = ConnectException.FromJson("{\"message\":\"oops\"}");
    Assert.Equal(ConnectCode.Unknown, ex.Code);
    Assert.Equal("oops", ex.Message);
}

[Fact]
public void FromJson_NullCode_DefaultsToUnknown()
{
    var ex = ConnectException.FromJson("{\"code\":null,\"message\":\"oops\"}");
    Assert.Equal(ConnectCode.Unknown, ex.Code);
}

[Fact]
public void FromJson_UnrecognizedCode_DefaultsToUnknown()
{
    var ex = ConnectException.FromJson("{\"code\":\"bogus_code\",\"message\":\"oops\"}");
    Assert.Equal(ConnectCode.Unknown, ex.Code);
}

[Fact]
public void FromJson_MissingMessage_DefaultsToEmpty()
{
    var ex = ConnectException.FromJson("{\"code\":\"internal\"}");
    Assert.Equal(ConnectCode.Internal, ex.Code);
    Assert.Equal("", ex.Message);
}

[Fact]
public void FromJson_NullMessage_DefaultsToEmpty()
{
    var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":null}");
    Assert.Equal("", ex.Message);
}

[Fact]
public void FromJson_UnrecognizedFields_Ignored()
{
    var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"extra\":123}");
    Assert.Equal(ConnectCode.Internal, ex.Code);
}

[Fact]
public void FromJson_NullDetails_Ignored()
{
    var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"details\":null}");
    Assert.Empty(ex.Details);
}

[Fact]
public void FromJson_DetailsWithDebugField_Ignored()
{
    // details items may have optional "debug" field that should be ignored
    var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"details\":[{\"type\":\"t\",\"value\":\"AQ\",\"debug\":{}}]}");
    Assert.Single(ex.Details);
}
```

- [ ] **Step 2: テスト実行、失敗を確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ConnectExceptionTests" -v minimal
```

- [ ] **Step 3: FromJsonを堅牢化、TryFromJsonメソッドを追加**

`src/ConnectNet/ConnectException.cs` の `FromJson` を修正:

```csharp
public static ConnectException? TryFromJson(string json)
{
    if (string.IsNullOrWhiteSpace(json))
        return null;

    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var code = ConnectCode.Unknown;
        if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
        {
            code = CodeFromString(codeProp.GetString() ?? "");
        }

        var message = "";
        if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
        {
            message = msgProp.GetString() ?? "";
        }

        var details = new List<ConnectErrorDetail>();
        if (root.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in detailsProp.EnumerateArray())
            {
                if (d.ValueKind != JsonValueKind.Object)
                    continue;
                if (!d.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!d.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.String)
                    continue;
                var type = typeProp.GetString() ?? "";
                var value = Base64DecodeUnpadded(valueProp.GetString() ?? "");
                details.Add(new ConnectErrorDetail(type, value));
            }
        }

        return new ConnectException(code, message, details);
    }
    catch (JsonException)
    {
        return null;
    }
}

public static ConnectException FromJson(string json)
{
    return TryFromJson(json) ?? new ConnectException(ConnectCode.Unknown);
}
```

- [ ] **Step 4: EndStream内の `"error": null` をハンドル**

`src/ConnectNet.Client/ConnectChannel.cs` 375-379行、`src/ConnectNet.Client/ClientStreamCall.cs` 141-145行、`src/ConnectNet.Client/BidiStreamCall.cs` 148-152行で、EndStreamのerrorフィールドがnullの場合をスキップ:

各ファイルの `if (root.TryGetProperty("error", out var errorElement))` ブロックを修正:

```csharp
if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
{
    var errorJson = errorElement.GetRawText();
    var connectError = ConnectException.TryFromJson(errorJson);
    if (connectError != null)
        throw connectError;
}
```

- [ ] **Step 5: テスト実行、全パスを確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ConnectExceptionTests" -v minimal
```

- [ ] **Step 6: ビルドしてconformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

- [ ] **Step 7: コミット**

```bash
git add src/ConnectNet/ConnectException.cs tests/ConnectNet.Tests/ConnectExceptionTests.cs \
  src/ConnectNet.Client/ConnectChannel.cs src/ConnectNet.Client/ClientStreamCall.cs \
  src/ConnectNet.Client/BidiStreamCall.cs
git commit -m "fix: harden ConnectException.FromJson for null/invalid JSON inputs"
```

---

### Task 2: HTTP Status → Connect Code マッピング修正

**Files:**
- Modify: `src/ConnectNet/ConnectException.cs:132-150`
- Modify: `tests/ConnectNet.Tests/ConnectExceptionTests.cs`

- [ ] **Step 1: CodeFromHttpStatusのテストを追加**

`tests/ConnectNet.Tests/ConnectExceptionTests.cs` に追加:

```csharp
[Theory]
[InlineData(400, ConnectCode.InvalidArgument)]
[InlineData(401, ConnectCode.Unauthenticated)]
[InlineData(403, ConnectCode.PermissionDenied)]
[InlineData(404, ConnectCode.Unimplemented)]
[InlineData(408, ConnectCode.DeadlineExceeded)]
[InlineData(409, ConnectCode.Unknown)]
[InlineData(412, ConnectCode.Unknown)]
[InlineData(413, ConnectCode.Unknown)]
[InlineData(415, ConnectCode.Unknown)]
[InlineData(429, ConnectCode.Unavailable)]
[InlineData(431, ConnectCode.Unavailable)]
[InlineData(502, ConnectCode.Unavailable)]
[InlineData(503, ConnectCode.Unavailable)]
[InlineData(504, ConnectCode.Unavailable)]
[InlineData(500, ConnectCode.Unknown)]
[InlineData(501, ConnectCode.Unknown)]
[InlineData(422, ConnectCode.Unknown)]
[InlineData(505, ConnectCode.Unknown)]
public void CodeFromHttpStatus_MapsPerConnectSpec(int httpStatus, ConnectCode expectedCode)
{
    Assert.Equal(expectedCode, ConnectException.CodeFromHttpStatus(httpStatus));
}
```

- [ ] **Step 2: テスト実行、失敗を確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~CodeFromHttpStatus" -v minimal
```

409, 412, 413, 415 はUnknownを期待するが現在は別のコードを返すため失敗する。431はUnavailableを期待するがResourceExhaustedを返すため失敗する。400はInvalidArgumentを期待するがInternalを返すため失敗する。

- [ ] **Step 3: CodeFromHttpStatusをConnect仕様通りに修正**

`src/ConnectNet/ConnectException.cs:132-150` を以下に置換:

```csharp
public static ConnectCode CodeFromHttpStatus(int statusCode) => statusCode switch
{
    400 => ConnectCode.InvalidArgument,
    401 => ConnectCode.Unauthenticated,
    403 => ConnectCode.PermissionDenied,
    404 => ConnectCode.Unimplemented,
    408 => ConnectCode.DeadlineExceeded,
    429 => ConnectCode.Unavailable,
    431 => ConnectCode.Unavailable,
    502 => ConnectCode.Unavailable,
    503 => ConnectCode.Unavailable,
    504 => ConnectCode.Unavailable,
    _ => ConnectCode.Unknown,
};
```

Connect仕様で定義されていない409, 412, 413, 415のマッピングを削除。

- [ ] **Step 4: テスト実行、全パスを確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ConnectExceptionTests" -v minimal
```

- [ ] **Step 5: conformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet/ConnectException.cs tests/ConnectNet.Tests/ConnectExceptionTests.cs
git commit -m "fix: correct HTTP status to Connect code mapping per spec"
```

---

### Task 3: ParseErrorResponseの改善

**Files:**
- Modify: `src/ConnectNet.Client/ConnectChannel.cs:427-444`
- Modify: `tests/ConnectNet.Tests/ConnectChannelTests.cs`

- [ ] **Step 1: ParseErrorResponseのテストを追加**

`tests/ConnectNet.Tests/ConnectChannelTests.cs` を確認し、以下のテストを追加:

```csharp
[Fact]
public void ParseErrorResponse_ValidConnectJson_UsesJsonCode()
{
    var error = ConnectChannel.ParseErrorResponse(
        "{\"code\":\"not_found\",\"message\":\"gone\"}", 500);
    Assert.Equal(ConnectCode.NotFound, error.Code);
    Assert.Equal("gone", error.Message);
}

[Fact]
public void ParseErrorResponse_InvalidJson_UsesHttpStatus()
{
    var error = ConnectChannel.ParseErrorResponse("not json", 401);
    Assert.Equal(ConnectCode.Unauthenticated, error.Code);
}

[Fact]
public void ParseErrorResponse_EmptyBody_UsesHttpStatus()
{
    var error = ConnectChannel.ParseErrorResponse("", 503);
    Assert.Equal(ConnectCode.Unavailable, error.Code);
}

[Fact]
public void ParseErrorResponse_NullJson_UsesHttpStatus()
{
    var error = ConnectChannel.ParseErrorResponse("null", 404);
    Assert.Equal(ConnectCode.Unimplemented, error.Code);
}

[Fact]
public void ParseErrorResponse_ConnectJsonWithDetails_PreservesDetails()
{
    var json = "{\"code\":\"internal\",\"message\":\"err\",\"details\":[{\"type\":\"t\",\"value\":\"AQ\"}]}";
    var error = ConnectChannel.ParseErrorResponse(json, 500);
    Assert.Equal(ConnectCode.Internal, error.Code);
    Assert.Single(error.Details);
}
```

- [ ] **Step 2: テスト実行、失敗を確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ParseErrorResponse" -v minimal
```

- [ ] **Step 3: ParseErrorResponseをTryFromJsonベースに修正**

`src/ConnectNet.Client/ConnectChannel.cs:427-444` を修正:

```csharp
internal static ConnectException ParseErrorResponse(string errorBody, int httpStatusCode)
{
    if (!string.IsNullOrWhiteSpace(errorBody))
    {
        var parsed = ConnectException.TryFromJson(errorBody);
        if (parsed != null)
            return parsed;
    }

    var code = ConnectException.CodeFromHttpStatus(httpStatusCode);
    return new ConnectException(code, $"HTTP {httpStatusCode}");
}
```

- [ ] **Step 4: テスト実行、全パスを確認**

```bash
dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ParseErrorResponse" -v minimal
```

- [ ] **Step 5: conformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet.Client/ConnectChannel.cs tests/ConnectNet.Tests/ConnectChannelTests.cs
git commit -m "fix: improve ParseErrorResponse to use TryFromJson and correct fallback"
```

---

### Task 4: Client Cancellation の修正

**Files:**
- Modify: `tests/ConnectNet.Conformance/ClientHarness.cs:338-364`

- [ ] **Step 1: ServerStream cancel-after-responses の修正**

`tests/ConnectNet.Conformance/ClientHarness.cs` の `ExecuteServerStreamAsync` メソッド内、cancel後にbreakだけでなくエラーを設定:

```csharp
try
{
    await foreach (var response in channel.ServerStreamAsync<ServerStreamRequest, ServerStreamResponse>(
        procedure, requestMsg, callOptions, cts.Token).ConfigureAwait(false))
    {
        if (response.Payload != null)
            payloads.Add(response.Payload);

        if (afterNumResponses.HasValue && payloads.Count >= afterNumResponses.Value)
        {
            cts.Cancel();
            // After cancel, throw so the catch block reports the error
            throw new OperationCanceledException(cts.Token);
        }
    }
}
```

- [ ] **Step 2: BidiStream cancel-after-responses の同様の修正**

`ExecuteBidiStreamAsync` メソッド内（525-530行付近）も同様:

```csharp
if (afterNumResponses.HasValue && payloads.Count >= afterNumResponses.Value)
{
    cts.Cancel();
    throw new OperationCanceledException(cts.Token);
}
```

- [ ] **Step 3: ビルドしてconformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

Client Cancellation系テストの改善を確認。

- [ ] **Step 4: コミット**

```bash
git add tests/ConnectNet.Conformance/ClientHarness.cs
git commit -m "fix(conformance): propagate cancellation error on cancel-after-responses"
```

---

### Task 5: Timeoutテストの修正

**Files:**
- Modify: `tests/ConnectNet.Conformance/ClientHarness.cs`

- [ ] **Step 1: Timeout処理の調査**

conformanceテストのTimeout失敗パターンを確認:
```bash
grep -A3 "FAILED: Timeouts/HTTPVersion:1" /tmp/conformance-output.txt | head -30
```

主な問題は「expecting an error but received none」と「expecting 0 response messages but instead got N」。
タイムアウト時にDeadlineExceededエラーが返るべきだが、正常に完了してしまっている。

- [ ] **Step 2: OperationCanceledExceptionのタイムアウト判定を改善**

`ClientHarness.cs` の各Executeメソッドで、`OperationCanceledException` が発生したとき、タイムアウトによるものかユーザーキャンセルによるものかを区別する必要がある。

`ExecuteRequestAsync` の前にタイムアウト用CancellationTokenSourceをセットアップ:

```csharp
// ExecuteRequestAsync内、cts作成後に追加:
using var timeoutCts = new CancellationTokenSource();
if (request.HasTimeoutMs)
{
    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(request.TimeoutMs));
}
using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, timeoutCts.Token);
```

各catch (OperationCanceledException) で:
```csharp
catch (OperationCanceledException)
{
    if (timeoutCts.IsCancellationRequested)
    {
        result.Error = new Error
        {
            Code = Code.DeadlineExceeded,
            Message = "deadline exceeded",
        };
    }
    else
    {
        result.Error = new Error
        {
            Code = Code.Canceled,
            Message = "canceled",
        };
    }
}
```

注意: `timeoutCts`を各Executeメソッドに渡すか、クラスフィールドにする必要がある。実装時に最適な構造を選択する。

- [ ] **Step 3: ビルドしてconformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

- [ ] **Step 4: コミット**

```bash
git add tests/ConnectNet.Conformance/ClientHarness.cs
git commit -m "fix(conformance): distinguish timeout from cancellation in client harness"
```

---

### Task 6: Unexpected Responses / Empty Responses / 圧縮エラーのバリデーション

**Files:**
- Modify: `src/ConnectNet.Client/ConnectChannel.cs`
- Modify: `src/ConnectNet.Client/ClientStreamCall.cs`
- Modify: `src/ConnectNet.Client/BidiStreamCall.cs`

- [ ] **Step 1: conformance失敗パターンの詳細調査**

```bash
grep -A5 "FAILED:.*HTTPVersion:1.*unexpected" /tmp/conformance-output.txt | head -50
grep -A5 "FAILED:.*HTTPVersion:1.*empty-response" /tmp/conformance-output.txt | head -20
grep -A5 "FAILED:.*HTTPVersion:1.*compressed" /tmp/conformance-output.txt | head -20
```

各失敗の具体的なエラーメッセージを分析し、必要な修正を特定する。

- [ ] **Step 2: Content-Typeバリデーションの追加**

unaryレスポンスで、`Content-Type`が期待するコーデック（`application/proto` or `application/json`）と一致しない場合、エラーをスローする。

`ConnectChannel.cs` の `SendUnaryAsync` メソッドに追加:

```csharp
// httpResponse.IsSuccessStatusCode の後、レスポンス読み取り前に:
var contentType = httpResponse.Content.Headers.ContentType?.MediaType;
var expectedContentType = $"application/{_codec.Name}";
if (contentType != null && !string.Equals(contentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
{
    throw new ConnectException(ConnectCode.Internal, $"unexpected content-type: {contentType}");
}
```

ストリーミングレスポンスでも同様に `application/connect+{_codec.Name}` を検証する。

- [ ] **Step 3: 圧縮バリデーションの追加**

未知のContent-Encodingを受信した場合のエラー:

```csharp
if (responseContentEncoding != null)
{
    var decompressor = _channelOptions.Decompressors.FirstOrDefault(d =>
        string.Equals(d.Name, responseContentEncoding, StringComparison.OrdinalIgnoreCase));
    if (decompressor != null)
        responseBytes = decompressor.Decompress(responseBytes);
    else
        throw new ConnectException(ConnectCode.Internal, $"unknown encoding: {responseContentEncoding}");
}
```

- [ ] **Step 4: ビルドしてconformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

- [ ] **Step 5: 残存する失敗パターンを分析し追加修正**

conformance出力を再度分析し、残っている失敗を修正する。反復的に進める。

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet.Client/ConnectChannel.cs src/ConnectNet.Client/ClientStreamCall.cs \
  src/ConnectNet.Client/BidiStreamCall.cs
git commit -m "fix: add content-type and compression validation for responses"
```

---

### Task 7: HTTP/2対応（YAHA統合）

**Files:**
- Modify: `tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj`
- Modify: `tests/ConnectNet.Conformance/ClientHarness.cs:185-240`

- [ ] **Step 1: YAHAのネイティブライブラリビルド状況を確認**

```bash
ls references/YetAnotherHttpHandler/native/
```

YAHAはRust製ネイティブライブラリが必要。ビルド済みかどうかを確認する。

NuGetパッケージが利用不可（nuget.orgに未公開）のため:
- Option A: referenceのソースからビルド（Rustツールチェーン必要）
- Option B: GitHub Releasesから.nupkgを取得
- Option C: プロジェクト参照でリンク

ネイティブバイナリが必要なため、先にRustビルド環境を確認:
```bash
which cargo
cargo --version
```

- [ ] **Step 2: YAHAネイティブライブラリをビルド**

```bash
cd references/YetAnotherHttpHandler/native
cargo build --release --target x86_64-unknown-linux-gnu
```

ビルド成功後、ネイティブライブラリのパスを確認。

- [ ] **Step 3: csprojにYAHAプロジェクト参照を追加**

`tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj` に追加:

```xml
<ProjectReference Include="../../references/YetAnotherHttpHandler/src/YetAnotherHttpHandler/YetAnotherHttpHandler.csproj" />
```

- [ ] **Step 4: ClientHarnessのCreateHttpClientをYAHA対応に修正**

`tests/ConnectNet.Conformance/ClientHarness.cs` の `CreateHttpClient` メソッドを修正:

```csharp
private static HttpClient CreateHttpClient(ClientCompatRequest request)
{
    var useHttp2 = request.HttpVersion == Connectrpc.Conformance.V1.HTTPVersion._2;

    if (useHttp2)
    {
        // HTTP/2: YetAnotherHttpHandler (Rust/hyper based, supports h2c and h2)
        var handler = new Cysharp.Net.Http.YetAnotherHttpHandler();

        if (!request.ServerTlsCert.IsEmpty)
        {
            // TLSの設定（YAHA APIに合わせて調整）
            // YAHAは独自のTLS設定を持つ — 実装時にAPIを確認
        }

        return new HttpClient(handler)
        {
            DefaultRequestVersion = System.Net.HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
    }

    // HTTP/1.1: 標準SocketsHttpHandler
    var httpVersion = System.Net.HttpVersion.Version11;

    if (!request.ServerTlsCert.IsEmpty)
    {
        // 既存のTLSハンドラ設定（変更なし）
        // ...（現在のTLS設定コードをそのまま維持）
    }

    var defaultHandler = new SocketsHttpHandler();
    return new HttpClient(defaultHandler)
    {
        DefaultRequestVersion = httpVersion,
        DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
    };
}
```

注意: YAHAのTLS証明書設定APIは実装時に`references/YetAnotherHttpHandler/src/YetAnotherHttpHandler/`のソースを確認して正確な設定方法を把握すること。

- [ ] **Step 5: ビルドしてconformanceテスト実行**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -5
```

HTTP/2テストのパス数が劇的に改善されるはず。

- [ ] **Step 6: HTTP/2固有の失敗を修正**

HTTP/2テストで新たに見つかる失敗パターンがあれば修正。主に:
- h2c (cleartext HTTP/2) の接続
- TLS + HTTP/2 (ALPN negotiation)
- ストリーミングのHTTP/2固有の動作

- [ ] **Step 7: コミット**

```bash
git add tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj \
  tests/ConnectNet.Conformance/ClientHarness.cs
git commit -m "feat(conformance): integrate YetAnotherHttpHandler for HTTP/2 support"
```

---

### Task 8: 最終検証と残存失敗の修正

- [ ] **Step 1: 全conformanceテスト実行と結果分析**

```bash
dotnet build tests/ConnectNet.Conformance/ConnectNet.Conformance.csproj
/tmp/connectconformance --mode client --conf tests/ConnectNet.Conformance/config.yaml \
  -- dotnet run --no-build --project tests/ConnectNet.Conformance -- --mode client 2>&1 | tail -10
```

- [ ] **Step 2: 残存失敗の分析と修正**

全テストパスするまで、失敗パターンを分析して修正を繰り返す。

- [ ] **Step 3: ユニットテスト全体の回帰確認**

```bash
dotnet test tests/ConnectNet.Tests -v minimal
```

- [ ] **Step 4: 最終コミット**

```bash
git add -A
git commit -m "fix: resolve remaining conformance test failures"
```
