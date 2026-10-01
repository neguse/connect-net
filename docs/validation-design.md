# Protobuf validation and CEL

`ConnectNet.Validation` validates server requests against `buf.validate` rules,
including custom CEL expressions. It targets .NET 10, independently of the
.NET Standard 2.1 client and shared RPC types. Validation on Unity is not a
requirement. The validator and its CEL evaluator are maintained in this repository;
no external CEL engine is a runtime dependency.

## Public contract

`ProtoValidator.Validate(IMessage)` returns a `ValidationResult`. Invalid input
produces violations; invalid schemas or expressions produce a compilation
exception, and evaluation failures produce a separate evaluation exception.
An invalid schema must never be treated as valid input or an `InvalidArgument`
response caused by the caller. Compilation validates rule types, field references,
overloads, and CEL result types before evaluation, including rules on absent fields.

Existing constructors, `IgnoreUnsupportedRules`, `ValidationResult`, `Violation`,
and `ValidateInterceptor` remain available. The explicit unsupported-rule opt-out
only skips an unsupported feature; it must not suppress malformed rules, invalid
types, compilation failures, or exhausted evaluation budgets.

The interceptor validates unary requests and returns `InvalidArgument` with a
`buf.validate.Violations` detail. Streaming interception is outside this change.
Standalone validation remains available on the server runtime. `Buf.Validate`
generated types continue to belong to this assembly so callers do not acquire a
second, incompatible copy of the extension types.

## Implementation

The CEL evaluator is an internal module of `ConnectNet.Validation`, with no new
public general-purpose expression API. Its syntax, checking, and evaluation layers
do not depend on Connect or Protobuf. A Protobuf adapter supplies message types,
field access and presence, enums, extensions, and well-known types.

The pipeline is source -> lexer -> parser and macro expansion -> typed expression
-> immutable evaluation plan. Source positions survive macro expansion for
diagnostics. Compiled plans may be shared across requests; activation values,
time, cost counters, cancellation, and violations belong to one validation call.

CEL values distinguish signed 64-bit integers, unsigned 64-bit integers, doubles,
Unicode strings, bytes, booleans, null, lists, maps, objects, types, and errors.
Arithmetic observes CEL overflow and conversion semantics. Strings count Unicode
code points. Timestamp and duration arithmetic retains nanosecond precision.
Equality, numeric comparison, map key lookup, short-circuiting, and error propagation
follow CEL rather than C# coercion rules. `has()` uses descriptor presence semantics.
Comprehension macros have lexical bindings that cannot leak into an outer scope.

Regular expressions use RE2 syntax and semantics, including ASCII shorthand
classes. The existing RE2 translation is the starting point; .NET regex behavior
alone is not a compatibility oracle. Unsupported syntax fails at compilation.

Descriptors are compiled into message plans. The cache is keyed by descriptor
identity and does not permanently root dynamically loaded descriptors. Recursive
message schemas use plan references rather than recursive compilation. The entire
reachable schema is checked before the first message is evaluated.

## Rule evaluation

Standard rules execute the CEL definitions embedded in `validate.proto`.
Custom `cel`, `cel_expression`, and predefined extension rules use the same engine.
The environment binds `this`, `rules`, `rule`, and one `now` captured at the start
of validation. A rule's message and result are combined according to the pinned
Protovalidate contract; a false shorthand expression receives its prescribed
default message.

Structural rules remain descriptor operations: required/presence/ignore,
message-level and native oneof rules, collection traversal, enum definitions,
and Any type URL membership. Field and rule paths are structured while evaluating,
including map key versus value violations. Public textual paths are rendered at
the API boundary. Error details retain the full structured paths.

Protobuf adaptation covers proto2, proto3 and editions, extension fields, wrappers,
Any, Struct/Value/ListValue, Timestamp, Duration and FieldMask. Type registries include
transitive file dependencies. Field numbers, not CLR or JSON spelling, identify
fields in plans.

## Resource limits

The existing default limits remain: 32 nested messages, 100 materialized violations,
and 512 characters per stored violation message. Reaching the violation limit stops
evaluation and marks the result truncated; the interceptor emits its truncation
marker. Truncation happens before constructing an unbounded diagnostic string.

One validation-wide budget covers CEL operations, comprehension iterations,
collection traversal, and produced collection/string/byte values. Nested rules
and repeated elements cannot reset it. Evaluation has a finite default budget and
supports cancellation. Budget exhaustion is an evaluation failure and never a
successful or partially successful validation. Parsing and checking also bound
source size, AST size and nesting before the CLR stack can overflow. Recursive
object graphs must terminate through a checked limit.

The precise budget units and defaults are part of the evaluator API tests. They
must allow every bounded official conformance input with default settings while
rejecting deliberately excessive inputs. No unbounded execution mode is used by
the server interceptor.

## Verification

The compatibility baselines are immutable upstream revisions:

- CEL: `cel-expr/cel-spec@59505c14f3187e6eb9684fbd3d07146f614c6148`.
- Protovalidate: `bufbuild/protovalidate@3807e3d1c38b48295eae269e2f3b97cee668edd3`.

Conformance tooling obtains its schemas and test corpus from these upstream
revisions, with their license notices. It must not use Celly's implementation or
its expected results. The CEL harness exercises the official suite with the
environment requested by each test; alternate enum modes are explicit. The
Protovalidate executor speaks the official binary stdin/stdout harness protocol.

The CEL corpus is the `tests/simple/testdata` suite of the pinned cel-spec
revision. Its scope follows the environment Protovalidate defines for every
implementation: the CEL standard library, the strings extension (including
`format`, which renders every standard rule message), the Protovalidate
functions, UTC as the default time zone, and cross-type numeric comparisons.
No optional types, bindings, math, encoders, protos, lists or sets extension is
enabled, so the suites that exercise only those are outside the contract.

- Required, every case: `basic`, `comparisons`, `conversions`, `dynamic`,
  `enums`, `fields`, `fp_math`, `integer_math`, `lists`, `logic`, `macros`,
  `namespace`, `parse`, `plumbing`, `proto2`, `proto3`, `string`,
  `string_ext`, `timestamps`, `wrappers` (1,958 cases at the pinned revision).
- Excluded: `bindings_ext`, `block_ext`, `encoders_ext`, `macros2` (the
  two-variable comprehension extension), `math_ext`, `network_ext`,
  `optionals`, `proto2_ext`, `unknowns`.
- Reported but not gating: `type_deduction`. Protovalidate only needs the
  checker to accept or reject an expression and to know its result type.

A required file is run in full. Skipping individual cases inside a required
file is not permitted; a case that cannot pass is a defect to fix or a
documented deviation agreed before merge, never an entry in an ignore list.

Acceptance requires all of the following, with no ignored failures:

1. Existing RPC unit and integration tests pass; intentional changes to validation
   assertions correspond to the upstream contract, not weakened assertions.
2. CEL conformance passes for every required file of the pinned corpus, with
   per-file test counts checked so an empty or partially discovered corpus
   cannot pass.
3. All 2,872 pinned Protovalidate cases pass with `--strict_error --strict_message`.
4. Regression tests cover ASCII regex classes, nanosecond time, numeric boundaries,
   absent fields, nested map/repeated rules, extension identity and recursive schemas.
5. Limits and cancellation tests demonstrate bounded violations, traversal,
   comprehensions, output construction, compilation and diagnostic allocation.
6. Concurrent validations share plans without sharing request state; separate
   descriptors with the same full name cannot reuse the wrong schema.
7. A packed validation package is consumed by a separate .NET 10 smoke application,
   including a cross-field CEL rule and decoding the interceptor's error detail.

The same pinned conformance commands run in CI, including strict comparisons.
Unit tests cover each layer as it is introduced rather than relying on the final
integration suite to discover elementary failures.

## Delivery and replacement

The review units are a dependent series:

1. This design and its acceptance contract.
2. Server target, lexical analysis, syntax tree, parser and macro expansion.
3. Type checking, name resolution and diagnostics.
4. Value model, evaluator, standard operations and functions.
5. Protobuf type and value adaptation.
6. Protovalidate functions and RE2 compatibility.
7. Descriptor plans, standard/custom rule evaluation and interceptor integration.
8. Remaining conformance differences, resource-boundary verification and CI gates.

Each implementation unit includes its tests. Existing handwritten rule evaluators
are removed as the CEL-backed validator replaces them; no second production
validation backend or investigation prototype remains in the final package.
Public API compatibility does not require retaining nonconforming rule IDs or
messages. Migration documentation names those observable changes and the server
runtime requirement.

The release boundary is a green, reviewable PR series. Merging and package
publication belong to the repository owner. After publication, the package smoke
application and strict conformance runner are executed against the published
version, not a project reference. A consumer can roll back by pinning the preceding
package version; source rollback reverts the implementation series in reverse
dependency order. No persistent data migration is involved.
