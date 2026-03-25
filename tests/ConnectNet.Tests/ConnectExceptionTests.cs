using ConnectNet;
using Xunit;

namespace ConnectNet.Tests;

public class ConnectExceptionTests
{
    [Fact]
    public void ToJson_BasicError()
    {
        var ex = new ConnectException(ConnectCode.InvalidArgument, "bad input");
        var json = ex.ToJson();
        Assert.Contains("\"code\":\"invalid_argument\"", json);
        Assert.Contains("\"message\":\"bad input\"", json);
    }

    [Fact]
    public void FromJson_BasicError()
    {
        var json = "{\"code\":\"not_found\",\"message\":\"missing\"}";
        var ex = ConnectException.FromJson(json);
        Assert.Equal(ConnectCode.NotFound, ex.Code);
        Assert.Equal("missing", ex.Message);
    }

    [Fact]
    public void FromJson_WithDetails()
    {
        var json = "{\"code\":\"internal\",\"message\":\"err\",\"details\":[{\"type\":\"type.googleapis.com/example.Foo\",\"value\":\"AQID\"}]}";
        var ex = ConnectException.FromJson(json);
        Assert.Single(ex.Details);
        Assert.Equal("type.googleapis.com/example.Foo", ex.Details[0].Type);
        Assert.Equal(new byte[] { 1, 2, 3 }, ex.Details[0].Value);
    }

    [Fact]
    public void ToJson_RoundTrips()
    {
        var original = new ConnectException(
            ConnectCode.PermissionDenied, "denied",
            new[] { new ConnectErrorDetail("type.googleapis.com/x", new byte[] { 42 }) });
        var restored = ConnectException.FromJson(original.ToJson());
        Assert.Equal(original.Code, restored.Code);
        Assert.Equal(original.Message, restored.Message);
        Assert.Equal(original.Details.Count, restored.Details.Count);
    }

    [Theory]
    [InlineData(ConnectCode.InvalidArgument, 400)]
    [InlineData(ConnectCode.Unauthenticated, 401)]
    [InlineData(ConnectCode.PermissionDenied, 403)]
    [InlineData(ConnectCode.NotFound, 404)]
    [InlineData(ConnectCode.AlreadyExists, 409)]
    [InlineData(ConnectCode.ResourceExhausted, 429)]
    [InlineData(ConnectCode.FailedPrecondition, 400)]
    [InlineData(ConnectCode.Aborted, 409)]
    [InlineData(ConnectCode.OutOfRange, 400)]
    [InlineData(ConnectCode.Unimplemented, 501)]
    [InlineData(ConnectCode.Internal, 500)]
    [InlineData(ConnectCode.Unavailable, 503)]
    [InlineData(ConnectCode.DataLoss, 500)]
    [InlineData(ConnectCode.DeadlineExceeded, 504)]
    [InlineData(ConnectCode.Canceled, 499)]
    [InlineData(ConnectCode.Unknown, 500)]
    public void ToHttpStatus_MapsCorrectly(ConnectCode code, int expectedStatus)
    {
        Assert.Equal(expectedStatus, ConnectException.ToHttpStatus(code));
    }

    [Fact]
    public void CodeToString_UsesSnakeCase()
    {
        Assert.Equal("invalid_argument", ConnectException.CodeToString(ConnectCode.InvalidArgument));
        Assert.Equal("not_found", ConnectException.CodeToString(ConnectCode.NotFound));
        Assert.Equal("deadline_exceeded", ConnectException.CodeToString(ConnectCode.DeadlineExceeded));
    }

    [Fact]
    public void CodeFromString_ParsesSnakeCase()
    {
        Assert.Equal(ConnectCode.InvalidArgument, ConnectException.CodeFromString("invalid_argument"));
        Assert.Equal(ConnectCode.NotFound, ConnectException.CodeFromString("not_found"));
    }

    [Fact]
    public void CodeFromString_Unknown_ForInvalidInput()
    {
        Assert.Equal(ConnectCode.Unknown, ConnectException.CodeFromString("bogus"));
    }

    [Fact]
    public void TryFromJson_NullJsonValue_ReturnsNull()
    {
        var result = ConnectException.TryFromJson("null");
        Assert.Null(result);
    }

    [Fact]
    public void FromJson_MissingCode_DefaultsToUnknown()
    {
        var ex = ConnectException.FromJson("{\"message\":\"oops\"}");
        Assert.Equal(ConnectCode.Unknown, ex.Code);
        Assert.Equal("oops", ex.Message);
    }

    [Fact]
    public void FromJson_NullCode_DefaultsToUnknown()
    {
        var ex = ConnectException.FromJson("{\"code\":null,\"message\":\"oops\"}");
        Assert.Equal(ConnectCode.Unknown, ex.Code);
    }

    [Fact]
    public void FromJson_UnrecognizedCode_DefaultsToUnknown()
    {
        var ex = ConnectException.FromJson("{\"code\":\"bogus_code\",\"message\":\"oops\"}");
        Assert.Equal(ConnectCode.Unknown, ex.Code);
    }

    [Fact]
    public void FromJson_MissingMessage_DefaultsToEmpty()
    {
        var ex = ConnectException.FromJson("{\"code\":\"internal\"}");
        Assert.Equal(ConnectCode.Internal, ex.Code);
        Assert.Equal("", ex.Message);
    }

    [Fact]
    public void FromJson_NullMessage_DefaultsToEmpty()
    {
        var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":null}");
        Assert.Equal("", ex.Message);
    }

    [Fact]
    public void FromJson_UnrecognizedFields_Ignored()
    {
        var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"extra\":123}");
        Assert.Equal(ConnectCode.Internal, ex.Code);
    }

    [Fact]
    public void FromJson_NullDetails_Ignored()
    {
        var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"details\":null}");
        Assert.Empty(ex.Details);
    }

    [Fact]
    public void FromJson_DetailsWithDebugField_Ignored()
    {
        var ex = ConnectException.FromJson("{\"code\":\"internal\",\"message\":\"err\",\"details\":[{\"type\":\"t\",\"value\":\"AQ\",\"debug\":{}}]}");
        Assert.Single(ex.Details);
    }

    [Fact]
    public void TryFromJson_EmptyString_ReturnsNull()
    {
        Assert.Null(ConnectException.TryFromJson(""));
        Assert.Null(ConnectException.TryFromJson("   "));
    }

    [Fact]
    public void TryFromJson_InvalidJson_ReturnsNull()
    {
        Assert.Null(ConnectException.TryFromJson("not json"));
    }
}
