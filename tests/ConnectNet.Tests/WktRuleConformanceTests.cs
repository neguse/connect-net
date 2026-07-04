using System;
using ConnectNet.Tests.Proto;
using ConnectNet.Validation;
using Google.Protobuf.WellKnownTypes;
using Xunit;

namespace ConnectNet.Tests;

public class WktRuleConformanceTests
{
    private readonly ProtoValidator _validator = new();

    private static Timestamp Ts(long seconds) => new() { Seconds = seconds };
    private static Duration Dur(long seconds) => new() { Seconds = seconds };
    private static Timestamp NowOffset(TimeSpan offset)
        => Timestamp.FromDateTime(DateTime.UtcNow.Add(offset));

    // --- timestamp.const ---

    [Fact]
    public void TimestampConst_Match_NoViolation()
    {
        var msg = new WktRulesTestMessage { ConstTs = Ts(1000) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "constTs");
    }

    [Fact]
    public void TimestampConst_Mismatch_Violation()
    {
        var msg = new WktRulesTestMessage { ConstTs = Ts(999) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "constTs" && v.ConstraintId == "timestamp.const");
    }

    // --- timestamp.lt_now / gt_now ---

    [Fact]
    public void LtNow_Past_NoViolation()
    {
        var msg = new WktRulesTestMessage { LtNowTs = NowOffset(TimeSpan.FromHours(-1)) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "ltNowTs");
    }

    [Fact]
    public void LtNow_Future_Violation()
    {
        var msg = new WktRulesTestMessage { LtNowTs = NowOffset(TimeSpan.FromHours(1)) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "ltNowTs" && v.ConstraintId == "timestamp.lt_now");
    }

    [Fact]
    public void GtNow_Future_NoViolation()
    {
        var msg = new WktRulesTestMessage { GtNowTs = NowOffset(TimeSpan.FromHours(1)) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "gtNowTs");
    }

    [Fact]
    public void GtNow_Past_Violation()
    {
        var msg = new WktRulesTestMessage { GtNowTs = NowOffset(TimeSpan.FromHours(-1)) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "gtNowTs" && v.ConstraintId == "timestamp.gt_now");
    }

    // --- timestamp.within ---

    [Fact]
    public void Within_Near_NoViolation()
    {
        var msg = new WktRulesTestMessage { WithinTs = NowOffset(TimeSpan.FromMinutes(-5)) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "withinTs");
    }

    [Fact]
    public void Within_TooOld_Violation()
    {
        var msg = new WktRulesTestMessage { WithinTs = NowOffset(TimeSpan.FromHours(-2)) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "withinTs" && v.ConstraintId == "timestamp.within");
    }

    [Fact]
    public void Within_TooFarFuture_Violation()
    {
        var msg = new WktRulesTestMessage { WithinTs = NowOffset(TimeSpan.FromHours(2)) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "withinTs" && v.ConstraintId == "timestamp.within");
    }

    // --- timestamp reversed range: gt:1000, lt:500 → outside [500,1000] ---

    [Fact]
    public void TimestampReversedRange_Outside_NoViolation()
    {
        var msg = new WktRulesTestMessage { RangeTs = Ts(1200) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "rangeTs");

        msg = new WktRulesTestMessage { RangeTs = Ts(100) };
        result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "rangeTs");
    }

    [Fact]
    public void TimestampReversedRange_InsideForbiddenBand_Violation()
    {
        var msg = new WktRulesTestMessage { RangeTs = Ts(700) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "rangeTs" && v.ConstraintId == "timestamp.gt_lt_exclusive");
    }

    // --- duration.const / in / not_in ---

    [Fact]
    public void DurationConst_Match_NoViolation()
    {
        var msg = new WktRulesTestMessage { ConstDur = Dur(5) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "constDur");
    }

    [Fact]
    public void DurationConst_Mismatch_Violation()
    {
        var msg = new WktRulesTestMessage { ConstDur = Dur(4) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "constDur" && v.ConstraintId == "duration.const");
    }

    [Fact]
    public void DurationIn_Member_NoViolation()
    {
        var msg = new WktRulesTestMessage { InDur = Dur(1) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "inDur");
    }

    [Fact]
    public void DurationIn_NonMember_Violation()
    {
        var msg = new WktRulesTestMessage { InDur = Dur(3) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "inDur" && v.ConstraintId == "duration.in");
    }

    [Fact]
    public void DurationNotIn_Member_Violation()
    {
        var msg = new WktRulesTestMessage { NotInDur = Dur(3) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "notInDur" && v.ConstraintId == "duration.not_in");
    }

    [Fact]
    public void DurationNotIn_NonMember_NoViolation()
    {
        var msg = new WktRulesTestMessage { NotInDur = Dur(4) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "notInDur");
    }

    // --- duration reversed range: gt:10, lt:5 ---

    [Fact]
    public void DurationReversedRange_InsideForbiddenBand_Violation()
    {
        var msg = new WktRulesTestMessage { RangeDur = Dur(7) };
        var result = _validator.Validate(msg);
        Assert.Contains(result.Violations, v =>
            v.FieldPath == "rangeDur" && v.ConstraintId == "duration.gt_lt_exclusive");
    }

    [Fact]
    public void DurationReversedRange_Outside_NoViolation()
    {
        var msg = new WktRulesTestMessage { RangeDur = Dur(12) };
        var result = _validator.Validate(msg);
        Assert.DoesNotContain(result.Violations, v => v.FieldPath == "rangeDur");
    }

    // --- unset presence-tracked WKT fields are skipped ---

    [Fact]
    public void UnsetWktFields_NoViolations()
    {
        var msg = new WktRulesTestMessage();
        var result = _validator.Validate(msg);
        Assert.True(result.IsValid);
    }
}
