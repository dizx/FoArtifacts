# fo-batchgen — design

Date: 2026-11-17
Status: draft for review

## 1. Purpose

A personal MCP tool for D365 Finance & Operations development. One call, `generate_fo_artifacts`,
takes a whole feature described as typed tasks (enums, EDTs, tables, forms, menu items, security,
entities, …) and writes every artifact into a model — in dependency order, with the model prefix
applied, all-or-nothing.

It is inspired by xplusplus.ai's `generate_fo_artifacts`, but it does not generate XML itself.
It orchestrates the open-source `d365fo` CLI (github.com/dynamics365ninja/d365fo-cli), which
already has the per-object generators, the metadata index, the form-pattern gate and an undo
journal. What `d365fo` lacks, and this tool adds, is the batch layer:

- one call for many artifacts,
- automatic prefixing of bare names, including references between artifacts in the batch,
- dependency ordering, with the index refreshed between stages so later artifacts see earlier ones,
- full rollback when any step fails,
- one compact summary instead of one response per object.

**User:** the author, on their own D365 dev VM, driven by an AI coding agent (Claude Code, Copilot,
Cursor, …). Not a product: no API keys, plans, billing or hosting.

**Success:** a feature batch (e.g. enum + EDT + table + form + menu item + privilege + duty + role)
from one call lands in the model, builds in Visual Studio without hand-fixing, and every
artifact is findable with `d365fo search`/`get` afterwards.

## 2. Evidence from the feasibility spike (2026-11-16)

Run against `d365fo` at commit `640406b357b5fc9bc33ba499c8ec325c5b8a5c5f`, on Linux, using the
repo's `tests/Samples/MiniAot` model registered as custom (`D365FO_CUSTOM_MODELS`), bridge disabled.

- Reads of existing custom elements work: `search`, `get table|edt|enum|form`, `models list`.
- An 8-object batch (enum, EDT, table, form, display menu item, privilege, duty, role) generated with
  `--install-to <model> --no-refresh` plus `index refresh --model <model>` after each stage:
  all `ok`, enum field typed `AxTableFieldEnum`, form passes `form-pattern validate` (0/0),
  `security coverage` resolves role → duty → privilege → menu item. 7.7 s total, ~0.5 s per refresh.
- **Without** the refresh between stages, an enum-typed field is written as an EDT string field
  (`EDT_BASETYPE_UNRESOLVED`) and the form warns its table is unknown. The refresh is mandatory.
- `d365fo undo --steps N` reverted every write and deleted the files. The journal recorded 9 entries
  for 8 tasks (a command can journal more than one write), so rollback cannot count tasks.
- `d365fo <cmd> --help` prints nothing useful when stdout is piped, so options cannot be discovered
  from help at runtime.
- `search any` does not return security objects; `security role|duty|privilege|coverage` does.
- `d365fo` registers 42 `generate` subcommands (its README says 43); `simple-list` is a deprecated
  alias of `form`.

## 3. Scope

In scope:

- An MCP server (stdio) exposing one tool, `generate_fo_artifacts`.
- All 41 non-deprecated `d365fo generate` subcommands as task types.
- Dry run (plan without writing).

Out of scope: building, DB sync, deployment packages, label translation, editing existing objects
outside what a `generate` subcommand does, multi-user/shared hosting, its own XML templates.

## 4. Architecture

A .NET 10 C# solution (the same runtime `d365fo` already needs on the VM):

```
fo-batchgen/
  src/FoBatchGen.Core/        logic, no MCP dependency
  src/FoBatchGen/             MCP server host (console app, stdio)
  tests/FoBatchGen.Tests/              unit tests
  tests/FoBatchGen.IntegrationTests/   runs the real d365fo against a fixture model
```

`d365fo` is located by the `D365FO_CLI` environment variable (path to the executable), else `d365fo`
on `PATH`. The server passes its own environment through, so `D365FO_PACKAGES_PATH`,
`D365FO_CUSTOM_MODELS`, `D365FO_INDEX_DB`, `D365FO_BRIDGE_ENABLED`, etc. are configured once in the
MCP client config.

### 4.1 Tool contract

Input:

```json
{
  "model": "ContosoFleet",
  "prefix": "CFlt",
  "dry_run": false,
  "tasks": [
    { "type": "enum",  "name": "VehicleStatus", "options": { "value": ["Active:0", "Retired:1"] } },
    { "type": "edt",   "name": "VehicleId",     "options": { "base-type": "String", "size": 20 } },
    { "type": "table", "name": "Vehicle",       "options": { "pattern": "main",
        "field": ["VehicleId:VehicleId:mandatory", "Status:VehicleStatus", "Vin:VIN"] } },
    { "type": "form",  "name": "Vehicle",       "options": { "pattern": "SimpleListDetails",
        "table": "Vehicle", "field": ["VehicleId", "Status"] } },
    { "type": "menu-item", "name": "Vehicle",   "options": { "kind": "Display", "object": "Vehicle" } },
    { "type": "privilege", "name": "VehicleMaintain",
        "options": { "entry-point": "Vehicle", "entry-kind": "MenuItemDisplay", "access": "Delete" } },
    { "type": "duty",  "name": "VehicleMaintain", "options": { "privilege": "VehicleMaintain" } },
    { "type": "role",  "name": "VehicleManager",  "options": { "duty": "VehicleMaintain" } }
  ]
}
```

- `model` (required): target model name, passed as `--install-to`.
- `prefix` (required, may be empty string to disable prefixing).
- `dry_run` (optional, default false).
- `tasks` (required, 1–500). Each task:
  - `type`: one of the 41 supported subcommands.
  - `name`: the command's main positional argument. What it means depends on the subcommand
    (declared in `ReferenceMap`, see 4.2):
    - *define* (most commands): the new object's bare name; it is prefixed.
    - *reference* (`table-relation`, `find-methods`, `coc`, `datasource-method`, `control-method`,
      `extension`): an existing object the command changes; rewritten only if it names an object
      defined in the same batch, otherwise passed unchanged.
    - *literal* (`report-extension` pattern, `label-file` id): passed unchanged.
  - For `extension`, `options.kind` (Table, Form, …) is required and is passed as the positional
    argument before `name`.
  - `options`: map of option name (without `--`) to a string, number, boolean or array of
    strings/numbers. An array repeats the option. `true` emits a bare flag; `false` omits it.

The server always adds `--no-refresh --output json`, and says where to write:

- most subcommands: `--install-to <model>`;
- `table-relation` and `find-methods`, which refuse `--install-to` and edit an existing table file:
  `--apply-to <path>`, where the path is taken from the batch task that created the table, or else
  from `d365fo get table <name>` (`data.table.sourcePath`). A table that cannot be found fails the
  task (and so the batch).

Tasks may not set `out`, `install-to`, `apply-to`, `output`, `overwrite`, `no-refresh`, `add-to`,
`into-role` or any `out-*` option; those are rejected. (`add-to`/`into-role` edit an existing role file
in place without a journal entry, and `out-*` can write outside the model, so a rollback could not undo
them.) Option values are passed as `--key=value`, so a value starting with `-` stays a value. Creating an object that already exists fails in `d365fo` (`WRITE_FAILED`), which rolls the
batch back.

The tool description lists the supported types and points the agent at `d365fo generate <type> --help`
(run in a terminal) or the d365fo docs for each type's options.

Output (success): a short JSON summary —

```json
{
  "ok": true,
  "model": "ContosoFleet",
  "stages": 6,
  "elapsed_ms": 7700,
  "artifacts": [ { "type": "table", "name": "CFltVehicle", "paths": ["…/AxTable/CFltVehicle.xml"] } ],
  "warnings": [ { "task": "privilege CFltVehicleMaintain", "message": "…" } ]
}
```

Warnings from `d365fo` are passed through, except the "bridge is not available" warning, which is
collapsed into one line when present.

Output (failure): `ok: false`, the failing task, `d365fo`'s error code/message/hint, and the
rollback result (`rolled_back: true` with the reverted count, or the list of files left behind).

`dry_run: true` returns the expanded names, the stages, and the exact command lines, and writes nothing.

### 4.2 Components (in FoBatchGen.Core)

| Unit | Responsibility | Depends on |
|---|---|---|
| `BatchSpec` | Parse and validate input: required fields, known `type`, option value shapes, reserved options, 1–500 tasks, no duplicate (kind, name). Collects all errors, not just the first. | — |
| `ReferenceMap` | Data file (embedded JSON) declaring, per subcommand: the role of `name` (define/reference/literal) and its kinds; leading positionals taken from options; the kinds of object it produces; options that **define** further new objects (e.g. `sysoperation --contract-name`, `report --dp`), which are prefixed like names; and reference positions — which options, or which segment of a `:`/`=`/`/`-separated value, name another object, with the kinds referenced. A tie-break rank. Has an entry for every supported subcommand. | — |
| `NameExpander` | Prefix defined names — task names with role *define* and values of defining options — unless they already start with the prefix. Rewrite referenced names: only positions declared in `ReferenceMap`, only when the value exactly matches (case-insensitive) a name defined in the batch whose kind fits the reference. Everything else passes through unchanged (standard objects such as `VIN`, `SysGroup`, `CustAccount`; field names; captions). Rejects two definitions of the same name with overlapping kinds. | `ReferenceMap` |
| `Planner` | Dependency edge from task A to task B when A references B's name with a kind B satisfies. Assign stages by longest dependency path; order within a stage by a fixed type rank for deterministic output. Reject cycles, naming the tasks involved. | `ReferenceMap` |
| `D365foRunner` | Run `d365fo` with an argument list (no shell), per-call timeout (default 120 s, `FO_BATCHGEN_TIMEOUT_SECONDS`), parse the `{ ok, data, warnings, error }` envelope. Treat non-JSON output, non-zero exit without an envelope, or timeout as failure. | — |
| `BatchExecutor` | Preflight, anchor, run stages, refresh, roll back. See 4.3. | Runner, Planner |
| `SummaryBuilder` | Build the compact result. | — |

`src/FoBatchGen` hosts the MCP server with the official ModelContextProtocol C# SDK over stdio and
maps the tool call to `BatchExecutor`.

### 4.3 Execution flow

1. **Validate** with `BatchSpec`; expand with `NameExpander`; plan with `Planner`. Any error →
   return all errors, nothing written. `dry_run` stops here and returns the plan.
2. **Preflight**: `d365fo` is runnable; `models list` contains `model` (warn if it is not marked
   custom). This check is needed because `index refresh --model <unknown>` reports success.
   Failure → return error, nothing written.
3. **Lock**: one batch at a time per server process (`SemaphoreSlim(1)`).
4. **Anchor**: `journal list -n 1`; remember the newest entry id (or none). The journal is a stack,
   most recent first; `undo --steps N` pops the newest N entries.
   **Ownership**: after each task, `journal list -n 50`; every entry newer than the last one seen belongs
   to the batch (this covers companion objects journaled under unrelated names, and bridge entries with no
   path). After each index refresh, `journal list -n 1` moves the marker without claiming anything, so
   writes by others between the batch's commands are never the batch's. d365fo calls that write are not
   cancelled; a client cancellation is honoured between calls.
5. **Run** each stage in order; within a stage, tasks run sequentially. After each stage, including
   the last, run `index refresh --model <model>`.
6. **On any failure** (task `ok:false`, refresh failure, timeout, crash):
   1. `journal list -n 2000`; take the entries above the anchor. If the anchor is not in the list,
      do **not** undo (report and list the files written).
   2. Check that each of those entries is a write from this batch: its id was claimed after one of the
      batch's commands and its `model` equals `model`. Check also that every claimed id is still in the
      journal (d365fo prunes beyond `D365FO_JOURNAL_MAX_ENTRIES`). If either check fails, do **not**
      undo; report the failure plus the files written. (Replaces the earlier name-containment rule, which
      undid other sessions' writes that shared the prefix.)
   3. Otherwise `undo --steps <count of those entries>`, then `index refresh --model <model>`.
   4. If undo fails, report it plainly with the list of files that may remain.
   5. After undo, every file a `--install-to` task reported (`path`, and the `.label.txt` under
      `content`) must be gone; any that remain (d365fo did not journal them) are reported with
      `rolled_back: false`. A task stopped by the timeout is reported the same way, since it may have
      written files it never journaled.
7. **Return** the summary.

## 5. ReferenceMap coverage

Every one of the 41 subcommands gets an entry, written from the d365fo source at the pinned commit.
Examples:

| Subcommand | Reference positions → kinds |
|---|---|
| `table` | `field` segment 2 → edt, enum; `configuration-key` → configuration-key |
| `edt` | `extends` → edt; `enum-type` → enum |
| `form` | `table`, `lines-table` → table/view |
| `menu-item` | `object` → form/class/report/query; `config-key` → configuration-key |
| `privilege` | `entry-point` → menu-item/service |
| `duty` | `privilege` → privilege |
| `role` | `duty` → duty; `privilege` → privilege; `sub-role` → role |
| `sysoperation` | defines `contract-name`, `service-name`, `controller-name` (class); `param` segment 2 → edt/enum |
| `extension` | name is a reference (any kind); leading positional from `options.kind`; `field` segment 2 → edt/enum; `item` last `/` part of segment 1 → menu-item |

A unit test fails if `ReferenceMap` and the list of `d365fo` generate subcommands (kept in a
checked-in list taken from `CliApp.cs` at the pinned commit) disagree.

## 6. Error handling summary

| Situation | Result |
|---|---|
| Invalid spec, unknown type, reserved option, cycle | All errors returned; nothing written |
| `d365fo` missing, no index, model not found | Preflight error; nothing written |
| Task fails / refresh fails / timeout | Full rollback via journal; failing task + `d365fo` error returned |
| Journal has entries not from this batch | No undo; failure + written-file list returned |
| Undo fails | Reported with possibly-remaining files |
| Unknown option name for a subcommand | `d365fo` rejects it → normal rollback path |

## 7. Testing

- **Unit (xUnit)**: `BatchSpec` validation; `NameExpander` (prefixing, no double prefix, standard names
  untouched, field-name segment untouched, case-insensitive match); `Planner` (stages, rank order,
  cycle detection); `ReferenceMap` completeness; `D365foRunner` envelope parsing with canned output;
  rollback decision logic with a fake runner.
- **Integration**: build `d365fo` at the pinned commit; point `D365FO_CLI` at it; copy a fixture model
  (the MIT-licensed MiniAot sample, vendored with attribution) to a temp packages folder; build the
  index. Cases:
  - the Vehicle batch from 4.1 succeeds, enum field is `AxTableFieldEnum`, `security coverage`
    resolves the chain, all artifacts found via `get`;
  - standard and existing names (`VIN`) are not prefixed;
  - an unknown option in the last task rolls back everything and leaves no files;
  - `dry_run` writes nothing and the journal is unchanged;
  - one smoke task per subcommand that runs without the metadata bridge; subcommands that cannot run
    on Linux are listed in the test file with the reason.
  Integration tests are skipped when `D365FO_CLI` is not set.
- **Manual, on the dev VM** (checklist in the README): bridge enabled, time `index refresh --model`
  on the real custom model, run the Vehicle batch, build in Visual Studio.

## 8. Risks and assumptions

- **Refresh time on a large real model** is unmeasured; the spike model was tiny. If it is slow,
  a later version can refresh only when the next stage references something from the current one.
- **Form method commands need the bridge**: `datasource-method` and `control-method` change a form
  only through the metadata bridge, so they work on the dev VM but cannot be tested on Linux.
- **Undo of update-style commands** (`datasource-method`, `control-method`, `table-relation`,
  `find-methods`, `extension`, which modify existing objects) is assumed to restore the previous file
  via the journal; the integration smoke tests must confirm it for each.
- **CLI drift**: options and journal format are those of the pinned commit. Upgrading `d365fo` means
  re-running the integration tests and updating `ReferenceMap`.
- **Concurrent writes**: other `d365fo` writes during a batch are detected at rollback time (6.2) but
  not prevented. A write by another process to the same model at the very moment one of the batch's own
  commands runs is indistinguishable from the command's own companion writes and would be undone with
  the batch.
