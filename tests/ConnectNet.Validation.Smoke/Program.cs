using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Buf.Validate;
using ConnectNet;
using ConnectNet.Validation;
using ConnectNet.Validation.Interceptors;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Smoke;

// Exercises the packed ConnectNet.Validation package end to end. Any mismatch throws, so the
// process exit code is the verdict.

var validator = new ProtoValidator();
var now = DateTime.UtcNow;

var valid = new Reservation
{
    Start = Timestamp.FromDateTime(now),
    End = Timestamp.FromDateTime(now.AddHours(1)),
    Email = "guest@example.com",
};
valid.Tags.AddRange(new[] { "window", "quiet" });
valid.Seats["12A"] = 1;

Expect(validator.Validate(valid).IsValid, "a valid reservation passes");

var invalid = new Reservation
{
    Start = Timestamp.FromDateTime(now.AddHours(1)),
    End = Timestamp.FromDateTime(now),
    Email = "not an email",
};
invalid.Tags.AddRange(new[] { "same", "same", "far-too-long-tag" });
invalid.Seats[""] = 1;

var result = validator.Validate(invalid);
Expect(!result.IsValid, "an invalid reservation fails");
var ids = result.Violations.Select(v => v.ConstraintId).OrderBy(id => id).ToArray();
ExpectEqual(new[] { "repeated.unique", "reservation.order", "string.email", "string.max_len", "string.min_len" }, ids, "rule ids");

var order = result.Violations.Single(v => v.ConstraintId == "reservation.order");
ExpectEqual("end must be after start", order.Message, "cross-field CEL message");
ExpectEqual("", order.FieldPath, "message-level rule has no field path");

var item = result.Violations.Single(v => v.ConstraintId == "string.max_len");
ExpectEqual("tags[2]", item.FieldPath, "repeated item path");

var key = result.Violations.Single(v => v.ConstraintId == "string.min_len");
ExpectEqual("seats[\"\"]", key.FieldPath, "map key path");
Expect(key.ForKey, "map key violation is marked for_key");

// The interceptor turns the violations into an InvalidArgument error carrying the
// buf.validate.Violations detail, which a client decodes with the same package.
var interceptor = new ValidateInterceptor(validator);
ConnectException? error = null;
try
{
    await interceptor.InterceptUnaryAsync(
        new UnaryServerContext("smoke.Reservations/Reserve", invalid, new ConnectContext()),
        _ => Task.FromResult<IMessage>(new Reservation()));
}
catch (ConnectException e)
{
    error = e;
}
Expect(error != null, "the interceptor rejects the request");
ExpectEqual(ConnectCode.InvalidArgument, error!.Code, "error code");
var detail = error.Details.Single(d => d.Type == "buf.validate.Violations");
var decoded = Violations.Parser.ParseFrom(detail.Value);
ExpectEqual(5, decoded.Violations_.Count, "decoded violation count");
var decodedKey = decoded.Violations_.Single(v => v.RuleId == "string.min_len");
ExpectEqual("seats", decodedKey.Field.Elements[0].FieldName, "structured field path survives the wire");
ExpectEqual("", decodedKey.Field.Elements[0].StringKey, "map key subscript survives the wire");
ExpectEqual(new[] { "map", "keys", "string", "min_len" }, decodedKey.Rule.Elements.Select(e => e.FieldName).ToArray(), "structured rule path");

var passed = await interceptor.InterceptUnaryAsync(
    new UnaryServerContext("smoke.Reservations/Reserve", valid, new ConnectContext()),
    _ => Task.FromResult<IMessage>(valid));
Expect(ReferenceEquals(passed, valid), "the interceptor lets a valid request through");

Console.WriteLine("ConnectNet.Validation package smoke test passed");
return 0;

static void Expect(bool condition, string what)
{
    if (!condition)
        throw new InvalidOperationException("smoke test failed: " + what);
}

static void ExpectEqual<T>(T expected, T actual, string what)
{
    var equal = expected is IEnumerable<string> es && actual is IEnumerable<string> @as
        ? es.SequenceEqual(@as)
        : Equals(expected, actual);
    if (!equal)
        throw new InvalidOperationException($"smoke test failed: {what}: expected {Render(expected)}, got {Render(actual)}");
}

static string Render<T>(T value) => value is IEnumerable<string> list ? "[" + string.Join(", ", list) + "]" : value?.ToString() ?? "null";
