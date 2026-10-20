using AgentReel.Capture;
using AgentReel.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScreenToGif.Core.Capture;

//Before anything touches UserSettings or LocalizationHelper.
WpfResources.Initialize();

var builder = Host.CreateApplicationBuilder(args);

//stdout belongs to the MCP protocol, every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<ICaptureService, WgcCaptureService>();
builder.Services.AddSingleton<RecordingSession>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "agentreel", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
