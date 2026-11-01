using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ConnectNet.Pooling;
using ConnectNet.Tests.Proto;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>Generated readers must accept and reject the same documents as Google.Protobuf.</summary>
public class GeneratedJsonReadTests
{
    private static readonly TypeRegistry Registry = TypeRegistry.FromMessages(HelloRequest.Descriptor);
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default
        .WithIgnoreUnknownFields(true)
        .WithRecursionLimit(JsonCodec.DefaultRecursionLimit)
        .WithTypeRegistry(Registry));
    private static readonly JsonCodec Codec = new(Registry);

    private static void AssertMatchesGoogle<T>(string json) where T : class, IMessage<T>, new()
    {
        T? expected = null;
        Exception? expectedError = null;
        try { expected = Parser.Parse<T>(json); } catch (Exception ex) { expectedError = ex; }

        T? actual = null;
        Exception? actualError = null;
        try { actual = Codec.Deserialize<T>(Encoding.UTF8.GetBytes(json)); } catch (Exception ex) { actualError = ex; }

        Assert.True((expectedError == null) == (actualError == null),
            $"{json}\n google: {expectedError?.GetType().Name ?? "ok"} {expectedError?.Message}\n generated: {actualError?.GetType().Name ?? "ok"} {actualError?.Message}");
        if (expected != null) Assert.Equal(expected, actual);
        if (actualError != null)
        {
            Assert.True(actualError is FormatException or InvalidProtocolBufferException or InvalidJsonException,
                $"{json} threw {actualError.GetType().Name}, which the server would report as internal");
        }
    }

    public static IEnumerable<object[]> Documents(params string[] documents) => documents.Select(d => new object[] { d });

    public static IEnumerable<object[]> ScalarDocuments() => Documents(
        "{}", " { } ", "", "null", "[]", "1", "\"x\"", "{} {}", "{", "{\"fInt32\":1,}", "{\"fInt32\":1}x", "// c\n{}",
        // names
        "{\"fInt32\":1}", "{\"f_int32\":1}", "{\"FInt32\":1}", "{\"renamed\":\"a\"}", "{\"custom_name\":\"a\"}", "{\"customName\":\"a\"}",
        "{\"snakeCase2Digits\":\"a\"}", "{\"snake_case_2_digits\":\"a\"}", "{\"f\\u0049nt32\":1}", "{\"unknown\":{\"a\":[1,2,{}]},\"fInt32\":1}",
        "{\"fInt32\":1,\"fInt32\":2}", "{\"fInt32\":1,\"f_int32\":2}", "{\"fInt32\":null,\"fInt32\":2}",
        // integers
        "{\"fInt32\":2147483647}", "{\"fInt32\":2147483648}", "{\"fInt32\":-2147483648}", "{\"fInt32\":-2147483649}",
        "{\"fInt32\":1.0}", "{\"fInt32\":1.5}", "{\"fInt32\":1e3}", "{\"fInt32\":\"12\"}", "{\"fInt32\":\"1.0\"}", "{\"fInt32\":\"1.5\"}", "{\"fInt32\":\"1e3\"}",
        "{\"fInt32\":\"+1\"}", "{\"fInt32\":\"01\"}", "{\"fInt32\":\"-01\"}", "{\"fInt32\":\"\"}", "{\"fInt32\":\" 1\"}", "{\"fInt32\":\"-\"}", "{\"fInt32\":\"abc\"}",
        "{\"fInt32\":true}", "{\"fInt32\":[]}", "{\"fInt32\":{}}", "{\"fInt32\":null}", "{\"fUint32\":-1}", "{\"fUint32\":4294967295}", "{\"fUint32\":4294967296}",
        "{\"fInt64\":\"0\"}", "{\"fInt64\":\"-0\"}", "{\"fInt64\":\"1\"}", "{\"fInt64\":\"-1\"}", "{\"fInt64\":\"01\"}", "{\"fInt64\":\"-01\"}", "{\"fInt64\":\"+1\"}", "{\"fInt64\":\"1e3\"}", "{\"fInt64\":\"1.0\"}", "{\"fInt64\":\"-\"}", "{\"fInt64\":\"\"}", "{\"fInt64\":\"1 \"}", "{\"fInt64\":\" 1\"}", "{\"fInt64\":\"\\u0031\"}", "{\"fInt64\":\"12a\"}", "{\"fInt64\":\"18446744073709551615\"}", "{\"fInt64\":\"18446744073709551616\"}", "{\"fInt64\":\"-9223372036854775809\"}", "{\"fUint64\":\"0\"}", "{\"fUint64\":\"-0\"}", "{\"fUint64\":\"1\"}", "{\"fUint64\":\"-1\"}", "{\"fUint64\":\"01\"}", "{\"fUint64\":\"-01\"}", "{\"fUint64\":\"+1\"}", "{\"fUint64\":\"1e3\"}", "{\"fUint64\":\"1.0\"}", "{\"fUint64\":\"-\"}", "{\"fUint64\":\"\"}", "{\"fUint64\":\"1 \"}", "{\"fUint64\":\" 1\"}", "{\"fUint64\":\"\\u0031\"}", "{\"fUint64\":\"12a\"}", "{\"fUint64\":\"18446744073709551615\"}", "{\"fUint64\":\"18446744073709551616\"}", "{\"fUint64\":\"-9223372036854775809\"}", "{\"fInt64\":\"9223372036854775807\"}", "{\"fInt64\":\"9223372036854775808\"}", "{\"fInt64\":\"-9223372036854775808\"}", "{\"fInt64\":1}", "{\"fInt64\":1.0}",
        "{\"fUint64\":\"18446744073709551615\"}", "{\"fUint64\":\"18446744073709551616\"}", "{\"fUint64\":\"-1\"}", "{\"fSint64\":\"5\"}", "{\"fFixed64\":\"5\"}", "{\"fSfixed64\":\"-5\"}", "{\"fFixed32\":5}", "{\"fSfixed32\":-5}", "{\"fSint32\":-5}",
        // floats
        "{\"fDouble\":1.5}", "{\"fDouble\":\"1.5\"}", "{\"fDouble\":\"NaN\"}", "{\"fDouble\":\"Infinity\"}", "{\"fDouble\":\"-Infinity\"}", "{\"fDouble\":\"nan\"}",
        "{\"fDouble\":1e999}", "{\"fDouble\":\"1e999\"}", "{\"fDouble\":-1e999}", "{\"fDouble\":1e-999}", "{\"fDouble\":true}", "{\"fDouble\":null}", "{\"fDouble\":\"+1\"}", "{\"fDouble\":\"01\"}", "{\"fDouble\":\"\"}",
        "{\"fFloat\":1.5}", "{\"fFloat\":\"NaN\"}", "{\"fFloat\":\"-Infinity\"}", "{\"fFloat\":1e39}", "{\"fFloat\":-1e39}", "{\"fFloat\":\"1e39\"}", "{\"fFloat\":1e-50}", "{\"fFloat\":3.4028235e38}", "{\"fFloat\":3.41e38}", "{\"fFloat\":-3.4028235e38}",
        "{\"fDouble\":\".5\"}", "{\"fDouble\":\"-.5\"}", "{\"fDouble\":\"5.\"}", "{\"fDouble\":\"1E5\"}", "{\"fDouble\":\"infinity\"}", "{\"fDouble\":\"-nan\"}", "{\"fDouble\":\"-\"}", "{\"fDouble\":\"--1\"}", "{\"fDouble\":\"0\"}", "{\"fDouble\":\"-0\"}", "{\"fDouble\":\"0.5\"}", "{\"fInt32\":\"0\"}", "{\"fInt32\":\"-0\"}", "{\"fInt32\":\".0\"}", "{\"fInt32\":\"1.\"}",
        // bool, string, bytes
        "{\"fBool\":true}", "{\"fBool\":false}", "{\"fBool\":\"true\"}", "{\"fBool\":1}", "{\"fBool\":null}",
        "{\"fString\":\"a\"}", "{\"fString\":\"\\u65e5\\ud83d\\ude00\\n\"}", "{\"fString\":\"日本語😀\"}", "{\"fString\":1}", "{\"fString\":null}", "{\"fString\":true}", "{\"fString\":[]}", "{\"fString\":\"\\ud83d\"}",
        "{\"fBytes\":\"AAEC\"}", "{\"fBytes\":\"\"}", "{\"fBytes\":\"AA==\"}", "{\"fBytes\":\"AAE=\"}", "{\"fBytes\":\"=\"}", "{\"fBytes\":\"==\"}", "{\"fBytes\":\"====\"}", "{\"fBytes\":\"A===\"}", "{\"fBytes\":\"A\"}", "{\"fBytes\":\"AA\"}", "{\"fBytes\":\"AAE\"}", "{\"fBytes\":\"AAECAw\"}", "{\"fBytes\":\"-_-_\"}", "{\"fBytes\":\"AA=C\"}", "{\"fBytes\":\"\\u0041AEC\"}", "{\"fBytes\":\"AA\\u003D\\u003D\"}", "{\"fBytes\":\"AA EC\"}", "{\"fBytes\":\"AA\\nEC\"}", "{\"fBytes\":\" AAEC \"}", "{\"fBytes\":\"AAE=A\"}", "{\"fBytes\":\"====\"}", "{\"fBytes\":\"AAE=\"}", "{\"fBytes\":\"\"}", "{\"fBytes\":\"!!!\"}", "{\"fBytes\":\"-_-_\"}", "{\"fBytes\":\"AAE\"}", "{\"fBytes\":1}", "{\"fBytes\":null}", "{\"fBytes\":\"A\\u0041EC\"}",
        // enums
        "{\"fEnum\":\"TOP_ONE\"}", "{\"fEnum\":1}", "{\"fEnum\":99}", "{\"fEnum\":\"NOPE\"}", "{\"fEnum\":\"top_one\"}", "{\"fEnum\":1.5}", "{\"fEnum\":true}", "{\"fEnum\":null}", "{\"fEnum\":\"1\"}", "{\"fEnum\":\"TOP\\u005fONE\"}",
        "{\"fAliased\":\"ALIAS_FIRST\"}", "{\"fAliased\":\"ALIAS_SECOND\"}");

    [Theory]
    [MemberData(nameof(ScalarDocuments))]
    public void Scalars_MatchGoogle(string json) => AssertMatchesGoogle<Scalars>(json);

    public static IEnumerable<object[]> ContainerDocuments() => Documents(
        "{\"rInt32\":[1,2,3]}", "{\"rInt32\":[]}", "{\"rInt32\":null}", "{\"rInt32\":1}", "{\"rInt32\":[null]}", "{\"rInt32\":[\"1\",1.0]}", "{\"rInt32\":[[1]]}", "{\"rInt32\":[1],\"rInt32\":[2]}",
        "{\"rInt64\":[\"1\",2]}", "{\"rDouble\":[\"NaN\",1]}", "{\"rString\":[\"a\",null]}", "{\"rBytes\":[\"AAEC\"]}",
        "{\"rEnum\":[\"TOP_ONE\",1,\"NOPE\",5]}", "{\"rEnum\":[null]}",
        "{\"rNested\":[{},{\"value\":1}]}", "{\"rNested\":[null]}", "{\"rNested\":{}}",
        "{\"mString\":{\"a\":1,\"b\":2}}", "{\"mString\":{\"a\":null}}", "{\"mString\":[]}", "{\"mString\":null}", "{\"mString\":{}}", "{\"mString\":{\"a\":1,\"a\":2}}",
        "{\"mInt32\":{\"1\":\"a\",\"-2\":\"b\"}}", "{\"mInt32\":{\"x\":\"a\"}}", "{\"mInt32\":{\"1.0\":\"a\"}}", "{\"mInt32\":{\"1.5\":\"a\"}}", "{\"mInt32\":{\"2147483648\":\"a\"}}", "{\"mInt32\":{\"01\":\"a\"}}",
        "{\"mInt64\":{\"-9223372036854775808\":true}}", "{\"mUint32\":{\"4294967295\":1.5}}", "{\"mUint32\":{\"-1\":1.5}}", "{\"mUint64\":{\"18446744073709551615\":\"AA==\"}}",
        "{\"mBool\":{\"true\":\"TOP_ONE\",\"false\":0}}", "{\"mBool\":{\"True\":0}}", "{\"mBool\":{\"1\":0}}", "{\"mBool\":{\"true\":\"NOPE\"}}",
        "{\"mNested\":{\"k\":{\"value\":1}}}", "{\"mNested\":{\"k\":null}}",
        "{\"nested\":{}}", "{\"nested\":null}", "{\"nested\":1}", "{\"nested\":{\"kind\":\"NESTED_ONE\",\"child\":{\"value\":2}}}", "{\"nested\":[]}", "{\"nested\":{},\"nested\":{}}", "{\"nested\":{\"value\":1},\"nested\":{\"kind\":\"NESTED_ONE\"}}", "{\"nested\":{\"value\":1},\"nested\":null}", "{\"nested\":{\"value\":1},\"nested\":null,\"nested\":{\"kind\":1}}", "{\"rNested\":[{}],\"rNested\":[{\"value\":1}]}", "{\"rInt32\":[1],\"rInt32\":null,\"rInt32\":[2]}", "{\"mString\":{\"a\":1},\"mString\":{\"b\":2}}", "{\"mString\":{\"a\":1},\"mString\":null}",
        "{\"imported\":{\"name\":\"x\"}}", "{\"imported\":{\"unknown\":1}}");

    [Theory]
    [MemberData(nameof(ContainerDocuments))]
    public void Containers_MatchGoogle(string json) => AssertMatchesGoogle<Containers>(json);

    public static IEnumerable<object[]> PresenceDocuments() => Documents(
        "{\"oInt32\":0}", "{\"oString\":\"\"}", "{\"oEnum\":\"TOP_ZERO\"}", "{\"oBool\":false}", "{\"oInt32\":null}", "{\"oInt32\":0,\"oInt32\":1}", "{\"oEnum\":\"NOPE\"}",
        "{\"cInt32\":0}", "{\"cString\":\"\"}", "{\"cMessage\":{}}", "{\"cWrapper\":0}", "{\"cEnum\":\"TOP_ZERO\"}", "{\"cEnum\":\"NOPE\"}",
        "{\"cInt32\":1,\"cString\":\"a\"}", "{\"cInt32\":null,\"cString\":\"a\"}", "{\"cInt32\":1,\"cString\":null}", "{\"cInt32\":1,\"cInt32\":2}", "{\"cInt32\":null}", "{\"cWrapper\":null}", "{\"cMessage\":null}", "{\"c_int32\":1,\"cInt32\":2}");

    [Theory]
    [MemberData(nameof(PresenceDocuments))]
    public void Presence_MatchesGoogle(string json) => AssertMatchesGoogle<Presence>(json);

    public static IEnumerable<object[]> WellKnownDocuments() => Documents(
        "{\"timestamp\":\"2026-10-31T01:02:03.500Z\"}", "{\"timestamp\":\"bad\"}", "{\"timestamp\":null}", "{\"timestamp\":1}",
        "{\"duration\":\"1.5s\"}", "{\"struct\":{\"a\":1},\"struct\":{\"b\":2}}", "{\"timestamp\":\"2026-10-31T01:02:03Z\",\"timestamp\":\"2020-01-01T00:00:00Z\"}", "{\"duration\":\"1s\",\"duration\":null}", "{\"wInt32\":1,\"wInt32\":2}", "{\"wInt32\":1,\"wInt32\":null}", "{\"value\":1,\"value\":null}", "{\"duration\":\"bad\"}", "{\"struct\":{\"a\":1,\"b\":null,\"c\":[1,{}]}}", "{\"struct\":1}", "{\"struct\":null}",
        "{\"value\":null}", "{\"value\":1}", "{\"value\":\"s\"}", "{\"value\":{\"a\":null}}", "{\"value\":[null]}", "{\"list\":[1,null]}", "{\"list\":1}",
        "{\"any\":{\"@type\":\"type.googleapis.com/example.HelloRequest\",\"name\":\"x\"}}", "{\"any\":{\"@type\":\"type.googleapis.com/no.Such\"}}", "{\"any\":{}}", "{\"any\":null}",
        "{\"wInt32\":0}", "{\"wInt32\":null}", "{\"wInt32\":\"1\"}", "{\"wInt32\":{}}", "{\"wInt64\":\"5\"}", "{\"wUint32\":1}", "{\"wUint64\":\"5\"}", "{\"wBool\":false}", "{\"wBool\":1}", "{\"wString\":\"\"}", "{\"wString\":1}", "{\"wBytes\":\"AA==\"}", "{\"wDouble\":\"NaN\"}", "{\"wFloat\":1.5}",
        "{\"rTimestamp\":[\"2020-01-01T00:00:00Z\"]}", "{\"rTimestamp\":[null]}", "{\"mDuration\":{\"d\":\"60s\"}}", "{\"mDuration\":{\"d\":null}}");

    [Theory]
    [MemberData(nameof(WellKnownDocuments))]
    public void WellKnownTypes_MatchGoogle(string json) => AssertMatchesGoogle<WellKnown>(json);

    public static IEnumerable<object[]> RenameDocuments() => Documents(
        "{\"types\":\"a\",\"descriptor\":\"b\",\"toString\":\"c\",\"to_string\":null}", "{\"equals\":\"d\",\"parser\":\"e\",\"renames\":\"f\"}", "{\"none\":\"g\"}", "{\"class\":\"h\"}", "{\"none\":\"g\",\"class\":\"h\"}");

    [Theory]
    [MemberData(nameof(RenameDocuments))]
    public void RenamedMembers_MatchGoogle(string json) => AssertMatchesGoogle<Renames>(json);

    [Fact]
    public void ParserOverload_UsesGeneratedCode()
    {
        var message = (Scalars)Codec.Deserialize(Encoding.UTF8.GetBytes("{\"fInt32\":5}"), Scalars.Parser);
        Assert.Equal(5, message.FInt32);
    }

    [Fact]
    public void InvalidUtf8_IsAFormatError()
    {
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'f', (byte)'S', (byte)'t', (byte)'r', (byte)'i', (byte)'n', (byte)'g', (byte)'"', (byte)':', (byte)'"', 0xFF, (byte)'"', (byte)'}' };
        Assert.Throws<FormatException>(() => Codec.Deserialize<Scalars>(bytes));
    }

    [Fact]
    public void DeepNesting_IsRejectedWithoutOverflowingTheStack()
    {
        var json = string.Concat(Enumerable.Repeat("{\"child\":", 100_000)) + "{}" + string.Concat(Enumerable.Repeat("}", 100_000));
        Assert.Throws<FormatException>(() => Codec.Deserialize<Containers.Types.Nested>(Encoding.UTF8.GetBytes(json)));
    }
}
