using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectNet.Server;

public static class ConnectServiceExtensions
{
    public static IServiceCollection AddConnectServices(this IServiceCollection services)
    {
        services.AddSingleton<ICodec, ProtobufCodec>();
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
                await ConnectUnaryHandler.HandleAsync(context, method, service, codec);
            });
        }
    }
}
