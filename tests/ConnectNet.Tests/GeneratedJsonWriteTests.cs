using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ConnectNet.Pooling;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Xunit;
using Type = System.Type;

namespace ConnectNet.Tests;

/// <summary>Generated writers must produce the same JSON document as Google.Protobuf.</summary>
public class GeneratedJsonWriteTests
{
    private static readonly TypeRegistry Registry = TypeRegistry.FromMessages(HelloRequest.Descriptor);
    private static readonly JsonFormatter Formatter = new(JsonFormatter.Settings.Default.WithTypeRegistry(Registry));
    private static readonly JsonCodec Codec = new(Registry);

    private static void AssertMatchesGoogle(IMessage message)
    {
        Assert.Equal(Canonical(Formatter.Format(message)), Canonical(Encoding.UTF8.GetString(Codec.SerializeToArray(message))));
    }

    public static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        Append(document.RootElement, builder);
        return builder.ToString();
    }

    private static void Append(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    builder.Append(property.Name).Append(':');
                    Append(property.Value, builder);
                    builder.Append(',');
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                foreach (var item in element.EnumerateArray())
                {
                    Append(item, builder);
                    builder.Append(',');
                }
                builder.Append(']');
                break;
            case JsonValueKind.Number:
                builder.Append(element.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                break;
            case JsonValueKind.String:
                builder.Append('"').Append(element.GetString()).Append('"');
                break;
            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    [Theory]
    [InlineData(typeof(Scalars))]
    [InlineData(typeof(Containers))]
    [InlineData(typeof(Containers.Types.Nested))]
    [InlineData(typeof(Presence))]
    [InlineData(typeof(WellKnown))]
    [InlineData(typeof(Renames))]
    public void GeneratedShapes_ImplementTheInterface(Type type)
    {
        Assert.True(typeof(IConnectJsonMessage).IsAssignableFrom(type));
    }

    [Fact]
    public void UnsupportedShapes_AreLeftToGoogle()
    {
        Assert.False(typeof(IConnectJsonMessage).IsAssignableFrom(typeof(NotGenerated)));
        AssertMatchesGoogle(new NotGenerated { NullValue = NullValue.NullValue, RWrapper = { 1, 2 }, MWrapper = { ["a"] = "b" } });
    }

    [Fact]
    public void EmptyMessages_MatchGoogle()
    {
        AssertMatchesGoogle(new Scalars());
        AssertMatchesGoogle(new Containers());
        AssertMatchesGoogle(new Presence());
        AssertMatchesGoogle(new WellKnown());
    }

    public static IEnumerable<object[]> ScalarCases()
    {
        Scalars[] cases =
        {
            new() { FDouble = 1.5, FFloat = 2.5f, FInt32 = -1, FInt64 = long.MinValue, FUint32 = uint.MaxValue, FUint64 = ulong.MaxValue },
            new() { FSint32 = int.MinValue, FSint64 = long.MaxValue, FFixed32 = 7, FFixed64 = 8, FSfixed32 = -9, FSfixed64 = -10 },
            new() { FBool = true, FString = "text", FBytes = ByteString.CopyFrom(0, 1, 254, 255), FEnum = TopEnum.TopOne },
            new() { FDouble = double.NaN, FFloat = float.NaN },
            new() { FDouble = double.PositiveInfinity, FFloat = float.NegativeInfinity },
            new() { FDouble = double.MaxValue, FFloat = float.Epsilon },
            new() { FDouble = -0.0, FFloat = -0.0f },
            new() { FDouble = 1e-300, FFloat = 1.17549435E-38f },
            new() { FEnum = (TopEnum)99, FAliased = AliasedEnum.AliasFirst },
            new() { FEnum = (TopEnum)(-1) },
            new() { CustomName = "renamed", SnakeCase2Digits = "x" },
            new() { FString = "\"quotes\\ and\tcontrol\u0001 <html> +'&" },
            new() { FString = "日本語 😀 \u00e9 \u2028\u2029" },
            new() { FBytes = ByteString.CopyFrom(new byte[1000]) },
        };
        return cases.Select(c => new object[] { c });
    }

    [Theory]
    [MemberData(nameof(ScalarCases))]
    public void Scalars_MatchGoogle(Scalars message) => AssertMatchesGoogle(message);

    [Fact]
    public void LoneSurrogate_MatchesBinaryEncodingInsteadOfThrowing()
    {
        var json = Encoding.UTF8.GetString(Codec.SerializeToArray(new Scalars { FString = "a\ud83d" }));
        Assert.Equal("a�", JsonDocument.Parse(json).RootElement.GetProperty("fString").GetString());
    }

    [Fact]
    public void Containers_MatchGoogle()
    {
        AssertMatchesGoogle(new Containers
        {
            RInt32 = { 1, -2, 0 },
            RInt64 = { long.MaxValue, 0 },
            RDouble = { 0, double.NaN, double.NegativeInfinity },
            RString = { "", "a" },
            RBytes = { ByteString.Empty, ByteString.CopyFromUtf8("b") },
            REnum = { TopEnum.TopZero, TopEnum.TopOne, (TopEnum)5 },
            RNested = { new Containers.Types.Nested(), new Containers.Types.Nested { Value = 1, Kind = Containers.Types.Nested.Types.NestedEnum.NestedOne } },
            MString = { ["a"] = 0, ["\"esc\n"] = 1, ["日本語"] = 2 },
            MInt32 = { [-1] = "neg", [0] = "" },
            MInt64 = { [long.MinValue] = true, [1] = false },
            MUint32 = { [uint.MaxValue] = double.PositiveInfinity },
            MUint64 = { [ulong.MaxValue] = ByteString.CopyFromUtf8("x") },
            MBool = { [true] = TopEnum.TopOne, [false] = TopEnum.TopZero },
            MNested = { ["k"] = new Containers.Types.Nested { Child = new Containers.Types.Nested { Value = 2 } } },
            Nested = new Containers.Types.Nested(),
            Imported = new HelloRequest { Name = "imported" },
        });
    }

    [Fact]
    public void Presence_MatchesGoogle()
    {
        AssertMatchesGoogle(new Presence { OInt32 = 0, OString = "", OEnum = TopEnum.TopZero, OBool = false });
        AssertMatchesGoogle(new Presence { CInt32 = 0 });
        AssertMatchesGoogle(new Presence { CString = "" });
        AssertMatchesGoogle(new Presence { CMessage = new Containers.Types.Nested() });
        AssertMatchesGoogle(new Presence { CWrapper = 0 });
        AssertMatchesGoogle(new Presence { CEnum = TopEnum.TopZero });
    }

    [Fact]
    public void WellKnownTypes_MatchGoogle()
    {
        AssertMatchesGoogle(new WellKnown
        {
            Timestamp = Timestamp.FromDateTime(new DateTime(2026, 10, 31, 1, 2, 3, 500, DateTimeKind.Utc)),
            Duration = Duration.FromTimeSpan(TimeSpan.FromSeconds(1.5)),
            Struct = new Struct { Fields = { ["a"] = Value.ForNumber(1), ["b"] = Value.ForNull() } },
            Value = Value.ForNull(),
            List = new ListValue { Values = { Value.ForString("x"), Value.ForBool(true) } },
            Any = Any.Pack(new HelloRequest { Name = "packed" }),
            RTimestamp = { Timestamp.FromDateTime(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)) },
            MDuration = { ["d"] = Duration.FromTimeSpan(TimeSpan.FromMinutes(1)) },
        });
    }

    [Fact]
    public void Wrappers_IncludingDefaults_MatchGoogle()
    {
        AssertMatchesGoogle(new WellKnown { WInt32 = 0, WInt64 = 0, WUint32 = 0, WUint64 = 0, WBool = false, WString = "", WBytes = ByteString.Empty, WDouble = 0, WFloat = 0 });
        AssertMatchesGoogle(new WellKnown { WInt32 = -1, WInt64 = long.MaxValue, WUint32 = 1, WUint64 = ulong.MaxValue, WBool = true, WString = "s", WBytes = ByteString.CopyFromUtf8("b"), WDouble = double.NaN, WFloat = float.PositiveInfinity });
    }

    [Fact]
    public void RenamedMembers_MatchGoogle()
    {
        AssertMatchesGoogle(new Renames { Types_ = "a", Descriptor_ = "b", ToString_ = "c", Equals_ = "d", Parser_ = "e", Renames_ = "h", None = "f" });
        AssertMatchesGoogle(new Renames { Class = "g" });
    }

    [Fact]
    public void OutputIsCompact()
    {
        var json = Encoding.UTF8.GetString(Codec.SerializeToArray(new Containers { RInt32 = { 1, 2 } }));
        Assert.Equal("{\"rInt32\":[1,2]}", json);
    }
}
