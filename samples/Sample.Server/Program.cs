using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using ConnectNet.Server;
using Sample.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddConnectServices();
builder.Services.AddSingleton<GreeterServiceImpl>();

var app = builder.Build();
app.MapConnectService<GreeterServiceImpl>(GreeterServiceDefinition.Instance);

app.Run();
