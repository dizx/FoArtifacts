# Build step and index refresh — design

Status: approved in conversation on 2026-11-18, pending review of this written spec.

## Goal

Let the agent find out whether what it generated compiles, and keep the d365fo index current after
changes made outside fo-artifacts, without leaving the MCP server:

- `generate_fo_artifacts` gets an optional `build` flag: after a successful batch, compile the model and
  return the compiler errors in the same result.
- Two new tools, `build_model` and `refresh_index`, do the same work without a batch (after the agent
  fixes errors, after a git pull, after edits in Visual Studio).

Success: an agent can create a feature, see which compiler errors are in the objects it just wrote,
fix them and rebuild, all through fo-artifacts, with the existing rollback guarantees unchanged.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Build fails after a successful batch | Keep the batch and report the errors. No rollback. | `d365fo build --model` compiles the whole model, so an error may predate the batch. Undoing good work for someone else's error is wrong; the agent fixes forward. |
| How the build is triggered | `build: true` on the batch, plus standalone `build_model` and `refresh_index` tools. | One call for the common case; standalone tools for fix-and-rebuild and for changes made outside fo-artifacts. |
| Where the code lives | A separate `ModelBuild` unit in Core, not an extra batch stage. | A stage failure triggers rollback; keeping the build outside the stage loop leaves rollback untouched. |
| d365fo-mcp instead | Not used for this. | It adds 32 tools (including writes that bypass the batch's journal checks), has no `in_batch` tagging, and its own time limits. |
| Build time limit | New `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS`, default 900. | Builds of large models take minutes; generate/refresh calls keep the 120 s limit. |
| Cancellation | A cancelled call may stop a running build. Generate, refresh and undo calls stay uncancellable. | The rule exists so a killed write cannot leave a file the journal does not know about. A build writes no source files, only `bin` output the next build overwrites. |
| Which models | `build_model` refuses a model d365fo does not mark custom, before compiling. `refresh_index` refreshes first, then checks. | Building a Microsoft model by mistake takes an hour. A refresh only touches the index, and is how a new model gets into it; d365fo's `D365FO_CUSTOM_MODELS` accepts globs and `!` exclusions that fo-artifacts must not reimplement. |

## What d365fo returns

Measured on the dev VM against FoArtifactsLab (fork commit `b60ffdd`):

- `d365fo build --model X --output json` (7.8 s): `ok` is `true` even when the build fails. `data` holds
  `buildSucceeded`, `exitCode`, `elapsedMs`, `errorCount`, `warningCount`, `errors[]`, `warnings[]`,
  `models[].xppc.log` (path of `BuildModelResult.log`) and `tail`. Each diagnostic has `severity`,
  `kind` (e.g. `AxForm`), `object` (e.g. `FALVehicle`), `member` and `message`.
- `d365fo index refresh --model X --output json` (0.27 s when unchanged): `data.modelsProcessed`,
  `data.modelsSkipped`, `data.elapsedMs`. `--force` re-reads the model even when unchanged.

## Tool surface

### `generate_fo_artifacts` — new parameter `build` (bool, default false)

- With `build: true`, after the batch succeeds, run `d365fo build --model <model> --output json` while
  still holding the batch lock, and add a `build` section to the result.
- A failed batch is rolled back exactly as today and is not built.
- With `dry_run: true`, the result gets `"build": "d365fo build --model <model> --output json"` after
  the plan's stages.
- A failed build, or a build that could not run, never changes `ok`: `ok` still means "the batch was
  written". The tool description gains one sentence saying a failed build does not undo the batch.

### `build_model(model)`

Compiles one custom model and returns `{ ok, model, build }`. `ok` (and MCP `isError`) mean "the build
ran and succeeded", because the build is the whole call. A model that is not in the index, or not marked
custom, is refused without compiling.

### `refresh_index(model, force = false)`

Runs `d365fo index refresh --model <model>` (with `--force` when asked), then looks the model up with
`models list`:

- model missing → `ok: false`, code `MODEL_NOT_FOUND`, with a message that the model is still not in
  the index after the refresh and the hint to create an empty Ax* folder in the model folder;
- model present but not custom → `ok: true` with a warning to add it to `D365FO_CUSTOM_MODELS`;
- otherwise `ok: true`.

Returns `{ ok, model, refreshed, elapsed_ms, warnings }`. `refreshed: false` means d365fo skipped the
model as unchanged (`modelsProcessed` 0, `modelsSkipped` 1).

## Components (`FoArtifacts.Core`)

| Unit | Responsibility | Depends on |
|---|---|---|
| `ModelLock` (new) | The `SemaphoreSlim` that is now private to `BatchExecutor`. One shared instance, so a batch, a build and a refresh never overlap. | — |
| `ModelCheck` (extracted from `BatchExecutor.PreflightAsync`) | `models list` lookup: found, custom, known names, the not-indexed hint. Used by the batch preflight, `build_model` and `refresh_index`. The batch preflight keeps today's behaviour (error when missing, warning when not custom). | `ID365fo` |
| `BuildReport` (new) | Pure parser from d365fo's build envelope to the report below, tagging each diagnostic `in_batch` against a set of object names, ordering and capping. | — |
| `ModelBuild` (new) | Runs `build --model X --output json` and returns a `BuildReport`. Passes the cancellation token to the runner. | its own `ID365fo` built with the build time limit |
| `IndexRefresh` (new) | Runs the refresh, then `ModelCheck`. | `ID365fo`, `ModelCheck` |
| `CliLocator.BuildTimeout` (new) | Reads `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS`; default 900 s; same parsing rules as `Timeout`. | — |
| `BatchRequest`, `ValidBatch`, `ExpandedBatch` | Each gets `bool Build` (default false). | — |
| `BatchExecutor` | Takes `ModelLock`, `ModelCheck` and `ModelBuild`. After success, if `Build`, calls `ModelBuild` inside the lock. `BatchResult` gets `BuildReport? Build`. Rollback code is unchanged. | — |
| `Summary` | The `build` section, and the JSON for the two new tools. | — |

In `src/FoArtifacts`: `BuildModelTool` and `RefreshIndexTool` are thin wrappers like
`GenerateFoArtifactsTool`. `Program.cs` registers `ModelLock`, `ModelCheck`, `IndexRefresh`, and a
`ModelBuild` with a second `D365foRunner` that uses `CliLocator.BuildTimeout`.

### Objects of the batch (`in_batch`)

The set of object names the batch wrote or changed, compared case-insensitively with a diagnostic's
`object`:

- every task's expanded name (for `datasource-method` and `control-method` this is the form, so their
  errors match even though bridge updates report no path; for `table-relation` and `find-methods` it
  is the table they change);
- the names a task defines through its options;
- the file name, without `.xml`, of every path a task reported.

## Result shape

The `build` section, in the batch result and in `build_model`'s result:

```json
"build": {
  "succeeded": false,
  "error_count": 2,
  "warning_count": 0,
  "elapsed_ms": 7235,
  "errors": [
    { "object": "FALVehicle", "kind": "AxForm",
      "member": "Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode",
      "message": "Property 'ColumnsMode' ... must have value 'Fill' ...", "in_batch": true }
  ],
  "warnings": [],
  "truncated": true,
  "log": "C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.log"
}
```

- `errors`: in-batch errors first, then the rest, at most 50. `error_count` is always d365fo's full count.
- `warnings`: in a batch, only in-batch warnings; in `build_model` (no batch), the first warnings. At
  most 20 either way. `warning_count` is the full count.
- A failed build makes d365fo exit 1 with `ok: true` in its envelope; the compiler errors are read
  from the data regardless.
- `truncated: true` appears only when either list was cut.
- `in_batch` is omitted in `build_model`'s result (there is no batch).
- Null and empty fields follow `Summary`'s existing rules (nulls omitted).

When the build could not run (d365fo `ok: false`, time limit, xppc not found, model not custom):

```json
"build": { "error": { "code": "TIMEOUT", "message": "...", "hint": "Raise FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS ..." } }
```

In a batch result `ok` stays `true`; in `build_model` `ok` is `false`. A build timeout's hint names
`FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS`, not `FO_ARTIFACTS_TIMEOUT_SECONDS`.

## Testing

Every behaviour gets a failing test before its code.

Unit tests (`tests/FoArtifacts.Tests`, with `FakeD365fo`):

- `BuildReportTests`: parses the two real build envelopes captured on the VM (clean build; the
  ColumnsMode failure) saved as fixtures; `in_batch` tagging is case-insensitive; in-batch errors come
  first; the 50/20 caps and `truncated`; an envelope with `ok: false` becomes `error`.
- `BatchExecutorTests`: `build: false` never calls `build`; `build: true` calls `build --model X` once,
  after the last refresh; a failed batch is rolled back and never built; a failed build leaves `ok: true`
  and makes no undo call; the dry-run plan ends with the build command; the existing tests that d365fo
  writes are never cancelled still pass, and a new test shows cancellation reaches the build.
- `ModelBuild` / `ModelCheck`: a model that is not custom, or not indexed, is refused before any build
  call; a timeout's hint names `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS`.
- `IndexRefresh`: `refreshed` comes from `modelsProcessed` / `modelsSkipped`; `force` adds `--force`; a
  model missing afterwards gives `ok: false` with the Ax* hint; a non-custom model gives a warning.
- `CliLocatorTests`: the build limit defaults to 900 s and reads the environment variable.
- `SummaryTests` and tool tests: the JSON shapes above; the new parameters and their descriptions.

Integration tests (`tests/FoArtifacts.IntegrationTests`, pinned d365fo, sample-model sandbox):

- `refresh_index` refreshes a changed model, then reports `refreshed: false` when nothing changed.
- `build_model` in the sandbox, which has no xppc, returns the "could not run" `error` from real d365fo.

Manual check on the dev VM, added to the README's Dev VM checklist as step 7. It writes to
FoArtifactsLab, so its dry-run plan is shown and approved first:

1. A Vehicle-style batch with `build: true` → `build.succeeded: true`.
2. A batch whose `control-method` body does not compile → errors tagged `in_batch: true`, batch kept.
3. Fix the method, then `build_model` → clean.
4. Edit a model file by hand, then `refresh_index` → `refreshed: true`.

## Documentation

README: the `build` parameter, `build_model`, `refresh_index`, `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS` in the
configuration table, and checklist step 7.

## Out of scope

- `d365fo index build` / `index extract` (full index rebuilds): slow, whole-index, run by hand at setup.
- Incremental builds (`--incremental`), cross-reference updates (`--xref`), DB sync, best-practice checks
  and tests (`d365fo sync`, `bp`, `test`).
- Explaining build errors (`d365fo explain-error`); the agent can run it, and it may be added later.
- Rolling back on a failed build.
