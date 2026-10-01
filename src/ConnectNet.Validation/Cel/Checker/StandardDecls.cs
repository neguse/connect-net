using System.Collections.Generic;
using ConnectNet.Validation.Cel.Syntax;

namespace ConnectNet.Validation.Cel.Checker;

/// <summary>Overload ids of the standard library, shared by the checker and the evaluator.</summary>
internal static class OverloadIds
{
    public const string Conditional = "conditional";
    public const string LogicalAnd = "logical_and";
    public const string LogicalOr = "logical_or";
    public const string LogicalNot = "logical_not";
    public const string NotStrictlyFalse = "not_strictly_false";
    public const string EqualsOp = "equals";
    public const string NotEquals = "not_equals";

    public const string AddInt = "add_int64";
    public const string AddUint = "add_uint64";
    public const string AddDouble = "add_double";
    public const string AddString = "add_string";
    public const string AddBytes = "add_bytes";
    public const string AddList = "add_list";
    public const string AddTimestampDuration = "add_timestamp_duration";
    public const string AddDurationTimestamp = "add_duration_timestamp";
    public const string AddDurationDuration = "add_duration_duration";
    public const string SubtractInt = "subtract_int64";
    public const string SubtractUint = "subtract_uint64";
    public const string SubtractDouble = "subtract_double";
    public const string SubtractTimestampTimestamp = "subtract_timestamp_timestamp";
    public const string SubtractTimestampDuration = "subtract_timestamp_duration";
    public const string SubtractDurationDuration = "subtract_duration_duration";
    public const string MultiplyInt = "multiply_int64";
    public const string MultiplyUint = "multiply_uint64";
    public const string MultiplyDouble = "multiply_double";
    public const string DivideInt = "divide_int64";
    public const string DivideUint = "divide_uint64";
    public const string DivideDouble = "divide_double";
    public const string ModuloInt = "modulo_int64";
    public const string ModuloUint = "modulo_uint64";
    public const string NegateInt = "negate_int64";
    public const string NegateDouble = "negate_double";

    public const string IndexList = "index_list";
    public const string IndexMap = "index_map";
    public const string InList = "in_list";
    public const string InMap = "in_map";

    public const string SizeString = "size_string";
    public const string SizeBytes = "size_bytes";
    public const string SizeList = "size_list";
    public const string SizeMap = "size_map";
    public const string SizeStringInst = "string_size";
    public const string SizeBytesInst = "bytes_size";
    public const string SizeListInst = "list_size";
    public const string SizeMapInst = "map_size";

    public const string ContainsString = "contains_string";
    public const string EndsWithString = "ends_with_string";
    public const string StartsWithString = "starts_with_string";
    public const string Matches = "matches";
    public const string MatchesString = "matches_string";

    public const string TypeConvertType = "type";
    public const string ToDyn = "to_dyn";
    public const string BoolToBool = "bool_to_bool";
    public const string StringToBool = "string_to_bool";
    public const string BytesToBytes = "bytes_to_bytes";
    public const string StringToBytes = "string_to_bytes";
    public const string DoubleToDouble = "double_to_double";
    public const string IntToDouble = "int64_to_double";
    public const string StringToDouble = "string_to_double";
    public const string UintToDouble = "uint64_to_double";
    public const string DurationToDuration = "duration_to_duration";
    public const string StringToDuration = "string_to_duration";
    public const string IntToInt = "int64_to_int64";
    public const string DoubleToInt = "double_to_int64";
    public const string DurationToInt = "duration_to_int64";
    public const string StringToInt = "string_to_int64";
    public const string TimestampToInt = "timestamp_to_int64";
    public const string UintToInt = "uint64_to_int64";
    public const string StringToString = "string_to_string";
    public const string BoolToString = "bool_to_string";
    public const string BytesToString = "bytes_to_string";
    public const string DoubleToString = "double_to_string";
    public const string DurationToString = "duration_to_string";
    public const string IntToString = "int64_to_string";
    public const string TimestampToString = "timestamp_to_string";
    public const string UintToString = "uint64_to_string";
    public const string TimestampToTimestamp = "timestamp_to_timestamp";
    public const string IntToTimestamp = "int64_to_timestamp";
    public const string StringToTimestamp = "string_to_timestamp";
    public const string UintToUint = "uint64_to_uint64";
    public const string DoubleToUint = "double_to_uint64";
    public const string IntToUint = "int64_to_uint64";
    public const string StringToUint = "string_to_uint64";

    public const string TimestampToYear = "timestamp_to_year";
    public const string TimestampToMonth = "timestamp_to_month";
    public const string TimestampToDayOfYear = "timestamp_to_day_of_year";
    public const string TimestampToDayOfMonthZeroBased = "timestamp_to_day_of_month";
    public const string TimestampToDayOfMonthOneBased = "timestamp_to_day_of_month_1_based";
    public const string TimestampToDayOfWeek = "timestamp_to_day_of_week";
    public const string TimestampToHours = "timestamp_to_hours";
    public const string TimestampToMinutes = "timestamp_to_minutes";
    public const string TimestampToSeconds = "timestamp_to_seconds";
    public const string TimestampToMilliseconds = "timestamp_to_milliseconds";
    public const string TimestampToYearWithTz = "timestamp_to_year_with_tz";
    public const string TimestampToMonthWithTz = "timestamp_to_month_with_tz";
    public const string TimestampToDayOfYearWithTz = "timestamp_to_day_of_year_with_tz";
    public const string TimestampToDayOfMonthZeroBasedWithTz = "timestamp_to_day_of_month_with_tz";
    public const string TimestampToDayOfMonthOneBasedWithTz = "timestamp_to_day_of_month_1_based_with_tz";
    public const string TimestampToDayOfWeekWithTz = "timestamp_to_day_of_week_with_tz";
    public const string TimestampToHoursWithTz = "timestamp_to_hours_with_tz";
    public const string TimestampToMinutesWithTz = "timestamp_to_minutes_with_tz";
    public const string TimestampToSecondsWithTz = "timestamp_to_seconds_tz";
    public const string TimestampToMillisecondsWithTz = "timestamp_to_milliseconds_with_tz";
    public const string DurationToHours = "duration_to_hours";
    public const string DurationToMinutes = "duration_to_minutes";
    public const string DurationToSeconds = "duration_to_seconds";
    public const string DurationToMilliseconds = "duration_to_milliseconds";

    // strings extension
    public const string StringCharAt = "string_char_at_int";
    public const string StringIndexOf = "string_index_of_string";
    public const string StringIndexOfInt = "string_index_of_string_int";
    public const string StringLastIndexOf = "string_last_index_of_string";
    public const string StringLastIndexOfInt = "string_last_index_of_string_int";
    public const string StringLowerAscii = "string_lower_ascii";
    public const string StringUpperAscii = "string_upper_ascii";
    public const string StringReplace = "string_replace_string_string";
    public const string StringReplaceInt = "string_replace_string_string_int";
    public const string StringSplit = "string_split_string";
    public const string StringSplitInt = "string_split_string_int";
    public const string StringSubstring = "string_substring_int";
    public const string StringSubstringInt = "string_substring_int_int";
    public const string StringTrim = "string_trim";
    public const string StringFormat = "string_format";
    public const string StringsQuote = "strings_quote";
    public const string ListJoin = "list_join";
    public const string ListJoinString = "list_join_string";
    public const string StringReverse = "string_reverse";
}

/// <summary>Function names of the standard library that are not operators.</summary>
internal static class FunctionNames
{
    public const string Size = "size";
    public const string Contains = "contains";
    public const string EndsWith = "endsWith";
    public const string StartsWith = "startsWith";
    public const string Matches = "matches";
    public const string Int = "int";
    public const string Uint = "uint";
    public const string Double = "double";
    public const string Bool = "bool";
    public const string String = "string";
    public const string Bytes = "bytes";
    public const string Timestamp = "timestamp";
    public const string Duration = "duration";
    public const string Type = "type";
    public const string Dyn = "dyn";
    public const string GetFullYear = "getFullYear";
    public const string GetMonth = "getMonth";
    public const string GetDayOfYear = "getDayOfYear";
    public const string GetDate = "getDate";
    public const string GetDayOfMonth = "getDayOfMonth";
    public const string GetDayOfWeek = "getDayOfWeek";
    public const string GetHours = "getHours";
    public const string GetMinutes = "getMinutes";
    public const string GetSeconds = "getSeconds";
    public const string GetMilliseconds = "getMilliseconds";
    public const string CharAt = "charAt";
    public const string IndexOf = "indexOf";
    public const string LastIndexOf = "lastIndexOf";
    public const string LowerAscii = "lowerAscii";
    public const string UpperAscii = "upperAscii";
    public const string Replace = "replace";
    public const string Split = "split";
    public const string Substring = "substring";
    public const string Trim = "trim";
    public const string Format = "format";
    public const string StringsQuote = "strings.quote";
    public const string Join = "join";
    public const string Reverse = "reverse";
}

/// <summary>The declarations of the CEL standard library and of the strings extension.</summary>
internal static class StandardDecls
{
    private static readonly CelType A = CelType.TypeParam("A");
    private static readonly CelType B = CelType.TypeParam("B");
    private static readonly CelType ListOfA = CelType.List(A);
    private static readonly CelType MapOfAB = CelType.Map(A, B);

    /// <summary>The type names declared as identifiers: <c>int</c>, <c>list</c>, …, each of type <c>type(T)</c>.</summary>
    public static IReadOnlyList<VariableDecl> TypeIdents { get; } = new[]
    {
        TypeIdent("bool", CelType.Bool),
        TypeIdent("bytes", CelType.Bytes),
        TypeIdent("double", CelType.Double),
        TypeIdent("duration", CelType.Duration),
        TypeIdent("int", CelType.Int),
        TypeIdent("list", ListOfA),
        TypeIdent("map", MapOfAB),
        TypeIdent("null_type", CelType.Null),
        TypeIdent("string", CelType.String),
        TypeIdent("timestamp", CelType.Timestamp),
        TypeIdent("type", CelType.TypeType),
        TypeIdent("uint", CelType.Uint),
    };

    private static VariableDecl TypeIdent(string name, CelType type) => new(name, CelType.TypeOf(type));

    public static IReadOnlyList<FunctionDecl> Functions { get; } = BuildFunctions();

    public static IReadOnlyList<FunctionDecl> StringsExtension { get; } = BuildStringsExtension();

    private static List<FunctionDecl> BuildFunctions()
    {
        var bool_ = CelType.Bool;
        var int_ = CelType.Int;
        var uint_ = CelType.Uint;
        var double_ = CelType.Double;
        var string_ = CelType.String;
        var bytes_ = CelType.Bytes;
        var ts = CelType.Timestamp;
        var dur = CelType.Duration;

        var fns = new List<FunctionDecl>
        {
            new FunctionDecl(Operators.Conditional).AddOverload(OverloadIds.Conditional, A, bool_, A, A),
            new FunctionDecl(Operators.LogicalAnd).AddOverload(OverloadIds.LogicalAnd, bool_, bool_, bool_),
            new FunctionDecl(Operators.LogicalOr).AddOverload(OverloadIds.LogicalOr, bool_, bool_, bool_),
            new FunctionDecl(Operators.LogicalNot).AddOverload(OverloadIds.LogicalNot, bool_, bool_),
            new FunctionDecl(Operators.NotStrictlyFalse).AddOverload(OverloadIds.NotStrictlyFalse, bool_, bool_),
            new FunctionDecl(Operators.EqualsOp).AddOverload(OverloadIds.EqualsOp, bool_, A, A),
            new FunctionDecl(Operators.NotEquals).AddOverload(OverloadIds.NotEquals, bool_, A, A),

            new FunctionDecl(Operators.Add)
                .AddOverload(OverloadIds.AddBytes, bytes_, bytes_, bytes_)
                .AddOverload(OverloadIds.AddDouble, double_, double_, double_)
                .AddOverload(OverloadIds.AddDurationDuration, dur, dur, dur)
                .AddOverload(OverloadIds.AddDurationTimestamp, ts, dur, ts)
                .AddOverload(OverloadIds.AddTimestampDuration, ts, ts, dur)
                .AddOverload(OverloadIds.AddInt, int_, int_, int_)
                .AddOverload(OverloadIds.AddList, ListOfA, ListOfA, ListOfA)
                .AddOverload(OverloadIds.AddString, string_, string_, string_)
                .AddOverload(OverloadIds.AddUint, uint_, uint_, uint_),
            new FunctionDecl(Operators.Divide)
                .AddOverload(OverloadIds.DivideDouble, double_, double_, double_)
                .AddOverload(OverloadIds.DivideInt, int_, int_, int_)
                .AddOverload(OverloadIds.DivideUint, uint_, uint_, uint_),
            new FunctionDecl(Operators.Modulo)
                .AddOverload(OverloadIds.ModuloInt, int_, int_, int_)
                .AddOverload(OverloadIds.ModuloUint, uint_, uint_, uint_),
            new FunctionDecl(Operators.Multiply)
                .AddOverload(OverloadIds.MultiplyDouble, double_, double_, double_)
                .AddOverload(OverloadIds.MultiplyInt, int_, int_, int_)
                .AddOverload(OverloadIds.MultiplyUint, uint_, uint_, uint_),
            new FunctionDecl(Operators.Negate)
                .AddOverload(OverloadIds.NegateDouble, double_, double_)
                .AddOverload(OverloadIds.NegateInt, int_, int_),
            new FunctionDecl(Operators.Subtract)
                .AddOverload(OverloadIds.SubtractDouble, double_, double_, double_)
                .AddOverload(OverloadIds.SubtractDurationDuration, dur, dur, dur)
                .AddOverload(OverloadIds.SubtractInt, int_, int_, int_)
                .AddOverload(OverloadIds.SubtractTimestampDuration, ts, ts, dur)
                .AddOverload(OverloadIds.SubtractTimestampTimestamp, dur, ts, ts)
                .AddOverload(OverloadIds.SubtractUint, uint_, uint_, uint_),

            Comparison(Operators.Less, "less"),
            Comparison(Operators.LessEquals, "less_equals"),
            Comparison(Operators.Greater, "greater"),
            Comparison(Operators.GreaterEquals, "greater_equals"),

            new FunctionDecl(Operators.Index)
                .AddOverload(OverloadIds.IndexList, A, ListOfA, int_)
                .AddOverload(OverloadIds.IndexMap, B, MapOfAB, A),
            new FunctionDecl(Operators.In)
                .AddOverload(OverloadIds.InList, bool_, A, ListOfA)
                .AddOverload(OverloadIds.InMap, bool_, A, MapOfAB),

            new FunctionDecl(FunctionNames.Size)
                .AddOverload(OverloadIds.SizeBytes, int_, bytes_)
                .AddMemberOverload(OverloadIds.SizeBytesInst, int_, bytes_)
                .AddOverload(OverloadIds.SizeList, int_, ListOfA)
                .AddMemberOverload(OverloadIds.SizeListInst, int_, ListOfA)
                .AddOverload(OverloadIds.SizeMap, int_, MapOfAB)
                .AddMemberOverload(OverloadIds.SizeMapInst, int_, MapOfAB)
                .AddOverload(OverloadIds.SizeString, int_, string_)
                .AddMemberOverload(OverloadIds.SizeStringInst, int_, string_),

            new FunctionDecl(FunctionNames.Type).AddOverload(OverloadIds.TypeConvertType, CelType.TypeOf(A), A),
            new FunctionDecl(FunctionNames.Bool)
                .AddOverload(OverloadIds.BoolToBool, bool_, bool_)
                .AddOverload(OverloadIds.StringToBool, bool_, string_),
            new FunctionDecl(FunctionNames.Bytes)
                .AddOverload(OverloadIds.BytesToBytes, bytes_, bytes_)
                .AddOverload(OverloadIds.StringToBytes, bytes_, string_),
            new FunctionDecl(FunctionNames.Double)
                .AddOverload(OverloadIds.DoubleToDouble, double_, double_)
                .AddOverload(OverloadIds.IntToDouble, double_, int_)
                .AddOverload(OverloadIds.StringToDouble, double_, string_)
                .AddOverload(OverloadIds.UintToDouble, double_, uint_),
            new FunctionDecl(FunctionNames.Duration)
                .AddOverload(OverloadIds.DurationToDuration, dur, dur)
                .AddOverload(OverloadIds.StringToDuration, dur, string_),
            new FunctionDecl(FunctionNames.Dyn).AddOverload(OverloadIds.ToDyn, CelType.Dyn, A),
            new FunctionDecl(FunctionNames.Int)
                .AddOverload(OverloadIds.IntToInt, int_, int_)
                .AddOverload(OverloadIds.DoubleToInt, int_, double_)
                .AddOverload(OverloadIds.DurationToInt, int_, dur)
                .AddOverload(OverloadIds.StringToInt, int_, string_)
                .AddOverload(OverloadIds.TimestampToInt, int_, ts)
                .AddOverload(OverloadIds.UintToInt, int_, uint_),
            new FunctionDecl(FunctionNames.String)
                .AddOverload(OverloadIds.StringToString, string_, string_)
                .AddOverload(OverloadIds.BoolToString, string_, bool_)
                .AddOverload(OverloadIds.BytesToString, string_, bytes_)
                .AddOverload(OverloadIds.DoubleToString, string_, double_)
                .AddOverload(OverloadIds.DurationToString, string_, dur)
                .AddOverload(OverloadIds.IntToString, string_, int_)
                .AddOverload(OverloadIds.TimestampToString, string_, ts)
                .AddOverload(OverloadIds.UintToString, string_, uint_),
            new FunctionDecl(FunctionNames.Timestamp)
                .AddOverload(OverloadIds.TimestampToTimestamp, ts, ts)
                .AddOverload(OverloadIds.IntToTimestamp, ts, int_)
                .AddOverload(OverloadIds.StringToTimestamp, ts, string_),
            new FunctionDecl(FunctionNames.Uint)
                .AddOverload(OverloadIds.UintToUint, uint_, uint_)
                .AddOverload(OverloadIds.DoubleToUint, uint_, double_)
                .AddOverload(OverloadIds.IntToUint, uint_, int_)
                .AddOverload(OverloadIds.StringToUint, uint_, string_),

            new FunctionDecl(FunctionNames.Contains).AddMemberOverload(OverloadIds.ContainsString, bool_, string_, string_),
            new FunctionDecl(FunctionNames.EndsWith).AddMemberOverload(OverloadIds.EndsWithString, bool_, string_, string_),
            new FunctionDecl(FunctionNames.StartsWith).AddMemberOverload(OverloadIds.StartsWithString, bool_, string_, string_),
            new FunctionDecl(FunctionNames.Matches)
                .AddOverload(OverloadIds.Matches, bool_, string_, string_)
                .AddMemberOverload(OverloadIds.MatchesString, bool_, string_, string_),

            TimeAccessor(FunctionNames.GetFullYear, OverloadIds.TimestampToYear, OverloadIds.TimestampToYearWithTz),
            TimeAccessor(FunctionNames.GetMonth, OverloadIds.TimestampToMonth, OverloadIds.TimestampToMonthWithTz),
            TimeAccessor(FunctionNames.GetDayOfYear, OverloadIds.TimestampToDayOfYear, OverloadIds.TimestampToDayOfYearWithTz),
            TimeAccessor(FunctionNames.GetDayOfMonth, OverloadIds.TimestampToDayOfMonthZeroBased, OverloadIds.TimestampToDayOfMonthZeroBasedWithTz),
            TimeAccessor(FunctionNames.GetDate, OverloadIds.TimestampToDayOfMonthOneBased, OverloadIds.TimestampToDayOfMonthOneBasedWithTz),
            TimeAccessor(FunctionNames.GetDayOfWeek, OverloadIds.TimestampToDayOfWeek, OverloadIds.TimestampToDayOfWeekWithTz),
            TimeAccessor(FunctionNames.GetHours, OverloadIds.TimestampToHours, OverloadIds.TimestampToHoursWithTz, OverloadIds.DurationToHours),
            TimeAccessor(FunctionNames.GetMinutes, OverloadIds.TimestampToMinutes, OverloadIds.TimestampToMinutesWithTz, OverloadIds.DurationToMinutes),
            TimeAccessor(FunctionNames.GetSeconds, OverloadIds.TimestampToSeconds, OverloadIds.TimestampToSecondsWithTz, OverloadIds.DurationToSeconds),
            TimeAccessor(FunctionNames.GetMilliseconds, OverloadIds.TimestampToMilliseconds, OverloadIds.TimestampToMillisecondsWithTz, OverloadIds.DurationToMilliseconds),
        };
        return fns;
    }

    private static FunctionDecl Comparison(string op, string prefix)
    {
        var bool_ = CelType.Bool;
        return new FunctionDecl(op)
            .AddOverload(prefix + "_bool", bool_, CelType.Bool, CelType.Bool)
            .AddOverload(prefix + "_int64", bool_, CelType.Int, CelType.Int)
            .AddOverload(prefix + "_int64_double", bool_, CelType.Int, CelType.Double)
            .AddOverload(prefix + "_int64_uint64", bool_, CelType.Int, CelType.Uint)
            .AddOverload(prefix + "_uint64", bool_, CelType.Uint, CelType.Uint)
            .AddOverload(prefix + "_uint64_double", bool_, CelType.Uint, CelType.Double)
            .AddOverload(prefix + "_uint64_int64", bool_, CelType.Uint, CelType.Int)
            .AddOverload(prefix + "_double", bool_, CelType.Double, CelType.Double)
            .AddOverload(prefix + "_double_int64", bool_, CelType.Double, CelType.Int)
            .AddOverload(prefix + "_double_uint64", bool_, CelType.Double, CelType.Uint)
            .AddOverload(prefix + "_string", bool_, CelType.String, CelType.String)
            .AddOverload(prefix + "_bytes", bool_, CelType.Bytes, CelType.Bytes)
            .AddOverload(prefix + "_timestamp", bool_, CelType.Timestamp, CelType.Timestamp)
            .AddOverload(prefix + "_duration", bool_, CelType.Duration, CelType.Duration);
    }

    /// <summary>The overload ids that only exist when cross-type numeric comparisons are enabled.</summary>
    public static bool IsCrossTypeNumericComparison(string overloadId)
    {
        return overloadId.EndsWith("_int64_double") || overloadId.EndsWith("_int64_uint64")
            || overloadId.EndsWith("_uint64_double") || overloadId.EndsWith("_uint64_int64")
            || overloadId.EndsWith("_double_int64") || overloadId.EndsWith("_double_uint64");
    }

    private static FunctionDecl TimeAccessor(string name, string tsId, string tsTzId, string? durId = null)
    {
        var fn = new FunctionDecl(name)
            .AddMemberOverload(tsId, CelType.Int, CelType.Timestamp)
            .AddMemberOverload(tsTzId, CelType.Int, CelType.Timestamp, CelType.String);
        if (durId != null)
            fn.AddMemberOverload(durId, CelType.Int, CelType.Duration);
        return fn;
    }

    private static List<FunctionDecl> BuildStringsExtension()
    {
        var s = CelType.String;
        var i = CelType.Int;
        var listOfString = CelType.List(s);
        return new List<FunctionDecl>
        {
            new FunctionDecl(FunctionNames.CharAt).AddMemberOverload(OverloadIds.StringCharAt, s, s, i),
            new FunctionDecl(FunctionNames.IndexOf)
                .AddMemberOverload(OverloadIds.StringIndexOf, i, s, s)
                .AddMemberOverload(OverloadIds.StringIndexOfInt, i, s, s, i),
            new FunctionDecl(FunctionNames.LastIndexOf)
                .AddMemberOverload(OverloadIds.StringLastIndexOf, i, s, s)
                .AddMemberOverload(OverloadIds.StringLastIndexOfInt, i, s, s, i),
            new FunctionDecl(FunctionNames.LowerAscii).AddMemberOverload(OverloadIds.StringLowerAscii, s, s),
            new FunctionDecl(FunctionNames.UpperAscii).AddMemberOverload(OverloadIds.StringUpperAscii, s, s),
            new FunctionDecl(FunctionNames.Replace)
                .AddMemberOverload(OverloadIds.StringReplace, s, s, s, s)
                .AddMemberOverload(OverloadIds.StringReplaceInt, s, s, s, s, i),
            new FunctionDecl(FunctionNames.Split)
                .AddMemberOverload(OverloadIds.StringSplit, listOfString, s, s)
                .AddMemberOverload(OverloadIds.StringSplitInt, listOfString, s, s, i),
            new FunctionDecl(FunctionNames.Substring)
                .AddMemberOverload(OverloadIds.StringSubstring, s, s, i)
                .AddMemberOverload(OverloadIds.StringSubstringInt, s, s, i, i),
            new FunctionDecl(FunctionNames.Trim).AddMemberOverload(OverloadIds.StringTrim, s, s),
            new FunctionDecl(FunctionNames.Format).AddMemberOverload(OverloadIds.StringFormat, s, s, CelType.ListOfDyn),
            new FunctionDecl(FunctionNames.StringsQuote).AddOverload(OverloadIds.StringsQuote, s, s),
            new FunctionDecl(FunctionNames.Join)
                .AddMemberOverload(OverloadIds.ListJoin, s, listOfString)
                .AddMemberOverload(OverloadIds.ListJoinString, s, listOfString, s),
            new FunctionDecl(FunctionNames.Reverse).AddMemberOverload(OverloadIds.StringReverse, s, s),
        };
    }
}
