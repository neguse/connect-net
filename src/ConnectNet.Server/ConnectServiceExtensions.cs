using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ConnectNet.Server;

public class ConnectServerOptions
{
    public List<IServerInterceptor> Interceptors { get; } = new();

    /// <summary>
    /// Maximum allowed size in bytes for incoming messages. 0 means no limit.
    /// When set, messages exceeding this size will be rejected with ResourceExhausted.
    /// Defaults to 4 MiB to provide a safe out-of-the-box value; raise this for services
    /// that legitimately accept larger payloads.
    /// </summary>
    public uint MessageReceiveLimit { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum upper-bound timeout (ms) accepted from a client via Connect-Timeout-Ms.
    /// Defaults to 1 hour. Set to 0 to disable the cap.
    /// </summary>
    public long MaxTimeoutMs { get; set; } = 60L * 60L * 1000L;

    /// <summary>
    /// Maximum time (in milliseconds) the server will wait between two consecutive envelopes
    /// on a streaming request before aborting the call with DeadlineExceeded. Defaults to
    /// <c>0</c> (no per-message idle bound), matching grpc-go / grpc-java defaults that rely
    /// on the transport layer (HTTP/2 PING, TCP keep-alive, Kestrel MinRequestBodyDataRate)
    /// for liveness. Set this to a positive value to defend against slow-loris style
    /// attacks on streaming methods.
    /// </summary>
    public long StreamIdleTimeoutMs { get; set; } = 0;

    /// <summary>
    /// When true, requests must carry a valid <c>Connect-Protocol-Version: 1</c> header
    /// and are rejected with InvalidArgument otherwise. Defaults to false, matching
    /// connect-go's opt-in <c>WithRequireConnectProtocolHeader</c> behavior.
    /// </summary>
    public bool RequireConnectProtocolHeader { get; set; } = false;

    internal int EffectiveReceiveLimit
        => MessageReceiveLimit == 0
            ? int.MaxValue
            : MessageReceiveLimit > int.MaxValue ? int.MaxValue : (int)MessageReceiveLimit;
}

public static class ConnectServiceExtensions
{
    public static IServiceCollection AddConnectServices(this IServiceCollection services, Action<ConnectServerOptions>? configure = null, Google.Protobuf.Reflection.TypeRegistry? typeRegistry = null)
    {
        var registry = new ConnectCodecRegistry();
        registry.Register(new ProtobufCodec());
        registry.Register(typeRegistry != null ? new JsonCodec(typeRegistry) : new JsonCodec());
        services.AddSingleton(registry);
        services.AddSingleton<ICodec>(sp => sp.GetRequiredService<ConnectCodecRegistry>().Default);

        var compressorRegistry = new ConnectCompressorRegistry();
        compressorRegistry.Register(new GzipCompressor());
        compressorRegistry.Register(new DeflateCompressor());
        services.AddSingleton(compressorRegistry);
        // Keep existing ICompressor registration for backward compatibility
        services.AddSingleton<ICompressor, GzipCompressor>();

        var options = new ConnectServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<ConnectReflectionService>();
        services.TryAddSingleton<ConnectServerReflectionImpl>();
        services.TryAddSingleton<ConnectHealthService>();

        return services;
    }

    public static void MapConnectService<TService>(
        this IEndpointRouteBuilder builder,
        IConnectServiceDefinition definition)
        where TService : class
    {
        var reflection = builder.ServiceProvider.GetService<ConnectReflectionService>();
        reflection?.AddService(definition.ServiceName, definition.FileDescriptor);

        foreach (var method in definition.Methods)
        {
            builder.MapPost(method.Procedure, async (HttpContext context) =>
            {
                try
                {
                    var registry = context.RequestServices.GetRequiredService<ConnectCodecRegistry>();

                    // Strict content-type resolution: unknown codecs or wrong framing are
                    // rejected with 415 instead of silently falling back to the default codec.
                    var codec = ConnectServerProtocol.ResolveCodec(context.Request.ContentType, registry, method.MethodType);
                    if (codec == null)
                    {
                        await WriteUnsupportedMediaTypeAsync(context, registry, method.MethodType);
                        return;
                    }

                    var service = context.RequestServices.GetRequiredService<TService>();

                    switch (method.MethodType)
                    {
                        case ConnectMethodType.ServerStreaming:
                            await ConnectServerStreamHandler.HandleAsync(context, method, service, codec);
                            break;
                        case ConnectMethodType.ClientStreaming:
                            await ConnectClientStreamHandler.HandleAsync(context, method, service, codec);
                            break;
                        case ConnectMethodType.BidiStreaming:
                            await ConnectBidiStreamHandler.HandleAsync(context, method, service, codec);
                            break;
                        case ConnectMethodType.Unary:
                        default:
                            await ConnectUnaryHandler.HandleAsync(context, method, service, codec);
                            break;
                    }
                }
                catch (Exception)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.StatusCode = 500;
                        context.Response.ContentType = "application/json";
                        var error = new ConnectException(ConnectCode.Internal, "internal error");
                        await context.Response.WriteAsync(error.ToJson());
                    }
                    // Exception already handled by inner handler
                }
            });

            // Register a GET route only for unary methods explicitly marked side-effect
            // free (idempotency_level = NO_SIDE_EFFECTS). Exposing every unary method over
            // GET would allow CSRF against state-changing RPCs.
            if (method.MethodType == ConnectMethodType.Unary && method.IsNoSideEffects)
            {
                builder.MapGet(method.Procedure, async (HttpContext context) =>
                {
                    try
                    {
                        var service = context.RequestServices.GetRequiredService<TService>();
                        var registry = context.RequestServices.GetRequiredService<ConnectCodecRegistry>();
                        var codec = ResolveCodecFromEncoding(context.Request.Query, registry);
                        await ConnectUnaryHandler.HandleGetAsync(context, method, service, codec);
                    }
                    catch (Exception)
                    {
                        if (!context.Response.HasStarted)
                        {
                            context.Response.StatusCode = 500;
                            context.Response.ContentType = "application/json";
                            var error = new ConnectException(ConnectCode.Internal, "internal error");
                            await context.Response.WriteAsync(error.ToJson());
                        }
                    }
                });
            }
        }
    }

    private static async System.Threading.Tasks.Task WriteUnsupportedMediaTypeAsync(
        HttpContext context, ConnectCodecRegistry registry, ConnectMethodType methodType)
    {
        var response = context.Response;
        response.StatusCode = 415;
        response.Headers["Accept-Post"] = ConnectServerProtocol.BuildAcceptPost(registry, methodType);
        response.ContentType = "application/json";
        var error = new ConnectException(ConnectCode.Unknown,
            $"unsupported content type: {context.Request.ContentType}");
        await response.WriteAsync(error.ToJson());
    }

    /// <summary>
    /// Resolves the correct codec from the encoding query parameter (GET requests).
    /// </summary>
    private static ICodec ResolveCodecFromEncoding(IQueryCollection query, ConnectCodecRegistry registry)
    {
        if (query.TryGetValue("encoding", out var encoding) && !string.IsNullOrEmpty(encoding))
        {
            return registry.Get(encoding!) ?? registry.Default;
        }
        return registry.Default;
    }
}
