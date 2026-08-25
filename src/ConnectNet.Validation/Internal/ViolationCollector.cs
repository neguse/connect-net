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

    public bool IsFull => _violations.Count >= Limit;

    /// <summary>True once at least one violation has been dropped by the limit.</summary>
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
    /// Records that there was more to check and the limit is why it was not checked. Callers
    /// that abandon a loop on <see cref="IsFull"/> call this, since nothing was handed to
    /// <see cref="Add"/> for the work they skipped.
    /// </summary>
    public void MarkTruncated() => Truncated = true;
}
