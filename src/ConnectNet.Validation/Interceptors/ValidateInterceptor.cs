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
                FormatMessage(result.Violations, result.Truncated),
                ToErrorDetails(result.Violations, result.Truncated));
        }
        return await next(context);
    }

    internal static string FormatMessage(IReadOnlyList<Violation> violations, bool truncated)
    {
        if (violations.Count == 0)
            return "validation failed";

        var head = $"validation failed: {violations[0].Message}";
        if (violations.Count > 1)
            head += $" (and {violations.Count - 1} more)";
        return truncated ? head + " [stopped at the violation limit]" : head;
    }

    /// <summary>
    /// Cap on how many violations are serialized into the error detail, independent of the
    /// validator's own limit: whatever produced the list, the response body this reflects back
    /// to the caller stays bounded.
    /// </summary>
    internal const int MaxSerializedViolations = 100;

    /// <summary>
    /// The <c>rule_id</c> of the synthetic trailing violation that tells a remote caller the
    /// serialized list is incomplete — because evaluation stopped at the violation limit, or
    /// because <see cref="MaxSerializedViolations"/> cut the serialization short. It is
    /// appended after the cap so it can never be the entry the cap drops.
    /// </summary>
    internal const string TruncationRuleId = "violation_limit";

    internal static IEnumerable<ConnectErrorDetail> ToErrorDetails(IReadOnlyList<Violation> violations, bool truncated)
    {
        var protoViolations = new Buf.Validate.Violations();
        foreach (var v in violations)
        {
            if (protoViolations.Violations_.Count >= MaxSerializedViolations)
            {
                truncated = true;
                break;
            }

            var protoViolation = v.ToProto();
            protoViolations.Violations_.Add(protoViolation);
        }

        if (truncated)
        {
            protoViolations.Violations_.Add(new Buf.Validate.Violation
            {
                RuleId = TruncationRuleId,
                Message = "not all violations are listed",
            });
        }

        var bytes = protoViolations.ToByteArray();
        return new[]
        {
            new ConnectErrorDetail("buf.validate.Violations", bytes)
        };
    }
}
