using FoArtifacts;
using FoArtifacts.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; all logging goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var cliPath = CliLocator.Resolve(Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(ReferenceMap.LoadEmbedded());
builder.Services.AddSingleton<ID365fo>(_ => new D365foRunner(cliPath, CliLocator.Timeout(Environment.GetEnvironmentVariable)));
builder.Services.AddSingleton<ModelLock>();
builder.Services.AddSingleton(sp => new ModelCheck(sp.GetRequiredService<ID365fo>()));
// Builds get their own runner: a large model compiles for minutes, far past the per-call limit for generate.
builder.Services.AddSingleton(sp => new ModelBuild(
    new D365foRunner(cliPath, CliLocator.BuildTimeout(Environment.GetEnvironmentVariable)),
    sp.GetRequiredService<ModelCheck>()));
builder.Services.AddSingleton(sp => new IndexRefresh(sp.GetRequiredService<ID365fo>(), sp.GetRequiredService<ModelCheck>()));
builder.Services.AddSingleton(sp => new BatchExecutor(
    sp.GetRequiredService<ID365fo>(),
    sp.GetRequiredService<ReferenceMap>(),
    sp.GetRequiredService<ModelLock>(),
    sp.GetRequiredService<ModelBuild>()));
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<GenerateFoArtifactsTool>()
    .WithTools<BuildModelTool>()
    .WithTools<RefreshIndexTool>();

await builder.Build().RunAsync();
