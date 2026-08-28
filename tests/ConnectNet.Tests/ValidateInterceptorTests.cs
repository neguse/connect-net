using System.Linq;
using System.Threading.Tasks;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using ConnectNet.Validation.Interceptors;
using Google.Protobuf;
using Xunit;

namespace ConnectNet.Tests;

public class ValidateInterceptorTests
{
    private readonly ValidateInterceptor _interceptor = new(new ProtoValidator());

    [Fact]
    public async Task InvalidRequest_ThrowsConnectException()
    {
        var context = new UnaryServerContext(
            "/test.TestService/Test",
            new StringTestMessage { Name = "Al", Email = "alice@example.com", Code = "abc" },
            new ConnectContext());

        var ex = await Assert.ThrowsAsync<ConnectException>(
            () => _interceptor.InterceptUnaryAsync(context, _ => Task.FromResult<IMessage>(new StringTestMessage())));

        Assert.Equal(ConnectCode.InvalidArgument, ex.Code);
        Assert.Contains("validation failed", ex.Message);
        Assert.NotEmpty(ex.Details);
        Assert.Equal("buf.validate.Violations", ex.Details[0].Type);
    }

    [Fact]
    public async Task ValidRequest_PassesThrough()
    {
        var request = new StringTestMessage
        {
            Name = "Alice",
            Email = "alice@example.com",
            Code = "abc",
            PrefixVal = "pre_value",
            PatternVal = "lowercase",
        };
        var context = new UnaryServerContext(
            "/test.TestService/Test",
            request,
            new ConnectContext());

        var called = false;
        var response = new StringTestMessage { Name = "response" };

        var result = await _interceptor.InterceptUnaryAsync(context, _ =>
        {
            called = true;
            return Task.FromResult<IMessage>(response);
        });

        Assert.True(called);
        Assert.Same(response, result);
    }

    [Fact]
    public void ToErrorDetails_SetsFieldPath()
    {
        var violations = new[] { new Violation("name", "string.min_len", "too short") };

        var details = ValidateInterceptor.ToErrorDetails(violations, truncated: false).ToArray();

        Assert.Single(details);
        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);
        Assert.Single(protoViolations.Violations_);
        var v = protoViolations.Violations_[0];
        Assert.Equal("string.min_len", v.RuleId);
        Assert.NotNull(v.Field);
        Assert.Single(v.Field.Elements);
        Assert.Equal("name", v.Field.Elements[0].FieldName);
    }

    [Fact]
    public void ToErrorDetails_SetsNestedFieldPath()
    {
        var violations = new[] { new Violation("inner.name", "string.min_len", "too short") };

        var details = ValidateInterceptor.ToErrorDetails(violations, truncated: false).ToArray();

        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);
        var v = protoViolations.Violations_[0];
        Assert.NotNull(v.Field);
        Assert.Equal(2, v.Field.Elements.Count);
        Assert.Equal("inner", v.Field.Elements[0].FieldName);
        Assert.Equal("name", v.Field.Elements[1].FieldName);
    }

    [Fact]
    public void ToFieldPath_ParsesRepeatedIndexSubscript()
    {
        var fieldPath = ValidateInterceptor.ToFieldPath("items[3].name");

        Assert.Equal(2, fieldPath.Elements.Count);
        Assert.Equal("items", fieldPath.Elements[0].FieldName);
        Assert.Equal(Buf.Validate.FieldPathElement.SubscriptOneofCase.Index, fieldPath.Elements[0].SubscriptCase);
        Assert.Equal(3ul, fieldPath.Elements[0].Index);
        Assert.Equal("name", fieldPath.Elements[1].FieldName);
    }

    [Fact]
    public void ToFieldPath_ParsesStringKeySubscript()
    {
        var fieldPath = ValidateInterceptor.ToFieldPath("entries[\"a.b\"]");

        Assert.Single(fieldPath.Elements);
        Assert.Equal("entries", fieldPath.Elements[0].FieldName);
        Assert.Equal("a.b", fieldPath.Elements[0].StringKey);
    }

    [Fact]
    public void ToErrorDetails_PropagatesForKey()
    {
        var violation = new Violation("entries[\"a\"]", "string.min_len", "too short")
        {
            ForKey = true,
        };

        var details = ValidateInterceptor.ToErrorDetails(new[] { violation }, truncated: false).ToArray();
        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);

        Assert.True(protoViolations.Violations_[0].ForKey);
    }

    [Fact]
    public async Task MessageWithoutConstraints_PassesThrough()
    {
        var context = new UnaryServerContext(
            "/test.TestService/SayHello",
            new HelloRequest { Name = "anything" },
            new ConnectContext());

        var called = false;
        var response = new HelloResponse { Message = "Hello" };

        var result = await _interceptor.InterceptUnaryAsync(context, _ =>
        {
            called = true;
            return Task.FromResult<IMessage>(response);
        });

        Assert.True(called);
        Assert.Same(response, result);
    }

    [Fact]
    public void ToErrorDetails_CapsHowManyViolationsAreSerialized()
    {
        var violations = Enumerable.Range(0, 10_000)
            .Select(i => new Violation($"items[{i}]", "string.min_len", "too short"))
            .ToArray();

        var details = ValidateInterceptor.ToErrorDetails(violations, truncated: false).ToArray();

        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);
        // The cap bounds the real violations; the truncation marker rides after it so the
        // remote caller can always see that the list is incomplete.
        Assert.Equal(ValidateInterceptor.MaxSerializedViolations + 1, protoViolations.Violations_.Count);
        Assert.Equal(ValidateInterceptor.TruncationRuleId, protoViolations.Violations_[^1].RuleId);
        Assert.Equal(ValidateInterceptor.MaxSerializedViolations,
            protoViolations.Violations_.Count(v => v.RuleId == "string.min_len"));
    }

    [Fact]
    public void ToErrorDetails_TruncatedResult_CarriesTheMarkerOnTheWire()
    {
        // At default settings the validator's limit and the serialization cap are equal, so
        // the marker must survive the cap rather than be the entry it drops.
        var violations = Enumerable.Range(0, ValidateInterceptor.MaxSerializedViolations)
            .Select(i => new Violation($"items[{i}]", "string.min_len", "too short"))
            .ToArray();

        var details = ValidateInterceptor.ToErrorDetails(violations, truncated: true).ToArray();

        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);
        Assert.Equal(ValidateInterceptor.MaxSerializedViolations + 1, protoViolations.Violations_.Count);
        Assert.Equal(ValidateInterceptor.TruncationRuleId, protoViolations.Violations_[^1].RuleId);
    }

    [Fact]
    public void ToErrorDetails_CompleteResult_HasNoMarker()
    {
        var violations = new[] { new Violation("name", "string.min_len", "too short") };

        var details = ValidateInterceptor.ToErrorDetails(violations, truncated: false).ToArray();

        var protoViolations = Buf.Validate.Violations.Parser.ParseFrom(details[0].Value);
        Assert.DoesNotContain(protoViolations.Violations_, v => v.RuleId == ValidateInterceptor.TruncationRuleId);
    }

    [Fact]
    public void FormatMessage_CountsOnlyRealViolations()
    {
        var violations = new[]
        {
            new Violation("a", "string.min_len", "too short"),
            new Violation("b", "string.min_len", "too short"),
        };

        Assert.Equal("validation failed: too short (and 1 more)",
            ValidateInterceptor.FormatMessage(violations, truncated: false));
        Assert.Equal("validation failed: too short (and 1 more) [stopped at the violation limit]",
            ValidateInterceptor.FormatMessage(violations, truncated: true));
        Assert.Equal("validation failed: too short",
            ValidateInterceptor.FormatMessage(new[] { violations[0] }, truncated: false));
    }
}
