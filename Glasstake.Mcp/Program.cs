using Glasstake.Capture;
using Glasstake.Editing;
using Glasstake.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

//Recordings of earlier versions, copied once.
LegacyImport.RunOnce();

var builder = Host.CreateApplicationBuilder(args);

//stdout belongs to the MCP protocol, every log line goes to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton<ICaptureService, WgcCaptureService>();
builder.Services.AddSingleton<RecordingSession>();

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "glasstake", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly();

await builder.Build().RunAsync();
