using System.Collections.Generic;
using Google.Protobuf.Reflection;

namespace ConnectNet.Server;

public interface IConnectServiceDefinition
{
    string ServiceName { get; }
    IReadOnlyList<ConnectMethodDescriptor> Methods { get; }

    /// <summary>
    /// FileDescriptor of the proto file that declares this service. Used by the gRPC
    /// Server Reflection endpoint to answer <c>file_containing_symbol</c> /
    /// <c>file_by_filename</c> requests. Default is <c>null</c> (the service is still
    /// listed by <c>list_services</c>, but its schema cannot be fetched).
    /// </summary>
    FileDescriptor? FileDescriptor => null;
}
