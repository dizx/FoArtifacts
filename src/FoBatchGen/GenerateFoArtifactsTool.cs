using System.ComponentModel;
using FoBatchGen.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FoBatchGen;

[McpServerToolType]
public sealed class GenerateFoArtifactsTool(BatchExecutor executor)
{
    public const string ToolDescription =
        "Create many D365 F&O artifacts in one call by driving the d365fo CLI. "
        + "Give bare names (e.g. \"Vehicle\"): the prefix is added to every new object and to references between objects "
        + "in the same batch, never to existing objects such as CustTable or VIN. Tasks run in dependency order with the "
        + "index refreshed between stages; if any task fails, every write of the batch is undone. "
        + "Each task is {type, name, options}: type is a `d365fo generate` subcommand, name is its main argument, and options "
        + "maps option names without -- to a string, number, boolean flag or array (an array repeats the option). "
        + "For extension, options.kind (Table, Form, ...) is required. Do not pass out, install-to, apply-to, output, overwrite, "
        + "no-refresh, add-to, into-role or any out-* option (their writes could not be rolled back). Run `d365fo generate <type> --help` in a terminal for each type's options. "
        + "Use dry_run to see the plan first. "
        + "Supported types: aggregate-entity, business-event, class, coc, composite-entity, configuration-key, control-method, "
        + "custom-service, datasource-method, duty, edt, entity, enum, event-handler, extension, find-methods, form, form-clone, "
        + "form-part, label-file, map, menu, menu-item, migration-script, number-sequence, privilege, query, report, "
        + "report-extension, resource, role, runbase, security-policy, sysoperation, systest, table, table-relation, tile, "
        + "view, workflow, workflow-category.";

    [McpServerTool(Name = "generate_fo_artifacts"), Description(ToolDescription)]
    public async Task<CallToolResult> GenerateFoArtifacts(
        [Description("D365 model to write into, e.g. ContosoFleet. It must be in the d365fo index.")] string model,
        [Description("Prefix added to every new object name, e.g. CFlt. Use \"\" for none.")] string prefix,
        [Description("Artifacts to create, e.g. {\"type\":\"table\",\"name\":\"Vehicle\",\"options\":{\"pattern\":\"main\",\"field\":[\"VehicleId:VehicleId:mandatory\"]}}.")]
        List<TaskInput> tasks,
        [Description("Plan only: return the stages and d365fo commands without writing anything.")] bool dry_run = false,
        CancellationToken cancellationToken = default)
    {
        var result = await executor.RunAsync(new BatchRequest(model, prefix, tasks, dry_run), cancellationToken);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = Summary.ToJson(result) }],
            IsError = !result.Ok,
        };
    }
}
