# fo-artifacts

An MCP server for D365 Finance & Operations development. One tool, `generate_fo_artifacts`, creates
a whole feature — enums, EDTs, tables, forms, menu items, security, entities and more — in one call:

- bare names get your model prefix, and so do references between objects in the same batch;
- tasks run in dependency order, with the index refreshed between stages;
- if any task fails, every write of the batch is undone.

It does not generate XML itself. It drives [d365fo-cli](https://github.com/dynamics365ninja/d365fo-cli)
(`d365fo`), which does the per-object generation and keeps the undo journal.

## Requirements

- .NET 10 SDK
- `d365fo` installed and indexed (`d365fo index build`, `d365fo index extract`), with your models
  listed in `D365FO_CUSTOM_MODELS`. Tested against d365fo commit `7b37a250ada8d36688123961dea6a84e451d9333`.
- On the dev VM: `D365FO_BRIDGE_ENABLED=1`, so files are written through the Metadata API and
  `datasource-method` / `control-method` work.

## Install

```powershell
dotnet publish src/FoArtifacts -c Release -o C:\tools\fo-artifacts
```

## Configure your AI client

The server reads the same environment variables as `d365fo`, plus:

| Variable | Meaning | Default |
|---|---|---|
| `D365FO_CLI` | Full path of the `d365fo` executable | `d365fo` on `PATH` |
| `FO_ARTIFACTS_TIMEOUT_SECONDS` | Time limit for each `d365fo` call | `120` |

Claude Code (`.mcp.json` in your X++ repo):
```json
{
  "mcpServers": {
    "fo-artifacts": {
      "command": "C:\\tools\\fo-artifacts\\FoArtifacts.exe",
      "env": {
        "D365FO_CLI": "C:\\tools\\d365fo\\d365fo.exe",
        "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory",
        "D365FO_CUSTOM_MODELS": "ContosoFleet",
        "D365FO_BRIDGE_ENABLED": "1"
      }
    }
  }
}
```

VS Code / Visual Studio (`.vscode/mcp.json` or `.mcp.json`):
```json
{
  "servers": {
    "fo-artifacts": {
      "type": "stdio",
      "command": "C:\\tools\\fo-artifacts\\FoArtifacts.exe",
      "env": { "D365FO_CLI": "C:\\tools\\d365fo\\d365fo.exe", "D365FO_CUSTOM_MODELS": "ContosoFleet", "D365FO_BRIDGE_ENABLED": "1" }
    }
  }
}
```

## Using it

Ask your agent for the feature in plain words ("a Vehicle table with VehicleId and Status, a
list-details form, a menu item and maintain security"). It calls the tool with something like:

```json
{
  "model": "ContosoFleet",
  "prefix": "CFlt",
  "tasks": [
    { "type": "enum",  "name": "VehicleStatus", "options": { "value": ["Active:0", "Retired:1"] } },
    { "type": "edt",   "name": "VehicleId",     "options": { "base-type": "String", "size": 20 } },
    { "type": "table", "name": "Vehicle",       "options": { "pattern": "main", "field": ["VehicleId:VehicleId:mandatory", "Status:VehicleStatus"] } },
    { "type": "form",  "name": "Vehicle",       "options": { "pattern": "SimpleListDetails", "table": "Vehicle", "field": ["VehicleId", "Status"] } },
    { "type": "menu-item", "name": "Vehicle",   "options": { "object": "Vehicle" } },
    { "type": "privilege", "name": "VehicleMaintain", "options": { "entry-point": "Vehicle", "entry-kind": "MenuItemDisplay", "access": "Delete" } },
    { "type": "duty",  "name": "VehicleMaintain", "options": { "privilege": "VehicleMaintain" } },
    { "type": "role",  "name": "VehicleManager",  "options": { "duty": "VehicleMaintain" } }
  ]
}
```

- `type` is any `d365fo generate` subcommand except the deprecated `simple-list`; `options` are its
  options without `--` (`d365fo generate <type> --help` lists them; run it in a terminal).
- `extension` needs `options.kind` (Table, Form, …): it is passed before the name.
- `table-relation` and `find-methods` edit an existing table; fo-artifacts finds its file.
- `dry_run: true` returns the stages and exact `d365fo` commands without writing.

On failure the result names the failing task, d365fo's error and hint, and whether the rollback
succeeded. If the d365fo journal shows writes that are not from the batch (for example you ran
`d365fo` by hand between two of its steps), nothing is undone and the written files are listed instead.
`rolled_back: true` means every file the batch created is gone; files d365fo could not undo (for
example a label file's `.label.txt`, which d365fo does not journal) are listed under `left_behind`.

Known limits:
- `add-to`, `into-role` and `out-*` options are refused: d365fo writes them without a journal entry.
- `table-relation` and `find-methods` only change tables of the model being written; use an
  `extension` task for other models' tables.
- A `d365fo` write to the same model by another process at the exact moment one of the batch's own
  commands runs cannot be told apart from that command's writes, and would be undone with the batch.

## Dev VM checklist

Run once after installing, and after upgrading `d365fo`:

1. `d365fo doctor --output json` reports the index and packages path.
2. Time `d365fo index refresh --model <YourModel>`; the batch runs it once per stage.
3. Ask for a dry run of the Vehicle example above; check the commands.
4. Run it for real, then build the model in Visual Studio: it must build without hand fixes.
5. Run a batch whose last task has a bad option (e.g. `"bogus": "x"`); check that every file from
   the batch is gone and Visual Studio shows nothing new.
6. With the bridge on, run a batch with `datasource-method` and `control-method` tasks on one of your
   forms; check the methods appear, then run the failing batch from step 5 with them in it and check
   the form is restored.

## Development

```bash
dotnet test --project tests/FoArtifacts.Tests
D365FO_CLI="$(scripts/build-d365fo.sh)" dotnet test --project tests/FoArtifacts.IntegrationTests
```

PowerShell (the script prints only the path; don't index into its output with `[-1]`, which picks
the last character of a single string):
```powershell
dotnet test --project tests/FoArtifacts.Tests
$env:D365FO_CLI = .\scripts\build-d365fo.ps1
dotnet test --project tests/FoArtifacts.IntegrationTests
```

Integration tests run the pinned `d365fo` against a copy of its sample model and are skipped when
`D365FO_CLI` is not set. When moving to a newer `d365fo`, change the commit in
`scripts/build-d365fo.*` and `src/FoArtifacts.Core/reference-map.json`, then run both test projects.
