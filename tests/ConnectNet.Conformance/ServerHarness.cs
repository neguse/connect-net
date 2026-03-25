using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Connectrpc.Conformance.V1;
using ConnectNet.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ConnectNet.Conformance;

internal static class ServerHarness
{
    public static async Task RunAsync()
    {
        // Open stdin ONCE and hold a reference for the lifetime of the process.
        var stdin = Console.OpenStandardInput();

        // 1. Read ServerCompatRequest from stdin
        var request = StdioProtobuf.Read<ServerCompatRequest>(stdin);
        if (request == null)
            throw new InvalidOperationException("Failed to read ServerCompatRequest from stdin");

        // Diagnostic logging to stderr (does not affect protocol)

        // 2. Build the ASP.NET Core app
        var builder = WebApplication.CreateBuilder(new string[0]);
        builder.Logging.ClearProviders(); // Suppress all stdout logging

        // Configure Kestrel to listen on port 0
        builder.WebHost.ConfigureKestrel(options =>
        {
            if (request.UseTls && request.ServerCreds != null)
            {
                var cert = X509Certificate2.CreateFromPem(
                    System.Text.Encoding.UTF8.GetString(request.ServerCreds.Cert.Span),
                    System.Text.Encoding.UTF8.GetString(request.ServerCreds.Key.Span));
                cert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pkcs12), null);

                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    listenOptions.UseHttps(cert);
                    ConfigureProtocols(listenOptions, request.HttpVersion, useTls: true);
                });
            }
            else
            {
                options.Listen(IPAddress.Loopback, 0, listenOptions =>
                {
                    ConfigureProtocols(listenOptions, request.HttpVersion, useTls: false);
                });
            }

            // Disable MaxRequestBodySize — conformance message_receive_limit applies
            // to individual messages, not to the HTTP body
            options.Limits.MaxRequestBodySize = null;

            // Allow synchronous IO for compatibility
            options.AllowSynchronousIO = true;
        });

        // Configure JSON codec with type registry for Any-packed types
        var typeRegistry = Google.Protobuf.Reflection.TypeRegistry.FromFiles(
            Connectrpc.Conformance.V1.ServiceReflection.Descriptor,
            Connectrpc.Conformance.V1.ConfigReflection.Descriptor);
        builder.Services.AddConnectServices(options =>
        {
            if (request.MessageReceiveLimit > 0)
            {
                options.MessageReceiveLimit = request.MessageReceiveLimit;
            }
        }, typeRegistry: typeRegistry);
        builder.Services.AddSingleton<ConformanceServiceImpl>();

        var app = builder.Build();

        app.MapConnectService<ConformanceServiceImpl>(ConformanceServiceDefinition.Instance);

        await app.StartAsync();

        // 5. Get actual listening port via IServerAddressesFeature
        var server = app.Services.GetRequiredService<IServer>();
        var addressFeature = server.Features.Get<IServerAddressesFeature>()!;
        var address = addressFeature.Addresses.First();
        var uri = new Uri(address);
        var port = (uint)uri.Port;

        // Diagnostic only

        // 6. Write exactly ONE ServerCompatResponse to stdout
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

        // 7. Wait until the process is killed (SIGTERM) by the runner.
        // The conformance runner closes stdin immediately after reading
        // the response, then runs HTTP tests against the server, and
        // finally sends SIGTERM to stop the process.
        // Use IHostApplicationLifetime to wait for shutdown signal.
        var lifetime = app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>();
        var tcs = new TaskCompletionSource();
        lifetime.ApplicationStopping.Register(() => tcs.TrySetResult());
        await tcs.Task;
    }

    private static void ConfigureProtocols(ListenOptions options, HTTPVersion httpVersion, bool useTls)
    {
        switch (httpVersion)
        {
            case HTTPVersion._1:
                if (useTls)
                {
                    // TLS with ALPN: support both HTTP/1.1 and HTTP/2
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                }
                else
                {
                    // Cleartext HTTP/1.1
                    options.Protocols = HttpProtocols.Http1;
                }
                break;
            case HTTPVersion._2:
                if (useTls)
                {
                    // TLS with ALPN: support both HTTP/1.1 and HTTP/2
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                }
                else
                {
                    // Cleartext HTTP/2 (h2c) with prior knowledge
                    options.Protocols = HttpProtocols.Http2;
                }
                break;
            default:
                if (useTls)
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                else
                    options.Protocols = HttpProtocols.Http1AndHttp2;
                break;
        }
    }
}
