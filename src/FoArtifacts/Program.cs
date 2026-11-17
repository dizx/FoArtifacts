using FoArtifacts;
using FoArtifacts.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddSingleton(ReferenceMap.LoadEmbedded());
builder.Services.AddSingleton<ID365fo>(_ => new D365foRunner(
    CliLocator.Resolve(Environment.GetEnvironmentVariable),
    CliLocator.Timeout(Environment.GetEnvironmentVariable)));
builder.Services.AddSingleton<BatchExecutor>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<GenerateFoArtifactsTool>();

await builder.Build().RunAsync();
