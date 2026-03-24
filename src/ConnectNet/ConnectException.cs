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
                writer.WriteString("value", Convert.ToBase64String(detail.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ConnectException FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var code = root.TryGetProperty("code", out var codeProp)
            ? CodeFromString(codeProp.GetString() ?? "")
            : ConnectCode.Unknown;

        var message = root.TryGetProperty("message", out var msgProp)
            ? msgProp.GetString() ?? ""
            : "";

        var details = new List<ConnectErrorDetail>();
        if (root.TryGetProperty("details", out var detailsProp) && detailsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in detailsProp.EnumerateArray())
            {
                var type = d.GetProperty("type").GetString() ?? "";
                var value = Convert.FromBase64String(d.GetProperty("value").GetString() ?? "");
                details.Add(new ConnectErrorDetail(type, value));
            }
        }

        return new ConnectException(code, message, details);
    }

    public static int ToHttpStatus(ConnectCode code) => code switch
    {
        ConnectCode.Canceled => 408,
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
