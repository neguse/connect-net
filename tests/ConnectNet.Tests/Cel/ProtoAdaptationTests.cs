using System.Collections.Generic;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation.Cel;
using ConnectNet.Validation.Cel.Checker;
using ConnectNet.Validation.Cel.Protobuf;
using ConnectNet.Validation.Cel.Runtime;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests.Cel;

public class ProtoAdaptationTests
{
    private static readonly ProtoTypeProvider Provider = new(new[] { ValidationTestReflection.Descriptor });

    private static CelEnvironment NewEnv(ProtoTypeProvider? provider = null, string container = "validation_test")
    {
        provider ??= Provider;
        return new CelEnvironment(new Container(container), provider, provider)
            .AddStandardLibrary()
            .AddStringsExtension()
            .AddVariable("msg", CelType.Message("validation_test.NestedTestMessage"))
            .AddVariable("e", CelType.Message("validation_test.EnumTestMessage"));
    }

    /// <summary>Evaluates without the checker so ad hoc bindings and dynamic comparisons are exercised.</summary>
    private static CelValue Eval(string expr, params (string Name, CelValue Value)[] bindings)
    {
        var dict = new Dictionary<string, CelValue>();
        foreach (var (name, value) in bindings) dict[name] = value;
        var env = NewEnv();
        var parsed = env.Parse(expr);
        Assert.True(parsed.IsSuccess, parsed.Errors.FormatAll());
        return env.PlanUnchecked(parsed).Evaluate(Activation.Of(dict));
    }

    private static CelValue EvalChecked(string expr, params (string Name, CelValue Value)[] bindings)
    {
        var dict = new Dictionary<string, CelValue>();
        foreach (var (name, value) in bindings) dict[name] = value;
        return NewEnv().Compile(expr).Evaluate(Activation.Of(dict));
    }

    private static CelValue Msg(Google.Protobuf.IMessage m) => Provider.ToCelValue(m);

    [Fact]
    public void ProviderExposesMessagesFieldsAndEnums()
    {
        Assert.True(Provider.HasMessage("validation_test.NestedTestMessage"));
        Assert.True(Provider.HasMessage("google.protobuf.Timestamp"));
        Assert.False(Provider.HasMessage("validation_test.Nope"));
        Assert.Equal("list(validation_test.StringTestMessage)", Provider.FindFieldType("validation_test.NestedTestMessage", "items")!.ToString());
        Assert.Equal("map(string, string)", Provider.FindFieldType("validation_test.MapTestMessage", "labels")!.ToString());
        Assert.Equal("int", Provider.FindFieldType("validation_test.EnumTestMessage", "status")!.ToString());
        Assert.Equal("timestamp", Provider.FindFieldType("validation_test.TimestampTestMessage", "created_at")!.ToString());
        Assert.Null(Provider.FindFieldType("validation_test.NestedTestMessage", "nope"));
        Assert.True(Provider.TryFindEnumValue("validation_test.Status.STATUS_ACTIVE", out var active));
        Assert.Equal(1, active);
        Assert.Equal("type(validation_test.NestedTestMessage)", Provider.FindType("validation_test.NestedTestMessage") is { } t ? CelType.TypeOf(t).ToString() : "");
    }

    [Fact]
    public void SelectsFieldsWithDefaultsAndPresence()
    {
        var msg = Msg(new NestedTestMessage { Inner = new StringTestMessage { Name = "abc" } });
        Assert.Equal("\"abc\"", EvalChecked("msg.inner.name", ("msg", msg)).ToString());
        Assert.Equal("\"abc\"", Eval("msg.inner.name", ("msg", msg)).ToString());
        Assert.Equal("[]", Eval("msg.items", ("msg", msg)).ToString());
        Assert.Equal("true", Eval("has(msg.inner)", ("msg", msg)).ToString());
        Assert.Equal("false", Eval("has(msg.items)", ("msg", msg)).ToString());
        Assert.Equal("true", Eval("has(msg.inner.name)", ("msg", msg)).ToString());
        Assert.Equal("false", Eval("has(msg.inner.email)", ("msg", msg)).ToString());

        var empty = Msg(new NestedTestMessage());
        Assert.Equal("false", Eval("has(msg.inner)", ("msg", empty)).ToString());
        // An unset message field reads as its default instance, not null.
        Assert.Equal("\"\"", Eval("msg.inner.name", ("msg", empty)).ToString());
        Assert.Equal("validation_test.NestedTestMessage", Eval("type(msg)", ("msg", empty)).ToString());
    }

    [Fact]
    public void UndefinedFieldsAreErrors()
    {
        var env = NewEnv();
        var parsed = env.Parse("msg.nope");
        var program = env.PlanUnchecked(parsed);
        var result = program.Evaluate(Activation.Of(new Dictionary<string, CelValue> { ["msg"] = Msg(new NestedTestMessage()) }));
        Assert.Contains("no such field 'nope'", Assert.IsType<ErrorValue>(result).Message);
        var ex = Assert.Throws<CelCompilationException>(() => env.Compile("msg.nope"));
        Assert.Contains("undefined field 'nope'", ex.Message);
    }

    [Fact]
    public void ConstructsMessagesWithRangeChecks()
    {
        var value = Eval("NestedTestMessage{inner: StringTestMessage{name: 'x'}, items: [StringTestMessage{code: 'c'}]}");
        var created = Assert.IsType<ProtoMessageValue>(value);
        var nested = Assert.IsType<NestedTestMessage>(created.Message);
        Assert.Equal("x", nested.Inner.Name);
        Assert.Equal("c", Assert.Single(nested.Items).Code);

        Assert.Equal("30", Eval("NumericTestMessage{age: 30}.age").ToString());
        var error = Assert.IsType<ErrorValue>(Eval("NumericTestMessage{age: 5000000000}"));
        Assert.Contains("range error", error.Message);
        Assert.Equal("[]", Eval("NestedTestMessage{inner: null}.items").ToString());
        Assert.Equal("true", Eval("NestedTestMessage{inner: null} == NestedTestMessage{}").ToString());
    }

    [Fact]
    public void ConvertsWellKnownTypesBothWays()
    {
        var ts = Msg(new TimestampTestMessage { CreatedAt = Timestamp.FromDateTime(new System.DateTime(2009, 2, 13, 23, 31, 30, System.DateTimeKind.Utc)) });
        Assert.Equal("2009", Eval("t.created_at.getFullYear()", ("t", ts)).ToString());
        var built = Eval("TimestampTestMessage{created_at: timestamp('2009-02-13T23:31:30Z')}");
        Assert.Equal(1234567890, Assert.IsType<TimestampTestMessage>(Assert.IsType<ProtoMessageValue>(built).Message).CreatedAt.Seconds);
        var dur = Eval("DurationTestMessage{timeout: duration('1.5s')}.timeout");
        Assert.Equal("duration(\"1.5s\")", dur.ToString());
        Assert.Equal("true", Eval("DurationTestMessage{timeout: null} == DurationTestMessage{}").ToString());
    }

    [Fact]
    public void MapsAndRepeatedFieldsRoundTrip()
    {
        var m = Eval("MapTestMessage{labels: {'a': 'x', 'b': 'y'}}");
        var labels = Assert.IsType<MapTestMessage>(Assert.IsType<ProtoMessageValue>(m).Message).Labels;
        Assert.Equal("y", labels["b"]);
        Assert.Equal("\"x\"", Eval("m.labels['a']", ("m", m)).ToString());
        Assert.Equal("true", Eval("'a' in m.labels && m.labels.size() == 2", ("m", m)).ToString());
        Assert.Equal("[\"a\", \"b\"]", Eval("m.labels.map(k, k)", ("m", m)).ToString());
        Assert.Equal("[1, 2]", Eval("RepeatedTestMessage{scores: [1, 2]}.scores").ToString());
        Assert.Equal("true", Eval("RepeatedTestMessage{tags: ['a']}.tags.all(t, t.size() == 1)").ToString());
    }

    [Fact]
    public void EnumsAreIntsByDefault()
    {
        var e = Msg(new EnumTestMessage { Status = Status.Active });
        Assert.Equal("1", Eval("e.status", ("e", e)).ToString());
        Assert.Equal("true", Eval("e.status == Status.STATUS_ACTIVE", ("e", e)).ToString());
        Assert.Equal("int", Eval("type(Status.STATUS_ACTIVE)").ToString());
        var built = Assert.IsType<EnumTestMessage>(Assert.IsType<ProtoMessageValue>(Eval("EnumTestMessage{status: 1}")).Message);
        Assert.Equal(Status.Active, built.Status);
    }

    [Fact]
    public void StrongEnumModeIsExplicit()
    {
        var strong = new ProtoTypeProvider(new[] { ValidationTestReflection.Descriptor }, EnumMode.Strong);
        var env = NewEnv(strong);
        CelValue Run(string expr) => env.Compile(expr).Evaluate(Activation.Of(new Dictionary<string, CelValue>
        {
            ["e"] = strong.ToCelValue(new EnumTestMessage { Status = Status.Active }),
        }));
        Assert.Equal("validation_test.Status(1)", Run("e.status").ToString());
        Assert.Equal("validation_test.Status", Run("type(e.status)").ToString());
        Assert.Equal("true", Run("e.status == Status.STATUS_ACTIVE").ToString());
        Assert.Equal("1", Run("int(e.status)").ToString());
        Assert.Equal("validation_test.Status(1)", Run("Status(1)").ToString());
        Assert.Equal("validation_test.Status(1)", Run("Status('STATUS_ACTIVE')").ToString());
        Assert.IsType<ErrorValue>(Run("Status('NOPE')"));
        Assert.Throws<CelCompilationException>(() => env.Compile("e.status == 1"));
    }

    [Fact]
    public void EqualityFollowsProtoSemantics()
    {
        Assert.Equal("true", Eval("StringTestMessage{name: 'a'} == StringTestMessage{name: 'a'}").ToString());
        Assert.Equal("false", Eval("StringTestMessage{name: 'a'} == StringTestMessage{name: 'b'}").ToString());
        Assert.Equal("false", Eval("StringTestMessage{} == NumericTestMessage{}").ToString());
        Assert.Equal("false", Eval("NumericTestMessage{score: 0.0/0.0} == NumericTestMessage{score: 0.0/0.0}").ToString());
        Assert.Equal("false", Eval("StringTestMessage{} == null").ToString());
    }

    [Fact]
    public void JsonAndAnyConversions()
    {
        var any = Any.Pack(new StringTestMessage { Name = "packed" });
        var value = Provider.ToCelValue(any);
        Assert.Equal("\"packed\"", Eval("a.name", ("a", value)).ToString());
        Assert.IsType<ErrorValue>(Provider.ToCelValue(new Any { TypeUrl = "type.googleapis.com/no.Such" }));

        var json = Provider.ToCelValue(Value.ForStruct(new Struct { Fields = { ["k"] = Value.ForNumber(1), ["l"] = Value.ForList(Value.ForBool(true)) } }));
        Assert.Equal("{\"k\": 1, \"l\": [true]}", json.ToString());
        Assert.Equal("null", Provider.ToCelValue(Value.ForNull()).ToString());
        Assert.Equal("1", Provider.ToCelValue(new Int64Value { Value = 1 }).ToString());
    }

    [Fact]
    public void StrictEqualityOfTypeProvider()
    {
        // Descriptors from one file set resolve to the same provider instance behavior.
        var other = new ProtoTypeProvider(new[] { ValidationTestReflection.Descriptor });
        Assert.True(other.HasMessage("validation_test.StringTestMessage"));
        Assert.NotNull(other.FindMessageFactory("validation_test.StringTestMessage"));
        Assert.Null(other.FindMessageFactory("validation_test.Nope"));
    }
}
