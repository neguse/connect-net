using System;
using System.Diagnostics;
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
    private readonly CancellationTokenSource? _timerCts;
    private readonly CancellationTokenSource? _linkedCts;
    private readonly long _deadlineTimestamp;

    /// <summary>Token to use for all I/O belonging to the call.</summary>
    public CancellationToken Token { get; }

    public ClientCallScope(TimeSpan? timeout, CancellationToken userToken)
    {
        if (timeout is TimeSpan value)
        {
            // The timer gets its own CTS (not linked to the user token) so that
            // "the deadline fired" can be read off unambiguously in DeadlineExpired.
            _timerCts = new CancellationTokenSource();
            _timerCts.CancelAfter(value);
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userToken, _timerCts.Token);
            Token = _linkedCts.Token;
            _deadlineTimestamp = Stopwatch.GetTimestamp()
                + (long)(value.TotalSeconds * Stopwatch.Frequency);
        }
        else
        {
            Token = userToken;
        }
    }

    // The elapsed-time fallback covers callers that run their own timer and cancel the
    // user token exactly at the deadline: losing that race must not turn a deadline
    // expiry into Canceled.
    private bool DeadlineExpired =>
        _timerCts != null
        && (_timerCts.IsCancellationRequested || Stopwatch.GetTimestamp() >= _deadlineTimestamp);

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

    public void Dispose()
    {
        _linkedCts?.Dispose();
        _timerCts?.Dispose();
    }
}
