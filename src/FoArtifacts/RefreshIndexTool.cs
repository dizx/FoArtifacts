using System.ComponentModel;
using FoArtifacts.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FoArtifacts;

[McpServerToolType]
public sealed class RefreshIndexTool(IndexRefresh refresh, ModelLock gate)
{
    public const string ToolDescription =
        "Re-read one model into the d365fo index with `d365fo index refresh --model`, after changes made outside "
        + "fo-artifacts (a git pull, edits in Visual Studio) or to add a new model. d365fo skips a model whose files have "
        + "not changed (refreshed: false); force re-reads it anyway. generate_fo_artifacts already refreshes its own model, "
        + "so this is not needed after a batch.";

    [McpServerTool(Name = "refresh_index"), Description(ToolDescription)]
    public async Task<CallToolResult> RefreshIndex(
        [Description("Model to re-read, e.g. ContosoFleet.")] string model,
        [Description("Re-read the model even if d365fo thinks its files have not changed.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        RefreshReport report;
        using (await gate.AcquireAsync(cancellationToken))
            report = await refresh.RunAsync(model, force, cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summary.ToJson(report) }],
            IsError = !report.Ok,
        };
    }
}
