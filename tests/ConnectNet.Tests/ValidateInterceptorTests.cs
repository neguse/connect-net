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
}
