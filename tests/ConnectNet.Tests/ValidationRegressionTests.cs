using System;
using System.Collections.Generic;
using System.Linq;
using Buf.Validate;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using ConnectNet.Validation.Evaluation;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

/// <summary>
/// Behaviours the CEL-backed validator must keep: nanosecond time comparisons, exact
/// 64-bit boundaries, absent-field semantics, ASCII regex classes, and plans keyed by
/// descriptor identity rather than by name.
/// </summary>
public class ValidationRegressionTests
{
    private readonly ProtoValidator _validator = new();

    [Fact]
    public void Timestamps_CompareWithNanosecondPrecision()
    {
        Assert.Contains(_validator.Validate(new TimeTestMessage { At = new Timestamp { Seconds = 1, Nanos = 500 } }).Violations,
            v => v.ConstraintId == "timestamp.gt");
        Assert.True(_validator.Validate(new TimeTestMessage { At = new Timestamp { Seconds = 1, Nanos = 501 } }).IsValid);
    }

    [Fact]
    public void Durations_CompareWithNanosecondPrecision()
    {
        Assert.True(_validator.Validate(new TimeTestMessage { D = new Duration { Nanos = 999 } }).IsValid);
        Assert.Contains(_validator.Validate(new TimeTestMessage { D = new Duration { Nanos = 1000 } }).Violations,
            v => v.ConstraintId == "duration.lt");
    }

    [Fact]
    public void NumericBoundaries_AreExact()
    {
        var ok = new NumericBoundaryTestMessage
        {
            Big = long.MaxValue - 1,
            Ubig = ulong.MaxValue - 1,
            Small = int.MinValue,
            Dbl = double.PositiveInfinity,
            Flt = float.MaxValue,
            U = uint.MaxValue,
            Sf = long.MinValue,
        };
        Assert.True(_validator.Validate(ok).IsValid);

        var bad = new NumericBoundaryTestMessage
        {
            Big = long.MaxValue - 2,
            Ubig = ulong.MaxValue,
            Small = int.MinValue + 2,
            Dbl = double.MaxValue,
            Flt = float.PositiveInfinity,
            U = uint.MaxValue - 1,
            Sf = long.MinValue + 1,
        };
        var ids = _validator.Validate(bad).Violations.Select(v => v.ConstraintId).OrderBy(x => x);
        Assert.Equal(new[] { "double.gt", "float.lte", "int32.lte", "int64.gte", "sfixed64.const", "uint32.gt", "uint64.lte" }, ids);
    }

    [Fact]
    public void AbsentFields_FollowPresence()
    {
        var result = _validator.Validate(new AbsentFieldsTestMessage());

        // Presence-tracking fields and unset messages are skipped; implicit-presence
        // scalars and empty collections are validated as their zero value.
        Assert.Equal(new[] { "implicit", "list" }, result.Violations.Select(v => v.FieldPath).OrderBy(p => p));

        var set = new AbsentFieldsTestMessage { Opt = "", OptNum = 0, Implicit = "abc" };
        set.List.Add("x");
        Assert.Equal(new[] { "opt", "opt_num" }, _validator.Validate(set).Violations.Select(v => v.FieldPath).OrderBy(p => p));
    }

    [Fact]
    public void RegexClasses_AreAscii()
    {
        Assert.True(_validator.Validate(new AsciiPatternTestMessage { Digits = "123", Word = "a_1" }).IsValid);

        var result = _validator.Validate(new AsciiPatternTestMessage { Digits = "١٢٣", Word = "ａ" });

        Assert.Equal(new[] { "digits", "word" }, result.Violations.Select(v => v.FieldPath).OrderBy(p => p));
    }

    [Fact]
    public void Plans_AreKeyedByDescriptorIdentity_NotName()
    {
        // Two descriptors with the same full name but different rules: each gets its own plan.
        var original = StringTestMessage.Descriptor;
        var relaxed = RebuildWithMinLen(original, "name", 1);
        Assert.Equal(original.FullName, relaxed.FullName);
        Assert.NotSame(original, relaxed);

        var builder = new PlanBuilder();
        var originalPlan = builder.GetPlan(original);
        var relaxedPlan = builder.GetPlan(relaxed);

        Assert.NotSame(originalPlan, relaxedPlan);
        Assert.Same(originalPlan, builder.GetPlan(original));
        Assert.Equal(3UL, MinLenRuleValue(originalPlan));
        Assert.Equal(1UL, MinLenRuleValue(relaxedPlan));
    }

    private static ulong MinLenRuleValue(MessagePlan plan)
    {
        var field = plan.Nested.OfType<FieldEvaluator>().Single(f => f.Field.Name == "name");
        var rules = Assert.IsType<FieldCelRules>(Assert.Single(field.Value.Rules));
        var minLen = rules.Programs.Rules.Single(r => r.Source.Id == "string.min_len");
        return (ulong)minLen.RuleRaw!;
    }

    /// <summary>Builds a second descriptor of the test schema with one field's <c>min_len</c> changed.</summary>
    private static MessageDescriptor RebuildWithMinLen(MessageDescriptor message, string field, uint minLen)
    {
        var file = message.File.ToProto();
        var messageProto = file.MessageType.Single(m => m.Name == message.Name);
        var fieldProto = messageProto.Field.Single(f => f.Name == field);
        var rules = fieldProto.Options.GetExtension(ValidateExtensions.Field);
        rules.String.MinLen = minLen;
        fieldProto.Options.SetExtension(ValidateExtensions.Field, rules);

        var registry = new ExtensionRegistry { ValidateExtensions.Field, ValidateExtensions.Message, ValidateExtensions.Oneof, ValidateExtensions.Predefined };
        var files = new List<ByteString>();
        foreach (var dependency in TransitiveDependencies(message.File))
            files.Add(dependency.ToProto().ToByteString());
        files.Add(file.ToByteString());
        var built = FileDescriptor.BuildFromByteStrings(files, registry);
        return built.Last().FindTypeByName<MessageDescriptor>(message.Name);
    }

    private static IEnumerable<FileDescriptor> TransitiveDependencies(FileDescriptor file)
    {
        var seen = new HashSet<string>();
        var ordered = new List<FileDescriptor>();
        void Visit(FileDescriptor f)
        {
            foreach (var dep in f.Dependencies)
            {
                if (seen.Add(dep.Name))
                {
                    Visit(dep);
                    ordered.Add(dep);
                }
            }
        }
        Visit(file);
        return ordered;
    }
}
