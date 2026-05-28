using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ConnectNet;

public class ConnectException : Exception
{
    /// <summary>
    /// Maximum recursion depth accepted when parsing an error JSON returned by an untrusted peer.
    /// Shared with end-stream JSON parsing in client streaming paths.
    /// </summary>
    internal const int MaxJsonDepth = 32;

    /// <summary>
    /// Maximum number of detail entries kept when parsing an error JSON. A malicious peer
    /// could otherwise return millions of details to exhaust memory on the receiver.
    /// </summary>
    private const int MaxDetailCount = 256;

    /// <summary>
    /// Maximum size in bytes of any single detail value (base64-decoded). Anything larger is
    /// silently dropped during parsing.
    /// </summary>
    private const int MaxDetailValueSize = 64 * 1024;

    public ConnectCode Code { get; }
    public IReadOnlyList<ConnectErrorDetail> Details { get; }

    public ConnectException(ConnectCode code, string? message = null, IEnumerable<ConnectErrorDetail>? details = null)
        : base(message ?? code.ToString())
    {
        Code = code;
        Details = details?.ToList().AsReadOnly() ?? (IReadOnlyList<ConnectErrorDetail>)Array.Empty<ConnectErrorDetail>();
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("code", CodeToString(Code));
        writer.WriteString("message", Message);
        if (Details.Count > 0)
        {
            writer.WriteStartArray("details");
            foreach (var detail in Details)
            {
                writer.WriteStartObject();
                writer.WriteString("type", detail.Type);
                // Connect protocol uses standard base64 encoding without padding
                writer.WriteString("value", Base64EncodeUnpadded(detail.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ConnectException? TryFromJson(string json, ConnectCode fallbackCode = ConnectCode.Unknown)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var options = new JsonDocumentOptions { MaxDepth = MaxJsonDepth };
            using var doc = JsonDocument.Parse(json, options);
            return TryFromJsonElement(doc.RootElement, fallbackCode);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            // Triggered, e.g., when JsonDocument receives a string with invalid surrogate pairs.
            return null;
        }
    }

    /// <summary>
    /// UTF-8 entry point that skips the <c>byte[] → string</c> conversion. Use this when the
    /// error JSON is available as a <see cref="ReadOnlyMemory{T}"/>.
    /// </summary>
    public static ConnectException? TryFromJson(ReadOnlyMemory<byte> utf8Json, ConnectCode fallbackCode = ConnectCode.Unknown)
    {
        if (utf8Json.IsEmpty) return null;
        try
        {
            var options = new JsonDocumentOptions { MaxDepth = MaxJsonDepth };
            using var doc = JsonDocument.Parse(utf8Json, options);
            return TryFromJsonElement(doc.RootElement, fallbackCode);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a <see cref="ConnectException"/> from an already-parsed JSON element. Used by
    /// the streaming code path where <see cref="EnvelopeFrame"/> JSON has already been
    /// decoded into a <see cref="JsonDocument"/>.
    /// </summary>
    public static ConnectException? TryFromJsonElement(JsonElement root, ConnectCode fallbackCode = ConnectCode.Unknown)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var code = fallbackCode;
        if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
        {
            code = TryCodeFromString(codeProp.GetString() ?? "") ?? fallbackCode;
        }

        var message = "";
        if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
        {
            message = msgProp.GetString() ?? "";
        }

        var details = new List<ConnectErrorDetail>();
        if (root.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in detailsProp.EnumerateArray())
            {
                if (details.Count >= MaxDetailCount)
                    break;
                if (d.ValueKind != JsonValueKind.Object)
                    continue;
                if (!d.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    continue;
                if (!d.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.String)
                    continue;
                var type = typeProp.GetString() ?? "";
                byte[] value;
                try
                {
                    value = Base64DecodeUnpadded(valueProp.GetString() ?? "");
                }
                catch (FormatException)
                {
                    continue;
                }
                if (value.Length > MaxDetailValueSize)
                    continue;
                details.Add(new ConnectErrorDetail(type, value));
            }
        }

        return new ConnectException(code, message, details);
    }

    public static ConnectException FromJson(string json)
    {
        return TryFromJson(json) ?? new ConnectException(ConnectCode.Unknown);
    }

    public static int ToHttpStatus(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => 499,
        ConnectCode.Unknown => 500,
        ConnectCode.InvalidArgument => 400,
        ConnectCode.DeadlineExceeded => 504,
        ConnectCode.NotFound => 404,
        ConnectCode.AlreadyExists => 409,
        ConnectCode.PermissionDenied => 403,
        ConnectCode.ResourceExhausted => 429,
        ConnectCode.FailedPrecondition => 400,
        ConnectCode.Aborted => 409,
        ConnectCode.OutOfRange => 400,
        ConnectCode.Unimplemented => 501,
        ConnectCode.Internal => 500,
        ConnectCode.Unavailable => 503,
        ConnectCode.DataLoss => 500,
        ConnectCode.Unauthenticated => 401,
        _ => 500,
    };

    public static string CodeToString(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => "canceled",
        ConnectCode.Unknown => "unknown",
        ConnectCode.InvalidArgument => "invalid_argument",
        ConnectCode.DeadlineExceeded => "deadline_exceeded",
        ConnectCode.NotFound => "not_found",
        ConnectCode.AlreadyExists => "already_exists",
        ConnectCode.PermissionDenied => "permission_denied",
        ConnectCode.ResourceExhausted => "resource_exhausted",
        ConnectCode.FailedPrecondition => "failed_precondition",
        ConnectCode.Aborted => "aborted",
        ConnectCode.OutOfRange => "out_of_range",
        ConnectCode.Unimplemented => "unimplemented",
        ConnectCode.Internal => "internal",
        ConnectCode.Unavailable => "unavailable",
        ConnectCode.DataLoss => "data_loss",
        ConnectCode.Unauthenticated => "unauthenticated",
        _ => "unknown",
    };

    private static string Base64EncodeUnpadded(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=');
    }

    private static byte[] Base64DecodeUnpadded(string input)
    {
        // Add padding if needed for standard base64 decoder. Length % 4 == 1 is invalid and
        // would otherwise cause Convert.FromBase64String to throw a FormatException up the stack.
        switch (input.Length % 4)
        {
            case 1: throw new FormatException("invalid base64 length");
            case 2: input += "=="; break;
            case 3: input += "="; break;
        }
        return Convert.FromBase64String(input);
    }

    public static ConnectCode CodeFromHttpStatus(int statusCode) => statusCode switch
    {
        400 => ConnectCode.Internal,
        401 => ConnectCode.Unauthenticated,
        403 => ConnectCode.PermissionDenied,
        404 => ConnectCode.Unimplemented,
        408 => ConnectCode.DeadlineExceeded,
        429 => ConnectCode.Unavailable,
        502 => ConnectCode.Unavailable,
        503 => ConnectCode.Unavailable,
        504 => ConnectCode.Unavailable,
        _ => ConnectCode.Unknown,
    };

    public static ConnectCode? TryCodeFromString(string s) => s switch
    {
        "canceled" => ConnectCode.Canceled,
        "unknown" => ConnectCode.Unknown,
        "invalid_argument" => ConnectCode.InvalidArgument,
        "deadline_exceeded" => ConnectCode.DeadlineExceeded,
        "not_found" => ConnectCode.NotFound,
        "already_exists" => ConnectCode.AlreadyExists,
        "permission_denied" => ConnectCode.PermissionDenied,
        "resource_exhausted" => ConnectCode.ResourceExhausted,
        "failed_precondition" => ConnectCode.FailedPrecondition,
        "aborted" => ConnectCode.Aborted,
        "out_of_range" => ConnectCode.OutOfRange,
        "unimplemented" => ConnectCode.Unimplemented,
        "internal" => ConnectCode.Internal,
        "unavailable" => ConnectCode.Unavailable,
        "data_loss" => ConnectCode.DataLoss,
        "unauthenticated" => ConnectCode.Unauthenticated,
        _ => null,
    };

    public static ConnectCode CodeFromString(string s) => s switch
    {
        "canceled" => ConnectCode.Canceled,
        "unknown" => ConnectCode.Unknown,
        "invalid_argument" => ConnectCode.InvalidArgument,
        "deadline_exceeded" => ConnectCode.DeadlineExceeded,
        "not_found" => ConnectCode.NotFound,
        "already_exists" => ConnectCode.AlreadyExists,
        "permission_denied" => ConnectCode.PermissionDenied,
        "resource_exhausted" => ConnectCode.ResourceExhausted,
        "failed_precondition" => ConnectCode.FailedPrecondition,
        "aborted" => ConnectCode.Aborted,
        "out_of_range" => ConnectCode.OutOfRange,
        "unimplemented" => ConnectCode.Unimplemented,
        "internal" => ConnectCode.Internal,
        "unavailable" => ConnectCode.Unavailable,
        "data_loss" => ConnectCode.DataLoss,
        "unauthenticated" => ConnectCode.Unauthenticated,
        _ => ConnectCode.Unknown,
    };
}
