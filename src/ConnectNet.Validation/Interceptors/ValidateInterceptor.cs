using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf;

namespace ConnectNet.Validation.Interceptors;

public class ValidateInterceptor : IServerInterceptor
{
    private readonly ProtoValidator _validator;

    public ValidateInterceptor() : this(new ProtoValidator()) { }

    public ValidateInterceptor(ProtoValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    public async Task<IMessage> InterceptUnaryAsync(
        UnaryServerContext context,
        Func<UnaryServerContext, Task<IMessage>> next)
    {
        var result = _validator.Validate(context.Request);
        if (!result.IsValid)
        {
            throw new ConnectException(
                ConnectCode.InvalidArgument,
                FormatMessage(result.Violations),
                ToErrorDetails(result.Violations));
        }
        return await next(context);
    }

    internal static string FormatMessage(IReadOnlyList<Violation> violations)
    {
        if (violations.Count == 0)
            return "validation failed";

        if (violations.Count == 1)
            return $"validation failed: {violations[0].Message}";

        return $"validation failed: {violations[0].Message} (and {violations.Count - 1} more)";
    }

    internal static IEnumerable<ConnectErrorDetail> ToErrorDetails(IReadOnlyList<Violation> violations)
    {
        var protoViolations = new Buf.Validate.Violations();
        foreach (var v in violations)
        {
            var protoViolation = new Buf.Validate.Violation
            {
                RuleId = v.ConstraintId,
                Message = v.Message,
            };

            if (!string.IsNullOrEmpty(v.FieldPath))
            {
                protoViolation.Field = ToFieldPath(v.FieldPath);
            }

            protoViolations.Violations_.Add(protoViolation);
        }

        var bytes = protoViolations.ToByteArray();
        return new[]
        {
            new ConnectErrorDetail("buf.validate.Violations", bytes)
        };
    }

    internal static Buf.Validate.FieldPath ToFieldPath(string path)
    {
        var fieldPath = new Buf.Validate.FieldPath();
        // Split on '.' but preserve array subscripts like "items[0]"
        var segments = path.Split('.');
        foreach (var segment in segments)
        {
            var element = new Buf.Validate.FieldPathElement
            {
                FieldName = segment,
            };
            fieldPath.Elements.Add(element);
        }
        return fieldPath;
    }
}
