using System;

namespace ConnectNet.Validation;

/// <summary>
/// A rule could not be evaluated for a message: a CEL expression produced an error at runtime
/// (a type error through <c>dyn</c>, an invalid regular expression input, ...), the evaluation
/// budget was exhausted, or validation was cancelled. The message is neither valid nor invalid.
/// </summary>
public class ValidationEvaluationException : Exception
{
    public ValidationEvaluationException(string message) : base(message)
    {
    }

    public ValidationEvaluationException(string message, Exception? innerException) : base(message, innerException)
    {
    }
}
