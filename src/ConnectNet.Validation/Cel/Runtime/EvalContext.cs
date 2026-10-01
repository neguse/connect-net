using System;
using System.Collections.Generic;
using System.Threading;

namespace ConnectNet.Validation.Cel.Runtime;

/// <summary>Variable bindings for one evaluation.</summary>
internal abstract class Activation
{
    public static readonly Activation Empty = new DictionaryActivation(new Dictionary<string, CelValue>());

    public abstract bool TryResolve(string name, out CelValue value);

    public static Activation Of(IReadOnlyDictionary<string, CelValue> bindings) => new DictionaryActivation(bindings);

    private sealed class DictionaryActivation : Activation
    {
        private readonly IReadOnlyDictionary<string, CelValue> _bindings;

        public DictionaryActivation(IReadOnlyDictionary<string, CelValue> bindings)
        {
            _bindings = bindings;
        }

        public override bool TryResolve(string name, out CelValue value) => _bindings.TryGetValue(name, out value!);
    }
}

/// <summary>Raised when an evaluation exceeds its budget or is cancelled. Never absorbed by operators.</summary>
internal sealed class CelEvaluationException : Exception
{
    public CelEvaluationException(string message) : base(message)
    {
    }

    public CelEvaluationException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// Per-evaluation state: the bindings, comprehension variable slots, the evaluation budget and
/// cancellation. The budget counts operations, loop iterations and produced aggregate sizes so
/// that no input can drive unbounded work.
/// </summary>
internal sealed class EvalContext
{
    public const long DefaultBudget = 1_000_000;

    private long _remaining;

    public EvalContext(Activation activation, int slotCount, long budget = DefaultBudget,
        CancellationToken cancellationToken = default)
    {
        Activation = activation;
        Slots = slotCount == 0 ? Array.Empty<CelValue>() : new CelValue[slotCount];
        _remaining = budget;
        CancellationToken = cancellationToken;
    }

    public Activation Activation { get; }

    /// <summary>Values of comprehension variables, indexed by the slot the planner assigned.</summary>
    public CelValue[] Slots { get; }

    public CancellationToken CancellationToken { get; }

    public long Remaining => _remaining;

    /// <summary>Charges the budget; throws when exhausted or cancelled.</summary>
    public void Consume(long cost)
    {
        _remaining -= cost;
        if (_remaining < 0)
            throw new CelEvaluationException("evaluation budget exhausted");
        if (CancellationToken.IsCancellationRequested)
            throw new CelEvaluationException("evaluation cancelled", new OperationCanceledException(CancellationToken));
    }
}
