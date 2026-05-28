using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ConnectNet.Client;

/// <summary>
/// An <see cref="HttpContent"/> backed by a <see cref="ReadOnlyMemory{Byte}"/> that the caller
/// keeps alive (typically an <see cref="Pooling.ArrayPoolBufferWriter"/>'s WrittenMemory).
/// Unlike <see cref="ByteArrayContent"/> this does not require the buffer to be a
/// freshly allocated <c>byte[]</c>; the pooled buffer is referenced in place, so the
/// request path avoids a per-call materialization.
/// </summary>
internal sealed class PooledMemoryHttpContent : HttpContent
{
    private readonly ReadOnlyMemory<byte> _body;

    public PooledMemoryHttpContent(ReadOnlyMemory<byte> body, string contentType, string? contentEncoding = null)
    {
        _body = body;
        Headers.ContentType = new MediaTypeHeaderValue(contentType);
        if (contentEncoding != null)
        {
            Headers.Add("Content-Encoding", contentEncoding);
        }
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => stream.WriteAsync(_body).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = _body.Length;
        return true;
    }
}
