using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Client;
using ConnectNet.Pooling;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// Mock-transport tests for client-side hardening: response size limits, bidi stream
/// startup/teardown, local deadline enforcement, exception normalization, URI building,
/// interceptor isolation, content-type validation, and channel option snapshotting.
/// </summary>
public class ClientHardeningTests
{
    private const string Procedure = "/example.GreeterService/SayHello";

    // --- helpers ---

    private sealed class AsyncMockHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public AsyncMockHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => _handler(request, ct);
    }

    private static ConnectChannel Channel(HttpMessageHandler handler, Action<ConnectChannelOptions>? configure = null)
    {
        var options = new ConnectChannelOptions { HttpHandler = handler };
        configure?.Invoke(options);
        return ConnectChannel.ForAddress("https://example.com", options);
    }

    private static HttpResponseMessage ProtoResponse(IMessage message)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(message.ToByteArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/proto") }
            }
        };
    }

    private static Task<byte[]> BuildStreamBodyAsync(params IMessage[] messages)
        => BuildStreamBodyAsync("{}", messages);

    private static async Task<byte[]> BuildStreamBodyAsync(string endStreamPayload, params IMessage[] messages)
    {
        var codec = new ProtobufCodec();
        using var ms = new MemoryStream();
        foreach (var message in messages)
        {
            await Envelope.WriteAsync(ms, 0x00, codec.SerializeToArray(message));
        }
        await Envelope.WriteAsync(ms, Envelope.FlagEndStream, Encoding.UTF8.GetBytes(endStreamPayload));
        return ms.ToArray();
    }

    private static HttpResponseMessage StreamResponse(byte[] body, string contentType = "application/connect+proto")
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new MediaTypeHeaderValue(contentType) }
            }
        };
    }

    private static async Task<T> WithinAsync<T>(Task<T> task, int timeoutMs = 5000)
    {
        var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(ReferenceEquals(winner, task), $"operation did not complete within {timeoutMs}ms");
        return await task;
    }

    private static async Task WithinAsync(Task task, int timeoutMs = 5000)
    {
        var winner = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(ReferenceEquals(winner, task), $"operation did not complete within {timeoutMs}ms");
        await task;
    }

    /// <summary>Serves a fixed number of zero bytes and counts how many were actually read.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly long _length;
        private long _position;
        private long _totalRead;
        public CountingStream(long length) => _length = length;
        public long TotalRead => Interlocked.Read(ref _totalRead);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = _length - _position;
            if (remaining <= 0) return 0;
            var n = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, n);
            _position += n;
            Interlocked.Add(ref _totalRead, n);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Never yields data; honors only the read cancellation token.</summary>
    private sealed class HangingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisposeTrackingContent : HttpContent
    {
        private readonly byte[] _body;
        private readonly TaskCompletionSource<bool> _disposed;
        public DisposeTrackingContent(byte[] body, TaskCompletionSource<bool> disposed)
        {
            _body = body;
            _disposed = disposed;
            Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
        }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => stream.WriteAsync(_body, 0, _body.Length);
        protected override bool TryComputeLength(out long length)
        {
            length = _body.Length;
            return true;
        }
        protected override void Dispose(bool disposing)
        {
            _disposed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    private sealed class AddHeaderInterceptor : IClientInterceptor
    {
        public async Task<IMessage> InterceptUnaryAsync(
            UnaryRequestContext context,
            Func<UnaryRequestContext, Task<IMessage>> next,
            CancellationToken ct)
        {
            context.Headers["x-intercepted"] = "yes";
            return await next(context);
        }
    }

    // --- 1. MaxResponseBytes must be enforced without buffering the whole body ---

    [Fact]
    public async Task Unary_ResponseLimit_DoesNotBufferBeyondLimit()
    {
        var counting = new CountingStream(10 * 1024 * 1024);
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(counting) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler, o => o.MaxResponseBytes = 64 * 1024);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest { Name = "x" })));

        Assert.Equal(ConnectCode.ResourceExhausted, ex.Code);
        Assert.True(counting.TotalRead < 2 * 1024 * 1024, $"client read {counting.TotalRead} bytes before enforcing the limit");
    }

    [Fact]
    public async Task UnaryGet_ResponseLimit_DoesNotBufferBeyondLimit()
    {
        var counting = new CountingStream(10 * 1024 * 1024);
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(counting) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler, o => o.MaxResponseBytes = 64 * 1024);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest { Name = "x" }, new CallOptions { UseGet = true })));

        Assert.Equal(ConnectCode.ResourceExhausted, ex.Code);
        Assert.True(counting.TotalRead < 2 * 1024 * 1024, $"client read {counting.TotalRead} bytes before enforcing the limit");
    }

    // --- 2. Bidi must not hang when the connection fails ---

    [Fact]
    public async Task Bidi_ConnectionFailure_FailsFastWithUnavailable()
    {
        var handler = new AsyncMockHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            call.SendAsync(new HelloRequest { Name = "x" })));
        Assert.Equal(ConnectCode.Unavailable, ex.Code);
    }

    // --- 3. Concurrent first operations must start exactly one HTTP request ---

    [Fact]
    public async Task Bidi_ConcurrentFirstOperations_SendSingleHttpRequest()
    {
        int requests = 0;
        var handler = new AsyncMockHandler((_, _) =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"));
        });
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            gate.Wait();
            try { await call.SendAsync(new HelloRequest { Name = "x" }); }
            catch (ConnectException) { }
        })).ToArray();
        gate.Set();

        await WithinAsync(Task.WhenAll(tasks));
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    // --- 4. Dispose must release the pending response ---

    [Fact]
    public async Task Bidi_Dispose_DisposesPendingResponse()
    {
        var disposed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((request, token) =>
        {
            _ = request.Content!.ReadAsStreamAsync(); // start consuming the request body
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new DisposeTrackingContent(body, disposed)
            });
        });
        using var channel = Channel(handler);
        var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);

        await WithinAsync(call.WaitForResponseAsync());
        call.Dispose();

        await WithinAsync(disposed.Task, 2000);
    }

    // --- 5. Streaming requests must ask for HTTP/2 ---

    [Fact]
    public async Task BidiStream_RequestsHttp2()
    {
        Version? seen = null;
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((request, token) =>
        {
            seen = request.Version;
            _ = request.Content!.ReadAsStreamAsync();
            return Task.FromResult(StreamResponse(body));
        });
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);
        await call.SendAsync(new HelloRequest { Name = "a" });
        await foreach (var _ in call.CompleteAndReadAsync()) { }

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Major);
    }

    [Fact]
    public async Task ClientStream_RequestsHttp2()
    {
        Version? seen = null;
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((request, _) =>
        {
            seen = request.Version;
            return Task.FromResult(StreamResponse(body));
        });
        using var channel = Channel(handler);
        using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(Procedure);
        await call.SendAsync(new HelloRequest { Name = "a" });
        await call.CloseAndReceiveAsync();

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Major);
    }

    [Fact]
    public async Task ServerStream_RequestsHttp2()
    {
        Version? seen = null;
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((request, _) =>
        {
            seen = request.Version;
            return Task.FromResult(StreamResponse(body));
        });
        using var channel = Channel(handler);
        await foreach (var _ in channel.ServerStreamAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest())) { }

        Assert.NotNull(seen);
        Assert.Equal(2, seen!.Major);
    }

    // --- 6. Channel-owned HttpClients must not impose the default 100 s timeout ---

    [Fact]
    public void ChannelOwnedHttpClient_HasInfiniteTimeout()
    {
        using var defaultChannel = ConnectChannel.ForAddress("https://example.com");
        Assert.Equal(Timeout.InfiniteTimeSpan, defaultChannel.HttpClient.Timeout);

        using var handler = new AsyncMockHandler((_, _) => Task.FromResult(new HttpResponseMessage()));
        using var handlerChannel = Channel(handler);
        Assert.Equal(Timeout.InfiniteTimeSpan, handlerChannel.HttpClient.Timeout);
    }

    // --- 7. Local deadline enforcement and exception normalization ---

    [Fact]
    public async Task Unary_Timeout_ThrowsDeadlineExceeded()
    {
        var handler = new AsyncMockHandler(async (_, ct) =>
        {
            await Task.Delay(30000, ct);
            return ProtoResponse(new HelloResponse());
        });
        using var channel = Channel(handler);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest(), new CallOptions { Timeout = TimeSpan.FromMilliseconds(100) })));
        Assert.Equal(ConnectCode.DeadlineExceeded, ex.Code);
    }

    [Fact]
    public async Task UnaryGet_Timeout_ThrowsDeadlineExceeded()
    {
        var handler = new AsyncMockHandler(async (_, ct) =>
        {
            await Task.Delay(30000, ct);
            return ProtoResponse(new HelloResponse());
        });
        using var channel = Channel(handler);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest(),
                new CallOptions { UseGet = true, Timeout = TimeSpan.FromMilliseconds(100) })));
        Assert.Equal(ConnectCode.DeadlineExceeded, ex.Code);
    }

    [Fact]
    public async Task Unary_UserCancellation_ThrowsCanceled()
    {
        var handler = new AsyncMockHandler(async (_, ct) =>
        {
            await Task.Delay(30000, ct);
            return ProtoResponse(new HelloResponse());
        });
        using var channel = Channel(handler);
        using var cts = new CancellationTokenSource(100);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest(), options: null, cts.Token)));
        Assert.Equal(ConnectCode.Canceled, ex.Code);
    }

    [Fact]
    public async Task Unary_TransportError_ThrowsUnavailable()
    {
        var handler = new AsyncMockHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("dns failure")));
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest()));
        Assert.Equal(ConnectCode.Unavailable, ex.Code);
    }

    [Fact]
    public async Task ServerStream_Timeout_ThrowsDeadlineExceeded()
    {
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in channel.ServerStreamAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest(), new CallOptions { Timeout = TimeSpan.FromMilliseconds(100) })) { }
        }));
        Assert.Equal(ConnectCode.DeadlineExceeded, ex.Code);
    }

    [Fact]
    public async Task ClientStream_Timeout_ThrowsDeadlineExceeded()
    {
        var handler = new AsyncMockHandler(async (_, ct) =>
        {
            await Task.Delay(30000, ct);
            return StreamResponse(Array.Empty<byte>());
        });
        using var channel = Channel(handler);
        using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(
            Procedure, new CallOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        await call.SendAsync(new HelloRequest());

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(() => call.CloseAndReceiveAsync()));
        Assert.Equal(ConnectCode.DeadlineExceeded, ex.Code);
    }

    [Fact]
    public async Task Bidi_Timeout_ThrowsDeadlineExceeded()
    {
        var handler = new AsyncMockHandler(async (_, ct) =>
        {
            await Task.Delay(30000, ct);
            return StreamResponse(Array.Empty<byte>());
        });
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(
            Procedure, new CallOptions { Timeout = TimeSpan.FromMilliseconds(100) });

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in call.CompleteAndReadAsync()) { }
        }));
        Assert.Equal(ConnectCode.DeadlineExceeded, ex.Code);
    }

    // --- 8. Base URI path prefixes must be preserved ---

    [Fact]
    public async Task BaseUri_PathPrefix_IsPreserved()
    {
        string? path = null;
        var handler = new AsyncMockHandler((request, _) =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(ProtoResponse(new HelloResponse { Message = "ok" }));
        });
        var options = new ConnectChannelOptions { HttpHandler = handler };
        using var channel = ConnectChannel.ForAddress("https://example.com/rpc", options);

        await channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest());
        Assert.Equal("/rpc/example.GreeterService/SayHello", path);
    }

    [Fact]
    public async Task BaseUri_PathPrefixWithTrailingSlash_IsPreserved()
    {
        string? path = null;
        var handler = new AsyncMockHandler((request, _) =>
        {
            path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(ProtoResponse(new HelloResponse { Message = "ok" }));
        });
        var options = new ConnectChannelOptions { HttpHandler = handler };
        using var channel = ConnectChannel.ForAddress(new Uri("https://example.com/rpc/"), options);

        await channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest());
        Assert.Equal("/rpc/example.GreeterService/SayHello", path);
    }

    [Fact]
    public async Task Procedure_NetworkPathReference_IsStillRejected()
    {
        var handler = new AsyncMockHandler((_, _) =>
            Task.FromResult(ProtoResponse(new HelloResponse())));
        using var channel = Channel(handler);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>("//evil.com/x", new HelloRequest()));
    }

    // --- 9. Interceptors must not mutate the caller's CallOptions ---

    [Fact]
    public async Task Interceptor_DoesNotMutateCallerOptions()
    {
        string? seenHeader = null;
        var handler = new AsyncMockHandler((request, _) =>
        {
            seenHeader = request.Headers.TryGetValues("x-intercepted", out var values) ? values.First() : null;
            var response = ProtoResponse(new HelloResponse { Message = "ok" });
            response.Headers.Add("x-server", "1");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler, o => o.Interceptors.Add(new AddHeaderInterceptor()));

        var callerHeaders = new Dictionary<string, string> { ["x-user"] = "u" };
        var options = new CallOptions { Headers = callerHeaders };
        await channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest(), options);

        Assert.Equal("yes", seenHeader);
        Assert.Same(callerHeaders, options.Headers);
        Assert.False(callerHeaders.ContainsKey("x-intercepted"));
        Assert.Equal("1", options.ResponseHeaders["x-server"]);
    }

    // --- 10. Streaming responses must validate Content-Type ---

    [Fact]
    public async Task ClientStream_UnexpectedContentType_ThrowsInternal()
    {
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body, "text/html")));
        using var channel = Channel(handler);
        using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(Procedure);
        await call.SendAsync(new HelloRequest());

        var ex = await Assert.ThrowsAsync<ConnectException>(() => call.CloseAndReceiveAsync());
        Assert.Equal(ConnectCode.Internal, ex.Code);
        Assert.Contains("content-type", ex.Message);
    }

    [Fact]
    public async Task Bidi_UnexpectedContentType_ThrowsInternal()
    {
        var body = await BuildStreamBodyAsync(new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body, "text/html")));
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);

        var ex = await WithinAsync(Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in call.ReadResponsesAsync()) { }
        }));
        Assert.Equal(ConnectCode.Internal, ex.Code);
        Assert.Contains("content-type", ex.Message);
    }

    // --- 11. Unary responses must reject streaming/missing content types ---

    [Fact]
    public async Task Unary_StreamingContentType_IsRejected()
    {
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new HelloResponse { Message = "ok" }.ToByteArray())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest()));
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public async Task Unary_MissingContentType_IsRejected()
    {
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new HelloResponse { Message = "ok" }.ToByteArray())
        }));
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest()));
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public async Task UnaryGet_StreamingContentType_IsRejected()
    {
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new HelloResponse { Message = "ok" }.ToByteArray())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/connect+proto");
            return Task.FromResult(response);
        });
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(() =>
            channel.UnaryAsync<HelloRequest, HelloResponse>(
                Procedure, new HelloRequest(), new CallOptions { UseGet = true }));
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    // --- 14. Channel options must be snapshotted at construction ---

    [Fact]
    public async Task ChannelOptions_InterceptorsAddedAfterConstruction_AreIgnored()
    {
        bool sawHeader = false;
        var handler = new AsyncMockHandler((request, _) =>
        {
            sawHeader = request.Headers.Contains("x-intercepted");
            return Task.FromResult(ProtoResponse(new HelloResponse()));
        });
        var options = new ConnectChannelOptions { HttpHandler = handler };
        using var channel = ConnectChannel.ForAddress("https://example.com", options);

        options.Interceptors.Add(new AddHeaderInterceptor());
        await channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest());
        Assert.False(sawHeader);
    }

    [Fact]
    public async Task ChannelOptions_DecompressorsMutatedAfterConstruction_AreIgnored()
    {
        var gzip = new GzipCompressor();
        var payload = new HelloResponse { Message = "zipped" }.ToByteArray();
        var compressed = gzip.CompressToArray(payload);
        var handler = new AsyncMockHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(compressed) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/proto");
            response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        });
        var options = new ConnectChannelOptions { HttpHandler = handler };
        using var channel = ConnectChannel.ForAddress("https://example.com", options);

        options.Decompressors.Clear();
        var result = await channel.UnaryAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest());
        Assert.Equal("zipped", result.Message);
    }

    // --- 15. A peer-chosen end-stream payload must not choose the exception type ---

    [Fact]
    public async Task ServerStream_MalformedEndStreamJson_ThrowsConnectException()
    {
        var body = await BuildStreamBodyAsync("not json", new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body)));
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in channel.ServerStreamAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest())) { }
        });
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public async Task ServerStream_EmptyEndStreamPayload_CompletesNormally()
    {
        var body = await BuildStreamBodyAsync("", new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body)));
        using var channel = Channel(handler);

        var received = new List<string>();
        await foreach (var msg in channel.ServerStreamAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest()))
        {
            received.Add(msg.Message);
        }
        Assert.Equal(new[] { "hi" }, received);
    }

    [Fact]
    public async Task ServerStream_NonObjectEndStreamJson_ThrowsConnectException()
    {
        var body = await BuildStreamBodyAsync("5", new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body)));
        using var channel = Channel(handler);

        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in channel.ServerStreamAsync<HelloRequest, HelloResponse>(Procedure, new HelloRequest())) { }
        });
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public async Task ClientStream_MalformedEndStreamJson_ThrowsConnectException()
    {
        var body = await BuildStreamBodyAsync("not json", new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((_, _) => Task.FromResult(StreamResponse(body)));
        using var channel = Channel(handler);
        using var call = channel.ClientStreamAsync<HelloRequest, HelloResponse>(Procedure);
        await call.SendAsync(new HelloRequest { Name = "a" });

        var ex = await Assert.ThrowsAsync<ConnectException>(() => call.CloseAndReceiveAsync());
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public async Task BidiStream_MalformedEndStreamJson_ThrowsConnectException()
    {
        var body = await BuildStreamBodyAsync("not json", new HelloResponse { Message = "hi" });
        var handler = new AsyncMockHandler((request, token) =>
        {
            _ = request.Content!.ReadAsStreamAsync();
            return Task.FromResult(StreamResponse(body));
        });
        using var channel = Channel(handler);
        using var call = channel.BidiStreamAsync<HelloRequest, HelloResponse>(Procedure);
        await call.SendAsync(new HelloRequest { Name = "a" });

        var ex = await Assert.ThrowsAsync<ConnectException>(async () =>
        {
            await foreach (var _ in call.CompleteAndReadAsync()) { }
        });
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }
}
