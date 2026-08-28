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

            var protoViolation = new Buf.Validate.Violation
            {
                RuleId = v.ConstraintId,
                Message = v.Message,
                ForKey = v.ForKey,
            };

            if (!string.IsNullOrEmpty(v.FieldPath))
            {
                protoViolation.Field = ToFieldPath(v.FieldPath);
            }

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

    internal static Buf.Validate.FieldPath ToFieldPath(string path)
    {
        // Parses textual paths like `inner.name`, `items[0].name` or `entries["key"]` into
        // protovalidate FieldPathElements with the subscript carried in the subscript oneof.
        // Note: quoted map keys may contain '.', so this cannot simply split on dots.
        var fieldPath = new Buf.Validate.FieldPath();
        int i = 0;
        int n = path.Length;
        while (i < n)
        {
            int start = i;
            bool inQuotes = false;
            while (i < n && (inQuotes || (path[i] != '.' && path[i] != '[')))
            {
                if (path[i] == '"') inQuotes = !inQuotes;
                i++;
            }

            var element = new Buf.Validate.FieldPathElement
            {
                FieldName = path.Substring(start, i - start),
            };

            if (i < n && path[i] == '[')
            {
                int close = FindSubscriptEnd(path, i + 1);
                ApplySubscript(element, path.Substring(i + 1, close - i - 1));
                i = close < n ? close + 1 : n;
            }

            fieldPath.Elements.Add(element);

            if (i < n && path[i] == '.')
                i++;
        }
        return fieldPath;
    }

    private static int FindSubscriptEnd(string path, int start)
    {
        bool inQuotes = false;
        for (int i = start; i < path.Length; i++)
        {
            if (path[i] == '"') inQuotes = !inQuotes;
            else if (path[i] == ']' && !inQuotes) return i;
        }
        return path.Length; // malformed; consume the rest
    }

    private static void ApplySubscript(Buf.Validate.FieldPathElement element, string subscript)
    {
        if (subscript.Length >= 2 && subscript[0] == '"' && subscript[subscript.Length - 1] == '"')
        {
            element.StringKey = subscript.Substring(1, subscript.Length - 2);
        }
        else if (subscript == "true" || subscript == "false")
        {
            element.BoolKey = subscript == "true";
        }
        else if (ulong.TryParse(subscript, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var index))
        {
            // Non-negative integers are ambiguous between repeated indices and integer map
            // keys in the textual form; repeated indices are by far the common case.
            element.Index = index;
        }
        else if (long.TryParse(subscript, System.Globalization.NumberStyles.AllowLeadingSign,
            System.Globalization.CultureInfo.InvariantCulture, out var intKey))
        {
            element.IntKey = intKey;
        }
        else
        {
            element.StringKey = subscript;
        }
    }
}
