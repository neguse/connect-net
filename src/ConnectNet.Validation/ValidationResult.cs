using System.Collections.Generic;
using System.Linq;

namespace ConnectNet.Validation;

public class ValidationResult
{
    public static readonly ValidationResult Success = new(Enumerable.Empty<Violation>(), truncated: false);

    public bool IsValid => Violations.Count == 0;

    /// <summary>
    /// True when the violation limit stopped evaluation early: <see cref="Violations"/> is a
    /// prefix of what a full evaluation would have produced, not the complete set.
    /// </summary>
    public bool Truncated { get; }

    public IReadOnlyList<Violation> Violations { get; }

    private ValidationResult(IEnumerable<Violation> violations, bool truncated)
    {
        Violations = violations.ToList().AsReadOnly();
        Truncated = truncated;
    }

    public static ValidationResult Fail(IEnumerable<Violation> violations)
    {
        return new ValidationResult(violations, truncated: false);
    }

    public static ValidationResult Fail(IEnumerable<Violation> violations, bool truncated)
    {
        return new ValidationResult(violations, truncated);
    }
}
