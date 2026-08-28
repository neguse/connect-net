using System.Collections.Generic;

namespace ConnectNet.Validation.Internal;

/// <summary>
/// Bounded sink for the violations one message produces. How many violations a message can
/// produce is decided by the message itself — one per failing repeated element or map entry —
/// so a request that fits inside the receive limit could otherwise force the server to
/// materialize millions of them, and then serialize them all back out in the error response.
/// Collection stops at <see cref="Limit"/>; evaluators that iterate over attacker-sized
/// collections check <see cref="IsFull"/> and stop with it.
/// </summary>
internal sealed class ViolationCollector
{
    private readonly List<Violation> _violations = new();

    public ViolationCollector(int limit)
    {
        Limit = limit;
    }

    public int Limit { get; }

    public int Count => _violations.Count;

    private bool IsFull => _violations.Count >= Limit;

    /// <summary>True once the limit has dropped a violation or cut an evaluation short.</summary>
    public bool Truncated { get; private set; }

    public Violation this[int index] => _violations[index];

    public IReadOnlyList<Violation> Violations => _violations;

    public void Add(Violation violation)
    {
        if (IsFull)
        {
            Truncated = true;
            return;
        }
        _violations.Add(violation);
    }

    /// <summary>
    /// True when the limit has been reached. Asking also records the truncation, because the
    /// caller is about to skip the rest of its work: evaluators that iterate request-sized
    /// data guard each step with this and stop when it returns true.
    /// </summary>
    public bool LimitReached()
    {
        if (!IsFull)
            return false;
        Truncated = true;
        return true;
    }
}
