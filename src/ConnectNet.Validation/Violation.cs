namespace ConnectNet.Validation;

public class Violation
{
    public string FieldPath { get; }
    public string ConstraintId { get; }
    public string Message { get; }
    public object? Value { get; }

    public Violation(string fieldPath, string constraintId, string message, object? value = null)
    {
        FieldPath = fieldPath;
        ConstraintId = constraintId;
        Message = message;
        Value = value;
    }
}
