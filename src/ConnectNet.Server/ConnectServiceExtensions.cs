using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

public class ConnectServerOptions
{
    public List<IServerInterceptor> Interceptors { get; } = new();

    /// <summary>
    /// Maximum allowed size in bytes for incoming messages. 0 means no limit.
    /// When set, messages exceeding this size will be rejected with ResourceExhausted.
    /// </summary>
    public uint MessageReceiveLimit { get; set; }
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

        return services;
    }

    public static void MapConnectService<TService>(
        this IEndpointRouteBuilder builder,
        IConnectServiceDefinition definition)
        where TService : class
    {
        var reflection = builder.ServiceProvider.GetService<ConnectReflectionService>();
        reflection?.AddService(definition.ServiceName);

        foreach (var method in definition.Methods)
        {
            builder.MapPost(method.Procedure, async (HttpContext context) =>
            {
                try
                {
                    var service = context.RequestServices.GetRequiredService<TService>();
                    var registry = context.RequestServices.GetRequiredService<ConnectCodecRegistry>();
                    var codec = ResolveCodecFromContentType(context.Request.ContentType, registry, method.MethodType);

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
                catch (Exception ex)
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

            // Register GET route for unary methods (idempotent/safe RPCs)
            if (method.MethodType == ConnectMethodType.Unary)
            {
                builder.MapGet(method.Procedure, async (HttpContext context) =>
                {
                    var service = context.RequestServices.GetRequiredService<TService>();
                    var registry = context.RequestServices.GetRequiredService<ConnectCodecRegistry>();
                    var codec = ResolveCodecFromEncoding(context.Request.Query, registry);
                    await ConnectUnaryHandler.HandleGetAsync(context, method, service, codec);
                });
            }
        }
    }

    /// <summary>
    /// Resolves the correct codec based on the Content-Type header.
    /// For unary: application/proto, application/json
    /// For streaming: application/connect+proto, application/connect+json
    /// Falls back to the default codec (proto) if content type is unrecognized.
    /// </summary>
    private static ICodec ResolveCodecFromContentType(string? contentType, ConnectCodecRegistry registry, ConnectMethodType methodType)
    {
        if (contentType == null)
            return registry.Default;

        // Streaming content types: application/connect+{codec}
        if (contentType.StartsWith("application/connect+", StringComparison.OrdinalIgnoreCase))
        {
            var codecName = contentType.Substring("application/connect+".Length);
            // Remove any parameters (e.g., ;charset=utf-8)
            var semiIndex = codecName.IndexOf(';');
            if (semiIndex >= 0)
                codecName = codecName.Substring(0, semiIndex);
            return registry.Get(codecName.Trim()) ?? registry.Default;
        }

        // Unary content types: application/{codec}
        if (contentType.StartsWith("application/", StringComparison.OrdinalIgnoreCase))
        {
            var codecName = contentType.Substring("application/".Length);
            var semiIndex = codecName.IndexOf(';');
            if (semiIndex >= 0)
                codecName = codecName.Substring(0, semiIndex);
            return registry.Get(codecName.Trim()) ?? registry.Default;
        }

        return registry.Default;
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
