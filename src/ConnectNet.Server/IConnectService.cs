using System.Collections.Generic;

namespace ConnectNet.Server;

public interface IConnectServiceDefinition
{
    string ServiceName { get; }
    IReadOnlyList<ConnectMethodDescriptor> Methods { get; }
}
