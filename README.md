# connect-net

A C# implementation of the [Connect protocol](https://connectrpc.com/docs/protocol) for .NET.

Build type-safe RPC clients and servers that work over HTTP/1.1 and HTTP/2.

## Features

- **Unity compatible** — Client library targets .NET Standard 2.1
- **Full RPC support** — Unary, Server Streaming, Client Streaming, Bidirectional Streaming
- **Multiple codecs** — Protobuf and JSON
- **gzip/deflate compression** — Automatic request/response compression with negotiation
- **Interceptors** — Client-side and server-side middleware for unary RPCs
- **GET requests** — Cache-friendly idempotent RPCs via query parameters
- **Health checks** — gRPC-compatible health endpoint (`grpc.health.v1.Health/Check`)
- **Service discovery** — Server reflection via `/connect/v1/services` and gRPC reflection
- **Code generation** — `protoc-gen-connect-csharp` plugin generates typed clients and server stubs

## Quick Start

### Define your service

```protobuf
syntax = "proto3";
package example;
option csharp_namespace = "Example";

service GreeterService {
  rpc SayHello (HelloRequest) returns (HelloResponse);
  rpc SayHelloStream (HelloRequest) returns (stream HelloResponse);
  rpc CollectHellos (stream HelloRequest) returns (HelloResponse);
  rpc Chat (stream HelloRequest) returns (stream HelloResponse);
}

message HelloRequest { string name = 1; }
message HelloResponse { string message = 1; }
```

### Generate code

```bash
protoc --csharp_out=. --connect-csharp_out=. greeter.proto
```

This generates two files:
- `Greeter.cs` — Protobuf message classes (from `--csharp_out`)
- `GreeterService.connect.cs` — Connect RPC client, server base class, and service definition (from `--connect-csharp_out`)

### Server

```csharp
using ConnectNet;
using ConnectNet.Server;

// Implement the generated base class
public class GreeterImpl : GreeterServiceBase
{
    public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
    {
        return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}!" });
    }
}

// Register in ASP.NET Core
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConnectServices();
builder.Services.AddSingleton<GreeterImpl>();

var app = builder.Build();
app.MapConnectService<GreeterImpl>(GreeterServiceDefinition.Instance);
app.Run();
```

### Client

```csharp
using ConnectNet.Client;

var channel = new ConnectChannel(new HttpClient(), "https://localhost:5000");
var client = new GreeterServiceClient(channel);

var response = await client.SayHelloAsync(new HelloRequest { Name = "World" });
Console.WriteLine(response.Message); // Hello World!
```

### Client (Unity)

```csharp
// HTTP/1.1 (default): works with Unity's built-in HTTP stack
// Supports: Unary, Server Streaming
var channel = new ConnectChannel(new HttpClient(), "https://api.example.com");
var client = new GreeterServiceClient(channel);
var response = await client.SayHelloAsync(new HelloRequest { Name = "Unity" });
```

#### HTTP/2 with YetAnotherHttpHandler

For Client Streaming and Bidirectional Streaming, HTTP/2 is required. Use [YetAnotherHttpHandler](https://github.com/Cysharp/YetAnotherHttpHandler) (YAHA) as the HTTP transport:

```csharp
// HTTP/2: enables all RPC types including Client/Bidi Streaming
using Cysharp.Net.Http;

var handler = new YetAnotherHttpHandler { Http2Only = true };
var channel = new ConnectChannel(new HttpClient(handler), "https://api.example.com");
var client = new GreeterServiceClient(channel);
```

YAHA requires building its native Rust library for your target platform. See the [YAHA documentation](https://github.com/Cysharp/YetAnotherHttpHandler) for build instructions.

#### Unity WebGL

Unity WebGL builds cannot use `SocketsHttpHandler`. Use the bundled `UnityWebRequestHandler`, which is backed by `UnityWebRequest` and supports Unary and Server Streaming over HTTP/1.1:

```csharp
var channel = new ConnectChannel(new HttpClient(new UnityWebRequestHandler()), "https://api.example.com");
var client = new GreeterServiceClient(channel);
```

Client Streaming and Bidirectional Streaming are not available on WebGL because the browser stack does not expose HTTP/2.

## Streaming

### Server Streaming

The server sends multiple responses for a single request. The client receives an `IAsyncEnumerable<TResponse>`.

```csharp
// Server
public override async IAsyncEnumerable<HelloResponse> SayHelloStream(
    HelloRequest request,
    ConnectContext context,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    for (int i = 0; i < 5; i++)
    {
        yield return new HelloResponse { Message = $"Hello {request.Name} #{i}" };
        await Task.Delay(100, ct);
    }
}

// Client
await foreach (var response in client.SayHelloStreamAsync(new HelloRequest { Name = "World" }))
{
    Console.WriteLine(response.Message);
}
```

### Client Streaming

The client sends multiple requests and receives a single response. Uses `ClientStreamCall<TReq, TRes>`.

```csharp
// Server
public override async Task<HelloResponse> CollectHellos(
    IAsyncEnumerable<HelloRequest> requests,
    ConnectContext context)
{
    var names = new List<string>();
    await foreach (var request in requests)
    {
        names.Add(request.Name);
    }
    return new HelloResponse { Message = $"Hello {string.Join(", ", names)}!" };
}

// Client
using var call = client.CollectHellosAsync();
await call.SendAsync(new HelloRequest { Name = "Alice" });
await call.SendAsync(new HelloRequest { Name = "Bob" });
var response = await call.CloseAndReceiveAsync();
Console.WriteLine(response.Message); // Hello Alice, Bob!
```

### Bidirectional Streaming

Both client and server stream messages. Uses `BidiStreamCall<TReq, TRes>`.

```csharp
// Server
public override async IAsyncEnumerable<HelloResponse> Chat(
    IAsyncEnumerable<HelloRequest> requests,
    ConnectContext context,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    await foreach (var request in requests.WithCancellation(ct))
    {
        yield return new HelloResponse { Message = $"Echo: {request.Name}" };
    }
}

// Client (half-duplex: send all, then read all)
using var call = client.ChatAsync();
await call.SendAsync(new HelloRequest { Name = "Alice" });
await call.SendAsync(new HelloRequest { Name = "Bob" });
await foreach (var response in call.CompleteAndReadAsync())
{
    Console.WriteLine(response.Message);
}
```

Full-duplex (HTTP/2 only) reads responses while still sending requests:

```csharp
using var call = client.ChatAsync();

var readTask = Task.Run(async () =>
{
    await foreach (var response in call.ReadResponsesAsync())
        Console.WriteLine(response.Message);
});

await call.SendAsync(new HelloRequest { Name = "Alice" });
await call.SendAsync(new HelloRequest { Name = "Bob" });
call.CloseSend();
await readTask;
```

## Configuration

### Compression

Enable gzip compression for requests. Response compression is negotiated automatically via `Accept-Encoding`.

```csharp
var options = new ConnectChannelOptions
{
    RequestCompressor = new GzipCompressor(),  // compress outgoing requests
    AcceptCompression = true,                  // accept compressed responses (default: true)
};
var channel = new ConnectChannel(new HttpClient(), "https://localhost:5000", channelOptions: options);
```

On the server side, gzip compression is registered automatically by `AddConnectServices()`.

### Interceptors

#### Client Interceptor

```csharp
public class AuthInterceptor : IClientInterceptor
{
    public async Task<IMessage> InterceptUnaryAsync(
        UnaryRequestContext context,
        Func<UnaryRequestContext, Task<IMessage>> next,
        CancellationToken ct)
    {
        context.Headers["Authorization"] = "Bearer my-token";
        return await next(context);
    }
}

var options = new ConnectChannelOptions
{
    Interceptors = { new AuthInterceptor() }
};
var channel = new ConnectChannel(new HttpClient(), "https://localhost:5000", channelOptions: options);
```

#### Server Interceptor

```csharp
public class LoggingInterceptor : IServerInterceptor
{
    public async Task<IMessage> InterceptUnaryAsync(
        UnaryServerContext context,
        Func<UnaryServerContext, Task<IMessage>> next)
    {
        Console.WriteLine($"Calling {context.Procedure}");
        var result = await next(context);
        Console.WriteLine($"Completed {context.Procedure}");
        return result;
    }
}

builder.Services.AddConnectServices(options =>
{
    options.Interceptors.Add(new LoggingInterceptor());
});
```

### Timeout

Set a per-call timeout using `CallOptions`. The client enforces the deadline locally
(the call fails with `ConnectCode.DeadlineExceeded`), and the server also receives it
via `Connect-Timeout-Ms`.

```csharp
var response = await client.SayHelloAsync(
    new HelloRequest { Name = "World" },
    new CallOptions { Timeout = TimeSpan.FromSeconds(5) });
```

### JSON Codec

Use JSON instead of Protobuf for the wire format.

```csharp
var channel = new ConnectChannel(
    new HttpClient(),
    "https://localhost:5000",
    codec: new JsonCodec());
```

The server supports both `application/proto` and `application/json` content types automatically.

### GET Requests

For idempotent/safe RPCs, use HTTP GET to enable caching.

```csharp
var response = await client.SayHelloAsync(
    new HelloRequest { Name = "World" },
    new CallOptions { UseGet = true });
```

The server registers GET endpoints only for unary methods declared side-effect free in the
proto (`option idempotency_level = NO_SIDE_EFFECTS;`), mirroring connect-go. Exposing
arbitrary unary methods over GET would make state-changing RPCs vulnerable to CSRF. The
request is encoded in query parameters (`?encoding=proto&message=...&base64=1&connect=v1`).

### Health Checks

Map the gRPC-compatible health check endpoint.

```csharp
builder.Services.AddSingleton<ConnectHealthService>();

var app = builder.Build();
app.MapConnectHealthCheck();

// Optionally set per-service status
var health = app.Services.GetRequiredService<ConnectHealthService>();
health.SetStatus("example.GreeterService", HealthStatus.Serving);
```

Clients can check health via `POST /grpc.health.v1.Health/Check` with either Protobuf or JSON.

### Service Reflection

Enable service discovery for tooling and debugging.

```csharp
app.MapConnectReflection();
```

This exposes:
- `GET /connect/v1/services` — JSON list of registered services
- `POST /grpc.reflection.v1.ServerReflection/ServerReflectionInfo` (plus the
  `grpc.reflection.v1alpha` alias) — the standard **gRPC Server Reflection** protocol,
  served as a bidirectional stream over the Connect protocol
  (`application/connect+proto` / `application/connect+json`). `list_services`,
  `file_containing_symbol`, and `file_by_filename` (including the transitive dependency
  closure of each file) are supported; extension lookups (`file_containing_extension`,
  `all_extension_numbers_of_type`) answer an `error_response` with `UNIMPLEMENTED`.

Connect protocol reflection clients interoperate directly, e.g.:

```sh
buf curl --protocol connect --http2-prior-knowledge \
  http://localhost:5000/example.GreeterService/SayHello \
  -d '{"name": "World"}'
```

(`buf curl` uses server reflection by default when no `--schema` is given.) Clients that
only speak native gRPC framing over the reflection endpoint — such as grpcurl's default
mode — are not supported, since ConnectNet serves the Connect protocol, not gRPC framing.

> **Security note** — `MapConnectReflection` and `MapConnectHealthCheck` expose service
> inventory and liveness state with no built-in authentication. Treat them like internal
> admin endpoints: restrict reachability at the infrastructure layer (LB / Ingress ACL,
> private VPC, mTLS) rather than relying on application-level auth. If you only need
> reflection for local development, gate the calls on an environment check.

### Custom Headers and Trailers

```csharp
// Client: send custom headers and read response trailers
var callOptions = new CallOptions
{
    Headers = { ["X-Request-Id"] = "abc123" }
};
var response = await client.SayHelloAsync(new HelloRequest { Name = "World" }, callOptions);
var trailer = callOptions.ResponseTrailers["my-trailer"];

// Server: read request headers and set response trailers
public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
{
    var requestId = context.RequestHeaders["X-Request-Id"];
    context.ResponseTrailers["my-trailer"] = "value";
    return Task.FromResult(new HelloResponse { Message = $"Hello {request.Name}!" });
}
```

### Error Handling

Errors use the Connect protocol's error model with typed error codes.

```csharp
// Server: throw a ConnectException
throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

// Server: include error details
throw new ConnectException(
    ConnectCode.FailedPrecondition,
    "validation failed",
    new[] { new ConnectErrorDetail("type.googleapis.com/example.Error", errorBytes) });

// Client: catch ConnectException
try
{
    var response = await client.SayHelloAsync(new HelloRequest());
}
catch (ConnectException ex)
{
    Console.WriteLine($"{ex.Code}: {ex.Message}");
    foreach (var detail in ex.Details)
    {
        Console.WriteLine($"  {detail.Type}: {detail.Value.Length} bytes");
    }
}
```

## Validation

connect-net includes built-in support for [protovalidate](https://github.com/bufbuild/protovalidate) — validate protobuf messages using constraints defined in `.proto` files.

### Define constraints in your proto file

```protobuf
import "buf/validate/validate.proto";

message CreateUserRequest {
  string name = 1 [(buf.validate.field).string.min_len = 3];
  string email = 2 [(buf.validate.field).string.email = true];
  int32 age = 3 [(buf.validate.field).int32 = {gte: 0, lte: 150}];
}
```

### Add the validation interceptor

```csharp
builder.Services.AddConnectServices(options =>
{
    options.Interceptors.Add(new ValidateInterceptor());
});
```

Invalid requests automatically return `ConnectCode.InvalidArgument` with violation details.

### Manual validation

```csharp
var validator = new ProtoValidator();
var result = validator.Validate(message);
if (!result.IsValid)
{
    foreach (var violation in result.Violations)
        Console.WriteLine($"{violation.FieldPath}: {violation.Message}");
}
```

### Unsupported rules

Rules the validator does not implement (CEL expressions, `well_known_regex`, and a few
string/bytes well-known formats) throw `NotSupportedException` when first encountered,
rather than silently passing. To skip them instead, opt out explicitly:

```csharp
var validator = new ProtoValidator(ignoreUnsupportedRules: true);
```

## Architecture

| Package | Target | Description |
|---------|--------|-------------|
| **ConnectNet** | .NET Standard 2.1 | Core library: codecs (`ICodec`, `ProtobufCodec`, `JsonCodec`), errors (`ConnectException`, `ConnectCode`), envelope framing, compression (`GzipCompressor`) |
| **ConnectNet.Client** | .NET Standard 2.1 | Client library: `ConnectChannel`, `CallOptions`, `ClientStreamCall`, `BidiStreamCall`, `StreamingContent` |
| **ConnectNet.Validation** | .NET Standard 2.1 | protovalidate: `ProtoValidator`, `ValidateInterceptor` |
| **ConnectNet.Server** | .NET 10 | Server library: ASP.NET Core integration, unary/streaming handlers, health checks, reflection |
| **protoc-gen-connect-csharp** | Go | Code generation plugin for `protoc` |

## Conformance

connect-net passes **100%** of the [connectrpc/conformance](https://github.com/connectrpc/conformance) suite (v1.0.5) in both client and server modes.

## Installing

The library is not yet published to NuGet. Build from source and reference the projects directly:

```bash
dotnet build
```

The code generator is a Go binary. Build it and put it on `PATH` so `protoc` can find it:

```bash
cd tools/protoc-gen-connect-csharp
go build -o protoc-gen-connect-csharp .
```

## License

Released under the [MIT License](LICENSE).
