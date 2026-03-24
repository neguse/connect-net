using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ConnectNet;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Google.Protobuf;

namespace Sample.Server;

public class GreeterServiceImpl : GreeterServiceBase
{
    // Unary RPC: single request, single response
    public override Task<HelloResponse> SayHello(HelloRequest request, ConnectContext context)
    {
        if (string.IsNullOrEmpty(request.Name))
            throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

        return Task.FromResult(new HelloResponse
        {
            Message = $"Hello {request.Name} from connect-net!"
        });
    }

    // Server streaming RPC: single request, stream of responses
    public override async IAsyncEnumerable<HelloResponse> SayHelloStream(
        HelloRequest request,
        ConnectContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.Name))
            throw new ConnectException(ConnectCode.InvalidArgument, "name is required");

        for (int i = 1; i <= 5; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return new HelloResponse
            {
                Message = $"Hello {request.Name} #{i} from connect-net!"
            };
            await Task.Delay(100, ct);
        }
    }

    // Client streaming RPC: stream of requests, single response
    public override async Task<HelloResponse> CollectHellos(
        IAsyncEnumerable<HelloRequest> requests,
        ConnectContext context)
    {
        var names = new System.Collections.Generic.List<string>();
        await foreach (var request in requests)
        {
            if (!string.IsNullOrEmpty(request.Name))
                names.Add(request.Name);
        }

        return new HelloResponse
        {
            Message = $"Hello {string.Join(", ", names)} from connect-net!"
        };
    }

    // Bidirectional streaming RPC: stream of requests, stream of responses
    public override async IAsyncEnumerable<HelloResponse> Chat(
        IAsyncEnumerable<HelloRequest> requests,
        ConnectContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var request in requests.WithCancellation(ct))
        {
            yield return new HelloResponse
            {
                Message = $"Echo: {request.Name}"
            };
        }
    }
}
