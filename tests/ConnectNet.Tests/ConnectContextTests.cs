using System;
using System.Collections.Generic;
using Xunit;

namespace ConnectNet.Tests;

public class ConnectContextTests
{
    [Fact]
    public void RequestHeaders_AreCopiedOnFirstReadOnly()
    {
        var copies = 0;
        var context = new ConnectContext("source", source =>
        {
            copies++;
            return new Dictionary<string, string> { ["x-source"] = (string)source };
        }, default);

        Assert.Equal(0, copies);
        Assert.Equal("source", context.RequestHeaders["x-source"]);
        _ = context.RequestHeaders;
        Assert.Equal(1, copies);
    }

    [Fact]
    public void ResponseCollections_AreEmptyUntilWritten()
    {
        var context = new ConnectContext();

        Assert.Empty(context.ResponseHeadersOrEmpty);
        Assert.Empty(context.ResponseTrailersOrEmpty);

        context.ResponseHeaders["X-Header"] = "h";
        context.ResponseTrailers["X-Trailer"] = "t";

        Assert.Equal("h", context.ResponseHeadersOrEmpty["x-header"]);
        Assert.Equal("t", context.ResponseTrailersOrEmpty["x-trailer"]);
    }

    [Fact]
    public void WritingOneContext_DoesNotLeakIntoAnother()
    {
        new ConnectContext().ResponseHeaders["X-Header"] = "h";

        Assert.Empty(new ConnectContext().ResponseHeaders);
    }

    [Fact]
    public void PublicConstructor_KeepsSuppliedRequestHeaders()
    {
        var headers = new Dictionary<string, string> { ["a"] = "b" };

        Assert.Same(headers, new ConnectContext(headers).RequestHeaders);
        Assert.Empty(new ConnectContext().RequestHeaders);
    }
}
