using System.Collections.Generic;
using System.Linq;

namespace ConnectNet.Validation;

public class ValidationResult
{
    public static readonly ValidationResult Success = new(Enumerable.Empty<Violation>());

    public bool IsValid => Violations.Count == 0;
    public IReadOnlyList<Violation> Violations { get; }

    private ValidationResult(IEnumerable<Violation> violations)
    {
        Violations = violations.ToList().AsReadOnly();
    }

    public static ValidationResult Fail(IEnumerable<Violation> violations)
    {
        return new ValidationResult(violations);
    }
}
