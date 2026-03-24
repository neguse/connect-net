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
}

public static class ConnectServiceExtensions
{
    public static IServiceCollection AddConnectServices(this IServiceCollection services, Action<ConnectServerOptions>? configure = null)
    {
        services.AddSingleton<ICodec, ProtobufCodec>();
        services.AddSingleton<ICompressor, GzipCompressor>();

        var options = new ConnectServerOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        return services;
    }

    public static void MapConnectService<TService>(
        this IEndpointRouteBuilder builder,
        IConnectServiceDefinition definition)
        where TService : class
    {
        foreach (var method in definition.Methods)
        {
            builder.MapPost(method.Procedure, async (HttpContext context) =>
            {
                var service = context.RequestServices.GetRequiredService<TService>();
                var codec = context.RequestServices.GetRequiredService<ICodec>();

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
            });
        }
    }
}
