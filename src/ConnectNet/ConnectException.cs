using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ConnectNet;

public class ConnectException : Exception
{
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

    public static ConnectException? TryFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var code = ConnectCode.Unknown;
            if (root.TryGetProperty("code", out var codeProp) && codeProp.ValueKind == JsonValueKind.String)
            {
                code = CodeFromString(codeProp.GetString() ?? "");
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
                    if (d.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!d.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                        continue;
                    if (!d.TryGetProperty("value", out var valueProp) || valueProp.ValueKind != JsonValueKind.String)
                        continue;
                    var type = typeProp.GetString() ?? "";
                    var value = Base64DecodeUnpadded(valueProp.GetString() ?? "");
                    details.Add(new ConnectErrorDetail(type, value));
                }
            }

            return new ConnectException(code, message, details);
        }
        catch (JsonException)
        {
            return null;
        }
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
        // Add padding if needed for standard base64 decoder
        switch (input.Length % 4)
        {
            case 2: input += "=="; break;
            case 3: input += "="; break;
        }
        return Convert.FromBase64String(input);
    }

    public static ConnectCode CodeFromHttpStatus(int statusCode) => statusCode switch
    {
        400 => ConnectCode.InvalidArgument,
        401 => ConnectCode.Unauthenticated,
        403 => ConnectCode.PermissionDenied,
        404 => ConnectCode.Unimplemented,
        408 => ConnectCode.DeadlineExceeded,
        429 => ConnectCode.Unavailable,
        431 => ConnectCode.Unavailable,
        502 => ConnectCode.Unavailable,
        503 => ConnectCode.Unavailable,
        504 => ConnectCode.Unavailable,
        _ => ConnectCode.Unknown,
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
