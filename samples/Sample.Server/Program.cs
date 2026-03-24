using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ConnectNet.Server;
using ConnectNet.Tests.Proto;
using Sample.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConnectServices();
builder.Services.AddSingleton<GreeterServiceImpl>();
builder.Services.AddSingleton<ConnectHealthService>();

var app = builder.Build();
app.MapConnectService<GreeterServiceImpl>(GreeterServiceDefinition.Instance);
app.MapConnectHealthCheck();
app.MapConnectReflection();

app.Run();
