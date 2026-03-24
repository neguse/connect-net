using System;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Connectrpc.Conformance.V1;
using ConnectNet.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectNet.Conformance;

internal static class ServerHarness
{
    public static async Task RunAsync()
    {
        // 1. Read ServerCompatRequest from stdin
        var request = StdioProtobuf.Read<ServerCompatRequest>(Console.OpenStandardInput());
        if (request == null)
            throw new InvalidOperationException("Failed to read ServerCompatRequest from stdin");

        // 2. Build the ASP.NET Core app
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders(); // Avoid stdout pollution

        // Configure Kestrel to listen on port 0
        builder.WebHost.UseKestrel(options =>
        {
            if (request.UseTls && request.ServerCreds != null)
            {
                var cert = X509Certificate2.CreateFromPem(
                    System.Text.Encoding.UTF8.GetString(request.ServerCreds.Cert.Span),
                    System.Text.Encoding.UTF8.GetString(request.ServerCreds.Key.Span));
                // Some platforms require export/re-import for ephemeral keys
                cert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);

                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.UseHttps(cert);
                    ConfigureProtocols(listenOptions, request.HttpVersion);
                });
            }
            else
            {
                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    ConfigureProtocols(listenOptions, request.HttpVersion);
                });
            }
        });

        builder.Services.AddConnectServices();
        builder.Services.AddSingleton<ConformanceServiceImpl>();

        var app = builder.Build();

        app.MapConnectService<ConformanceServiceImpl>(ConformanceServiceDefinition.Instance);

        await app.StartAsync();

        // 5. Get actual listening port
        var server = app.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()!;
        var address = addressFeature.Addresses.First();
        var uri = new Uri(address);
        var port = (uint)uri.Port;

        // 6. Write ServerCompatResponse to stdout
        var response = new ServerCompatResponse
        {
            Host = "127.0.0.1",
            Port = port,
        };
        if (request.UseTls && request.ServerCreds != null)
        {
            response.PemCert = request.ServerCreds.Cert;
        }
        StdioProtobuf.Write(Console.OpenStandardOutput(), response);

        // 7. Wait for stdin EOF
        await Task.Run(() =>
        {
            var stdin = Console.OpenStandardInput();
            while (stdin.ReadByte() != -1) { }
        });

        await app.StopAsync();
    }

    private static void ConfigureProtocols(ListenOptions options, HTTPVersion httpVersion)
    {
        switch (httpVersion)
        {
            case HTTPVersion._1:
                options.Protocols = HttpProtocols.Http1;
                break;
            case HTTPVersion._2:
                options.Protocols = HttpProtocols.Http1AndHttp2;
                break;
            default:
                options.Protocols = HttpProtocols.Http1AndHttp2;
                break;
        }
    }
}
