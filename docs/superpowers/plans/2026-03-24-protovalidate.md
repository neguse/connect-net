# protovalidate C# 実装 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** connect-net に buf/validate 互換のバリデーションエンジンとサーバーインターセプターを追加する

**Architecture:** `src/ConnectNet.Validation/` に単一プロジェクトとして実装。`ProtoValidator` がリフレクションベースでprotobufメッセージのフィールド制約を評価し `ValidationResult` を返す。`ValidateInterceptor` が `IServerInterceptor` としてそれをConnectエラーに変換する。`buf/validate/validate.proto` のC#生成コードをvendoringして含める。

**Tech Stack:** C# / .NET Standard 2.1 / Google.Protobuf / xunit

---

## File Structure

```
src/ConnectNet.Validation/
├── ConnectNet.Validation.csproj       # Create: プロジェクトファイル (.NET Standard 2.1, Grpc.Tools含む)
├── Proto/
│   └── buf/validate/
│       ├── validate.proto             # Create: vendored (buf/validate本体)
│       └── priv/
│           └── private.proto          # Create: vendored (内部proto)
├── ValidationResult.cs                # Create: バリデーション結果型
├── Violation.cs                       # Create: 個別の違反型
├── ProtoValidator.cs                  # Create: バリデーションエンジン
├── Rules/
│   ├── FieldRuleEvaluator.cs          # Create: フィールドルールの振り分け
│   ├── StringRules.cs                 # Create: string制約評価
│   ├── NumericRules.cs                # Create: 数値型制約評価
│   ├── BoolRules.cs                   # Create: bool制約評価
│   ├── BytesRules.cs                  # Create: bytes制約評価
│   ├── EnumRules.cs                   # Create: enum制約評価
│   ├── RepeatedRules.cs              # Create: repeated制約評価
│   ├── MapRules.cs                    # Create: map制約評価
│   ├── OneofRules.cs                  # Create: oneof制約評価 (message required含む)
│   └── WellKnownTypeRules.cs         # Create: Timestamp/Duration制約
├── Interceptors/
│   └── ValidateInterceptor.cs         # Create: IServerInterceptor実装
└── Internal/
    └── ConstraintCache.cs             # Create: スレッドセーフなルールキャッシュ

tests/ConnectNet.Tests.Proto/
├── validation_test.proto              # Create: テスト用バリデーション付きメッセージ

tests/ConnectNet.Tests/
├── ValidationResultTests.cs           # Create: 結果型のユニットテスト
├── ProtoValidatorTests.cs             # Create: エンジン全体テスト
├── StringRulesTests.cs                # Create: string制約テスト
├── NumericRulesTests.cs               # Create: 数値型テスト
├── BoolBytesRulesTests.cs            # Create: bool/bytes制約テスト
├── RepeatedRulesTests.cs             # Create: repeated/mapテスト
├── EnumRulesTests.cs                  # Create: enum制約テスト
├── WellKnownTypeRulesTests.cs        # Create: Timestamp/Durationテスト
├── OneofRulesTests.cs                 # Create: oneof制約テスト
└── ValidateInterceptorTests.cs        # Create: インターセプターテスト

connect-net.slnx                       # Modify: Validationプロジェクト追加
```

**Proto vendoring 方針:** `buf/validate/validate.proto` は `src/ConnectNet.Validation/Proto/` 内にvendoringし、`Grpc.Tools` でC#コードを生成する。これにより `Buf.Validate.*` 型が `ConnectNet.Validation` プロジェクトから直接利用可能になる。テスト用protoは `tests/ConnectNet.Tests.Proto/` でこのプロジェクトを参照して `validation_test.proto` をコンパイルする。

**FieldPath の命名規則:** `field.JsonName`（camelCase）を使用する。これはConnect ProtocolのJSON表現と一致する。

---

### Task 1: プロジェクトセットアップとコア型

**Files:**
- Create: `src/ConnectNet.Validation/ConnectNet.Validation.csproj`
- Create: `src/ConnectNet.Validation/ValidationResult.cs`
- Create: `src/ConnectNet.Validation/Violation.cs`
- Create: `tests/ConnectNet.Tests/ValidationResultTests.cs`
- Modify: `connect-net.slnx`
- Modify: `tests/ConnectNet.Tests/ConnectNet.Tests.csproj`

- [ ] **Step 1: プロジェクトファイル作成**

```xml
<!-- src/ConnectNet.Validation/ConnectNet.Validation.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Google.Protobuf" Version="3.*" />
    <PackageReference Include="Grpc.Tools" Version="2.*" PrivateAssets="All" />
    <Protobuf Include="Proto/buf/validate/validate.proto" GrpcServices="None" AdditionalImportDirs="Proto" />
    <Protobuf Include="Proto/buf/validate/priv/private.proto" GrpcServices="None" AdditionalImportDirs="Proto" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../ConnectNet/ConnectNet.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: ソリューションにプロジェクト追加**

`connect-net.slnx` の `/src/` フォルダに追加:

```xml
<Project Path="src/ConnectNet.Validation/ConnectNet.Validation.csproj" />
```

テストプロジェクトに参照追加 (`tests/ConnectNet.Tests/ConnectNet.Tests.csproj`):

```xml
<ProjectReference Include="../../src/ConnectNet.Validation/ConnectNet.Validation.csproj" />
```

- [ ] **Step 3: Violation クラス作成**

```csharp
// src/ConnectNet.Validation/Violation.cs
namespace ConnectNet.Validation;

public class Violation
{
    public string FieldPath { get; }
    public string ConstraintId { get; }
    public string Message { get; }
    public object? Value { get; }

    public Violation(string fieldPath, string constraintId, string message, object? value = null)
    {
        FieldPath = fieldPath;
        ConstraintId = constraintId;
        Message = message;
        Value = value;
    }
}
```

- [ ] **Step 4: ValidationResult クラス作成**

```csharp
// src/ConnectNet.Validation/ValidationResult.cs
using System.Collections.Generic;
using System.Linq;

namespace ConnectNet.Validation;

public class ValidationResult
{
    public static readonly ValidationResult Success = new(Enumerable.Empty<Violation>());

    public bool IsValid => Violations.Count == 0;
    public IReadOnlyList<Violation> Violations { get; }

    private ValidationResult(IEnumerable<Violation> violations)
    {
        Violations = violations.ToList().AsReadOnly();
    }

    public static ValidationResult Fail(IEnumerable<Violation> violations)
    {
        return new ValidationResult(violations);
    }
}
```

- [ ] **Step 5: ValidationResult テスト作成**

```csharp
// tests/ConnectNet.Tests/ValidationResultTests.cs
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class ValidationResultTests
{
    [Fact]
    public void Success_IsValid()
    {
        Assert.True(ValidationResult.Success.IsValid);
        Assert.Empty(ValidationResult.Success.Violations);
    }

    [Fact]
    public void Fail_WithViolations_IsNotValid()
    {
        var result = ValidationResult.Fail(new[]
        {
            new Violation("name", "string.min_len", "value length must be at least 3", "ab")
        });

        Assert.False(result.IsValid);
        Assert.Single(result.Violations);
        Assert.Equal("name", result.Violations[0].FieldPath);
        Assert.Equal("string.min_len", result.Violations[0].ConstraintId);
        Assert.Equal("value length must be at least 3", result.Violations[0].Message);
        Assert.Equal("ab", result.Violations[0].Value);
    }

    [Fact]
    public void Fail_WithEmptyViolations_IsValid()
    {
        var result = ValidationResult.Fail(System.Array.Empty<Violation>());
        Assert.True(result.IsValid);
    }
}
```

- [ ] **Step 6: ビルドとテスト実行**

Run: `cd /home/neguse/ghq/github.com/neguse/connect-net && dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ValidationResultTests" -v n`
Expected: 3 tests PASS

- [ ] **Step 7: コミット**

```bash
git add src/ConnectNet.Validation/ connect-net.slnx tests/ConnectNet.Tests/ConnectNet.Tests.csproj tests/ConnectNet.Tests/ValidationResultTests.cs
git commit -m "feat(validation): add project setup and core types (ValidationResult, Violation)"
```

---

### Task 2: buf/validate proto の vendoring とテスト用protoの作成

**Files:**
- Create: `src/ConnectNet.Validation/Proto/buf/validate/validate.proto` (vendored)
- Create: `src/ConnectNet.Validation/Proto/buf/validate/priv/private.proto` (vendored)
- Create: `tests/ConnectNet.Tests.Proto/validation_test.proto`
- Modify: `tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj`

- [ ] **Step 1: buf/validate protoファイルの取得**

`buf/validate/validate.proto` と `buf/validate/priv/private.proto` を bufbuild/protovalidate リポジトリから取得し `src/ConnectNet.Validation/Proto/` にvendoringする。

Run:
```bash
cd /home/neguse/ghq/github.com/neguse/connect-net
mkdir -p src/ConnectNet.Validation/Proto/buf/validate/priv
curl -sL "https://raw.githubusercontent.com/bufbuild/protovalidate/main/proto/protovalidate/buf/validate/validate.proto" -o src/ConnectNet.Validation/Proto/buf/validate/validate.proto
curl -sL "https://raw.githubusercontent.com/bufbuild/protovalidate/main/proto/protovalidate/buf/validate/priv/private.proto" -o src/ConnectNet.Validation/Proto/buf/validate/priv/private.proto
```

もしURLが変わっている場合は、bufbuild/protovalidate リポジトリから手動で取得する。

- [ ] **Step 1.5: Validationプロジェクトのビルド確認**

Run: `cd /home/neguse/ghq/github.com/neguse/connect-net && dotnet build src/ConnectNet.Validation`
Expected: BUILD SUCCEEDED（validate.protoからC#コードが生成される）

ビルド後、生成されたC#コード（`obj/` 以下）を確認して、以降のタスクで使用するAPI名（`Buf.Validate.FieldConstraints`, `ValidateExtensions` 等）を特定すること。生成コードのAPI名がプラン内のコードと異なる場合は、プランのコードを調整する。

- [ ] **Step 2: テスト用protoファイル作成**

```protobuf
// tests/ConnectNet.Tests.Proto/validation_test.proto
syntax = "proto3";
package validation_test;
option csharp_namespace = "ConnectNet.Tests.Proto";

import "buf/validate/validate.proto";
import "google/protobuf/timestamp.proto";
import "google/protobuf/duration.proto";

// --- String rules ---
message StringTestMessage {
  string name = 1 [(buf.validate.field).string.min_len = 3];
  string email = 2 [(buf.validate.field).string.email = true];
  string code = 3 [(buf.validate.field).string = {min_len: 2, max_len: 10}];
  string prefix_val = 4 [(buf.validate.field).string.prefix = "pre_"];
  string pattern_val = 5 [(buf.validate.field).string.pattern = "^[a-z]+$"];
}

// --- Numeric rules ---
message NumericTestMessage {
  int32 age = 1 [(buf.validate.field).int32.gte = 0, (buf.validate.field).int32.lte = 150];
  double score = 2 [(buf.validate.field).double.gte = 0.0, (buf.validate.field).double.lte = 100.0];
  uint64 count = 3 [(buf.validate.field).uint64.gt = 0];
}

// --- Bool rules ---
message BoolTestMessage {
  bool must_be_true = 1 [(buf.validate.field).bool.const = true];
}

// --- Enum rules ---
enum Status {
  STATUS_UNSPECIFIED = 0;
  STATUS_ACTIVE = 1;
  STATUS_INACTIVE = 2;
}

message EnumTestMessage {
  Status status = 1 [(buf.validate.field).enum.defined_only = true];
}

// --- Repeated rules ---
message RepeatedTestMessage {
  repeated string tags = 1 [(buf.validate.field).repeated = {min_items: 1, max_items: 5}];
  repeated int32 scores = 2 [(buf.validate.field).repeated.unique = true];
}

// --- Map rules ---
message MapTestMessage {
  map<string, string> labels = 1 [(buf.validate.field).map = {min_pairs: 1, max_pairs: 10}];
}

// --- Message / nested ---
message NestedTestMessage {
  StringTestMessage inner = 1 [(buf.validate.field).required = true];
  repeated StringTestMessage items = 2;
}

// --- Oneof rules ---
message OneofTestMessage {
  oneof contact {
    option (buf.validate.oneof).required = true;
    string email = 1;
    string phone = 2;
  }
}

// --- Well-known types ---
message TimestampTestMessage {
  google.protobuf.Timestamp created_at = 1 [(buf.validate.field).required = true];
}

message DurationTestMessage {
  google.protobuf.Duration timeout = 1 [(buf.validate.field).required = true];
}

// --- Disabled / ignore field ---
message DisabledFieldMessage {
  string name = 1 [(buf.validate.field).string.min_len = 3];
  string skip_me = 2 [(buf.validate.field) = {ignore: IGNORE_ALWAYS, string: {min_len: 5}}];
}
```

- [ ] **Step 3: ConnectNet.Tests.Proto.csproj にprotoファイル追加**

`tests/ConnectNet.Tests.Proto/ConnectNet.Tests.Proto.csproj` を修正。`validation_test.proto` の import解決のため `ConnectNet.Validation` のProtoディレクトリをインポートパスに含める:

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
    <Protobuf Include="validation_test.proto" GrpcServices="None" AdditionalImportDirs="../../src/ConnectNet.Validation/Proto" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/ConnectNet/ConnectNet.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Client/ConnectNet.Client.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Server/ConnectNet.Server.csproj" />
    <ProjectReference Include="../../src/ConnectNet.Validation/ConnectNet.Validation.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: ビルド確認**

Run: `cd /home/neguse/ghq/github.com/neguse/connect-net && dotnet build tests/ConnectNet.Tests.Proto`
Expected: BUILD SUCCEEDED（protoからC#コードが生成される）

- [ ] **Step 5: コミット**

```bash
git add tests/ConnectNet.Tests.Proto/
git commit -m "feat(validation): vendor buf/validate proto and add validation test messages"
```

---

### Task 3: ConstraintCache と FieldRuleEvaluator の基盤

**Files:**
- Create: `src/ConnectNet.Validation/Internal/ConstraintCache.cs`
- Create: `src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs`
- Create: `src/ConnectNet.Validation/ProtoValidator.cs`
- Create: `tests/ConnectNet.Tests/ProtoValidatorTests.cs`

- [ ] **Step 1: ProtoValidator テスト作成（最小ケース）**

```csharp
// tests/ConnectNet.Tests/ProtoValidatorTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class ProtoValidatorTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Validate_ValidMessage_ReturnsSuccess()
    {
        var msg = new StringTestMessage { Name = "Alice", Email = "alice@example.com", Code = "abc" };
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_InvalidMessage_ReturnsViolations()
    {
        var msg = new StringTestMessage { Name = "Al" }; // min_len = 3
        var result = _validator.Validate(msg);
        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.FieldPath == "name" && v.ConstraintId == "string.min_len");
    }

    [Fact]
    public void Validate_MessageWithoutConstraints_ReturnsSuccess()
    {
        var msg = new HelloRequest { Name = "anything" };
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }
}
```

- [ ] **Step 2: ConstraintCache 作成**

```csharp
// src/ConnectNet.Validation/Internal/ConstraintCache.cs
using System.Collections.Concurrent;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Internal;

internal class ConstraintCache
{
    private readonly ConcurrentDictionary<string, FieldConstraintInfo[]> _cache = new();

    public FieldConstraintInfo[] GetOrAdd(MessageDescriptor descriptor, System.Func<MessageDescriptor, FieldConstraintInfo[]> factory)
    {
        return _cache.GetOrAdd(descriptor.FullName, _ => factory(descriptor));
    }
}

internal class FieldConstraintInfo
{
    public FieldDescriptor Field { get; }
    public Google.Protobuf.IMessage? Constraints { get; }

    public FieldConstraintInfo(FieldDescriptor field, Google.Protobuf.IMessage? constraints)
    {
        Field = field;
        Constraints = constraints;
    }
}
```

- [ ] **Step 4: FieldRuleEvaluator スタブ作成**

```csharp
// src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class FieldRuleEvaluator
{
    public static List<Violation> Evaluate(FieldDescriptor field, object? value, string path, IMessage? constraints)
    {
        var violations = new List<Violation>();

        if (constraints == null)
            return violations;

        // 各ルールタイプへの振り分けは後続タスクで実装
        switch (field.FieldType)
        {
            case FieldType.String:
                StringRules.Evaluate(violations, field, value as string ?? "", path, constraints);
                break;
        }

        return violations;
    }
}
```

- [ ] **Step 5: ProtoValidator 作成**

```csharp
// src/ConnectNet.Validation/ProtoValidator.cs
using System.Collections.Generic;
using System.Linq;
using ConnectNet.Validation.Internal;
using ConnectNet.Validation.Rules;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation;

public class ProtoValidator
{
    private readonly ConstraintCache _cache = new();

    public ValidationResult Validate(IMessage message)
    {
        var violations = new List<Violation>();
        ValidateMessage(message, "", violations);
        return violations.Count == 0 ? ValidationResult.Success : ValidationResult.Fail(violations);
    }

    private void ValidateMessage(IMessage message, string prefix, List<Violation> violations)
    {
        var descriptor = message.Descriptor;

        // ConstraintCache を使ってメッセージ型ごとのルール解析結果をキャッシュ
        var constraintInfos = _cache.GetOrAdd(descriptor, desc =>
        {
            var infos = new System.Collections.Generic.List<FieldConstraintInfo>();
            foreach (var f in desc.Fields.InFieldNumberOrder())
            {
                var opts = f.GetOptions();
                var c = opts != null ? GetFieldConstraints(opts) : null;
                infos.Add(new FieldConstraintInfo(f, c));
            }
            return infos.ToArray();
        });

        foreach (var info in constraintInfos)
        {
            var field = info.Field;
            var constraints = info.Constraints;
            var fieldPath = string.IsNullOrEmpty(prefix) ? field.JsonName : $"{prefix}.{field.JsonName}";
            var value = field.Accessor.GetValue(message);

            if (constraints == null)
                continue;

            // ignore / disabled チェック
            if (ShouldIgnore(constraints, field, value))
                continue;

            // required チェック (message型)
            if (field.FieldType == FieldType.Message && IsRequired(constraints) && value == null)
            {
                violations.Add(new Violation(fieldPath, "required", "value is required"));
                continue;
            }

            // repeated フィールド
            if (field.IsRepeated && !field.IsMap)
            {
                var list = value as System.Collections.IList;
                RepeatedRules.Evaluate(violations, field, list, fieldPath, constraints);
                // 各要素のバリデーション
                if (list != null && field.FieldType == FieldType.Message)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (list[i] is IMessage itemMsg)
                            ValidateMessage(itemMsg, $"{fieldPath}[{i}]", violations);
                    }
                }
                continue;
            }

            // map フィールド
            if (field.IsMap)
            {
                MapRules.Evaluate(violations, field, value, fieldPath, constraints);
                continue;
            }

            // スカラー/enum/messageフィールドのルール評価
            var fieldViolations = FieldRuleEvaluator.Evaluate(field, value, fieldPath, constraints);
            violations.AddRange(fieldViolations);

            // ネストされたmessageの再帰バリデーション
            if (field.FieldType == FieldType.Message && value is IMessage nestedMsg)
            {
                ValidateMessage(nestedMsg, fieldPath, violations);
            }
        }

        // oneof制約の評価
        OneofRules.EvaluateOneofs(violations, message, prefix);
    }

    private IMessage? GetFieldConstraints(Google.Protobuf.Reflection.FieldOptions options)
    {
        // buf/validate extension からFieldConstraintsを取得する
        // 具体的な実装は buf/validate の生成コードに依存
        // Task 2 で生成された Buf.Validate.FieldConstraints を使用
        try
        {
            var extension = Buf.Validate.ValidateExtensions.Field;
            return options.GetExtension(extension);
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldIgnore(IMessage constraints, FieldDescriptor field, object? value)
    {
        if (constraints is Buf.Validate.FieldConstraints fc)
        {
            // disabled フィールド: バリデーション完全スキップ
            if (fc.Disabled)
                return true;

            if (fc.Ignore == Buf.Validate.Ignore.Always)
                return true;

            // IGNORE_IF_UNPOPULATED: フィールドが未設定の場合スキップ
            if (fc.Ignore == Buf.Validate.Ignore.IfUnpopulated)
            {
                if (field.HasPresence)
                {
                    // optional/message: presence tracking でチェック
                    if (value == null) return true;
                }
                else
                {
                    // proto3 implicit presence: デフォルト値ならスキップ
                    if (IsDefaultValue(field, value)) return true;
                }
            }

            // IGNORE_IF_DEFAULT_VALUE: 値がデフォルトの場合スキップ
            if (fc.Ignore == Buf.Validate.Ignore.IfDefaultValue)
            {
                if (IsDefaultValue(field, value)) return true;
            }
        }
        return false;
    }

    private bool IsDefaultValue(FieldDescriptor field, object? value)
    {
        if (value == null) return true;
        return field.FieldType switch
        {
            FieldType.String => (string)value == "",
            FieldType.Bool => (bool)value == false,
            FieldType.Int32 or FieldType.SInt32 or FieldType.SFixed32 => System.Convert.ToInt32(value) == 0,
            FieldType.Int64 or FieldType.SInt64 or FieldType.SFixed64 => System.Convert.ToInt64(value) == 0,
            FieldType.UInt32 or FieldType.Fixed32 => System.Convert.ToUInt32(value) == 0,
            FieldType.UInt64 or FieldType.Fixed64 => System.Convert.ToUInt64(value) == 0,
            FieldType.Float => System.Convert.ToSingle(value) == 0f,
            FieldType.Double => System.Convert.ToDouble(value) == 0d,
            FieldType.Enum => System.Convert.ToInt32(value) == 0,
            FieldType.Bytes => ((Google.Protobuf.ByteString)value).IsEmpty,
            FieldType.Message => false, // message default is null, handled above
            _ => false
        };
    }

    private bool IsRequired(IMessage constraints)
    {
        if (constraints is Buf.Validate.FieldConstraints fc)
            return fc.Required;
        return false;
    }
}
```

注: `Buf.Validate.ValidateExtensions.Field` や `Buf.Validate.FieldConstraints` の正確なAPI名は、Task 2で生成されたコードに合わせて調整する必要がある。生成コードを確認してからメソッド名を修正すること。

- [ ] **Step 6: StringRules の最小実装**

```csharp
// src/ConnectNet.Validation/Rules/StringRules.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class StringRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, string value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc || fc.Type == null)
            return;

        if (fc.Type.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.String)
            return;

        var rules = fc.String;

        if (rules.MinLen > 0 && value.Length < (int)rules.MinLen)
        {
            violations.Add(new Violation(path, "string.min_len",
                $"value length must be at least {rules.MinLen}", value));
        }
    }
}
```

注: 他のStringルールは Task 4 で追加する。ここでは `min_len` のみで ProtoValidator の基本動作を確認する。

- [ ] **Step 7: RepeatedRules / MapRules / OneofRules のスタブ作成**

各ルールクラスのスタブを作成する（後続タスクで実装を追加）:

```csharp
// src/ConnectNet.Validation/Rules/RepeatedRules.cs
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class RepeatedRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, IList? list, string path, IMessage constraints)
    {
        // 後続タスクで実装
    }
}
```

```csharp
// src/ConnectNet.Validation/Rules/MapRules.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class MapRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, object? value, string path, IMessage constraints)
    {
        // 後続タスクで実装
    }
}
```

```csharp
// src/ConnectNet.Validation/Rules/OneofRules.cs
using System.Collections.Generic;
using Google.Protobuf;

namespace ConnectNet.Validation.Rules;

internal static class OneofRules
{
    public static void EvaluateOneofs(List<Violation> violations, IMessage message, string prefix)
    {
        // 後続タスクで実装
    }
}
```

- [ ] **Step 8: ビルドとテスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ProtoValidatorTests" -v n`
Expected: 3 tests PASS

- [ ] **Step 9: コミット**

```bash
git add src/ConnectNet.Validation/ tests/ConnectNet.Tests/ProtoValidatorTests.cs
git commit -m "feat(validation): add ProtoValidator engine with basic string.min_len support"
```

---

### Task 4: StringRules の完全実装

**Files:**
- Modify: `src/ConnectNet.Validation/Rules/StringRules.cs`
- Create: `tests/ConnectNet.Tests/StringRulesTests.cs`

- [ ] **Step 1: StringRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/StringRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class StringRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void MinLen_Valid()
    {
        var msg = new StringTestMessage { Name = "Alice" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.min_len" && v.FieldPath == "name");
    }

    [Fact]
    public void MinLen_Invalid()
    {
        var msg = new StringTestMessage { Name = "Al" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "string.min_len" && v.FieldPath == "name");
    }

    [Fact]
    public void MaxLen_Valid()
    {
        var msg = new StringTestMessage { Code = "abcde" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.max_len" && v.FieldPath == "code");
    }

    [Fact]
    public void MaxLen_Invalid()
    {
        var msg = new StringTestMessage { Code = "abcdefghijk" }; // > 10
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "string.max_len" && v.FieldPath == "code");
    }

    [Fact]
    public void Email_Valid()
    {
        var msg = new StringTestMessage { Name = "Alice", Email = "alice@example.com" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.email");
    }

    [Fact]
    public void Email_Invalid()
    {
        var msg = new StringTestMessage { Name = "Alice", Email = "not-an-email" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "string.email");
    }

    [Fact]
    public void Prefix_Valid()
    {
        var msg = new StringTestMessage { PrefixVal = "pre_something" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.prefix");
    }

    [Fact]
    public void Prefix_Invalid()
    {
        var msg = new StringTestMessage { PrefixVal = "something" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "string.prefix");
    }

    [Fact]
    public void Pattern_Valid()
    {
        var msg = new StringTestMessage { PatternVal = "abcdef" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "string.pattern");
    }

    [Fact]
    public void Pattern_Invalid()
    {
        var msg = new StringTestMessage { PatternVal = "ABC123" };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "string.pattern");
    }
}
```

- [ ] **Step 2: テスト実行、失敗確認**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~StringRulesTests" -v n`
Expected: min_len以外のテストが FAIL

- [ ] **Step 3: StringRules 完全実装**

`src/ConnectNet.Validation/Rules/StringRules.cs` を以下に置き換え:

```csharp
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class StringRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, string value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;

        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.String)
            return;

        var rules = fc.String;

        // const
        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, "string.const", $"value must equal '{rules.Const}'", value));

        // len
        if (rules.HasLen && value.Length != (int)rules.Len)
            violations.Add(new Violation(path, "string.len", $"value length must be exactly {rules.Len}", value));

        // min_len
        if (rules.HasMinLen && value.Length < (int)rules.MinLen)
            violations.Add(new Violation(path, "string.min_len", $"value length must be at least {rules.MinLen}", value));

        // max_len
        if (rules.HasMaxLen && value.Length > (int)rules.MaxLen)
            violations.Add(new Violation(path, "string.max_len", $"value length must be at most {rules.MaxLen}", value));

        // pattern
        if (rules.HasPattern && !Regex.IsMatch(value, rules.Pattern))
            violations.Add(new Violation(path, "string.pattern", $"value must match pattern '{rules.Pattern}'", value));

        // prefix
        if (rules.HasPrefix && !value.StartsWith(rules.Prefix))
            violations.Add(new Violation(path, "string.prefix", $"value must start with '{rules.Prefix}'", value));

        // suffix
        if (rules.HasSuffix && !value.EndsWith(rules.Suffix))
            violations.Add(new Violation(path, "string.suffix", $"value must end with '{rules.Suffix}'", value));

        // contains
        if (rules.HasContains && !value.Contains(rules.Contains))
            violations.Add(new Violation(path, "string.contains", $"value must contain '{rules.Contains}'", value));

        // in
        if (rules.In.Count > 0 && !rules.In.Contains(value))
            violations.Add(new Violation(path, "string.in", $"value must be in [{string.Join(", ", rules.In)}]", value));

        // not_in
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
            violations.Add(new Violation(path, "string.not_in", $"value must not be in [{string.Join(", ", rules.NotIn)}]", value));

        // well-known string format validations
        // Note: In buf/validate, email/hostname/ip/uri are part of a `well_known` oneof.
        // The C# generated code will expose them via a WellKnownCase enum.
        // Adjust the access pattern below based on the actual generated code API.
        // If they are accessed as `rules.WellKnownCase == StringRules.WellKnownOneofCase.Email`:
        //   check the case and validate accordingly.
        // If they are accessed as bool properties (e.g., `rules.Email`):
        //   use the pattern below directly.

        // email (simple validation)
        if (IsWellKnownCase(rules, "email"))
        {
            if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                violations.Add(new Violation(path, "string.email", "value must be a valid email address", value));
        }

        // hostname
        if (IsWellKnownCase(rules, "hostname"))
        {
            if (string.IsNullOrEmpty(value) || !Regex.IsMatch(value, @"^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)*$"))
                violations.Add(new Violation(path, "string.hostname", "value must be a valid hostname", value));
        }

        // ip
        if (IsWellKnownCase(rules, "ip"))
        {
            if (!System.Net.IPAddress.TryParse(value, out _))
                violations.Add(new Violation(path, "string.ip", "value must be a valid IP address", value));
        }

        // uri
        if (IsWellKnownCase(rules, "uri"))
        {
            if (!System.Uri.TryCreate(value, System.UriKind.Absolute, out _))
                violations.Add(new Violation(path, "string.uri", "value must be a valid URI", value));
        }
    }
}
```

注: `HasConst` 等の存在チェックは生成コードのAPIを確認して修正する。`IsWellKnownCase` ヘルパーメソッドは、生成コードの `WellKnownCase` enum に基づいて実装する:

```csharp
    // Helper: buf/validate の StringRules.well_known oneofを判定
    // 生成コードにより実装を調整。例:
    //   rules.WellKnownCase == Buf.Validate.StringRules.WellKnownOneofCase.Email
    private static bool IsWellKnownCase(object rules, string caseName)
    {
        // 生成コードの実際のAPI に合わせて実装する
        // 暫定: リフレクションで WellKnownCase プロパティを読み取る
        var prop = rules.GetType().GetProperty("WellKnownCase");
        if (prop == null) return false;
        var val = prop.GetValue(rules);
        return val?.ToString()?.Equals(caseName, System.StringComparison.OrdinalIgnoreCase) == true;
    }
```

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~StringRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Validation/Rules/StringRules.cs tests/ConnectNet.Tests/StringRulesTests.cs
git commit -m "feat(validation): implement full StringRules validation"
```

---

### Task 5: NumericRules の実装

**Files:**
- Create: `src/ConnectNet.Validation/Rules/NumericRules.cs`
- Modify: `src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs`
- Create: `tests/ConnectNet.Tests/NumericRulesTests.cs`

- [ ] **Step 1: NumericRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/NumericRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class NumericRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Int32_Gte_Valid()
    {
        var msg = new NumericTestMessage { Age = 25 };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "age" && v.ConstraintId == "int32.gte");
    }

    [Fact]
    public void Int32_Gte_Invalid()
    {
        var msg = new NumericTestMessage { Age = -1 };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "age" && v.ConstraintId == "int32.gte");
    }

    [Fact]
    public void Int32_Lte_Invalid()
    {
        var msg = new NumericTestMessage { Age = 200 };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "age" && v.ConstraintId == "int32.lte");
    }

    [Fact]
    public void Double_Range_Valid()
    {
        var msg = new NumericTestMessage { Score = 85.5 };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "score");
    }

    [Fact]
    public void Double_Range_Invalid()
    {
        var msg = new NumericTestMessage { Score = 101.0 };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "score" && v.ConstraintId == "double.lte");
    }

    [Fact]
    public void Uint64_Gt_Invalid()
    {
        var msg = new NumericTestMessage { Count = 0 };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "count" && v.ConstraintId == "uint64.gt");
    }
}
```

- [ ] **Step 2: テスト実行、失敗確認**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~NumericRulesTests" -v n`
Expected: FAIL

- [ ] **Step 3: NumericRules 実装**

```csharp
// src/ConnectNet.Validation/Rules/NumericRules.cs
using System;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class NumericRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, object? value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;

        switch (field.FieldType)
        {
            case FieldType.Int32:
            case FieldType.SInt32:
            case FieldType.SFixed32:
                EvaluateInt32(violations, path, Convert.ToInt32(value), fc);
                break;
            case FieldType.Int64:
            case FieldType.SInt64:
            case FieldType.SFixed64:
                EvaluateInt64(violations, path, Convert.ToInt64(value), fc);
                break;
            case FieldType.UInt32:
            case FieldType.Fixed32:
                EvaluateUInt32(violations, path, Convert.ToUInt32(value), fc);
                break;
            case FieldType.UInt64:
            case FieldType.Fixed64:
                EvaluateUInt64(violations, path, Convert.ToUInt64(value), fc);
                break;
            case FieldType.Float:
                EvaluateFloat(violations, path, Convert.ToSingle(value), fc);
                break;
            case FieldType.Double:
                EvaluateDouble(violations, path, Convert.ToDouble(value), fc);
                break;
        }
    }

    private static void EvaluateInt32(List<Violation> violations, string path, int value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Int32)
            return;
        var rules = fc.Int32;
        var prefix = "int32";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
        if (rules.In.Count > 0 && !rules.In.Contains(value))
            violations.Add(new Violation(path, $"{prefix}.in", $"value must be in [{string.Join(", ", rules.In)}]", value));
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
            violations.Add(new Violation(path, $"{prefix}.not_in", $"value must not be in [{string.Join(", ", rules.NotIn)}]", value));
    }

    // 同様のパターンで EvaluateInt64, EvaluateUInt32, EvaluateUInt64, EvaluateFloat, EvaluateDouble を実装
    // 各メソッドは対応する型のルール（Int64Rules, UInt32Rules等）から制約を読み取る
    // プロパティ名は生成コードに合わせて調整

    private static void EvaluateInt64(List<Violation> violations, string path, long value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Int64)
            return;
        var rules = fc.Int64;
        var prefix = "int64";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
    }

    private static void EvaluateUInt32(List<Violation> violations, string path, uint value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Uint32)
            return;
        var rules = fc.Uint32;
        var prefix = "uint32";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
    }

    private static void EvaluateUInt64(List<Violation> violations, string path, ulong value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Uint64)
            return;
        var rules = fc.Uint64;
        var prefix = "uint64";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
    }

    private static void EvaluateFloat(List<Violation> violations, string path, float value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Float)
            return;
        var rules = fc.Float;
        var prefix = "float";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
    }

    private static void EvaluateDouble(List<Violation> violations, string path, double value, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Double)
            return;
        var rules = fc.Double;
        var prefix = "double";

        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, $"{prefix}.const", $"value must equal {rules.Const}", value));
        if (rules.HasGt && value <= rules.Gt)
            violations.Add(new Violation(path, $"{prefix}.gt", $"value must be greater than {rules.Gt}", value));
        if (rules.HasGte && value < rules.Gte)
            violations.Add(new Violation(path, $"{prefix}.gte", $"value must be greater than or equal to {rules.Gte}", value));
        if (rules.HasLt && value >= rules.Lt)
            violations.Add(new Violation(path, $"{prefix}.lt", $"value must be less than {rules.Lt}", value));
        if (rules.HasLte && value > rules.Lte)
            violations.Add(new Violation(path, $"{prefix}.lte", $"value must be less than or equal to {rules.Lte}", value));
    }
}
```

- [ ] **Step 4: FieldRuleEvaluator に数値型の振り分けを追加**

`src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs` を修正して数値型を追加:

```csharp
case FieldType.Int32:
case FieldType.Int64:
case FieldType.UInt32:
case FieldType.UInt64:
case FieldType.SInt32:
case FieldType.SInt64:
case FieldType.Fixed32:
case FieldType.Fixed64:
case FieldType.SFixed32:
case FieldType.SFixed64:
case FieldType.Float:
case FieldType.Double:
    NumericRules.Evaluate(violations, field, value, path, constraints);
    break;
```

- [ ] **Step 5: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~NumericRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 6: コミット**

```bash
git add src/ConnectNet.Validation/Rules/NumericRules.cs src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs tests/ConnectNet.Tests/NumericRulesTests.cs
git commit -m "feat(validation): implement NumericRules for all 12 protobuf numeric types"
```

---

### Task 6: BoolRules, BytesRules, EnumRules の実装

**Files:**
- Create: `src/ConnectNet.Validation/Rules/BoolRules.cs`
- Create: `src/ConnectNet.Validation/Rules/BytesRules.cs`
- Create: `src/ConnectNet.Validation/Rules/EnumRules.cs`
- Modify: `src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs`
- Create: `tests/ConnectNet.Tests/EnumRulesTests.cs`
- Create: `tests/ConnectNet.Tests/BoolBytesRulesTests.cs`

- [ ] **Step 1: EnumRulesTests と BoolBytesRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/EnumRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class EnumRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void DefinedOnly_Valid()
    {
        var msg = new EnumTestMessage { Status = Status.Active };
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void DefinedOnly_Invalid_UndefinedValue()
    {
        var msg = new EnumTestMessage { Status = (Status)999 };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "enum.defined_only");
    }
}
```

BoolBytesRulesTests:

```csharp
// tests/ConnectNet.Tests/BoolBytesRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class BoolBytesRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Bool_Const_Valid()
    {
        var msg = new BoolTestMessage { MustBeTrue = true };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "bool.const");
    }

    [Fact]
    public void Bool_Const_Invalid()
    {
        var msg = new BoolTestMessage { MustBeTrue = false };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "bool.const");
    }
}
```

- [ ] **Step 2: BoolRules, BytesRules, EnumRules 実装**

```csharp
// src/ConnectNet.Validation/Rules/BoolRules.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class BoolRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, bool value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Bool)
            return;

        var rules = fc.Bool;
        if (rules.HasConst && value != rules.Const)
            violations.Add(new Violation(path, "bool.const", $"value must be {rules.Const}", value));
    }
}
```

```csharp
// src/ConnectNet.Validation/Rules/BytesRules.cs
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class BytesRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, ByteString value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Bytes)
            return;

        var rules = fc.Bytes;
        var len = value.Length;

        if (rules.HasConst && !value.Equals(rules.Const))
            violations.Add(new Violation(path, "bytes.const", "value must equal const", value));
        if (rules.HasLen && len != (int)rules.Len)
            violations.Add(new Violation(path, "bytes.len", $"value length must be exactly {rules.Len}", value));
        if (rules.HasMinLen && len < (int)rules.MinLen)
            violations.Add(new Violation(path, "bytes.min_len", $"value length must be at least {rules.MinLen}", value));
        if (rules.HasMaxLen && len > (int)rules.MaxLen)
            violations.Add(new Violation(path, "bytes.max_len", $"value length must be at most {rules.MaxLen}", value));
        if (rules.In.Count > 0 && !rules.In.Contains(value))
            violations.Add(new Violation(path, "bytes.in", "value must be in allowed set", value));
        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
            violations.Add(new Violation(path, "bytes.not_in", "value must not be in excluded set", value));
    }
}
```

```csharp
// src/ConnectNet.Validation/Rules/EnumRules.cs
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class EnumRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, int value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Enum)
            return;

        var rules = fc.Enum;

        if (rules.DefinedOnly)
        {
            var enumDescriptor = field.EnumType;
            if (enumDescriptor.FindValueByNumber(value) == null)
                violations.Add(new Violation(path, "enum.defined_only", $"value must be a defined enum value", value));
        }

        if (rules.In.Count > 0 && !rules.In.Contains(value))
            violations.Add(new Violation(path, "enum.in", $"value must be in [{string.Join(", ", rules.In)}]", value));

        if (rules.NotIn.Count > 0 && rules.NotIn.Contains(value))
            violations.Add(new Violation(path, "enum.not_in", $"value must not be in [{string.Join(", ", rules.NotIn)}]", value));
    }
}
```

- [ ] **Step 3: FieldRuleEvaluator に振り分け追加**

`FieldRuleEvaluator.Evaluate` に以下を追加:

```csharp
case FieldType.Bool:
    BoolRules.Evaluate(violations, field, (bool)(value ?? false), path, constraints);
    break;
case FieldType.Bytes:
    BytesRules.Evaluate(violations, field, value as ByteString ?? ByteString.Empty, path, constraints);
    break;
case FieldType.Enum:
    EnumRules.Evaluate(violations, field, (int)(value ?? 0), path, constraints);
    break;
```

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~EnumRulesTests|FullyQualifiedName~BoolBytesRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Validation/Rules/BoolRules.cs src/ConnectNet.Validation/Rules/BytesRules.cs src/ConnectNet.Validation/Rules/EnumRules.cs src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs tests/ConnectNet.Tests/EnumRulesTests.cs tests/ConnectNet.Tests/BoolBytesRulesTests.cs
git commit -m "feat(validation): implement BoolRules, BytesRules, EnumRules"
```

---

### Task 7: RepeatedRules と MapRules の実装

**Files:**
- Modify: `src/ConnectNet.Validation/Rules/RepeatedRules.cs`
- Modify: `src/ConnectNet.Validation/Rules/MapRules.cs`
- Create: `tests/ConnectNet.Tests/RepeatedRulesTests.cs`

- [ ] **Step 1: RepeatedRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/RepeatedRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class RepeatedRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void MinItems_Valid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("tag1");
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.min_items");
    }

    [Fact]
    public void MinItems_Invalid()
    {
        var msg = new RepeatedTestMessage(); // empty tags
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "repeated.min_items");
    }

    [Fact]
    public void MaxItems_Invalid()
    {
        var msg = new RepeatedTestMessage();
        for (int i = 0; i < 6; i++) msg.Tags.Add($"tag{i}");
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "repeated.max_items");
    }

    [Fact]
    public void Unique_Valid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("a");
        msg.Scores.AddRange(new[] { 1, 2, 3 });
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "repeated.unique");
    }

    [Fact]
    public void Unique_Invalid()
    {
        var msg = new RepeatedTestMessage();
        msg.Tags.Add("a");
        msg.Scores.AddRange(new[] { 1, 2, 2 });
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "repeated.unique");
    }

    [Fact]
    public void Map_MinPairs_Invalid()
    {
        var msg = new MapTestMessage(); // empty map, min_pairs = 1
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "map.min_pairs");
    }

    [Fact]
    public void Map_MinPairs_Valid()
    {
        var msg = new MapTestMessage();
        msg.Labels.Add("key", "value");
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "map.min_pairs");
    }
}
```

- [ ] **Step 2: RepeatedRules 実装**

```csharp
// src/ConnectNet.Validation/Rules/RepeatedRules.cs
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class RepeatedRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, IList? list, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Repeated)
            return;

        var rules = fc.Repeated;
        var count = list?.Count ?? 0;

        if (rules.HasMinItems && count < (int)rules.MinItems)
            violations.Add(new Violation(path, "repeated.min_items", $"list must contain at least {rules.MinItems} items", count));

        if (rules.HasMaxItems && count > (int)rules.MaxItems)
            violations.Add(new Violation(path, "repeated.max_items", $"list must contain at most {rules.MaxItems} items", count));

        if (rules.Unique && list != null)
        {
            var seen = new HashSet<object>();
            for (int i = 0; i < list.Count; i++)
            {
                if (!seen.Add(list[i]!))
                {
                    violations.Add(new Violation(path, "repeated.unique", "list must contain unique values", list[i]));
                    break;
                }
            }
        }
    }
}
```

- [ ] **Step 3: MapRules 実装**

```csharp
// src/ConnectNet.Validation/Rules/MapRules.cs
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class MapRules
{
    public static void Evaluate(List<Violation> violations, FieldDescriptor field, object? value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Map)
            return;

        var rules = fc.Map;
        var dict = value as IDictionary;
        var count = dict?.Count ?? 0;

        if (rules.HasMinPairs && count < (int)rules.MinPairs)
            violations.Add(new Violation(path, "map.min_pairs", $"map must contain at least {rules.MinPairs} entries", count));

        if (rules.HasMaxPairs && count > (int)rules.MaxPairs)
            violations.Add(new Violation(path, "map.max_pairs", $"map must contain at most {rules.MaxPairs} entries", count));
    }
}
```

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~RepeatedRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Validation/Rules/RepeatedRules.cs src/ConnectNet.Validation/Rules/MapRules.cs tests/ConnectNet.Tests/RepeatedRulesTests.cs
git commit -m "feat(validation): implement RepeatedRules and MapRules"
```

---

### Task 8: OneofRules の実装

**Files:**
- Modify: `src/ConnectNet.Validation/Rules/OneofRules.cs`
- Create: `tests/ConnectNet.Tests/OneofRulesTests.cs`

- [ ] **Step 1: OneofRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/OneofRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Xunit;

namespace ConnectNet.Tests;

public class OneofRulesTests
{
    private readonly ProtoValidator _validator = new();

    // --- Oneof required tests ---

    [Fact]
    public void Oneof_Required_Valid_EmailSet()
    {
        var msg = new OneofTestMessage { Email = "alice@example.com" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "oneof.required");
    }

    [Fact]
    public void Oneof_Required_Valid_PhoneSet()
    {
        var msg = new OneofTestMessage { Phone = "123-456" };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "oneof.required");
    }

    [Fact]
    public void Oneof_Required_Invalid_NoneSet()
    {
        var msg = new OneofTestMessage(); // neither email nor phone set
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "oneof.required");
    }

    // --- Nested message required + recursive validation ---

    [Fact]
    public void NestedMessage_Required_Valid()
    {
        var msg = new NestedTestMessage
        {
            Inner = new StringTestMessage { Name = "Alice" }
        };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.ConstraintId == "required" && v.FieldPath == "inner");
    }

    [Fact]
    public void NestedMessage_Required_Invalid()
    {
        var msg = new NestedTestMessage(); // inner is null
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.ConstraintId == "required" && v.FieldPath == "inner");
    }

    [Fact]
    public void NestedMessage_RecursiveValidation()
    {
        var msg = new NestedTestMessage
        {
            Inner = new StringTestMessage { Name = "Al" } // min_len = 3
        };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "inner.name" && v.ConstraintId == "string.min_len");
    }
}
```

- [ ] **Step 2: テスト実行、失敗確認**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~OneofRulesTests" -v n`
Expected: FAIL (一部テスト。ProtoValidatorの再帰ロジックが正しく動くか確認)

- [ ] **Step 3: OneofRules 実装**

oneof の required 制約は `buf/validate` の `OneofConstraints` で定義される。現在の proto 定義ではoneof required のアノテーションが無いため、基本的なoneof検出のみ実装:

```csharp
// src/ConnectNet.Validation/Rules/OneofRules.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace ConnectNet.Validation.Rules;

internal static class OneofRules
{
    public static void EvaluateOneofs(List<Violation> violations, IMessage message, string prefix)
    {
        var descriptor = message.Descriptor;
        foreach (var oneof in descriptor.Oneofs)
        {
            // synthetic oneofs (proto3 optional) をスキップ
            if (oneof.IsSynthetic)
                continue;

            // oneof制約を取得（OneofConstraintsが設定されている場合）
            var options = oneof.GetOptions();
            if (options == null)
                continue;

            // buf/validate の OneofConstraints extension を読み取る
            // required がtrueなら、oneofのいずれかのフィールドが設定されていることを確認
            try
            {
                var oneofConstraints = options.GetExtension(Buf.Validate.ValidateExtensions.Oneof);
                if (oneofConstraints != null && oneofConstraints.Required)
                {
                    var caseField = oneof.Accessor.GetCaseFieldDescriptor(message);
                    if (caseField == null)
                    {
                        var fieldPath = string.IsNullOrEmpty(prefix) ? oneof.Name : $"{prefix}.{oneof.Name}";
                        violations.Add(new Violation(fieldPath, "oneof.required", "exactly one field must be set", null));
                    }
                }
            }
            catch
            {
                // extension が見つからない場合はスキップ
            }
        }
    }
}
```

- [ ] **Step 4: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~OneofRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 5: コミット**

```bash
git add src/ConnectNet.Validation/Rules/OneofRules.cs tests/ConnectNet.Tests/OneofRulesTests.cs
git commit -m "feat(validation): implement OneofRules and nested message validation"
```

---

### Task 9: WellKnownTypeRules の実装

**Files:**
- Create: `src/ConnectNet.Validation/Rules/WellKnownTypeRules.cs`
- Modify: `src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs`
- Create: `tests/ConnectNet.Tests/WellKnownTypeRulesTests.cs`

- [ ] **Step 1: WellKnownTypeRulesTests 作成**

```csharp
// tests/ConnectNet.Tests/WellKnownTypeRulesTests.cs
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

public class WellKnownTypeRulesTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Timestamp_Required_Valid()
    {
        var msg = new TimestampTestMessage
        {
            CreatedAt = Timestamp.FromDateTime(System.DateTime.UtcNow)
        };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "createdAt" && v.ConstraintId == "required");
    }

    [Fact]
    public void Timestamp_Required_Invalid()
    {
        var msg = new TimestampTestMessage(); // created_at is null
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "createdAt" && v.ConstraintId == "required");
    }

    [Fact]
    public void Duration_Required_Valid()
    {
        var msg = new DurationTestMessage
        {
            Timeout = Duration.FromTimeSpan(System.TimeSpan.FromSeconds(30))
        };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "timeout" && v.ConstraintId == "required");
    }

    [Fact]
    public void Duration_Required_Invalid()
    {
        var msg = new DurationTestMessage(); // timeout is null
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v => v.FieldPath == "timeout" && v.ConstraintId == "required");
    }
}
```

- [ ] **Step 2: WellKnownTypeRules 実装**

```csharp
// src/ConnectNet.Validation/Rules/WellKnownTypeRules.cs
using System.Collections.Generic;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;

namespace ConnectNet.Validation.Rules;

internal static class WellKnownTypeRules
{
    public static bool IsWellKnownType(FieldDescriptor field)
    {
        if (field.FieldType != FieldType.Message)
            return false;

        var fullName = field.MessageType.FullName;
        return fullName == Timestamp.Descriptor.FullName
            || fullName == Duration.Descriptor.FullName;
    }

    public static void Evaluate(List<Violation> violations, FieldDescriptor field, object? value, string path, IMessage constraints)
    {
        if (constraints is not Buf.Validate.FieldConstraints fc)
            return;

        var fullName = field.MessageType.FullName;

        if (fullName == Timestamp.Descriptor.FullName)
            EvaluateTimestamp(violations, value as Timestamp, path, fc);
        else if (fullName == Duration.Descriptor.FullName)
            EvaluateDuration(violations, value as Duration, path, fc);
    }

    private static void EvaluateTimestamp(List<Violation> violations, Timestamp? value, string path, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Timestamp)
            return;

        var rules = fc.Timestamp;

        if (value == null)
            return;

        if (rules.HasGt && value.CompareTo(rules.Gt) <= 0)
            violations.Add(new Violation(path, "timestamp.gt", "value must be greater than specified timestamp", value));
        if (rules.HasGte && value.CompareTo(rules.Gte) < 0)
            violations.Add(new Violation(path, "timestamp.gte", "value must be greater than or equal to specified timestamp", value));
        if (rules.HasLt && value.CompareTo(rules.Lt) >= 0)
            violations.Add(new Violation(path, "timestamp.lt", "value must be less than specified timestamp", value));
        if (rules.HasLte && value.CompareTo(rules.Lte) > 0)
            violations.Add(new Violation(path, "timestamp.lte", "value must be less than or equal to specified timestamp", value));
    }

    private static void EvaluateDuration(List<Violation> violations, Duration? value, string path, Buf.Validate.FieldConstraints fc)
    {
        if (fc.Type?.TypeCase != Buf.Validate.FieldConstraints.Types.Type.OneofFieldCase.Duration)
            return;

        var rules = fc.Duration;

        if (value == null)
            return;

        if (rules.HasGt && value.CompareTo(rules.Gt) <= 0)
            violations.Add(new Violation(path, "duration.gt", "value must be greater than specified duration", value));
        if (rules.HasGte && value.CompareTo(rules.Gte) < 0)
            violations.Add(new Violation(path, "duration.gte", "value must be greater than or equal to specified duration", value));
        if (rules.HasLt && value.CompareTo(rules.Lt) >= 0)
            violations.Add(new Violation(path, "duration.lt", "value must be less than specified duration", value));
        if (rules.HasLte && value.CompareTo(rules.Lte) > 0)
            violations.Add(new Violation(path, "duration.lte", "value must be less than or equal to specified duration", value));
    }
}
```

注: `Timestamp.CompareTo`、`rules.HasGt` 等のAPI名は生成コードに合わせて調整。Timestamp/Duration の比較制約テストはprotoにルールを追加してから実装する。ここではrequiredのみテストする。

- [ ] **Step 3: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~WellKnownTypeRulesTests" -v n`
Expected: All tests PASS

- [ ] **Step 4: コミット**

```bash
git add src/ConnectNet.Validation/Rules/WellKnownTypeRules.cs src/ConnectNet.Validation/Rules/FieldRuleEvaluator.cs tests/ConnectNet.Tests/WellKnownTypeRulesTests.cs
git commit -m "feat(validation): implement WellKnownTypeRules for Timestamp and Duration"
```

---

### Task 10: ValidateInterceptor の実装

**Files:**
- Create: `src/ConnectNet.Validation/Interceptors/ValidateInterceptor.cs`
- Create: `tests/ConnectNet.Tests/ValidateInterceptorTests.cs`

- [ ] **Step 1: ValidateInterceptorTests 作成**

```csharp
// tests/ConnectNet.Tests/ValidateInterceptorTests.cs
using System.Threading.Tasks;
using ConnectNet.Client;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using ConnectNet.Validation.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ConnectNet.Tests;

public class ValidateInterceptorTests
{
    // GreeterServiceDefinition はInterceptorTestsと同じパターンを使用
    // 実際のサービスは ConnectNet.Tests.Proto の GreeterServiceBase を継承

    [Fact]
    public async Task InvalidRequest_ThrowsConnectException()
    {
        var validator = new ProtoValidator();
        var interceptor = new ValidateInterceptor(validator);

        var context = new UnaryServerContext(
            "/test/Method",
            new StringTestMessage { Name = "Al" }, // min_len = 3 violation
            new ConnectContext());

        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await interceptor.InterceptUnaryAsync(context, ctx =>
                Task.FromResult<Google.Protobuf.IMessage>(new StringTestMessage()));
        });

        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
        Assert.Contains("validation failed", ex.Message);
        Assert.NotEmpty(ex.Details);
        Assert.Equal("buf.validate.Violations", ex.Details[0].Type);
    }

    [Fact]
    public async Task ValidRequest_PassesThrough()
    {
        var validator = new ProtoValidator();
        var interceptor = new ValidateInterceptor(validator);

        var context = new UnaryServerContext(
            "/test/Method",
            new StringTestMessage { Name = "Alice", Email = "alice@example.com", Code = "abc" },
            new ConnectContext());

        var called = false;
        var response = await interceptor.InterceptUnaryAsync(context, ctx =>
        {
            called = true;
            return Task.FromResult<Google.Protobuf.IMessage>(new StringTestMessage { Name = "response" });
        });

        Assert.True(called, "next delegate should have been called");
        Assert.NotNull(response);
    }

    [Fact]
    public async Task MessageWithoutConstraints_PassesThrough()
    {
        var validator = new ProtoValidator();
        var interceptor = new ValidateInterceptor(validator);

        var context = new UnaryServerContext(
            "/test/Method",
            new HelloRequest { Name = "anything" },
            new ConnectContext());

        var response = await interceptor.InterceptUnaryAsync(context, ctx =>
            Task.FromResult<Google.Protobuf.IMessage>(new HelloResponse { Message = "ok" }));

        Assert.NotNull(response);
    }
}
```

- [ ] **Step 2: ValidateInterceptor 実装**

```csharp
// src/ConnectNet.Validation/Interceptors/ValidateInterceptor.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Validation.Interceptors;

public class ValidateInterceptor : IServerInterceptor
{
    private readonly ProtoValidator _validator;

    public ValidateInterceptor() : this(new ProtoValidator())
    {
    }

    public ValidateInterceptor(ProtoValidator validator)
    {
        _validator = validator;
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

    private static string FormatMessage(IReadOnlyList<Violation> violations)
    {
        var first = violations[0].Message;
        if (violations.Count == 1)
            return $"validation failed: {first}";
        return $"validation failed: {first} (and {violations.Count - 1} more)";
    }

    private static IEnumerable<ConnectErrorDetail> ToErrorDetails(IReadOnlyList<Violation> violations)
    {
        // buf.validate.Violations protobuf メッセージを構築してシリアライズ
        var protoViolations = new Buf.Validate.Violations();
        foreach (var v in violations)
        {
            protoViolations.Violations_.Add(new Buf.Validate.Violation
            {
                FieldPath = v.FieldPath,
                ConstraintId = v.ConstraintId,
                Message = v.Message,
            });
        }

        return new[]
        {
            new ConnectErrorDetail(
                "buf.validate.Violations",
                protoViolations.ToByteArray())
        };
    }
}
```

注: `Buf.Validate.Violations` および `Buf.Validate.Violation` のクラス名は `buf/validate/validate.proto` の生成コードに合わせて調整。生成コード内に `Violations` メッセージが無い場合は、独自のシリアライズ方式を使用する。

- [ ] **Step 3: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ValidateInterceptorTests" -v n`
Expected: All tests PASS

- [ ] **Step 4: コミット**

```bash
git add src/ConnectNet.Validation/Interceptors/ tests/ConnectNet.Tests/ValidateInterceptorTests.cs
git commit -m "feat(validation): implement ValidateInterceptor with ConnectException conversion"
```

---

### Task 11: ignore/disabled フィールドのテストと全体テスト

**Files:**
- Modify: `tests/ConnectNet.Tests/ProtoValidatorTests.cs`

- [ ] **Step 1: ignore/disabled テスト追加**

`ProtoValidatorTests.cs` に以下を追加:

```csharp
[Fact]
public void Validate_IgnoreAlways_SkipsValidation()
{
    var msg = new DisabledFieldMessage
    {
        Name = "Alice",
        SkipMe = "ab" // violates min_len=5 but should be skipped due to IGNORE_ALWAYS
    };
    var result = _validator.Validate(msg);
    Assert.DoesNotContain(result.Violations, v => v.FieldPath == "skipMe");
}

[Fact]
public void Validate_IgnoreAlways_WouldFailWithoutIgnore()
{
    // Verify that the constraint on skip_me is real (min_len=5) by checking
    // that a non-ignored field with the same value would fail
    var msg = new DisabledFieldMessage
    {
        Name = "Al", // min_len=3, this SHOULD fail
        SkipMe = "ab" // min_len=5, this should NOT fail (ignored)
    };
    var result = _validator.Validate(msg);
    Assert.Contains(result.Violations, v => v.FieldPath == "name");
    Assert.DoesNotContain(result.Violations, v => v.FieldPath == "skipMe");
}

[Fact]
public void Validate_NestedMessage_CollectsAllViolations()
{
    var msg = new NestedTestMessage
    {
        Inner = new StringTestMessage { Name = "Al", Email = "bad" }
    };
    var result = _validator.Validate(msg);
    Assert.Contains(result.Violations, v => v.FieldPath == "inner.name");
    Assert.Contains(result.Violations, v => v.FieldPath == "inner.email");
}
```

- [ ] **Step 2: テスト実行**

Run: `dotnet test tests/ConnectNet.Tests --filter "FullyQualifiedName~ProtoValidatorTests" -v n`
Expected: All tests PASS

- [ ] **Step 3: 全テスト実行**

Run: `dotnet test tests/ConnectNet.Tests -v n`
Expected: All tests PASS（既存テストを壊していないこと）

- [ ] **Step 4: コミット**

```bash
git add tests/ConnectNet.Tests/ProtoValidatorTests.cs
git commit -m "test(validation): add ignore/disabled and comprehensive nested validation tests"
```

---

### Task 12: サンプル更新とドキュメント

**Files:**
- Modify: `README.md` (バリデーションの使い方セクション追加)

- [ ] **Step 1: README にバリデーションセクション追加**

`README.md` に以下のセクションを追加:

```markdown
## Validation

connect-net includes built-in support for [protovalidate](https://github.com/bufbuild/protovalidate) — validate protobuf messages using constraints defined in `.proto` files.

### Define constraints in your proto file

\`\`\`protobuf
import "buf/validate/validate.proto";

message CreateUserRequest {
  string name = 1 [(buf.validate.field).string.min_len = 3];
  string email = 2 [(buf.validate.field).string.email = true];
  int32 age = 3 [(buf.validate.field).int32 = {gte: 0, lte: 150}];
}
\`\`\`

### Add the validation interceptor

\`\`\`csharp
builder.Services.AddConnectServices(options =>
{
    options.Interceptors.Add(new ValidateInterceptor());
});
\`\`\`

Invalid requests automatically return `ConnectCode.InvalidArgument` with violation details.

### Manual validation

\`\`\`csharp
var validator = new ProtoValidator();
var result = validator.Validate(message);
if (!result.IsValid)
{
    foreach (var violation in result.Violations)
        Console.WriteLine($"{violation.FieldPath}: {violation.Message}");
}
\`\`\`
```

- [ ] **Step 2: コミット**

```bash
git add README.md
git commit -m "docs: add protovalidate usage documentation"
```

---

## Important Notes for Implementer

1. **生成コードのAPI確認が最重要:** Task 2 の Step 1.5 で `validate.proto` をビルドした後、`obj/` 以下に生成されるC#コードを必ず読み、以下のAPI名を確認すること:
   - `Buf.Validate.FieldConstraints` の正確なクラス名と型判別方法 (`TypeCase` enum)
   - `ValidateExtensions.Field` extension の名前
   - `StringRules` の `well_known` oneof のアクセスパターン（`WellKnownCase` enum vs bool properties）
   - 各ルール型の `HasXxx` プロパティの有無
   - `NotIn` / `In` 等のコレクションプロパティ名

2. **プラン内コードは推定ベース:** プラン内のコードは `buf/validate` の proto 定義から推定したAPI名を使用しています。生成コードのAPIが異なる場合は、そちらに合わせて修正してください。ロジック自体は正しいはずです。

3. **テスト駆動で進めること:** 各タスクでテストを先に書き、ビルドが通った上でテストが失敗することを確認してから実装を書いてください。ビルドエラーの場合は生成コードのAPIを確認して修正してください。
