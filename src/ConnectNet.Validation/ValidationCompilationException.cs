using System;

namespace ConnectNet.Validation;

/// <summary>
/// The rules of a message type could not be compiled: a rule of the wrong type for its field,
/// a CEL expression that fails to parse or type-check, or a rule referencing a field that does
/// not exist. This is a defect in the schema, never a property of the message being validated,
/// so it is thrown rather than reported as a <see cref="Violation"/>.
/// </summary>
public class ValidationCompilationException : Exception
{
    public ValidationCompilationException(string message) : base(message)
    {
    }

    public ValidationCompilationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
