using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Reflection.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

/// <summary>
/// Registry of services exposed through reflection. <see cref="ConnectServiceExtensions.MapConnectService{TService}"/>
/// populates it automatically (including each service's <see cref="Google.Protobuf.Reflection.FileDescriptor"/>
/// when the generated code provides one); additional entries can be added manually.
/// </summary>
public class ConnectReflectionService
{
    // ConcurrentDictionary-based storage so AddService can be called during startup while
    // request threads enumerate Services concurrently without corruption.
    private readonly ConcurrentDictionary<string, FileDescriptor?> _services = new();
    private int _version;
    private volatile IndexCache? _indexCache;

    public void AddService(string serviceName) => AddService(serviceName, null);

    public void AddService(string serviceName, FileDescriptor? fileDescriptor)
    {
        _services[serviceName] = fileDescriptor;
        Interlocked.Increment(ref _version);
    }

    public IReadOnlyList<string> Services
        => _services.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList().AsReadOnly();

    /// <summary>
    /// Returns the current lookup index, rebuilding it only when the registered service
    /// set has changed since the last build.
    /// </summary>
    internal ReflectionIndex GetIndex()
    {
        var version = Volatile.Read(ref _version);
        var cache = _indexCache;
        if (cache != null && cache.Version == version)
            return cache.Index;

        var index = ReflectionIndex.Build(_services.Values);
        _indexCache = new IndexCache(version, index);
        return index;
    }

    private sealed class IndexCache
    {
        public IndexCache(int version, ReflectionIndex index)
        {
            Version = version;
            Index = index;
        }

        public int Version { get; }
        public ReflectionIndex Index { get; }
    }
}

/// <summary>
/// Immutable lookup tables built from the registered FileDescriptors: file name → descriptor
/// and fully-qualified symbol → descriptor, both covering the transitive dependency closure.
/// </summary>
internal sealed class ReflectionIndex
{
    private readonly Dictionary<string, FileDescriptor> _filesByName;
    private readonly Dictionary<string, FileDescriptor> _filesBySymbol;

    private ReflectionIndex(
        Dictionary<string, FileDescriptor> filesByName,
        Dictionary<string, FileDescriptor> filesBySymbol)
    {
        _filesByName = filesByName;
        _filesBySymbol = filesBySymbol;
    }

    public bool TryGetFileByName(string name, out FileDescriptor file)
        => _filesByName.TryGetValue(name, out file!);

    public bool TryGetFileBySymbol(string symbol, out FileDescriptor file)
        => _filesBySymbol.TryGetValue(symbol, out file!);

    public static ReflectionIndex Build(IEnumerable<FileDescriptor?> descriptors)
    {
        var filesByName = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);
        var filesBySymbol = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            if (descriptor != null)
                AddFile(descriptor, filesByName, filesBySymbol);
        }

        return new ReflectionIndex(filesByName, filesBySymbol);
    }

    private static void AddFile(
        FileDescriptor file,
        Dictionary<string, FileDescriptor> filesByName,
        Dictionary<string, FileDescriptor> filesBySymbol)
    {
        if (!filesByName.TryAdd(file.Name, file))
            return; // already indexed (shared dependency)

        foreach (var service in file.Services)
        {
            filesBySymbol.TryAdd(service.FullName, file);
            foreach (var method in service.Methods)
                filesBySymbol.TryAdd(method.FullName, file);
        }

        foreach (var message in file.MessageTypes)
            AddMessage(message, file, filesBySymbol);

        foreach (var enumType in file.EnumTypes)
            AddEnum(enumType, file, filesBySymbol);

        foreach (var dependency in file.Dependencies)
            AddFile(dependency, filesByName, filesBySymbol);
    }

    private static void AddMessage(
        MessageDescriptor message,
        FileDescriptor file,
        Dictionary<string, FileDescriptor> filesBySymbol)
    {
        filesBySymbol.TryAdd(message.FullName, file);

        foreach (var field in message.Fields.InDeclarationOrder())
            filesBySymbol.TryAdd(field.FullName, file);

        foreach (var nested in message.NestedTypes)
            AddMessage(nested, file, filesBySymbol);

        foreach (var enumType in message.EnumTypes)
            AddEnum(enumType, file, filesBySymbol);
    }

    private static void AddEnum(
        EnumDescriptor enumType,
        FileDescriptor file,
        Dictionary<string, FileDescriptor> filesBySymbol)
    {
        filesBySymbol.TryAdd(enumType.FullName, file);

        // Per protobuf scoping rules an enum value lives in the enum's *enclosing* scope
        // ("pkg.VALUE" for a file-level enum, "pkg.Msg.VALUE" for a nested one).
        var fullName = enumType.FullName;
        var lastDot = fullName.LastIndexOf('.');
        var scope = lastDot >= 0 ? fullName.Substring(0, lastDot + 1) : "";
        foreach (var value in enumType.Values)
            filesBySymbol.TryAdd(scope + value.Name, file);
    }
}

/// <summary>
/// Implementation of the standard gRPC Server Reflection protocol
/// (<c>grpc.reflection.v1.ServerReflection/ServerReflectionInfo</c>, bidi streaming),
/// served over the Connect protocol through the regular streaming pipeline.
/// </summary>
internal sealed class ConnectServerReflectionImpl
{
    // gRPC status codes carried in ErrorResponse.error_code.
    private const int GrpcInvalidArgument = 3;
    private const int GrpcNotFound = 5;
    private const int GrpcInternal = 13;

    private readonly ConnectReflectionService _registry;

    public ConnectServerReflectionImpl(ConnectReflectionService registry) => _registry = registry;

    public async IAsyncEnumerable<IMessage> ServerReflectionInfo(
        IAsyncEnumerable<IMessage> requests,
        ConnectContext context,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var message in requests.WithCancellation(ct))
        {
            yield return Process((ServerReflectionRequest)message);
        }
    }

    private ServerReflectionResponse Process(ServerReflectionRequest request)
    {
        var response = new ServerReflectionResponse
        {
            ValidHost = request.Host,
            OriginalRequest = request
        };

        try
        {
            switch (request.MessageRequestCase)
            {
                case ServerReflectionRequest.MessageRequestOneofCase.ListServices:
                    var list = new ListServiceResponse();
                    foreach (var name in _registry.Services)
                        list.Service.Add(new ServiceResponse { Name = name });
                    response.ListServicesResponse = list;
                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileByFilename:
                    if (_registry.GetIndex().TryGetFileByName(request.FileByFilename, out var byName))
                        response.FileDescriptorResponse = BuildFileDescriptorResponse(byName);
                    else
                        response.ErrorResponse = Error(GrpcNotFound, $"file not found: {request.FileByFilename}");
                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileContainingSymbol:
                    if (_registry.GetIndex().TryGetFileBySymbol(request.FileContainingSymbol, out var bySymbol))
                        response.FileDescriptorResponse = BuildFileDescriptorResponse(bySymbol);
                    else
                        response.ErrorResponse = Error(GrpcNotFound, $"symbol not found: {request.FileContainingSymbol}");
                    break;

                case ServerReflectionRequest.MessageRequestOneofCase.FileContainingExtension:
                case ServerReflectionRequest.MessageRequestOneofCase.AllExtensionNumbersOfType:
                    response.ErrorResponse = Error(12 /* UNIMPLEMENTED */, "extension reflection is not supported");
                    break;

                default:
                    response.ErrorResponse = Error(GrpcInvalidArgument, "message_request is not set");
                    break;
            }
        }
        catch (Exception)
        {
            // Reflection is best-effort: never let a malformed descriptor lookup take
            // down the stream, report it as an in-band reflection error instead.
            response.ErrorResponse = Error(GrpcInternal, "failed to process reflection request");
        }

        return response;
    }

    /// <summary>
    /// Serializes the file plus its full transitive dependency closure (deduplicated) so
    /// the client can rebuild a descriptor pool from the response alone.
    /// </summary>
    private static FileDescriptorResponse BuildFileDescriptorResponse(FileDescriptor root)
    {
        var response = new FileDescriptorResponse();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        AddWithDependencies(root);
        return response;

        void AddWithDependencies(FileDescriptor file)
        {
            if (!visited.Add(file.Name))
                return;
            response.FileDescriptorProto.Add(file.SerializedData);
            foreach (var dependency in file.Dependencies)
                AddWithDependencies(dependency);
        }
    }

    private static ErrorResponse Error(int code, string message)
        => new() { ErrorCode = code, ErrorMessage = message };
}

/// <summary>
/// Connect service definition for <c>ServerReflectionInfo</c>. The same handler is exposed
/// twice: as <c>grpc.reflection.v1.ServerReflection</c> and, for older clients, under the
/// <c>grpc.reflection.v1alpha</c> route (the request/response messages are wire-compatible,
/// so the v1alpha route reuses the v1 message types).
/// </summary>
internal sealed class ServerReflectionDefinition : IConnectServiceDefinition
{
    public static ServerReflectionDefinition V1 { get; } = new(
        "grpc.reflection.v1.ServerReflection",
        Grpc.Reflection.V1.ReflectionReflection.Descriptor);

    public static ServerReflectionDefinition V1Alpha { get; } = new(
        "grpc.reflection.v1alpha.ServerReflection",
        Grpc.Reflection.V1Alpha.ReflectionReflection.Descriptor);

    private ServerReflectionDefinition(string serviceName, FileDescriptor fileDescriptor)
    {
        ServiceName = serviceName;
        FileDescriptor = fileDescriptor;
        Methods = new[]
        {
            new ConnectMethodDescriptor(
                $"/{serviceName}/ServerReflectionInfo",
                ServerReflectionRequest.Parser,
                methodType: ConnectMethodType.BidiStreaming,
                bidiStreamHandler: (svc, requests, ctx)
                    => ((ConnectServerReflectionImpl)svc).ServerReflectionInfo(requests, ctx))
        };
    }

    public string ServiceName { get; }
    public IReadOnlyList<ConnectMethodDescriptor> Methods { get; }
    public FileDescriptor? FileDescriptor { get; }
}

public static class ConnectReflectionExtensions
{
    /// <summary>
    /// Maps the service discovery endpoints:
    /// <list type="bullet">
    /// <item><c>GET /connect/v1/services</c> — a Connect-native JSON listing.</item>
    /// <item><c>POST /grpc.reflection.v1.ServerReflection/ServerReflectionInfo</c> (and the
    /// <c>v1alpha</c> alias) — the standard gRPC Server Reflection protocol served over the
    /// Connect streaming protocol (<c>application/connect+proto</c> / <c>application/connect+json</c>).
    /// Supports <c>list_services</c>, <c>file_containing_symbol</c> and <c>file_by_filename</c>;
    /// extension lookups answer <c>error_response</c> with <c>UNIMPLEMENTED</c>. Connect
    /// protocol reflection clients (e.g. <c>buf curl --protocol connect --reflect</c>)
    /// interoperate; clients that only speak native gRPC framing (grpcurl's default) do not.</item>
    /// </list>
    /// </summary>
    public static void MapConnectReflection(this IEndpointRouteBuilder builder)
    {
        // Connect-native JSON endpoint for easy service discovery
        builder.MapGet("/connect/v1/services", (HttpContext httpContext) =>
        {
            var reflection = httpContext.RequestServices.GetRequiredService<ConnectReflectionService>();
            httpContext.Response.ContentType = "application/json";
            return httpContext.Response.WriteAsync(
                JsonSerializer.Serialize(new { services = reflection.Services }));
        });

        // Standard gRPC Server Reflection (bidi streaming) through the regular Connect
        // pipeline, so content types, envelopes, compression and EndStream behave exactly
        // like any other streaming RPC.
        builder.MapConnectService<ConnectServerReflectionImpl>(ServerReflectionDefinition.V1);
        builder.MapConnectService<ConnectServerReflectionImpl>(ServerReflectionDefinition.V1Alpha);
    }
}
