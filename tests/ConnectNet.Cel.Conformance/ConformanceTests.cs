using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace ConnectNet.Cel.Conformance;

public class ConformanceTests
{
    private readonly ITestOutputHelper _output;

    public ConformanceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>Writes the summary to the test output and to a file, so that passing files are visible too.</summary>
    private void Report(FileResult result)
    {
        var summary = ConformanceRunner.Summarize(result);
        _output.WriteLine(summary);
        var dir = System.IO.Path.Combine(System.AppContext.BaseDirectory, "conformance-results");
        System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, result.File + ".txt"), summary);
    }

    public static IEnumerable<object[]> RequiredFiles() =>
        ConformanceRunner.ExpectedCounts.Keys.Select(f => new object[] { f });

    /// <summary>
    /// Files with cases that construct or read protobuf messages. They become gating with the
    /// Protobuf adaptation unit of the series; until then they are run and reported only.
    /// </summary>
    private static readonly HashSet<string> AwaitingProtobufAdaptation = new()
    {
        "comparisons", "dynamic", "enums", "parse", "proto2", "proto3", "timestamps", "wrappers",
    };

    [Theory]
    [MemberData(nameof(RequiredFiles))]
    public void RequiredFilePasses(string file)
    {
        var result = ConformanceRunner.Run(file);
        Report(result);
        Assert.Equal(ConformanceRunner.ExpectedCounts[file], result.Total);
        if (AwaitingProtobufAdaptation.Contains(file))
            return;
        Assert.True(result.Failures.Any() == false, ConformanceRunner.Summarize(result));
    }

    /// <summary>Reported but not gating, per the design: the checker's deduced types.</summary>
    [Fact]
    public void TypeDeductionIsReported()
    {
        var result = ConformanceRunner.Run("type_deduction");
        Report(result);
        Assert.True(result.Total > 0);
    }
}
