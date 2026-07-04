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
                ForKey = v.ForKey,
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
