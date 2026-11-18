using System.ComponentModel;
using FoArtifacts.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FoArtifacts;

[McpServerToolType]
public sealed class BuildModelTool(ModelBuild build, ModelLock gate)
{
    public const string ToolDescription =
        "Compile one custom D365 F&O model with `d365fo build --model` and return the compiler errors and warnings "
        + "(at most 50 errors and the first 20 warnings; error_count and warning_count are always complete). Use it after "
        + "fixing errors a batch's build reported, or after changes made outside fo-artifacts. It waits while a batch is "
        + "writing. Models d365fo does not mark custom are refused.";

    [McpServerTool(Name = "build_model"), Description(ToolDescription)]
    public async Task<CallToolResult> BuildModel(
        [Description("Custom model to compile, e.g. ContosoFleet.")] string model,
        CancellationToken cancellationToken = default)
    {
        BuildReport report;
        using (await gate.AcquireAsync(cancellationToken))
            report = await build.RunAsync(model, null, cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summary.ToJson(model, report) }],
            IsError = report.Succeeded != true,
        };
    }
}
