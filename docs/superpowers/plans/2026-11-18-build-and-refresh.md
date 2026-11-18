# Build Step and Index Refresh Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an agent compile a model after a batch (`build: true`), and build or refresh a model on its own (`build_model`, `refresh_index`), with the batch's rollback behaviour unchanged.

**Architecture:** New Core units — `ModelLock` (shared lock), `ModelCheck` (the `models list` lookup taken out of `BatchExecutor`), `BuildReport` (pure parser of d365fo's build JSON), `ModelBuild` (runs the build through its own runner with a longer time limit) and `IndexRefresh`. `BatchExecutor` calls `ModelBuild` after a successful batch while still holding the lock. Two thin MCP tool classes wrap `ModelBuild` and `IndexRefresh`.

**Tech Stack:** .NET 10, C# 13, ModelContextProtocol C# SDK (`[McpServerToolType]`), xunit.v3 on Microsoft Testing Platform, the pinned d365fo fork (`b60ffdd`).

**Spec:** `docs/superpowers/specs/2026-11-18-build-and-refresh-design.md`

## Global Constraints

- Write each failing test first, run it and see it fail, then write the code (the user's rule).
- Commit with the repo-local identity already configured (`Morten Aasheim <3502342+dizx@users.noreply.github.com>`); no `Co-Authored-By` or `Claude-Session` trailers. Do not push.
- Never write to the FoArtifactsLab model without the user's OK on the dry-run plan (Task 9).
- Generate, refresh and undo calls to d365fo are passed `CancellationToken.None`; only the build call (and the read-only `models list` lookup) gets the caller's token.
- `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS`, default `900`, used only for `d365fo build`. `FO_ARTIFACTS_TIMEOUT_SECONDS` (default `120`) is unchanged.
- At most `50` errors and `20` warnings in a build report; `error_count` / `warning_count` are always d365fo's full counts.
- In a batch result `ok` means "the batch was written"; a failed build never changes it and never triggers a rollback.
- JSON goes through `Summary`'s existing options: snake_case names, nulls omitted.
- Run unit tests with `dotnet test --project tests/FoArtifacts.Tests` (one class: add `--filter-class "FoArtifacts.Tests.<Class>"`). Integration tests: `$env:D365FO_CLI = .\scripts\build-d365fo.ps1; dotnet test --project tests/FoArtifacts.IntegrationTests`.

## Review Focus

1. A failed build makes d365fo exit 1 with `ok: true`; `Envelope` turns that into `Ok=false, ErrorCode="EXIT_CODE"` with the data kept. The report must still read the compiler errors, not report "build could not run". (Task 2: `A_failed_build_exits_non_zero_but_its_errors_are_still_read`.)
2. `build_model` has no batch, so "only in-batch warnings" would always list none; it lists the first 20 warnings instead. (Task 2: `Without_a_batch_the_first_warnings_are_listed`.)
3. `build: true` on a model d365fo does not mark custom: the batch is written (with today's warning), the build is refused without compiling, and `ok` stays true. (Task 6: `A_batch_on_a_model_not_marked_custom_is_written_but_not_built`.)
4. A diagnostic with no `object` (e.g. a label error) is kept, with `in_batch: false`, not dropped or crashed on. (Task 2: `A_diagnostic_without_an_object_is_kept_and_not_in_the_batch`.)
5. The agent cancels while the batch is compiling: the batch is kept, the build reports `CANCELLED`, nothing is undone, and the lock is released. (Task 6: `Cancelling_during_the_build_keeps_the_batch`.)

## File Structure

| File | Responsibility |
|---|---|
| `src/FoArtifacts.Core/ModelLock.cs` (new) | The one lock shared by batches, builds and refreshes. |
| `src/FoArtifacts.Core/ModelCheck.cs` (new) | `models list` lookup and the not-indexed / not-custom messages. |
| `src/FoArtifacts.Core/ToolError.cs` (new) | `ToolError(Code, Message, Hint)` used by build and refresh reports. |
| `src/FoArtifacts.Core/BuildReport.cs` (new) | `BuildDiagnostic`, `BuildReport` and its parser. |
| `src/FoArtifacts.Core/ModelBuild.cs` (new) | Model check plus `d365fo build --model`. |
| `src/FoArtifacts.Core/IndexRefresh.cs` (new) | `RefreshReport` and `d365fo index refresh --model`. |
| `src/FoArtifacts.Core/BatchExecutor.cs` | Uses `ModelLock`, `ModelCheck`; builds after success; `BatchObjects`. |
| `src/FoArtifacts.Core/BatchSpec.cs`, `NameExpander.cs` | Carry `Build` through the request. |
| `src/FoArtifacts.Core/Summary.cs` | `build` section; JSON for the two new tools. |
| `src/FoArtifacts.Core/D365foRunner.cs` | `CliLocator.BuildTimeout`. |
| `src/FoArtifacts/BuildModelTool.cs`, `RefreshIndexTool.cs` (new) | MCP tools. |
| `src/FoArtifacts/GenerateFoArtifactsTool.cs`, `Program.cs` | `build` parameter; DI wiring. |
| `tests/FoArtifacts.Tests/Fixtures/build-clean.json`, `build-failed.json` (new) | Real d365fo build output from the dev VM. |
| `tests/FoArtifacts.Tests/*Tests.cs` | One test class per new unit, additions to existing ones. |
| `tests/FoArtifacts.IntegrationTests/ModelToolsIntegrationTests.cs` (new) | Refresh and build against real d365fo. |
| `README.md` | Docs and checklist step 7. |

---

### Task 1: ModelLock and ModelCheck

Take the lock and the model lookup out of `BatchExecutor` so the new tools can share them. Behaviour of batches does not change.

**Files:**
- Create: `src/FoArtifacts.Core/ModelLock.cs`, `src/FoArtifacts.Core/ModelCheck.cs`
- Modify: `src/FoArtifacts.Core/BatchExecutor.cs` (class header, `RunAsync` lines 45-53, `PreflightAsync` lines 188-213)
- Test: `tests/FoArtifacts.Tests/ModelLockTests.cs`, `tests/FoArtifacts.Tests/ModelCheckTests.cs`, `tests/FoArtifacts.Tests/BatchExecutorTests.cs`

**Interfaces:**
- Produces: `ModelLock.AcquireAsync(CancellationToken) : Task<IDisposable>`; `ModelInfo(bool Found, bool IsCustom, IReadOnlyList<string> Known)`; `ModelCheck(ID365fo cli).LookUpAsync(string model, CancellationToken ct) : Task<(ModelInfo? Info, string? Error)>`; `ModelCheck.NotIndexed(string model, IReadOnlyList<string> known) : string`; `ModelCheck.NotCustom(string model) : string`; `BatchExecutor(ID365fo cli, ReferenceMap map, ModelLock? modelLock = null)`.

- [ ] **Step 1: Write the failing tests**

`tests/FoArtifacts.Tests/ModelLockTests.cs`:

```csharp
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelLockTests
{
    [Fact]
    public async Task A_second_holder_waits_until_the_first_releases()
    {
        var gate = new ModelLock();
        var first = await gate.AcquireAsync(Ct);

        var second = gate.AcquireAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(second.IsCompleted);

        first.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public async Task Releasing_twice_releases_once()
    {
        var gate = new ModelLock();
        var first = await gate.AcquireAsync(Ct);
        first.Dispose();
        first.Dispose();

        var second = await gate.AcquireAsync(Ct);
        var third = gate.AcquireAsync(Ct);
        await Task.Delay(50, Ct);
        Assert.False(third.IsCompleted);

        second.Dispose();
        (await third).Dispose();
    }
}
```

`tests/FoArtifacts.Tests/ModelCheckTests.cs`:

```csharp
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelCheckTests
{
    [Fact]
    public async Task Finds_a_custom_model_ignoring_case()
    {
        var (info, error) = await new ModelCheck(new FakeD365fo()).LookUpAsync("contosofleet", Ct);

        Assert.Null(error);
        Assert.True(info!.Found);
        Assert.True(info.IsCustom);
    }

    [Fact]
    public async Task Reports_a_model_that_is_not_custom()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var (info, _) = await new ModelCheck(fake).LookUpAsync("ContosoFleet", Ct);

        Assert.True(info!.Found);
        Assert.False(info.IsCustom);
    }

    [Fact]
    public async Task Reports_a_missing_model_with_the_known_names()
    {
        var (info, error) = await new ModelCheck(new FakeD365fo()).LookUpAsync("Nope", Ct);

        Assert.Null(error);
        Assert.False(info!.Found);
        Assert.Equal(new[] { "ContosoFleet" }, info.Known);
    }

    [Fact]
    public async Task Reports_when_d365fo_is_not_usable()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "models" ? FakeD365fo.Fail("NO_INDEX", "No index", "run index build") : null };

        var (info, error) = await new ModelCheck(fake).LookUpAsync("ContosoFleet", Ct);

        Assert.Null(info);
        Assert.Equal("d365fo is not usable: NO_INDEX: No index Hint: run index build", error);
    }
}
```

Add to `tests/FoArtifacts.Tests/BatchExecutorTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task A_batch_waits_for_the_shared_model_lock()
    {
        var fake = new FakeD365fo();
        var gate = new ModelLock();
        var executor = new BatchExecutor(fake, Map, gate);
        var held = await gate.AcquireAsync(Ct);

        var run = executor.RunAsync(Vehicle(), Ct);
        await Task.Delay(50, Ct);
        Assert.Empty(fake.Calls);

        held.Dispose();
        Assert.True((await run).Ok);
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build errors — `ModelLock`, `ModelCheck` do not exist, and `BatchExecutor` has no 3-argument constructor.

- [ ] **Step 3: Write `ModelLock`**

`src/FoArtifacts.Core/ModelLock.cs`:

```csharp
namespace FoArtifacts.Core;

/// <summary>
/// One writer or builder at a time: a batch, a build and an index refresh never overlap, so a build never
/// compiles a model while a batch is writing to it. The MCP server shares one instance.
/// </summary>
public sealed class ModelLock
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
```

- [ ] **Step 4: Write `ModelCheck`** (the lookup and messages move here from `BatchExecutor.PreflightAsync`; the not-indexed text must stay word for word, existing tests check it)

`src/FoArtifacts.Core/ModelCheck.cs`:

```csharp
using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>What d365fo's index says about one model.</summary>
public sealed record ModelInfo(bool Found, bool IsCustom, IReadOnlyList<string> Known);

/// <summary>Looks a model up with <c>d365fo models list</c>.</summary>
public sealed class ModelCheck(ID365fo cli)
{
    public static IReadOnlyList<string> ListArgs { get; } = ["models", "list", "--output", "json"];

    /// <summary>The model's entry, or an error message when d365fo itself is not usable.</summary>
    public async Task<(ModelInfo? Info, string? Error)> LookUpAsync(string model, CancellationToken ct)
    {
        var models = await cli.RunAsync(ListArgs, ct);
        if (!models.Ok)
            return (null, $"d365fo is not usable: {models.ErrorCode}: {models.ErrorMessage}"
                + (models.ErrorHint is null ? "" : $" Hint: {models.ErrorHint}"));

        var items = new List<JsonElement>();
        if (models.Data.ValueKind == JsonValueKind.Object
            && models.Data.TryGetProperty("items", out var array)
            && array.ValueKind == JsonValueKind.Array)
            items.AddRange(array.EnumerateArray());

        var known = items.Select(i => Str(i, "name")).OfType<string>().ToList();
        var match = items.FirstOrDefault(i => string.Equals(Str(i, "name"), model, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == JsonValueKind.Undefined) return (new ModelInfo(false, false, known), null);
        var custom = match.TryGetProperty("isCustom", out var flag) && flag.ValueKind == JsonValueKind.True;
        return (new ModelInfo(true, custom, known), null);
    }

    public static string NotIndexed(string model, IReadOnlyList<string> known) =>
        $"Model '{model}' is not in the d365fo index. Known models: {string.Join(", ", known.Take(20))}. "
        + $"Check the name. If the model is new, run `d365fo index refresh --model {model}`; "
        + "d365fo skips an empty model (one with no Ax* folder such as AxClass yet), so create one "
        + "empty Ax* folder in the model folder first.";

    public static string NotCustom(string model) =>
        $"Model '{model}' is not marked as custom in d365fo. Add it to D365FO_CUSTOM_MODELS.";

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
```

- [ ] **Step 5: Use both in `BatchExecutor`**

Replace the class header and the `_gate` field (lines 24-28):

```csharp
public sealed class BatchExecutor(ID365fo cli, ReferenceMap map, ModelLock? modelLock = null)
{
    public const int JournalLimit = 2000;

    private readonly ModelLock _lock = modelLock ?? new ModelLock();
    private readonly ModelCheck _check = new(cli);
```

Replace the locking in `RunAsync` (lines 45-53):

```csharp
        using (await _lock.AcquireAsync(ct))
            return await ExecuteAsync(expanded, stages, clock, ct);
```

Replace `PreflightAsync` (lines 188-213):

```csharp
    private async Task<string?> PreflightAsync(string model, List<string> warnings, CancellationToken ct)
    {
        var (info, error) = await _check.LookUpAsync(model, ct);
        if (error is not null) return error;
        if (!info!.Found) return ModelCheck.NotIndexed(model, info.Known);
        if (!info.IsCustom) warnings.Add(ModelCheck.NotCustom(model));
        return null;
    }
```

Keep the private `Str` helper at the bottom of `BatchExecutor`; `ResolveTableFileAsync` still uses it.

- [ ] **Step 6: Run all unit tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass (126 existing + 7 new).

- [ ] **Step 7: Commit**

```bash
git add src/FoArtifacts.Core/ModelLock.cs src/FoArtifacts.Core/ModelCheck.cs src/FoArtifacts.Core/BatchExecutor.cs tests/FoArtifacts.Tests/ModelLockTests.cs tests/FoArtifacts.Tests/ModelCheckTests.cs tests/FoArtifacts.Tests/BatchExecutorTests.cs
git commit -m "Share the batch lock and model lookup through ModelLock and ModelCheck"
```

---

### Task 2: BuildReport parser

**Files:**
- Create: `src/FoArtifacts.Core/ToolError.cs`, `src/FoArtifacts.Core/BuildReport.cs`, `tests/FoArtifacts.Tests/Fixtures/build-clean.json`, `tests/FoArtifacts.Tests/Fixtures/build-failed.json`, `tests/FoArtifacts.Tests/BuildReportTests.cs`
- Modify: `tests/FoArtifacts.Tests/FoArtifacts.Tests.csproj`, `tests/FoArtifacts.Tests/TestData.cs`

**Interfaces:**
- Produces: `ToolError(string Code, string Message, string? Hint)`; `BuildDiagnostic(string? Object, string? Kind, string? Member, string Message, bool? InBatch)`; `BuildReport(bool? Succeeded, int ErrorCount, int WarningCount, long ElapsedMs, IReadOnlyList<BuildDiagnostic> Errors, IReadOnlyList<BuildDiagnostic> Warnings, bool Truncated, string? Log, ToolError? Error)` with `MaxErrors = 50`, `MaxWarnings = 20`, `TimeoutHint`, `static BuildReport Failed(string code, string message, string? hint, long elapsedMs = 0)`, `static BuildReport From(CliResult result, IReadOnlySet<string>? batchObjects, long elapsedMs)`; test helper `TestData.Fixture(string name, int exitCode) : CliResult`.

- [ ] **Step 1: Add the fixtures**

These are the exact envelopes d365fo printed on the dev VM on 2026-11-18 (`d365fo build --model FoArtifactsLab --output json`). The clean build exited 0; the failed one exited 1.

`tests/FoArtifacts.Tests/Fixtures/build-clean.json` (one line):

```json
{"ok":true,"data":{"buildSucceeded":true,"exitCode":0,"elapsedMs":7664,"engine":"xppc","engineReason":"--model: compiled with LabelC.exe and xppc.exe.","compiler":"C:\\AosService\\PackagesLocalDirectory\\bin\\xppc.exe","incremental":false,"models":[{"model":"FoArtifactsLab","module":"FoArtifactsLab","metadataRoot":"C:\\AosService\\PackagesLocalDirectory","xppc":{"exitCode":0,"elapsedMs":7599,"errorCount":0,"warningCount":0,"log":"C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.log","args":["-metadata=C:\\AosService\\PackagesLocalDirectory","-compilermetadata=C:\\AosService\\PackagesLocalDirectory","-modelmodule=FoArtifactsLab","-output=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\bin","-referencefolder=C:\\AosService\\PackagesLocalDirectory","-refPath=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\bin","-log=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.log","-xmlLog=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.xml"]}}],"errorCount":0,"warningCount":0,"errors":[],"warnings":[],"tail":"Microsoft (R) X\u002B\u002B Compiler 7.0.7996.43\nCopyright (C) Microsoft Corporation.  All rights reserved.\nFoArtifactsLab compilation completed.\nElapsed time: 00:00:07\nErrors: 0\nWarnings: 0"}}
```

`tests/FoArtifacts.Tests/Fixtures/build-failed.json` (one line):

```json
{"ok":true,"data":{"buildSucceeded":false,"exitCode":2,"elapsedMs":7235,"engine":"xppc","engineReason":"--model: compiled with LabelC.exe and xppc.exe.","compiler":"C:\\AosService\\PackagesLocalDirectory\\bin\\xppc.exe","incremental":false,"models":[{"model":"FoArtifactsLab","module":"FoArtifactsLab","metadataRoot":"C:\\AosService\\PackagesLocalDirectory","xppc":{"exitCode":2,"elapsedMs":7169,"errorCount":2,"warningCount":0,"log":"C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.log","args":["-metadata=C:\\AosService\\PackagesLocalDirectory","-compilermetadata=C:\\AosService\\PackagesLocalDirectory","-modelmodule=FoArtifactsLab","-output=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\bin","-referencefolder=C:\\AosService\\PackagesLocalDirectory","-refPath=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\bin","-log=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.log","-xmlLog=C:\\AosService\\PackagesLocalDirectory\\FoArtifactsLab\\BuildModelResult.xml"]}}],"errorCount":2,"warningCount":0,"errors":[{"severity":"error","kind":"AxForm","object":"FALVehicle","member":"Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode","message":"Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageOverview must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027."},{"severity":"error","kind":"AxForm","object":"FALVehicle","member":"Design/Controls/DetailsTabs/TabPageGeneral/ColumnsMode","message":"Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageGeneral must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027."}],"warnings":[],"xppcDiagnostics":[{"severity":"error","kind":"AxForm","object":"FALVehicle","member":"Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode","message":"Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageOverview must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027."},{"severity":"error","kind":"AxForm","object":"FALVehicle","member":"Design/Controls/DetailsTabs/TabPageGeneral/ColumnsMode","message":"Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageGeneral must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027."}],"tail":"Microsoft (R) X\u002B\u002B Compiler 7.0.7996.43\nCopyright (C) Microsoft Corporation.  All rights reserved.\nFoArtifactsLab compilation completed.\nElapsed time: 00:00:06\n==================================\nFormPatternValidation Error: AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode: Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageOverview must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027.\nFormPatternValidation Error: AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageGeneral/ColumnsMode: Property \u0027ColumnsMode\u0027 on control AxForm/FALVehicle/Design/Controls/DetailsTabs/TabPageGeneral must have value \u0027Fill\u0027 per pattern \u0027Fields and Field Groups\u0027.\n==================================\nErrors: 2\nWarnings: 0"},"warnings":["build-failed"]}
```

Copy them to the test output — add to `tests/FoArtifacts.Tests/FoArtifacts.Tests.csproj`, before `</Project>`:

```xml
  <ItemGroup>
    <None Include="Fixtures\*.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

Add to `TestData` in `tests/FoArtifacts.Tests/TestData.cs`:

```csharp
    /// <summary>A d365fo envelope saved under Fixtures, parsed as if d365fo had exited with <paramref name="exitCode"/>.</summary>
    public static CliResult Fixture(string name, int exitCode) =>
        Envelope.Parse(exitCode, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), "");
```

- [ ] **Step 2: Write the failing tests**

`tests/FoArtifacts.Tests/BuildReportTests.cs`:

```csharp
using System.Text.Json;
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class BuildReportTests
{
    // Lower case on purpose: d365fo reports FALVehicle.
    private static readonly IReadOnlySet<string> Batch = new HashSet<string> { "falvehicle" };

    private static object Diag(string obj, int i) =>
        new { severity = "error", kind = "AxClass", @object = obj, member = $"m{i}", message = $"problem {i}" };

    /// <summary>A failed build as d365fo prints it: ok true, exit code 1.</summary>
    private static CliResult Failed(object[] errors, object[] warnings) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new
        {
            ok = true,
            data = new { buildSucceeded = false, errorCount = errors.Length, warningCount = warnings.Length, errors, warnings },
        }), "");

    [Fact]
    public void A_clean_build_reads_as_succeeded()
    {
        var report = BuildReport.From(Fixture("build-clean.json", 0), Batch, 1);

        Assert.True(report.Succeeded);
        Assert.Null(report.Error);
        Assert.Equal(0, report.ErrorCount);
        Assert.Empty(report.Errors);
        Assert.Equal(7664, report.ElapsedMs);
        Assert.Equal(@"C:\AosService\PackagesLocalDirectory\FoArtifactsLab\BuildModelResult.log", report.Log);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void A_failed_build_exits_non_zero_but_its_errors_are_still_read()
    {
        var result = Fixture("build-failed.json", 1);
        Assert.False(result.Ok); // Envelope reports ok:true with exit code 1 as EXIT_CODE.

        var report = BuildReport.From(result, Batch, 1);

        Assert.False(report.Succeeded);
        Assert.Null(report.Error);
        Assert.Equal(2, report.ErrorCount);
        var first = report.Errors[0];
        Assert.Equal("FALVehicle", first.Object);
        Assert.Equal("AxForm", first.Kind);
        Assert.Equal("Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode", first.Member);
        Assert.Contains("ColumnsMode", first.Message);
        Assert.All(report.Errors, e => Assert.True(e.InBatch));
    }

    [Fact]
    public void Without_a_batch_in_batch_is_not_set()
    {
        var report = BuildReport.From(Fixture("build-failed.json", 1), null, 1);

        Assert.All(report.Errors, e => Assert.Null(e.InBatch));
    }

    [Fact]
    public void Errors_in_the_batch_come_first_and_the_list_is_capped()
    {
        var errors = Enumerable.Range(0, 60).Select(i => Diag(i == 55 ? "FALVehicle" : "Other", i)).ToArray();

        var report = BuildReport.From(Failed(errors, []), Batch, 1);

        Assert.Equal(60, report.ErrorCount);
        Assert.Equal(BuildReport.MaxErrors, report.Errors.Count);
        Assert.Equal("m55", report.Errors[0].Member);
        Assert.Equal("m0", report.Errors[1].Member);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Only_warnings_in_the_batch_are_listed_and_at_most_20()
    {
        var warnings = Enumerable.Range(0, 30).Select(i => Diag("FALVehicle", i))
            .Concat(Enumerable.Range(30, 5).Select(i => Diag("Other", i))).ToArray();

        var report = BuildReport.From(Failed([], warnings), Batch, 1);

        Assert.Equal(35, report.WarningCount);
        Assert.Equal(BuildReport.MaxWarnings, report.Warnings.Count);
        Assert.All(report.Warnings, w => Assert.True(w.InBatch));
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Without_a_batch_the_first_warnings_are_listed()
    {
        var warnings = Enumerable.Range(0, 25).Select(i => Diag("Other", i)).ToArray();

        var report = BuildReport.From(Failed([], warnings), null, 1);

        Assert.Equal(BuildReport.MaxWarnings, report.Warnings.Count);
        Assert.Equal("m0", report.Warnings[0].Member);
        Assert.True(report.Truncated);
    }

    [Fact]
    public void Short_lists_are_not_truncated()
    {
        var report = BuildReport.From(Failed([Diag("Other", 1)], [Diag("FALVehicle", 2), Diag("Other", 3)]), Batch, 1);

        Assert.Single(report.Errors);
        Assert.Single(report.Warnings);
        Assert.False(report.Truncated);
    }

    [Fact]
    public void A_diagnostic_without_an_object_is_kept_and_not_in_the_batch()
    {
        var report = BuildReport.From(Failed([new { severity = "error", message = "Label file missing" }], []), Batch, 1);

        var error = Assert.Single(report.Errors);
        Assert.Null(error.Object);
        Assert.Equal("Label file missing", error.Message);
        Assert.False(error.InBatch);
    }

    [Fact]
    public void A_build_that_could_not_run_reports_d365fos_error()
    {
        var report = BuildReport.From(FakeD365fo.Fail("XPPC_NOT_FOUND", "Not found at X", "Set D365FO_PACKAGES_PATH"), Batch, 3);

        Assert.Null(report.Succeeded);
        Assert.Equal(new ToolError("XPPC_NOT_FOUND", "Not found at X", "Set D365FO_PACKAGES_PATH"), report.Error);
        Assert.Equal(3, report.ElapsedMs);
    }

    [Fact]
    public void A_timed_out_build_points_at_the_build_time_limit()
    {
        var timeout = CliResult.Failure(-1, "TIMEOUT", "`d365fo build --model` did not finish within 900 s and was stopped.",
            "Raise FO_ARTIFACTS_TIMEOUT_SECONDS if the model is large.");

        var report = BuildReport.From(timeout, Batch, 900_000);

        Assert.Equal("TIMEOUT", report.Error!.Code);
        Assert.Contains("FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS", report.Error.Hint);
        Assert.DoesNotContain("FO_ARTIFACTS_TIMEOUT_SECONDS", report.Error.Hint);
    }

    [Fact]
    public void An_ok_result_without_buildSucceeded_is_an_error()
    {
        var report = BuildReport.From(FakeD365fo.Ok(new { something = 1 }), null, 1);

        Assert.Equal("NO_BUILD_RESULT", report.Error!.Code);
    }
}
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build errors — `BuildReport`, `ToolError` do not exist.

- [ ] **Step 4: Write `ToolError` and `BuildReport`**

`src/FoArtifacts.Core/ToolError.cs`:

```csharp
namespace FoArtifacts.Core;

/// <summary>Why a build or refresh could not run.</summary>
public sealed record ToolError(string Code, string Message, string? Hint);
```

`src/FoArtifacts.Core/BuildReport.cs`:

```csharp
using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>One compiler diagnostic. <see cref="InBatch"/> is null when the build was not part of a batch.</summary>
public sealed record BuildDiagnostic(string? Object, string? Kind, string? Member, string Message, bool? InBatch);

/// <summary>
/// The outcome of <c>d365fo build --model</c>: either the build ran (<see cref="Succeeded"/> is set) or it
/// could not run (<see cref="Error"/> is set).
/// </summary>
public sealed record BuildReport(
    bool? Succeeded,
    int ErrorCount,
    int WarningCount,
    long ElapsedMs,
    IReadOnlyList<BuildDiagnostic> Errors,
    IReadOnlyList<BuildDiagnostic> Warnings,
    bool Truncated,
    string? Log,
    ToolError? Error)
{
    public const int MaxErrors = 50;
    public const int MaxWarnings = 20;

    public const string TimeoutHint = "Raise FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS (default 900) if the model is large.";

    public static BuildReport Failed(string code, string message, string? hint, long elapsedMs = 0) =>
        new(null, 0, 0, elapsedMs, [], [], false, null, new ToolError(code, message, hint));

    /// <summary>
    /// Reads d365fo's build result. A failed build exits non-zero with <c>ok: true</c>, which
    /// <see cref="Envelope"/> reports as an EXIT_CODE failure, so the data is read whenever it holds
    /// <c>buildSucceeded</c>, whatever <see cref="CliResult.Ok"/> says. Errors in
    /// <paramref name="batchObjects"/> come first; with a batch only its warnings are listed.
    /// </summary>
    public static BuildReport From(CliResult result, IReadOnlySet<string>? batchObjects, long elapsedMs)
    {
        var data = result.Data;
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("buildSucceeded", out var succeeded)
            || succeeded.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            if (result.Ok)
                return Failed("NO_BUILD_RESULT", "d365fo build returned no buildSucceeded field.", null, elapsedMs);
            var hint = result.ErrorCode == "TIMEOUT" ? TimeoutHint : result.ErrorHint;
            return Failed(result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", hint, elapsedMs);
        }

        var inBatch = batchObjects is null ? null : new HashSet<string>(batchObjects, StringComparer.OrdinalIgnoreCase);
        var errors = Diagnostics(data, "errors", inBatch);
        var warnings = Diagnostics(data, "warnings", inBatch);
        // OrderByDescending is stable: within each group d365fo's order is kept.
        var shownErrors = errors.OrderByDescending(d => d.InBatch == true).Take(MaxErrors).ToList();
        var candidates = inBatch is null ? warnings : warnings.Where(d => d.InBatch == true).ToList();
        var shownWarnings = candidates.Take(MaxWarnings).ToList();

        return new BuildReport(
            succeeded.GetBoolean(),
            Int(data, "errorCount") ?? errors.Count,
            Int(data, "warningCount") ?? warnings.Count,
            data.TryGetProperty("elapsedMs", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var d365foMs)
                ? d365foMs
                : elapsedMs,
            shownErrors,
            shownWarnings,
            errors.Count > MaxErrors || candidates.Count > MaxWarnings,
            Log(data),
            null);
    }

    private static List<BuildDiagnostic> Diagnostics(JsonElement data, string name, HashSet<string>? inBatch)
    {
        var list = new List<BuildDiagnostic>();
        if (!data.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(new BuildDiagnostic(null, null, null, item.GetString()!, inBatch is null ? null : false));
                continue;
            }
            if (item.ValueKind != JsonValueKind.Object) continue;
            var obj = Str(item, "object");
            list.Add(new BuildDiagnostic(
                obj, Str(item, "kind"), Str(item, "member"), Str(item, "message") ?? item.GetRawText(),
                inBatch is null ? null : obj is not null && inBatch.Contains(obj)));
        }
        return list;
    }

    private static string? Log(JsonElement data)
    {
        if (!data.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var model in models.EnumerateArray())
            if (model.ValueKind == JsonValueKind.Object && model.TryGetProperty("xppc", out var xppc) && Str(xppc, "log") is { } log)
                return log;
        return null;
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/FoArtifacts.Tests --filter-class "FoArtifacts.Tests.BuildReportTests"`
Expected: 11 passed. Then `dotnet test --project tests/FoArtifacts.Tests`: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/FoArtifacts.Core/ToolError.cs src/FoArtifacts.Core/BuildReport.cs tests/FoArtifacts.Tests/Fixtures tests/FoArtifacts.Tests/FoArtifacts.Tests.csproj tests/FoArtifacts.Tests/TestData.cs tests/FoArtifacts.Tests/BuildReportTests.cs
git commit -m "Parse d365fo build results into a BuildReport"
```

---

### Task 3: ModelBuild

**Files:**
- Create: `src/FoArtifacts.Core/ModelBuild.cs`, `tests/FoArtifacts.Tests/ModelBuildTests.cs`
- Modify: `tests/FoArtifacts.Tests/FakeD365fo.cs` (`Default`, add a `build` case)

**Interfaces:**
- Consumes: `ModelCheck`, `ModelCheck.NotIndexed`, `BuildReport.From`, `BuildReport.Failed` (Tasks 1-2).
- Produces: `ModelBuild(ID365fo buildCli, ModelCheck check)`; `ModelBuild.Args(string model) : IReadOnlyList<string>`; `ModelBuild.RunAsync(string model, IReadOnlySet<string>? batchObjects, CancellationToken ct) : Task<BuildReport>` (never throws on cancellation; returns error code `CANCELLED`). Error codes it adds: `D365FO_UNUSABLE`, `MODEL_NOT_FOUND`, `MODEL_NOT_CUSTOM`, `CANCELLED`.

- [ ] **Step 1: Teach the fake to build**

In `FakeD365fo.Default`, add before `default:`:

```csharp
            case "build":
                return Ok(new
                {
                    buildSucceeded = true,
                    exitCode = 0,
                    elapsedMs = 5,
                    errorCount = 0,
                    warningCount = 0,
                    errors = Array.Empty<object>(),
                    warnings = Array.Empty<object>(),
                    models = new[] { new { model = a[2], xppc = new { log = $"/pkg/{a[2]}/BuildModelResult.log" } } },
                });
```

- [ ] **Step 2: Write the failing tests**

`tests/FoArtifacts.Tests/ModelBuildTests.cs`:

```csharp
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelBuildTests
{
    private const string BuildCall = "build --model ContosoFleet --output json";

    private static ModelBuild Make(FakeD365fo fake) => new(fake, new ModelCheck(fake));

    [Fact]
    public async Task Builds_a_custom_model()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.True(report.Succeeded);
        Assert.Equal("/pkg/ContosoFleet/BuildModelResult.log", report.Log);
        Assert.Contains(BuildCall, fake.Calls);
    }

    [Fact]
    public async Task A_model_not_marked_custom_is_not_built()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.Equal("MODEL_NOT_CUSTOM", report.Error!.Code);
        Assert.Contains("D365FO_CUSTOM_MODELS", report.Error.Hint);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("build"));
    }

    [Fact]
    public async Task An_unknown_model_is_not_built()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("Nope", null, Ct);

        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
        Assert.Contains("Ax", report.Error.Message);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("build"));
    }

    [Fact]
    public async Task An_unusable_d365fo_is_reported()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "models" ? FakeD365fo.Fail("NO_INDEX", "No index") : null };

        var report = await Make(fake).RunAsync("ContosoFleet", null, Ct);

        Assert.Equal("D365FO_UNUSABLE", report.Error!.Code);
    }

    [Fact]
    public async Task The_build_call_can_be_cancelled()
    {
        // A build writes no source files, so unlike generate, refresh and undo it may be stopped.
        var fake = new FakeD365fo();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Make(fake).RunAsync("ContosoFleet", null, cts.Token);

        Assert.True(fake.Cancellable[fake.Calls.IndexOf(BuildCall)]);
    }

    [Fact]
    public async Task A_cancelled_build_is_reported_not_thrown()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var fake = new FakeD365fo();
        fake.Override = a =>
        {
            if (a[0] != "build") return null;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var report = await Make(fake).RunAsync("ContosoFleet", null, cts.Token);

        Assert.Equal("CANCELLED", report.Error!.Code);
    }
}
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build error — `ModelBuild` does not exist.

- [ ] **Step 4: Write `ModelBuild`**

`src/FoArtifacts.Core/ModelBuild.cs`:

```csharp
using System.Diagnostics;

namespace FoArtifacts.Core;

/// <summary>
/// Compiles one custom model with <c>d365fo build --model</c>. A build writes no source files, only
/// <c>bin</c> output the next build overwrites, so unlike generate, refresh and undo calls it may be
/// cancelled while it runs. It takes no lock; callers hold <see cref="ModelLock"/>.
/// </summary>
public sealed class ModelBuild(ID365fo buildCli, ModelCheck check)
{
    public static IReadOnlyList<string> Args(string model) => ["build", "--model", model, "--output", "json"];

    public async Task<BuildReport> RunAsync(string model, IReadOnlySet<string>? batchObjects, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var (info, error) = await check.LookUpAsync(model, ct);
            if (error is not null)
                return BuildReport.Failed("D365FO_UNUSABLE", error, null, clock.ElapsedMilliseconds);
            if (!info!.Found)
                return BuildReport.Failed("MODEL_NOT_FOUND", ModelCheck.NotIndexed(model, info.Known), null, clock.ElapsedMilliseconds);
            if (!info.IsCustom)
                return BuildReport.Failed("MODEL_NOT_CUSTOM",
                    $"Model '{model}' is not marked as custom in d365fo, so fo-artifacts does not build it.",
                    "If the model is yours, add it to D365FO_CUSTOM_MODELS, restart the MCP server, then run refresh_index with force.",
                    clock.ElapsedMilliseconds);

            var result = await buildCli.RunAsync(Args(model), ct);
            return BuildReport.From(result, batchObjects, clock.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return BuildReport.Failed("CANCELLED", "The build was cancelled.", null, clock.ElapsedMilliseconds);
        }
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/FoArtifacts.Core/ModelBuild.cs tests/FoArtifacts.Tests/ModelBuildTests.cs tests/FoArtifacts.Tests/FakeD365fo.cs
git commit -m "Build a custom model with d365fo build --model"
```

---

### Task 4: IndexRefresh

**Files:**
- Create: `src/FoArtifacts.Core/IndexRefresh.cs`, `tests/FoArtifacts.Tests/IndexRefreshTests.cs`
- Modify: `src/FoArtifacts.Core/ModelCheck.cs` (add `StillNotIndexed`), `tests/FoArtifacts.Tests/FakeD365fo.cs` (`index` case)

**Interfaces:**
- Consumes: `ModelCheck`, `ToolError`, `BatchExecutor.RefreshArgs` (existing).
- Produces: `RefreshReport(bool Ok, string Model, bool Refreshed, long ElapsedMs, IReadOnlyList<string> Warnings, ToolError? Error)`; `IndexRefresh(ID365fo cli, ModelCheck check)`; `IndexRefresh.Args(string model, bool force)`; `IndexRefresh.RunAsync(string model, bool force, CancellationToken ct) : Task<RefreshReport>`; `ModelCheck.StillNotIndexed(string model) : string`.

- [ ] **Step 1: Make the fake's refresh report what d365fo reports**

In `FakeD365fo.Default`, replace the `index` case:

```csharp
            case "index":
                return Ok(new { modelsProcessed = 1, modelsSkipped = 0 });
```

- [ ] **Step 2: Write the failing tests**

`tests/FoArtifacts.Tests/IndexRefreshTests.cs`:

```csharp
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class IndexRefreshTests
{
    private const string RefreshCall = "index refresh --model ContosoFleet --output json";

    private static IndexRefresh Make(FakeD365fo fake) => new(fake, new ModelCheck(fake));

    [Fact]
    public async Task Refreshes_and_reports_a_reread_model()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.True(report.Refreshed);
        Assert.Empty(report.Warnings);
        Assert.Contains(RefreshCall, fake.Calls);
    }

    [Fact]
    public async Task Reports_a_model_d365fo_skipped_as_unchanged()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "index" ? FakeD365fo.Ok(new { modelsProcessed = 0, modelsSkipped = 1 }) : null };

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.False(report.Refreshed);
    }

    [Fact]
    public async Task Force_adds_the_force_flag()
    {
        var fake = new FakeD365fo();

        await Make(fake).RunAsync("ContosoFleet", true, Ct);

        Assert.Contains("index refresh --model ContosoFleet --force --output json", fake.Calls);
    }

    [Fact]
    public async Task A_model_still_missing_after_the_refresh_fails_with_the_Ax_hint()
    {
        var fake = new FakeD365fo();

        var report = await Make(fake).RunAsync("Nope", false, Ct);

        Assert.False(report.Ok);
        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
        Assert.Contains("still not in the d365fo index", report.Error.Message);
        Assert.Contains("Ax", report.Error.Message);
        Assert.Contains("index refresh --model Nope --output json", fake.Calls);
    }

    [Fact]
    public async Task A_model_not_marked_custom_is_refreshed_with_a_warning()
    {
        var fake = new FakeD365fo();
        fake.Models[0] = ("ContosoFleet", false);

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.True(report.Ok);
        Assert.Contains("D365FO_CUSTOM_MODELS", Assert.Single(report.Warnings));
    }

    [Fact]
    public async Task A_failed_refresh_reports_d365fos_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "index" ? FakeD365fo.Fail("LOCKED", "db locked", "retry") : null };

        var report = await Make(fake).RunAsync("ContosoFleet", false, Ct);

        Assert.False(report.Ok);
        Assert.Equal(new ToolError("LOCKED", "db locked", "retry"), report.Error);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("models"));
    }

    [Fact]
    public async Task The_refresh_is_not_cancellable_once_started()
    {
        var fake = new FakeD365fo();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Make(fake).RunAsync("ContosoFleet", false, cts.Token);

        Assert.False(fake.Cancellable[fake.Calls.IndexOf(RefreshCall)]);
    }
}
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build error — `IndexRefresh`, `RefreshReport` do not exist.

- [ ] **Step 4: Add `StillNotIndexed` to `ModelCheck`**

In `src/FoArtifacts.Core/ModelCheck.cs`, after `NotIndexed`:

```csharp
    public static string StillNotIndexed(string model) =>
        $"Model '{model}' is still not in the d365fo index after the refresh. d365fo skips an empty model "
        + "(one with no Ax* folder such as AxClass yet), so create one empty Ax* folder in the model folder, "
        + "and check that the model folder is under D365FO_PACKAGES_PATH.";
```

- [ ] **Step 5: Write `IndexRefresh`**

`src/FoArtifacts.Core/IndexRefresh.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;

namespace FoArtifacts.Core;

/// <summary>The outcome of <see cref="IndexRefresh"/>. <see cref="Refreshed"/> is false when d365fo skipped the model as unchanged.</summary>
public sealed record RefreshReport(bool Ok, string Model, bool Refreshed, long ElapsedMs, IReadOnlyList<string> Warnings, ToolError? Error);

/// <summary>
/// Re-reads one model into the index with <c>d365fo index refresh --model</c>, then checks the model is
/// there. The name is not checked against D365FO_CUSTOM_MODELS first: d365fo's patterns allow globs and
/// <c>!</c> exclusions, and a refresh only touches the index. It takes no lock; callers hold
/// <see cref="ModelLock"/>.
/// </summary>
public sealed class IndexRefresh(ID365fo cli, ModelCheck check)
{
    public static IReadOnlyList<string> Args(string model, bool force) =>
        force ? ["index", "refresh", "--model", model, "--force", "--output", "json"] : BatchExecutor.RefreshArgs(model);

    public async Task<RefreshReport> RunAsync(string model, bool force, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();
        // Like the batch's refreshes, a refresh is never cancelled once it has started.
        var result = await cli.RunAsync(Args(model, force), CancellationToken.None);
        if (!result.Ok)
            return Failed(model, result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", result.ErrorHint, clock);

        var (info, error) = await check.LookUpAsync(model, CancellationToken.None);
        if (error is not null) return Failed(model, "D365FO_UNUSABLE", error, null, clock);
        if (!info!.Found) return Failed(model, "MODEL_NOT_FOUND", ModelCheck.StillNotIndexed(model), null, clock);

        var warnings = result.Warnings.ToList();
        if (!info.IsCustom) warnings.Add(ModelCheck.NotCustom(model));
        var processed = result.Data.ValueKind == JsonValueKind.Object
            && result.Data.TryGetProperty("modelsProcessed", out var count)
            && count.ValueKind == JsonValueKind.Number
            && count.TryGetInt32(out var n)
            && n > 0;
        return new RefreshReport(true, model, processed, clock.ElapsedMilliseconds, warnings, null);
    }

    private static RefreshReport Failed(string model, string code, string message, string? hint, Stopwatch clock) =>
        new(false, model, false, clock.ElapsedMilliseconds, [], new ToolError(code, message, hint));
}
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass (the batch tests still see `index refresh` succeed).

- [ ] **Step 7: Commit**

```bash
git add src/FoArtifacts.Core/IndexRefresh.cs src/FoArtifacts.Core/ModelCheck.cs tests/FoArtifacts.Tests/IndexRefreshTests.cs tests/FoArtifacts.Tests/FakeD365fo.cs
git commit -m "Refresh one model in the d365fo index on request"
```

---

### Task 5: JSON for build and refresh results

**Files:**
- Modify: `src/FoArtifacts.Core/Summary.cs`
- Test: `tests/FoArtifacts.Tests/SummaryTests.cs`

**Interfaces:**
- Consumes: `BuildReport`, `BuildDiagnostic`, `RefreshReport`, `ToolError` (Tasks 2, 4).
- Produces: `Summary.ToJson(string model, BuildReport report) : string`; `Summary.ToJson(RefreshReport report) : string`; `internal static object Summary.BuildSection(BuildReport report)` (used by Task 6).

- [ ] **Step 1: Write the failing tests** (add to `SummaryTests`)

```csharp
    [Fact]
    public void Build_model_json_lists_errors_without_in_batch()
    {
        var report = BuildReport.From(Fixture("build-failed.json", 1), null, 1);

        var json = JsonDocument.Parse(Summary.ToJson("FoArtifactsLab", report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("FoArtifactsLab", json.GetProperty("model").GetString());
        var build = json.GetProperty("build");
        Assert.False(build.GetProperty("succeeded").GetBoolean());
        Assert.Equal(2, build.GetProperty("error_count").GetInt32());
        Assert.Equal(7235, build.GetProperty("elapsed_ms").GetInt64());
        var first = build.GetProperty("errors")[0];
        Assert.Equal("FALVehicle", first.GetProperty("object").GetString());
        Assert.Equal("AxForm", first.GetProperty("kind").GetString());
        Assert.False(first.TryGetProperty("in_batch", out _));
        Assert.Equal(0, build.GetProperty("warnings").GetArrayLength());
        Assert.False(build.TryGetProperty("truncated", out _));
        Assert.EndsWith("BuildModelResult.log", build.GetProperty("log").GetString());
    }

    [Fact]
    public void A_clean_build_model_is_ok()
    {
        var json = JsonDocument.Parse(Summary.ToJson("FoArtifactsLab", BuildReport.From(Fixture("build-clean.json", 0), null, 1))).RootElement;

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.True(json.GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void A_build_that_could_not_run_has_only_an_error()
    {
        var report = BuildReport.Failed("XPPC_NOT_FOUND", "Not found", "Set D365FO_PACKAGES_PATH");

        var json = JsonDocument.Parse(Summary.ToJson("ContosoFleet", report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        var build = json.GetProperty("build");
        Assert.False(build.TryGetProperty("succeeded", out _));
        Assert.Equal("XPPC_NOT_FOUND", build.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Set D365FO_PACKAGES_PATH", build.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Truncated_lists_say_so()
    {
        var report = new BuildReport(false, 60, 0, 1, [], [], true, null, null);

        var json = JsonDocument.Parse(Summary.ToJson("ContosoFleet", report)).RootElement;

        Assert.True(json.GetProperty("build").GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Refresh_json_reports_whether_the_model_was_reread()
    {
        var json = JsonDocument.Parse(Summary.ToJson(new RefreshReport(true, "ContosoFleet", false, 12, [], null))).RootElement;

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.False(json.GetProperty("refreshed").GetBoolean());
        Assert.Equal(12, json.GetProperty("elapsed_ms").GetInt64());
        Assert.False(json.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void A_failed_refresh_json_has_the_error()
    {
        var report = new RefreshReport(false, "Nope", false, 3, [], new ToolError("MODEL_NOT_FOUND", "still not in the index", null));

        var json = JsonDocument.Parse(Summary.ToJson(report)).RootElement;

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("MODEL_NOT_FOUND", json.GetProperty("error").GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("refreshed", out _));
    }
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests --filter-class "FoArtifacts.Tests.SummaryTests"`
Expected: build error — no `Summary.ToJson(string, BuildReport)` / `ToJson(RefreshReport)` overloads.

- [ ] **Step 3: Write the JSON**

In `src/FoArtifacts.Core/Summary.cs`, after `public static string ToJson(BatchResult result) => ...;` add:

```csharp
    /// <summary>The result of build_model: <c>ok</c> means the build ran and succeeded.</summary>
    public static string ToJson(string model, BuildReport report) =>
        JsonSerializer.Serialize(new { ok = report.Succeeded == true, model, build = BuildSection(report) }, Options);

    public static string ToJson(RefreshReport r) => JsonSerializer.Serialize(
        r.Error is not null
            ? new { ok = false, model = r.Model, error = ErrorJson(r.Error) }
            : (object)new
            {
                ok = true,
                model = r.Model,
                refreshed = r.Refreshed,
                elapsed_ms = r.ElapsedMs,
                warnings = r.Warnings.Count > 0 ? r.Warnings : null,
            },
        Options);

    /// <summary>The <c>build</c> section shared by a batch result and build_model.</summary>
    internal static object BuildSection(BuildReport b) =>
        b.Error is not null
            ? (object)new { error = ErrorJson(b.Error) }
            : new
            {
                succeeded = b.Succeeded,
                error_count = b.ErrorCount,
                warning_count = b.WarningCount,
                elapsed_ms = b.ElapsedMs,
                errors = b.Errors.Select(Diagnostic).ToList(),
                warnings = b.Warnings.Select(Diagnostic).ToList(),
                truncated = b.Truncated ? true : (bool?)null,
                log = b.Log,
            };

    private static object Diagnostic(BuildDiagnostic d) =>
        new { @object = d.Object, kind = d.Kind, member = d.Member, message = d.Message, in_batch = d.InBatch };

    private static object ErrorJson(ToolError e) => new { code = e.Code, message = e.Message, hint = e.Hint };
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/FoArtifacts.Core/Summary.cs tests/FoArtifacts.Tests/SummaryTests.cs
git commit -m "Report build and refresh results as JSON"
```

---

### Task 6: `build` flag on the batch

**Files:**
- Modify: `src/FoArtifacts.Core/BatchSpec.cs:10,26,150`, `src/FoArtifacts.Core/NameExpander.cs:20,124`, `src/FoArtifacts.Core/BatchExecutor.cs`, `src/FoArtifacts.Core/Summary.cs` (`Build`, dry-run and success branches), `tests/FoArtifacts.Tests/FakeD365fo.cs`
- Test: `tests/FoArtifacts.Tests/BatchExecutorTests.cs`, `tests/FoArtifacts.Tests/SummaryTests.cs`

**Interfaces:**
- Consumes: `ModelBuild`, `ModelCheck`, `ModelLock`, `Summary.BuildSection` (Tasks 1-5).
- Produces: `BatchRequest(..., bool DryRun = false, bool Build = false)`; `ValidBatch(..., IReadOnlyList<ValidTask> Tasks, bool Build = false)`; `ExpandedBatch(..., IReadOnlyList<ExpandedTask> Tasks, bool Build = false)`; `BatchResult(..., long ElapsedMs, bool BuildRequested = false, BuildReport? Build = null)`; `BatchExecutor(ID365fo cli, ReferenceMap map, ModelLock? modelLock = null, ModelBuild? builder = null)`; `public static IReadOnlySet<string> BatchExecutor.BatchObjects(IEnumerable<TaskOutcome> completed)`; `FakeD365fo.BuildFailed(params (string Object, string Message)[] errors) : CliResult`.

- [ ] **Step 1: Add a failed-build helper to the fake**

In `tests/FoArtifacts.Tests/FakeD365fo.cs`, next to `Ok` and `Fail`:

```csharp
    /// <summary>A failed build as d365fo reports it: ok true, exit code 1, one error per pair.</summary>
    public static CliResult BuildFailed(params (string Object, string Message)[] errors) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new
        {
            ok = true,
            data = new
            {
                buildSucceeded = false,
                errorCount = errors.Length,
                warningCount = 0,
                errors = errors.Select(e => new { severity = "error", kind = "AxTable", @object = e.Object, member = "", message = e.Message }),
                warnings = Array.Empty<object>(),
            },
            warnings = new[] { "build-failed" },
        }), "");
```

- [ ] **Step 2: Write the failing tests** (add to `BatchExecutorTests`)

```csharp
    private const string BuildCall = "build --model ContosoFleet --output json";

    [Fact]
    public async Task Without_build_the_model_is_not_compiled()
    {
        var (fake, executor) = Make();

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.True(result.Ok);
        Assert.DoesNotContain(BuildCall, fake.Calls);
        Assert.Null(result.Build);
    }

    [Fact]
    public async Task With_build_the_model_is_compiled_once_after_the_last_refresh()
    {
        var (fake, executor) = Make();

        var result = await executor.RunAsync(Vehicle() with { Build = true }, Ct);

        Assert.True(result.Ok);
        Assert.True(result.Build!.Succeeded);
        var steps = fake.Calls.Where(c => c.StartsWith("generate ") || c.StartsWith("index refresh") || c.StartsWith("build ")).ToList();
        Assert.Equal(new[] { Refresh, BuildCall }, steps.TakeLast(2));
        Assert.Equal(1, fake.Calls.Count(c => c == BuildCall));
    }

    [Fact]
    public async Task A_failed_batch_is_rolled_back_and_not_built()
    {
        var (fake, executor) = Make();
        fake.Override = FailWhen("table");

        var result = await executor.RunAsync(Vehicle() with { Build = true }, Ct);

        Assert.False(result.Ok);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Null(result.Build);
        Assert.DoesNotContain(BuildCall, fake.Calls);
    }

    [Fact]
    public async Task A_failed_build_keeps_the_batch()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "build" ? FakeD365fo.BuildFailed(("CFltVehicle", "Bad field")) : null;

        var result = await executor.RunAsync(Vehicle() with { Build = true }, Ct);

        Assert.True(result.Ok);
        Assert.False(result.Build!.Succeeded);
        Assert.Null(result.Rollback);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("undo"));
        Assert.Equal(3, fake.Journal.Count);
    }

    [Fact]
    public async Task Build_errors_in_the_batchs_objects_are_marked()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "build"
            ? FakeD365fo.BuildFailed(("OldThing", "Was already broken"), ("CFLTVEHICLE", "Bad field"))
            : null;

        var result = await executor.RunAsync(Vehicle() with { Build = true }, Ct);

        Assert.Equal("CFLTVEHICLE", result.Build!.Errors[0].Object);
        Assert.True(result.Build.Errors[0].InBatch);
        Assert.Equal("OldThing", result.Build.Errors[1].Object);
        Assert.False(result.Build.Errors[1].InBatch);
    }

    [Fact]
    public async Task Batch_objects_are_task_names_definitions_and_reported_files()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "generate" && a[1] == "table"
            ? FakeD365fo.Ok(new { path = "/pkg/ContosoFleet/AxTable/CFltVehicle.xml", companion = new { path = "/pkg/ContosoFleet/AxSecurityPrivilege/CFltVehicleView.xml" } })
            : null;

        var result = await executor.RunAsync(Vehicle(), Ct);
        var objects = BatchExecutor.BatchObjects(result.Completed);

        Assert.Contains("CFltVehicleStatus", objects);
        Assert.Contains("cfltvehicle", objects);
        Assert.Contains("CFltVehicleView", objects);
    }

    [Fact]
    public async Task A_batch_on_a_model_not_marked_custom_is_written_but_not_built()
    {
        var (fake, executor) = Make();
        fake.Models[0] = ("ContosoFleet", false);

        var result = await executor.RunAsync(Vehicle() with { Build = true }, Ct);

        Assert.True(result.Ok);
        Assert.Contains("D365FO_CUSTOM_MODELS", Assert.Single(result.Warnings));
        Assert.Equal("MODEL_NOT_CUSTOM", result.Build!.Error!.Code);
        Assert.DoesNotContain(BuildCall, fake.Calls);
    }

    [Fact]
    public async Task The_build_can_be_cancelled_but_the_writes_cannot()
    {
        var (fake, executor) = Make();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await executor.RunAsync(Vehicle() with { Build = true }, cts.Token);

        Assert.True(fake.Cancellable[fake.Calls.IndexOf(BuildCall)]);
        var writes = fake.Calls.Select((c, i) => (c, i)).Where(x => x.c.StartsWith("generate ") || x.c.StartsWith("index refresh"));
        Assert.All(writes, x => Assert.False(fake.Cancellable[x.i], $"'{x.c}' could be cancelled"));
    }

    [Fact]
    public async Task Cancelling_during_the_build_keeps_the_batch()
    {
        var gate = new ModelLock();
        var fake = new FakeD365fo();
        var executor = new BatchExecutor(fake, Map, gate);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        fake.Override = a =>
        {
            if (a[0] != "build") return null;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var result = await executor.RunAsync(Vehicle() with { Build = true }, cts.Token);

        Assert.True(result.Ok);
        Assert.Equal("CANCELLED", result.Build!.Error!.Code);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("undo"));
        using var again = await gate.AcquireAsync(Ct); // the lock was released
    }

    [Fact]
    public async Task A_dry_run_with_build_calls_nothing_and_notes_the_build()
    {
        var (fake, executor) = Make();

        var result = await executor.RunAsync(Vehicle() with { DryRun = true, Build = true }, Ct);

        Assert.Empty(fake.Calls);
        Assert.True(result.BuildRequested);
        Assert.Null(result.Build);
    }
```

Add to `SummaryTests`:

```csharp
    [Fact]
    public async Task A_dry_run_with_build_ends_with_the_build_command()
    {
        var json = await RunAsync(new FakeD365fo(), Request(dryRun: true) with { Build = true });

        Assert.Equal("d365fo build --model ContosoFleet --output json", json.GetProperty("build").GetString());
    }

    [Fact]
    public async Task A_dry_run_without_build_has_no_build_command()
    {
        var json = await RunAsync(new FakeD365fo(), Request(dryRun: true));

        Assert.False(json.TryGetProperty("build", out _));
    }

    [Fact]
    public async Task A_built_batch_reports_the_build_next_to_its_artifacts()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "build" ? FakeD365fo.BuildFailed(("CFltVehicle", "Bad field")) : null };

        var json = await RunAsync(fake, Request() with { Build = true });

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.GetProperty("artifacts").GetArrayLength());
        var build = json.GetProperty("build");
        Assert.False(build.GetProperty("succeeded").GetBoolean());
        Assert.True(build.GetProperty("errors")[0].GetProperty("in_batch").GetBoolean());
    }
```

- [ ] **Step 3: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build errors — `BatchRequest` has no `Build`, `BatchResult` has no `Build` / `BuildRequested`, no `BatchExecutor.BatchObjects`.

- [ ] **Step 4: Carry `Build` through the request**

`src/FoArtifacts.Core/BatchSpec.cs` line 10:

```csharp
public sealed record BatchRequest(string Model, string Prefix, IReadOnlyList<TaskInput> Tasks, bool DryRun = false, bool Build = false);
```

Line 26:

```csharp
public sealed record ValidBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ValidTask> Tasks, bool Build = false);
```

Line 150:

```csharp
            : (new ValidBatch(request.Model, request.Prefix ?? "", request.DryRun, valid, request.Build), errors);
```

`src/FoArtifacts.Core/NameExpander.cs` line 20:

```csharp
public sealed record ExpandedBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ExpandedTask> Tasks, bool Build = false);
```

Line 124:

```csharp
            : (new ExpandedBatch(batch.Model, batch.Prefix, batch.DryRun, expandedTasks, batch.Build), errors);
```

- [ ] **Step 5: Build after a successful batch**

In `src/FoArtifacts.Core/BatchExecutor.cs`, extend `BatchResult` (the trailing defaults keep every existing `new BatchResult(...)` compiling):

```csharp
public sealed record BatchResult(
    bool Ok,
    bool DryRun,
    string Model,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<Stage> Stages,
    IReadOnlyList<TaskOutcome> Completed,
    BatchFailure? Failure,
    RollbackReport? Rollback,
    long ElapsedMs,
    bool BuildRequested = false,
    BuildReport? Build = null);
```

Class header and fields:

```csharp
public sealed class BatchExecutor(ID365fo cli, ReferenceMap map, ModelLock? modelLock = null, ModelBuild? builder = null)
{
    public const int JournalLimit = 2000;

    private readonly ModelLock _lock = modelLock ?? new ModelLock();
    private readonly ModelCheck _check = new(cli);
    private readonly ModelBuild _build = builder ?? new ModelBuild(cli, new ModelCheck(cli));
```

Dry-run return in `RunAsync`:

```csharp
        if (expanded.DryRun)
            return new BatchResult(true, true, expanded.Model, [], [], stages, [], null, null, clock.ElapsedMilliseconds,
                BuildRequested: expanded.Build);
```

Success return in `ExecuteAsync` (replace `if (failure is null) return new BatchResult(true, ...);`):

```csharp
        if (failure is null)
        {
            // Still inside the lock, so nothing writes to the model while it compiles. A failed build keeps
            // the batch: d365fo compiles the whole model, so an error may predate the batch.
            var build = batch.Build ? await _build.RunAsync(batch.Model, BatchObjects(completed), ct) : null;
            return new BatchResult(true, false, batch.Model, [], warnings, stages, completed, null, null,
                clock.ElapsedMilliseconds, batch.Build, build);
        }
```

Add, after `Fail`:

```csharp
    /// <summary>
    /// Names of the objects a batch wrote or changed, for marking build errors <c>in_batch</c>: each task's
    /// name (for form methods, the form), the names it defines, and the file name of every path it reported.
    /// </summary>
    public static IReadOnlySet<string> BatchObjects(IEnumerable<TaskOutcome> completed)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var outcome in completed)
        {
            names.Add(outcome.Task.Name);
            names.UnionWith(outcome.Task.DefinedNames);
            foreach (var path in ArtifactPaths.From(outcome.Result.Data))
                names.Add(Path.GetFileNameWithoutExtension(path));
        }
        return names;
    }
```

- [ ] **Step 6: Show the build in the batch JSON**

In `src/FoArtifacts.Core/Summary.cs`, `Build(BatchResult r)`, dry-run branch — add a `build` property after `plan`:

```csharp
                build = r.BuildRequested ? CommandBuilder.Render(ModelBuild.Args(r.Model)) : null,
```

Success branch — replace the `if (r.Ok) return new { ... };` line:

```csharp
        if (r.Ok)
            return new
            {
                ok = true,
                model = r.Model,
                stages = r.Stages.Count,
                elapsed_ms = r.ElapsedMs,
                artifacts,
                build = r.Build is null ? null : BuildSection(r.Build),
                warnings,
            };
```

- [ ] **Step 7: Run all unit tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass, including the existing `A_running_command_is_not_interrupted_by_cancellation` and `Runs_stage_by_stage_and_refreshes_after_each_stage`.

- [ ] **Step 8: Commit**

```bash
git add src/FoArtifacts.Core/BatchSpec.cs src/FoArtifacts.Core/NameExpander.cs src/FoArtifacts.Core/BatchExecutor.cs src/FoArtifacts.Core/Summary.cs tests/FoArtifacts.Tests/FakeD365fo.cs tests/FoArtifacts.Tests/BatchExecutorTests.cs tests/FoArtifacts.Tests/SummaryTests.cs
git commit -m "Compile the model after a successful batch when build is set"
```

---

### Task 7: MCP tools and wiring

**Files:**
- Create: `src/FoArtifacts/BuildModelTool.cs`, `src/FoArtifacts/RefreshIndexTool.cs`, `tests/FoArtifacts.Tests/ModelToolsTests.cs`
- Modify: `src/FoArtifacts/GenerateFoArtifactsTool.cs`, `src/FoArtifacts/Program.cs`, `src/FoArtifacts.Core/D365foRunner.cs:147-150`
- Test: `tests/FoArtifacts.Tests/GenerateFoArtifactsToolTests.cs`, `tests/FoArtifacts.Tests/D365foRunnerTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: MCP tools `build_model(model)` and `refresh_index(model, force = false)`; `generate_fo_artifacts(..., dry_run = false, build = false)`; `CliLocator.BuildTimeout(Func<string, string?> getEnv) : TimeSpan`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/FoArtifacts.Tests/D365foRunnerTests.cs` (inside the class, after the `Timeout` theory):

```csharp
    [Theory]
    [InlineData("1800", 1800)]
    [InlineData("abc", 900)]
    [InlineData("0", 900)]
    [InlineData(null, 900)]
    public void Build_timeout_comes_from_FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS(string? value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CliLocator.BuildTimeout(name => name == "FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS" ? value : null));

    [Fact]
    public void The_build_timeout_ignores_the_per_call_timeout() =>
        Assert.Equal(TimeSpan.FromSeconds(900), CliLocator.BuildTimeout(name => name == "FO_ARTIFACTS_TIMEOUT_SECONDS" ? "30" : null));
```

In `tests/FoArtifacts.Tests/GenerateFoArtifactsToolTests.cs`, the new `build` parameter sits after `dry_run`, so the existing calls must name the token. Change both calls in `Returns_the_summary_and_flags_failures_as_errors` to:

```csharp
        var ok = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], dry_run: false, cancellationToken: Ct);
```

```csharp
        var bad = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("tabel", "X")], dry_run: false, cancellationToken: Ct);
```

and add:

```csharp
    [Fact]
    public async Task Build_is_passed_through_and_a_failed_build_is_not_an_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "build" ? FakeD365fo.BuildFailed(("CFltStatus", "Bad")) : null };
        var tool = new GenerateFoArtifactsTool(new BatchExecutor(fake, Map));

        var result = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], build: true, cancellationToken: Ct);

        Assert.False(result.IsError);
        var json = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;
        Assert.False(json.GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public void Description_says_a_failed_build_does_not_undo_the_batch() =>
        Assert.Contains("a failed build does not undo the batch", GenerateFoArtifactsTool.ToolDescription);
```

`tests/FoArtifacts.Tests/ModelToolsTests.cs`:

```csharp
using System.Text.Json;
using FoArtifacts;
using FoArtifacts.Core;
using ModelContextProtocol.Protocol;
using Xunit;
using static FoArtifacts.Tests.TestData;

namespace FoArtifacts.Tests;

public class ModelToolsTests
{
    private static JsonElement Json(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text).RootElement;

    private static BuildModelTool BuildTool(FakeD365fo fake, ModelLock? gate = null) =>
        new(new ModelBuild(fake, new ModelCheck(fake)), gate ?? new ModelLock());

    private static RefreshIndexTool RefreshTool(FakeD365fo fake) =>
        new(new IndexRefresh(fake, new ModelCheck(fake)), new ModelLock());

    [Fact]
    public async Task Build_model_returns_a_clean_build()
    {
        var result = await BuildTool(new FakeD365fo()).BuildModel("ContosoFleet", Ct);

        Assert.False(result.IsError);
        Assert.True(Json(result).GetProperty("build").GetProperty("succeeded").GetBoolean());
    }

    [Fact]
    public async Task Build_model_flags_a_failed_build_as_an_error()
    {
        var fake = new FakeD365fo { Override = a => a[0] == "build" ? Fixture("build-failed.json", 1) : null };

        var result = await BuildTool(fake).BuildModel("ContosoFleet", Ct);

        Assert.True(result.IsError);
        Assert.Equal(2, Json(result).GetProperty("build").GetProperty("error_count").GetInt32());
    }

    [Fact]
    public async Task Build_model_waits_while_a_batch_holds_the_lock()
    {
        var fake = new FakeD365fo();
        var gate = new ModelLock();
        var held = await gate.AcquireAsync(Ct);

        var call = BuildTool(fake, gate).BuildModel("ContosoFleet", Ct);
        await Task.Delay(50, Ct);
        Assert.Empty(fake.Calls);

        held.Dispose();
        Assert.False((await call).IsError);
    }

    [Fact]
    public async Task Refresh_index_returns_the_refresh_summary()
    {
        var result = await RefreshTool(new FakeD365fo()).RefreshIndex("ContosoFleet", force: true, Ct);

        Assert.False(result.IsError);
        Assert.True(Json(result).GetProperty("refreshed").GetBoolean());
    }

    [Fact]
    public async Task Refresh_index_flags_a_missing_model_as_an_error()
    {
        var result = await RefreshTool(new FakeD365fo()).RefreshIndex("Nope", force: false, Ct);

        Assert.True(result.IsError);
    }
}
```

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: build errors — no `CliLocator.BuildTimeout`, no `build` parameter, no `BuildModelTool` / `RefreshIndexTool`.

- [ ] **Step 3: Add the build time limit**

In `src/FoArtifacts.Core/D365foRunner.cs`, replace `CliLocator.Timeout` (lines 147-150):

```csharp
    public static TimeSpan Timeout(Func<string, string?> getEnv) => Seconds(getEnv("FO_ARTIFACTS_TIMEOUT_SECONDS"), 120);

    /// <summary>The limit for <c>d365fo build</c> alone: a large model compiles for minutes.</summary>
    public static TimeSpan BuildTimeout(Func<string, string?> getEnv) => Seconds(getEnv("FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS"), 900);

    private static TimeSpan Seconds(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(fallback);
```

- [ ] **Step 4: Add `build` to `generate_fo_artifacts`**

In `src/FoArtifacts/GenerateFoArtifactsTool.cs`, in `ToolDescription` replace `+ "Use dry_run to see the plan first. "` with:

```csharp
        + "Use dry_run to see the plan first. "
        + "Set build to compile the model after a successful batch: compiler errors come back under build, marked in_batch "
        + "when they are in objects this batch wrote, and a failed build does not undo the batch. "
```

Replace the method signature tail and the request line:

```csharp
        [Description("Plan only: return the stages and d365fo commands without writing anything.")] bool dry_run = false,
        [Description("Compile the model after the batch succeeds and return the compiler errors. A failed build does not undo the batch.")] bool build = false,
        CancellationToken cancellationToken = default)
    {
        var result = await executor.RunAsync(new BatchRequest(model, prefix, tasks, dry_run, build), cancellationToken);
```

- [ ] **Step 5: Write the two tools**

`src/FoArtifacts/BuildModelTool.cs`:

```csharp
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
```

`src/FoArtifacts/RefreshIndexTool.cs`:

```csharp
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
```

- [ ] **Step 6: Wire them up**

Replace the service registrations in `src/FoArtifacts/Program.cs` (from `builder.Services.AddSingleton(ReferenceMap.LoadEmbedded());` to `.WithTools<GenerateFoArtifactsTool>();`):

```csharp
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
```

- [ ] **Step 7: Run all unit tests**

Run: `dotnet test --project tests/FoArtifacts.Tests`
Expected: all pass.

- [ ] **Step 8: Check the server lists the three tools**

Run: `dotnet build src/FoArtifacts -c Release --nologo -v quiet`, then start the server with the scratchpad MCP client the earlier VM checks used (`mcp-call.ps1`) or any stdio MCP client, send `tools/list`.
Expected: `generate_fo_artifacts` (with `build` in its input schema), `build_model`, `refresh_index`.

- [ ] **Step 9: Commit**

```bash
git add src/FoArtifacts/BuildModelTool.cs src/FoArtifacts/RefreshIndexTool.cs src/FoArtifacts/GenerateFoArtifactsTool.cs src/FoArtifacts/Program.cs src/FoArtifacts.Core/D365foRunner.cs tests/FoArtifacts.Tests/ModelToolsTests.cs tests/FoArtifacts.Tests/GenerateFoArtifactsToolTests.cs tests/FoArtifacts.Tests/D365foRunnerTests.cs
git commit -m "Add the build_model and refresh_index tools and the batch build flag"
```

---

### Task 8: Integration tests against real d365fo

**Files:**
- Create: `tests/FoArtifacts.IntegrationTests/ModelToolsIntegrationTests.cs`

**Interfaces:**
- Consumes: `D365foSandbox` (`CreateAsync`, `Cli`, `ModelDir`, `Model`, `SkipReason`), `IndexRefresh`, `ModelBuild`, `ModelCheck`, `Summary`.

The sandbox's packages folder is a temp copy of d365fo's sample model with no `bin\xppc.exe`, and its model (`TestModel`) is listed in `D365FO_CUSTOM_MODELS`. So `build_model` passes the model check and reaches d365fo, which answers `XPPC_NOT_FOUND` (checked on the VM on 2026-11-18: `{"ok":false,"error":{"code":"XPPC_NOT_FOUND",...}}`). d365fo's refresh skips a model whose file fingerprint (file count, newest mtime, names) is unchanged, and `--force` with `--model` re-reads only that model (also checked on the VM).

- [ ] **Step 1: Write the tests**

```csharp
using FoArtifacts.Core;
using Xunit;
using static FoArtifacts.IntegrationTests.D365foSandbox;

namespace FoArtifacts.IntegrationTests;

public class ModelToolsIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireD365fo() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    [Fact]
    public async Task Refresh_skips_an_unchanged_model_and_rereads_a_changed_one()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var refresh = new IndexRefresh(box.Cli, new ModelCheck(box.Cli));
        await refresh.RunAsync(Model, force: false, Ct); // settle whatever extract left behind

        var unchanged = await refresh.RunAsync(Model, force: false, Ct);
        Assert.True(unchanged.Ok, Summary.ToJson(unchanged));
        Assert.False(unchanged.Refreshed, Summary.ToJson(unchanged));

        var file = Directory.EnumerateFiles(box.ModelDir, "*.xml", SearchOption.AllDirectories).First();
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        var changed = await refresh.RunAsync(Model, force: false, Ct);
        Assert.True(changed.Refreshed, Summary.ToJson(changed));

        var forced = await refresh.RunAsync(Model, force: true, Ct);
        Assert.True(forced.Refreshed, Summary.ToJson(forced));
    }

    [Fact]
    public async Task Refresh_of_an_unknown_model_fails_with_the_Ax_hint()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var report = await new IndexRefresh(box.Cli, new ModelCheck(box.Cli)).RunAsync("NoSuchModel", force: false, Ct);

        Assert.False(report.Ok);
        Assert.Equal("MODEL_NOT_FOUND", report.Error!.Code);
    }

    [Fact]
    public async Task Build_without_xppc_reports_that_it_could_not_run()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var report = await new ModelBuild(box.Cli, new ModelCheck(box.Cli)).RunAsync(Model, null, Ct);

        Assert.Null(report.Succeeded);
        Assert.Equal("XPPC_NOT_FOUND", report.Error!.Code);
    }
}
```

- [ ] **Step 2: Run them**

Run (PowerShell): `$env:D365FO_CLI = .\scripts\build-d365fo.ps1; dotnet test --project tests/FoArtifacts.IntegrationTests`
Expected: all pass (49 existing + 3 new). These tests use code already written, so they are expected to pass at once. If `unchanged.Refreshed` is true, d365fo did not store a fingerprint for the model: read `RefreshCommand` in `C:\Dev\d365fo-cli\src\D365FO.Cli\Commands\Index\IndexCommands.cs` (around line 590) before changing the test, and do not weaken it to skip the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/FoArtifacts.IntegrationTests/ModelToolsIntegrationTests.cs
git commit -m "Check refresh and build against the real d365fo"
```

---

### Task 9: README, publish, and the VM check

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Document the feature**

In `README.md`:

1. In the intro list (after "if any task fails, every write of the batch is undone."), add:

```markdown
- with `build: true`, the model is compiled after a successful batch and the compiler errors come back,
  marked `in_batch` when they are in objects the batch wrote. A failed build does not undo the batch.
```

and after the list, before "It does not generate XML itself":

```markdown
Two more tools work without a batch: `build_model` compiles a custom model, and `refresh_index`
re-reads one model into the d365fo index after changes made outside fo-artifacts (a git pull, edits
in Visual Studio).
```

2. Add a row to the variables table:

```markdown
| `FO_ARTIFACTS_BUILD_TIMEOUT_SECONDS` | Time limit for `d365fo build` | `900` |
```

and below the table:

```markdown
A build of a large model can take minutes. Your AI client's own limit for one tool call must be
longer than the build limit, or the client gives up while the build is still running.
```

3. Under "Using it", after the `dry_run` bullet:

```markdown
- `build: true` compiles the model after the batch. The result's `build` section has `succeeded`, the
  full `error_count` / `warning_count`, at most 50 errors (those in the batch's objects first) and up
  to 20 warnings in the batch's objects, and the path of xppc's log. A dry run shows the build command.
```

4. Add checklist step 7 after step 6:

```markdown
7. Run the Vehicle batch with `build: true` and check `build.succeeded`. Then run a batch whose
   `control-method` body does not compile, check its errors are marked `in_batch` and the batch is
   kept, fix the method, and run `build_model` until it is clean. Edit a model file by hand and check
   `refresh_index` reports `refreshed: true`.
```

- [ ] **Step 2: Run both test projects**

Run: `dotnet test --project tests/FoArtifacts.Tests` and `$env:D365FO_CLI = .\scripts\build-d365fo.ps1; dotnet test --project tests/FoArtifacts.IntegrationTests`
Expected: all pass.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Document the build step and the build_model and refresh_index tools"
```

- [ ] **Step 4: Publish**

Stop any running FoArtifacts.exe first (the MCP client holds it open; restart the client afterwards), then:

Run: `dotnet publish src/FoArtifacts -c Release -o C:\tools\fo-artifacts`
Expected: publish succeeds; after restarting the MCP client, `fo-artifacts` lists three tools.

- [ ] **Step 5: VM check (README step 7) — needs the user's OK first**

This writes to FoArtifactsLab. For each batch, run it with `dry_run: true` first, show the user the plan and wait for their OK.

1. A small batch with `build: true`, e.g. `{"type":"class","name":"BuildProbe"}` in FoArtifactsLab with prefix FAL → expect `ok: true`, `build.succeeded: true`.
2. `{"type":"control-method","name":"FALVehicle","options":{"control":"Overview_VehicleId","method":"clicked","body":"this.notAMethod();"}}` with `build: true` (check the exact option names with `d365fo generate control-method --help`) → expect `build.succeeded: false` and an error on `FALVehicle` with `in_batch: true`; the batch is kept.
3. Undo that method (`d365fo undo --steps 1`) or fix its body, then `build_model FoArtifactsLab` → `succeeded: true`.
4. Touch a model file (`(Get-Item <file>).LastWriteTime = Get-Date`), then `refresh_index FoArtifactsLab` → `refreshed: true`; again → `refreshed: false`.

Leave FoArtifactsLab as it was at the start (remove FALBuildProbe through `d365fo undo` or with the user's OK) and confirm `build_model FoArtifactsLab` is clean at the end.
