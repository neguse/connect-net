using System;
using System.Net.Http;
using System.Threading;

namespace ConnectNet.Client;

/// <summary>
/// Per-call scope that enforces <see cref="CallOptions.Timeout"/> locally and normalizes
/// transport-level failures into <see cref="ConnectException"/>: deadline expiry maps to
/// <see cref="ConnectCode.DeadlineExceeded"/>, caller cancellation to
/// <see cref="ConnectCode.Canceled"/>, and <see cref="HttpRequestException"/> to
/// <see cref="ConnectCode.Unavailable"/>. Existing <see cref="ConnectException"/>s pass
/// through untouched.
/// </summary>
internal sealed class ClientCallScope : IDisposable
{
    private readonly CancellationTokenSource? _timeoutCts;
    private readonly CancellationToken _userToken;

    /// <summary>Token to use for all I/O belonging to the call.</summary>
    public CancellationToken Token { get; }

    public ClientCallScope(TimeSpan? timeout, CancellationToken userToken)
    {
        _userToken = userToken;
        if (timeout is TimeSpan value)
        {
            _timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(userToken);
            _timeoutCts.CancelAfter(value);
            Token = _timeoutCts.Token;
        }
        else
        {
            Token = userToken;
        }
    }

    private bool DeadlineExpired =>
        _timeoutCts != null && _timeoutCts.IsCancellationRequested && !_userToken.IsCancellationRequested;

    /// <summary>Exception filter companion for <see cref="Normalize"/>.</summary>
    public static bool ShouldNormalize(Exception ex)
        => ex is OperationCanceledException || ex is HttpRequestException;

    public Exception Normalize(Exception ex)
    {
        switch (ex)
        {
            case ConnectException:
                return ex;
            case OperationCanceledException when DeadlineExpired:
                return new ConnectException(ConnectCode.DeadlineExceeded, "the call deadline was exceeded");
            case OperationCanceledException:
                return new ConnectException(ConnectCode.Canceled, "the call was canceled");
            case HttpRequestException:
                return new ConnectException(ConnectCode.Unavailable, $"transport error: {ex.Message}", ex);
            default:
                return ex;
        }
    }

    public void Dispose() => _timeoutCts?.Dispose();
}
