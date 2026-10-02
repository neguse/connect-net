# Migrating to the CEL-backed validator

`ConnectNet.Validation` evaluates `buf.validate` rules with a built-in CEL engine and
follows the [protovalidate](https://github.com/bufbuild/protovalidate) contract exactly:
the pinned conformance suite passes in full. The public API is unchanged
(`ProtoValidator` and its constructors, `IgnoreUnsupportedRules`, `ValidationResult`,
`Violation`, `ValidateInterceptor`), but what it reports moved to protovalidate's
spellings. This page lists every observable change.

## Runtime requirement

`ConnectNet.Validation` targets .NET 10. The client and shared RPC packages still target
.NET Standard 2.1; validation is a server-side concern.

## Every rule is evaluated

Nothing throws `NotSupportedException` any more. Custom `cel` and `cel_expression` rules on
messages and fields, predefined rule extensions, `any`, `field_mask`, every string and bytes
well-known format and `well_known_regex` are all evaluated. `ProtoValidator(ignoreUnsupportedRules:
true)` still compiles and the property still reads back, but there is no rule left for it to
skip.

## Schema problems are exceptions, not violations

| Situation | Before | Now |
|-----------|--------|-----|
| A rule of the wrong type for its field (`int32` rules on a string) | `NotSupportedException` or silently ignored | `ValidationCompilationException` |
| A CEL expression that does not parse or type-check | `NotSupportedException` | `ValidationCompilationException` |
| A CEL expression returning something other than bool or string | n/a | `ValidationCompilationException` |
| A rule that fails at runtime (a type error through `dyn`, invalid UTF-8 against a `bytes.pattern`) | violation | `ValidationEvaluationException` |
| Evaluation budget exhausted or `CancellationToken` cancelled | n/a | `ValidationEvaluationException` |

A compilation failure is raised the first time a message of that type is validated, even when
the field carrying the broken rule is unset: the schema is checked, not the message. The
interceptor does not catch these exceptions; a broken schema must never become an
`InvalidArgument` answer to a caller.

## Field paths use proto field names

`Violation.FieldPath` is rendered the way protovalidate renders it: proto field names, not
JSON names, with `[index]` for repeated items and `["key"]` / `[42]` / `[true]` for map
entries (string keys quoted like Go's `strconv.Quote`).

| Before | Now |
|--------|-----|
| `mustBeTrue` | `must_be_true` |
| `statusMap[1]` | `status_map[1]` |
| `items[0].innerName` | `items[0].inner_name` |

The structured paths are available directly: `Violation.Field` and `Violation.Rule` are the
`buf.validate.FieldPath`s, `Violation.ForKey` marks map-key violations, and
`Violation.ToProto()` is the `buf.validate.Violation` the interceptor puts on the wire.
`Violation.RuleValue`, `FieldDescriptor` and `RuleDescriptor` identify the failed rule.

## Rule ids and messages

Rule ids are protovalidate's, and messages are the ones defined by `validate.proto` (or a
custom rule's own `message`). Code that matched the old texts needs updating; the ids are
the stable contract.

- A required oneof reports `required` with `exactly one field is required in oneof`
  (was `oneof.required`).
- An empty string against `string.email` reports `string.email_empty`; the same split
  exists for `string.hostname` (`string.hostname_empty`), `string.ip` and the other formats
  that define an `_empty` variant.
- Standard rule messages follow `validate.proto` verbatim, for example
  `value length must be at least 3 characters` and `must be in list [a, b]`.
- A custom bool rule without a `message` reports `"<expression>" returned false`.
- A `(buf.validate.message).oneof` rule reports `message.oneof`.

## Semantics aligned with protovalidate

- `string.email` follows the HTML standard definition protovalidate uses: dots anywhere in
  the local part and no length limits, while domain labels are at most 63 characters.
- IPv6 addresses accept zone identifiers (`fe80::1%eth0`) in `string.ip`, `string.ipv6`,
  `isIp()` and `string.host_and_port`.
- `repeated.unique` on floating-point fields treats NaN as never equal to anything, and
  `-0.0` as equal to `0.0`.
- `required` on an absent field reports only the `required` violation; the field's other
  rules are not applied to its zero value.
- `%s` formatting of a list of strings in a custom rule message follows the pinned cel-spec
  corpus (`[a, b]`, members unquoted).

## Resource limits

The existing limits remain: 32 nested messages, 100 materialized violations
(`ValidationResult.Truncated`), 512 characters per stored violation message, and a 100 ms
cap per regular expression match. New is one evaluation budget per validation call
(`ProtoValidator.DefaultEvaluationBudget`), covering CEL operations, comprehension
iterations, produced collection, string and byte sizes, and the fields, items and map entries
traversed. Exhausting it throws `ValidationEvaluationException`; a message is never
partially validated. `ProtoValidator.Validate(message, cancellationToken)` cancels through
the same path.
