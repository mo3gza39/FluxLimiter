using FluxLimiter.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "FluxLimiter Service");
builder.Services.AddSingleton<DriverBridge>();
builder.Services.AddHostedService<PipeServerWorker>();

await builder.Build().RunAsync();
