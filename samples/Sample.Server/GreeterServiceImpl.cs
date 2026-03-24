using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;

namespace Sample.Server;

public class GreeterServiceImpl
{
    public Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
    {
        if (string.IsNullOrEmpty(request.Name))
            throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

        return Task.FromResult(new HelloResponse
        {
            Message = $"Hello {request.Name} from connect-net!"
        });
    }
}

public class GreeterServiceDefinition : IConnectServiceDefinition
{
    public static GreeterServiceDefinition Instance { get; } = new();
    public string ServiceName => "example.GreeterService";
    public System.Collections.Generic.IReadOnlyList<ConnectMethodDescriptor> Methods { get; } = new[]
    {
        new ConnectMethodDescriptor(
            "/example.GreeterService/SayHello",
            HelloRequest.Parser,
            async (service, req, ctx) => (IMessage)await ((GreeterServiceImpl)service)
                .SayHello((HelloRequest)req, ctx))
    };
}
