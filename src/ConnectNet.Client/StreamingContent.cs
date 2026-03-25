using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ConnectNet.Client;

internal class StreamingContent : HttpContent
{
    private readonly TaskCompletionSource<Stream> _streamTcs = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _completeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<Stream> GetStreamAsync() => _streamTcs.Task;
    public void Complete() => _completeTcs.TrySetResult(true);
    public void Abort(Exception ex) => _completeTcs.TrySetException(ex);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        _streamTcs.TrySetResult(stream);
        await _completeTcs.Task.ConfigureAwait(false);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
