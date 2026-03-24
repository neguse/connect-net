# protovalidate C# 実装 設計書

## 概要

connect-net に `buf/validate/validate.proto` 互換のバリデーションライブラリを追加する。バリデーションエンジン本体とConnect用インターセプターを単一プロジェクト内に責務分離して実装する。

## スコープ

- **含む:** スカラー型制約、repeated/map制約、oneof制約、well-known types（Timestamp/Duration）、message制約、`ignore`/`disabled` フィールド制約、サーバーインターセプター統合
- **含まない:** CEL（Common Expression Language）ベースのカスタムバリデーション式、ストリーミングRPCのバリデーション（Unary RPCのみ対応）
- **方式:** リフレクションベース（コード生成なし）
- **対象バージョン:** `buf/validate` の最新仕様に準拠（`ignore` フィールドを使用、非推奨の `skip` も後方互換として対応）

## プロジェクト構成

```
src/ConnectNet.Validation/
├── ConnectNet.Validation.csproj       # .NET Standard 2.1
├── ProtoValidator.cs                  # エンジン本体（エントリポイント）
├── ValidationResult.cs                # バリデーション結果
├── Violation.cs                       # 個別の違反
├── Rules/                             # ルール評価ロジック
│   ├── FieldRuleEvaluator.cs          # フィールドルールの振り分け
│   ├── StringRules.cs                 # string制約
│   ├── NumericRules.cs                # 数値型制約
│   ├── BoolRules.cs                   # bool制約
│   ├── BytesRules.cs                  # bytes制約
│   ├── EnumRules.cs                   # enum制約
│   ├── RepeatedRules.cs              # repeated制約
│   ├── MapRules.cs                    # map制約
│   ├── MessageRules.cs               # message制約
│   ├── OneofRules.cs                  # oneof制約
│   └── WellKnownTypeRules.cs         # Timestamp, Duration
├── Interceptors/
│   └── ValidateInterceptor.cs         # IServerInterceptor実装
└── Internal/
    └── ConstraintCache.cs             # ルールのキャッシュ
```

**依存関係:**
- `Google.Protobuf` — protobufリフレクション用
- `ConnectNet` — `IServerInterceptor` 用（Interceptorsフォルダのみ）

## buf/validate protoの取り込み

`buf/validate/validate.proto` および `buf/validate/priv/private.proto` のC#生成コードが必要。

**方式:** `buf/validate` の `.proto` ファイルをプロジェクト内にvendoringし、`protoc` でC#コードを生成してプロジェクトに含める。

```
src/ConnectNet.Validation/
├── Proto/                             # vendored proto + 生成コード
│   └── Buf/Validate/
│       ├── validate.proto             # vendored
│       ├── Validate.cs                # protoc生成
│       ├── priv/
│       │   ├── private.proto          # vendored
│       │   └── Private.cs             # protoc生成
│       └── Expression.cs              # protoc生成（CELは未実装だが型定義は必要）
```

`ExtensionRegistry` への登録は `ProtoValidator` 初期化時に `Buf.Validate.ValidateExtensions.ForceFieldConstraint` 等を登録する。これにより `FieldDescriptor.GetOptions()` でカスタムオプションを読み取れるようになる。

## コアAPI

```csharp
// --- ValidationResult.cs ---
public class ValidationResult
{
    public bool IsValid => Violations.Count == 0;
    public IReadOnlyList<Violation> Violations { get; }

    public static ValidationResult Success { get; }
    public static ValidationResult Fail(IEnumerable<Violation> violations);
}

// --- Violation.cs ---
public class Violation
{
    public string FieldPath { get; }      // e.g. "address.city"
    public string ConstraintId { get; }   // e.g. "string.min_len"
    public string Message { get; }        // e.g. "value length must be at least 3"
    public object? Value { get; }         // 実際の値（デバッグ用）
}

// --- ProtoValidator.cs ---
public class ProtoValidator
{
    public ValidationResult Validate(IMessage message);
}
```

**設計方針:**
- `ProtoValidator` はステートレスだが、内部で `ConstraintCache` を保持してルール解析結果をキャッシュ
- `ConstraintCache` は `ConcurrentDictionary<MessageDescriptor, CompiledConstraints>` でスレッドセーフ
- `Validate` は再帰的にネストされたメッセージも検証
- `FieldPath` はドット区切りでネストを表現（`"items[0].name"` のようにrepeatedのインデックスも含む）
- `ProtoValidator` はコンストラクタで生成し、DI登録は不要（設定不要のため）

## バリデーションエンジンの処理フロー

```
Validate(IMessage)
  │
  ├─ MessageDescriptor からフィールド一覧を取得
  │
  ├─ 各フィールドについて:
  │   ├─ FieldDescriptor.GetOptions() から buf.validate.field ルールを取得
  │   ├─ ルールが無ければスキップ
  │   ├─ フィールド型に応じた Rules クラスに振り分け
  │   │   (StringRules, NumericRules, EnumRules, etc.)
  │   ├─ message型フィールドの場合は再帰的に Validate()
  │   └─ repeated/map の場合は各要素に対してもルール適用
  │
  ├─ oneof制約の評価
  │   └─ OneofDescriptor から required チェック
  │
  └─ 全Violationを集約して ValidationResult を返す
```

**ルール取得の仕組み:**
- `buf/validate/validate.proto` で定義されたカスタムオプション（`FieldConstraints`）を `ExtensionRegistry` 経由で読み取る
- 初回アクセス時に `ConstraintCache` にメッセージ型ごとのルール構造をキャッシュ

## 対応する制約一覧

| カテゴリ | 制約 |
|---------|------|
| string | `min_len`, `max_len`, `pattern`, `prefix`, `suffix`, `contains`, `in`, `not_in`, `const`, `len`, `email`, `hostname`, `ip`, `uri` |
| 数値型（全12型: float, double, int32, int64, uint32, uint64, sint32, sint64, fixed32, fixed64, sfixed32, sfixed64） | `gt`, `lt`, `gte`, `lte`, `in`, `not_in`, `const` |
| bool | `const` |
| bytes | `min_len`, `max_len`, `in`, `not_in`, `const`, `len` |
| enum | `defined_only`, `in`, `not_in`, `const` |
| repeated | `min_items`, `max_items`, `unique`, `items`（要素へのルール適用） |
| map | `min_pairs`, `max_pairs`, `keys`, `values`（キー/値へのルール適用） |
| message | `required`, `skip`（非推奨、後方互換） |
| field | `ignore`（`IGNORE_IF_UNPOPULATED`, `IGNORE_IF_DEFAULT_VALUE`, `IGNORE_ALWAYS`）, `disabled` |
| oneof | `required` |
| well-known | `Timestamp`/`Duration` に対する `gt`, `lt`, `gte`, `lte`, `within` |

## インターセプター統合

```csharp
// --- ValidateInterceptor.cs ---
public class ValidateInterceptor : IServerInterceptor
{
    private readonly ProtoValidator _validator;

    public ValidateInterceptor()
    {
        _validator = new ProtoValidator();
    }

    public async Task<IMessage> InterceptUnaryAsync(
        UnaryServerContext context,
        Func<UnaryServerContext, Task<IMessage>> next)
    {
        var result = _validator.Validate(context.Request);
        if (!result.IsValid)
        {
            throw new ConnectException(
                ConnectCode.InvalidArgument,
                FormatMessage(result.Violations),
                ToErrorDetails(result.Violations));
        }
        return await next(context);
    }
}
```

**エラーメッセージのフォーマット:**
- `FormatMessage`: `"validation failed: {最初の違反メッセージ}"` 形式。複数違反がある場合は `"validation failed: {最初のメッセージ} (and {N} more)"` 形式

**エラー詳細のマッピング:**
- `Violation` のリストから `buf.validate.Violations` protobufメッセージを構築
- それを `ConnectErrorDetail(type: "buf.validate.Violations", value: serializedBytes)` としてエラーに添付
- connect-goの `connectrpc.com/validate` と同じ形式（`buf.validate.Violations` 型を使用）
- `google.rpc.BadRequest` への依存は不要（`buf.validate` が独自のViolations型を定義しているため）

**登録方法:**

```csharp
// サーバー側
builder.Services.AddConnectServices(options =>
{
    options.Interceptors.Add(new ValidateInterceptor());
});
```

**備考:** クライアント側バリデーション（`ValidateClientInterceptor`）は v1 ではスコープ外。エンジン本体（`ProtoValidator`）はConnect非依存なので、クライアント側で手動バリデーションしたい場合は直接 `ProtoValidator.Validate()` を呼べる。

## テスト戦略

**テスト用protoファイル:** `tests/ConnectNet.Tests.Proto/` にバリデーションルール付きの `.proto` を追加

```protobuf
syntax = "proto3";
import "buf/validate/validate.proto";

message TestStringMessage {
  string name = 1 [(buf.validate.field).string.min_len = 3];
  string email = 2 [(buf.validate.field).string.email = true];
}

message TestNestedMessage {
  TestStringMessage inner = 1 [(buf.validate.field).required = true];
  repeated string tags = 2 [(buf.validate.field).repeated.min_items = 1];
}
```

**テスト構成:**

| テストクラス | 対象 |
|-------------|------|
| `ProtoValidatorTests` | エンジン全体の基本動作 |
| `StringRulesTests` | string制約の各ルール |
| `NumericRulesTests` | 数値型制約 |
| `RepeatedRulesTests` | repeated/map制約 |
| `EnumRulesTests` | enum制約 |
| `WellKnownTypeRulesTests` | Timestamp/Duration制約 |
| `OneofRulesTests` | oneof制約 |
| `ValidateInterceptorTests` | インターセプター統合 |
| `ValidationResultTests` | 結果型のユニットテスト |

**テスト方針:**
- 各ルールの正常系・異常系・境界値をカバー
- ネストしたメッセージの再帰バリデーション
- `FieldPath` が正しく構築されることの検証
- インターセプターが `ConnectException(InvalidArgument)` を正しく投げることの確認
