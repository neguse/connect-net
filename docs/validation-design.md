# protovalidate Design

`src/ConnectNet.Validation` provides validation compatible with
[protovalidate](https://github.com/bufbuild/protovalidate) (`buf/validate/validate.proto`).
See the [README](../README.md#validation) for usage.

## Scope and approach

- Reflection-based, no code generation. The engine (`ProtoValidator`) has no Connect dependency and works standalone
- CEL (Common Expression Language) rules are not implemented: a hand-rolled CEL evaluator would be larger
  than the library itself
- Automatic validation via the interceptor (`ValidateInterceptor`) covers unary RPCs only
- Unsupported rules (CEL, `well_known_regex`, some string/bytes well-known formats) throw
  `NotSupportedException` when encountered. This fail-loud policy prevents "validated" messages from
  silently passing unchecked; skipping requires an explicit opt-out via
  `new ProtoValidator(ignoreUnsupportedRules: true)`

## Structure

- `ProtoValidator` — entry point. `Validate(IMessage)` recurses into nested messages and returns every
  violation as a `ValidationResult` (a list of `Violation`s)
- `Rules/` — evaluation logic per constraint category (string / numeric / bool / bytes / enum / repeated /
  map / oneof / well-known types), dispatched by field type through `FieldRuleEvaluator`
- `Internal/ConstraintCache` — caches parsed rules per `MessageDescriptor` in a `ConcurrentDictionary`
- `Internal/FieldPaths` — builds violation paths (nesting and indices, e.g. `items[0].name`)
- `Proto/buf/validate/` — vendored `validate.proto` with protoc-generated code. Rules are read from
  `FieldDescriptor.GetOptions()` as custom option extensions

## Interceptor integration

`ValidateInterceptor` validates each request and throws a `ConnectException` with
`ConnectCode.InvalidArgument` on violations. The violation list is attached as a `buf.validate.Violations`
message in the error details — the same shape as connect-go's `connectrpc.com/validate`, so clients can
recover typed violation data.
