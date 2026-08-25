# connect-net Design

connect-net is a native C# implementation of the [Connect RPC protocol](https://connectrpc.com/docs/protocol).
It provides a Unity-compatible client (.NET Standard 2.1), an ASP.NET Core server (.NET 10+), and a protoc
plugin for code generation.

It exists to solve the difficulty of using gRPC from Unity (no native HTTP/2 support): the Connect protocol
works over plain HTTP/1.1, enabling type-safe RPC from Unity.

See the [README](../README.md) for usage and features. This document covers internal design and design decisions.

## Design decisions

### Native implementation instead of wrapping gRPC-generated code

We considered transparently wrapping gRPC-generated code and translating to the Connect protocol, the way
grpc-dotnet supports gRPC-Web, and rejected it. gRPC-Web and gRPC share nearly the same wire format, but
Connect and gRPC differ fundamentally:

- Unary: Connect uses no envelope; gRPC requires the 5-byte envelope
- Stream end: Connect uses flag 0x02 + JSON; gRPC uses flag 0x80 + MIME headers
- Errors: Connect uses a JSON body + HTTP status code; gRPC uses trailers only, always HTTP 200

These differences turn a "transparent layer" into a protocol bridge at least as complex as a native
implementation. Every other Connect RPC implementation (Swift, Kotlin, ...) made the same call: native
implementation plus a dedicated protoc plugin.

### protoc plugin written in Go

`tools/protoc-gen-connect-csharp` is written in Go — the same choice as connect-swift and connect-kotlin,
because Go's protobuf library is best suited to driving the plugin API.

## Project layout

| Project | Contents |
|---|---|
| `src/ConnectNet` | Protocol core: `ICodec` (`ProtobufCodec` / `JsonCodec`), `ICompressor` (gzip / deflate), envelope framing (`Envelope` / `EnvelopeFrame`), error types (`ConnectException` / `ConnectCode`), interceptor abstractions, buffer pooling (`Pooling/`) |
| `src/ConnectNet.Client` | `ConnectChannel` (HTTP handling for all RPC kinds), `CallOptions`, `ClientStreamCall` / `BidiStreamCall`, `UnityWebRequestHandler` for Unity WebGL |
| `src/ConnectNet.Server` | ASP.NET Core integration: service registration and routing, per-RPC-kind handlers, health checks, server reflection |
| `src/ConnectNet.Validation` | protovalidate implementation (see [validation-design.md](validation-design.md)) |
| `tools/protoc-gen-connect-csharp` | Service stub code generation plugin (Go) |

Dependency direction: `ConnectNet.Client` / `ConnectNet.Server` → `ConnectNet` → Google.Protobuf only.
The client side stays on .NET Standard 2.1 for Unity compatibility; only the server side depends on ASP.NET Core.

## Code generation

Two protoc plugins run side by side:

- `protoc-gen-csharp` (official) → message types
- `protoc-gen-connect-csharp` (this project) → service stubs (`.connect.cs`)

The generated code for each service has four parts:

- `{Service}Methods` — service name and method path constants
- `I{Service}Client` / `{Service}Client` — client; a thin layer delegating to the matching `ConnectChannel` method
- `{Service}Base` — server base class; unimplemented methods throw `ConnectCode.Unimplemented`
- `{Service}Definition` — `IConnectServiceDefinition` implementation holding one `ConnectMethodDescriptor`
  per method (parser, RPC kind, handler delegate, GET eligibility) plus the `FileDescriptor`, consumed by
  server routing and reflection

## Wire format

Follows the [Connect protocol specification](https://connectrpc.com/docs/protocol). Key points:

- Unary bodies are raw bytes with no envelope; errors are an HTTP status code plus a JSON body
- Streaming frames messages with a 5-byte envelope (1 flag byte + big-endian uint32 length).
  Flags: 0x01 = compressed, 0x02 = EndStream (JSON carrying trailers / error)
- Content-Type is `application/{codec}` for unary and `application/connect+{codec}` for streaming

## Client architecture

`ConnectChannel.ForAddress(uri, options)` is the single entry point. `ConnectChannelOptions` accepts an
`HttpClient` or `HttpMessageHandler`:

- When omitted, the channel builds an HttpClient with conservative defaults (redirects, cookies, and
  automatic decompression disabled)
- Inject YetAnotherHttpHandler (Unity HTTP/2), `UnityWebRequestHandler` (WebGL), DelegatingHandler chains
  (Polly / OpenTelemetry), or test mocks as the handler

Streaming opens the response stream immediately via `HttpCompletionOption.ResponseHeadersRead` and exposes
it as `IAsyncEnumerable<T>` (server streaming) or `ClientStreamCall` / `BidiStreamCall` (with a send side).

Deadlines are not only propagated to the server via `Connect-Timeout-Ms` but also enforced locally on the
client; expiry is classified as `ConnectCode.DeadlineExceeded` even when racing a caller-side cancel.

## Server architecture

Endpoint routing via `AddConnectServices()` + `MapConnectService<TImpl>({Service}Definition.Instance)`.
The definition's `ConnectMethodDescriptor` list is enumerated and each method is routed to the handler for
its RPC kind (`ConnectUnaryHandler` / `ConnectServerStreamHandler` / `ConnectClientStreamHandler` /
`ConnectBidiStreamHandler`).

Protocol concerns shared across handlers — strict content-type parsing, Accept-Encoding negotiation with
q-values, bounded timeout handling — live in `ConnectServerProtocol`. Each handler validates the request,
deserializes, builds a `ConnectContext`, invokes the service, and writes the response, converting
`ConnectException` into an HTTP status + JSON (unary) or an EndStream frame (streaming).

Health checks (`MapConnectHealthCheck`) are grpc.health.v1-compatible; server reflection
(`MapConnectReflection`) serves standard gRPC Server Reflection over the Connect protocol, answering from
the `FileDescriptor` held by each definition.

## Error handling

`ConnectException` carries one of the 16 Connect error codes (`ConnectCode`) and `ConnectErrorDetail`
entries (protobuf Any).

- Client: non-200 / EndStream errors are JSON-parsed and thrown; transport exceptions are wrapped with the
  cause preserved
- Server: `ConnectException` maps to the code's HTTP status + JSON; unexpected exceptions become `Internal`

## Performance

Hot paths (serialization, compression, enveloping, HTTP bodies) avoid intermediate `byte[]` allocations via
`Pooling/ArrayPoolBufferWriter` and `PooledMemoryHttpContent`. The codec layer itself is effectively
zero-allocation; what remains is HttpClient / ASP.NET Core framework-internal allocation. We accept that
rather than dropping to a custom transport, which would forfeit compatibility with `IHttpClientFactory`,
Polly, and OpenTelemetry instrumentation.

`JsonCodec` cannot process UTF-8 directly because Google.Protobuf's `JsonParser` / `JsonFormatter` only
expose string-based APIs, so JSON is accepted as the slower path. The API boundary is
`ReadOnlyMemory<byte>`, so the internals can be swapped if a UTF-8 API becomes available.

## Testing

| Project | Contents |
|---|---|
| `tests/ConnectNet.Tests` | Unit tests (including in-process HTTP via `TestServer`) |
| `tests/ConnectNet.IntegrationTests` | Interop tests against the connect-go implementation |
| `tests/ConnectNet.Conformance` | Harness for the official [connectrpc/conformance](https://github.com/connectrpc/conformance) suite (client and server modes; 100% pass is maintained) |
| `tests/ConnectNet.Benchmarks` | Allocation measurements with BenchmarkDotNet |

The conformance run procedure (fetching connectconformance, `config.yaml`, launch commands) is kept in
executable form in `.github/workflows/ci.yml`; the same steps work locally.
