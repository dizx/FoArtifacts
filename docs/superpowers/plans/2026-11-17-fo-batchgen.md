# fo-batchgen Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An MCP server with one tool, `generate_fo_artifacts`, that turns a batch of typed D365 F&O artifact tasks into files in a model by driving the `d365fo` CLI — prefixed, in dependency order, all-or-nothing.

**Architecture:** A pure core library (`FoBatchGen.Core`) validates the request, applies the model prefix using a per-command data file (`reference-map.json`), plans dependency stages, and executes them through an `ID365fo` abstraction over the `d365fo` process, rolling back through `d365fo`'s undo journal on failure. A thin console host (`FoBatchGen`) exposes it over MCP stdio with the official C# SDK.

**Tech Stack:** .NET 10, C#, ModelContextProtocol 2.4.0, Microsoft.Extensions.Hosting 10.0.15, xUnit v3 4.0.3 on Microsoft.Testing.Platform, `d365fo` CLI pinned at commit `640406b357b5fc9bc33ba499c8ec325c5b8a5c5f`.

**Spec:** `docs/superpowers/specs/2026-11-17-fo-batchgen-design.md`

## Global Constraints

- Target framework `net10.0` for every project; `global.json` sets `"test": { "runner": "Microsoft.Testing.Platform" }` (required: VSTest-mode `dotnet test` fails on the .NET 10 SDK with xUnit v3).
- `FoBatchGen.Core` has no MCP or hosting dependency.
- `d365fo` is located by `D365FO_CLI` (path to the executable), else `d365fo` on `PATH`; per-call timeout `FO_BATCHGEN_TIMEOUT_SECONDS`, default 120.
- Every `d365fo generate` call gets `--no-refresh --output json` plus `--install-to <model>`, except `table-relation` and `find-methods`, which get `--apply-to <table file>`.
- Tasks may not set `out`, `install-to`, `apply-to`, `output`, `overwrite`, `no-refresh`.
- 1–500 tasks per batch.
- After every stage, including the last: `d365fo index refresh --model <model> --output json`.
- Rollback undoes exactly the journal entries above the anchor taken before the batch, and only when every one belongs to this batch (model matches and name contains a batch name, or target is a path a task reported). Otherwise nothing is undone and the files are listed.
- One batch at a time per server process.
- `d365fo` child processes never inherit the server's stdin or stdout (stdin closed, stdout/stderr captured) — they would corrupt the MCP stream.
- Supported task types: the 41 non-deprecated `d365fo generate` subcommands (all 42 registered except `simple-list`).
- No model identifiers in commits, code or docs. Commit messages end with the two attribution lines used in this repo's history.

## Review Focus

1. **Values with spaces, quotes, `&` and non-ASCII letters** (captions, labels such as `Kjøretøy & "lastebil"`) must reach `d365fo` byte-for-byte — tests in Task 5 (argument list) and Task 10 (label lands in the XML).
2. **The client cancels mid-batch** (MCP cancellation, closed client) — files already written must still be rolled back, not abandoned — test in Task 8.
3. **Inconsistent casing and already-prefixed names** (`"vehicle"` referring to `Vehicle`, a task named `CFltVehicle` with prefix `CFlt`) — treated as the same object, never double-prefixed — tests in Task 3.
4. **A batch name appearing where it is not an object reference** (form `field: "Vehicle"`, a caption `"Vehicle"`, the field-name part of `table --field Vehicle:VehicleId`) — left unchanged — tests in Task 3.
5. **Running the same batch twice** — the second run fails on the first existing object and leaves the first run's files untouched — test in Task 10.

---

## File Structure

```
fo-batchgen/
  global.json                      SDK + test runner selection
  Directory.Build.props            shared build settings
  .gitignore
  FoBatchGen.slnx
  README.md                        (Task 11)
  scripts/build-d365fo.sh          clone + build pinned d365fo (Task 10)
  scripts/build-d365fo.ps1
  src/FoBatchGen.Core/
    FoBatchGen.Core.csproj
    reference-map.json             per-command rules (Task 1)
    ReferenceMap.cs                loads/queries the rules (Task 1)
    BatchSpec.cs                   request types + validation (Task 2)
    NameExpander.cs                prefixing + reference rewriting (Task 3)
    Planner.cs                     dependency stages (Task 4)
    CommandBuilder.cs              d365fo argument lists (Task 5)
    D365foRunner.cs                process runner, envelope parsing, locator (Task 6)
    Journal.cs                     journal parsing + rollback decision (Task 7)
    BatchExecutor.cs               preflight/run/refresh/rollback (Task 8)
    ArtifactPaths.cs               file paths from d365fo results (Task 8)
    Summary.cs                     compact JSON result (Task 9)
  src/FoBatchGen/
    FoBatchGen.csproj
    Program.cs                     MCP host (Task 9)
    GenerateFoArtifactsTool.cs     the tool + its description (Task 9)
  tests/FoBatchGen.Tests/          unit tests (Tasks 1–9)
  tests/FoBatchGen.IntegrationTests/  real d365fo against its sample model (Task 10)
```

---

### Task 1: Solution scaffold and the reference map

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.gitignore`, `FoBatchGen.slnx`
- Create: `src/FoBatchGen.Core/FoBatchGen.Core.csproj`
- Create: `src/FoBatchGen.Core/reference-map.json`
- Create: `src/FoBatchGen.Core/ReferenceMap.cs`
- Create: `tests/FoBatchGen.Tests/FoBatchGen.Tests.csproj`
- Test: `tests/FoBatchGen.Tests/ReferenceMapTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `FoBatchGen.Core`):
  - `static class NameRoles { const string Define = "define", Reference = "reference", Literal = "literal"; }`
  - `static class InstallModes { const string InstallTo = "install-to", ApplyTo = "apply-to"; }`
  - `sealed class NameRule { string Role; List<string> Kinds; }`
  - `sealed class DefineRule { string Option; List<string> Kinds; }`
  - `sealed class RefRule { string Option; string? Split; int Segment; string? Subsplit; int? Subsegment; List<string> Kinds; }` — `Subsegment` null = every part, -1 = last part.
  - `sealed class CommandRule { int Rank; NameRule Name; string Install; List<string> Leading; List<string> Produces; List<DefineRule> Defines; List<RefRule> Refs; }`
  - `sealed class ReferenceMap { const string AnyKind = "*"; static IReadOnlySet<string> KnownKinds; string D365foCommit; Dictionary<string, CommandRule> Commands /* case-insensitive */; IReadOnlyList<string> Types /* ordinal-sorted */; bool TryGet(string type, out CommandRule rule); static ReferenceMap Parse(string json); static ReferenceMap LoadEmbedded(); }`

- [ ] **Step 1: Create the build files and projects**

```bash
cd /home/user/fo-batchgen
mkdir -p src/FoBatchGen.Core tests/FoBatchGen.Tests
```

`global.json`:
```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" }
}
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

`.gitignore`:
```
bin/
obj/
TestResults/
*.user
.vs/
.d365fo-cli/
```

`src/FoBatchGen.Core/FoBatchGen.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <EmbeddedResource Include="reference-map.json" LogicalName="FoBatchGen.Core.reference-map.json" />
  </ItemGroup>
</Project>
```

`tests/FoBatchGen.Tests/FoBatchGen.Tests.csproj` (test projects report analyzer findings as warnings, not errors):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/FoBatchGen.Core/FoBatchGen.Core.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet new sln -n FoBatchGen
dotnet sln FoBatchGen.slnx add src/FoBatchGen.Core/FoBatchGen.Core.csproj tests/FoBatchGen.Tests/FoBatchGen.Tests.csproj
```
Expected: `FoBatchGen.slnx` exists and lists both projects.

- [ ] **Step 2: Write the failing tests**

`tests/FoBatchGen.Tests/ReferenceMapTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class ReferenceMapTests
{
    // `generate` subcommands registered in d365fo's src/D365FO.Cli/CliApp.cs at the pinned commit.
    private static readonly string[] D365foGenerateCommands =
    [
        "table", "table-relation", "find-methods", "class", "coc", "form", "datasource-method",
        "control-method", "form-clone", "simple-list", "entity", "extension", "event-handler",
        "privilege", "duty", "role", "report", "report-extension", "sysoperation", "number-sequence",
        "workflow", "menu-item", "edt", "enum", "query", "view", "map", "business-event",
        "custom-service", "migration-script", "runbase", "security-policy", "systest",
        "configuration-key", "form-part", "label-file", "menu", "resource", "tile",
        "workflow-category", "composite-entity", "aggregate-entity",
    ];

    private static readonly ReferenceMap Map = ReferenceMap.LoadEmbedded();

    [Fact]
    public void Covers_every_d365fo_generate_command_except_the_deprecated_simple_list()
    {
        var expected = D365foGenerateCommands.Where(c => c != "simple-list").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(41, expected.Count);
        Assert.Equal(expected, Map.Types);
    }

    [Fact]
    public void Is_pinned_to_the_tested_d365fo_commit() =>
        Assert.Equal("640406b357b5fc9bc33ba499c8ec325c5b8a5c5f", Map.D365foCommit);

    [Fact]
    public void Uses_only_known_kinds()
    {
        foreach (var (type, rule) in Map.Commands)
        {
            var kinds = rule.Name.Kinds.Concat(rule.Produces)
                .Concat(rule.Defines.SelectMany(d => d.Kinds))
                .Concat(rule.Refs.SelectMany(r => r.Kinds));
            foreach (var kind in kinds)
                Assert.True(kind == ReferenceMap.AnyKind || ReferenceMap.KnownKinds.Contains(kind), $"{type}: unknown kind '{kind}'");
        }
    }

    [Fact]
    public void Rules_are_well_formed()
    {
        foreach (var (type, rule) in Map.Commands)
        {
            Assert.Contains(rule.Name.Role, new[] { NameRoles.Define, NameRoles.Reference, NameRoles.Literal });
            Assert.Contains(rule.Install, new[] { InstallModes.InstallTo, InstallModes.ApplyTo });
            if (rule.Name.Role != NameRoles.Literal)
                Assert.True(rule.Name.Kinds.Count > 0, $"{type}: a define/reference name needs kinds");
            foreach (var r in rule.Refs)
            {
                Assert.True(r.Kinds.Count > 0, $"{type} --{r.Option}: a reference needs kinds");
                if (r.Split is null) Assert.True(r.Segment == 0, $"{type} --{r.Option}: segment set without split");
                if (r.Subsplit is not null) Assert.True(r.Split is not null, $"{type} --{r.Option}: subsplit without split");
            }
        }
    }

    [Theory]
    [InlineData("table-relation")]
    [InlineData("find-methods")]
    public void Table_augmenting_commands_apply_to_an_existing_table_file(string type)
    {
        Assert.True(Map.TryGet(type, out var rule));
        Assert.Equal(InstallModes.ApplyTo, rule.Install);
        Assert.Equal(NameRoles.Reference, rule.Name.Role);
        Assert.Equal(new[] { "table" }, rule.Name.Kinds);
    }

    [Fact]
    public void Table_field_type_is_the_second_colon_segment()
    {
        Assert.True(Map.TryGet("table", out var rule));
        var field = Assert.Single(rule.Refs, r => r.Option == "field");
        Assert.Equal(":", field.Split);
        Assert.Equal(1, field.Segment);
        Assert.Equal(new[] { "edt", "enum" }, field.Kinds);
    }

    [Fact]
    public void Extension_takes_kind_as_a_leading_positional()
    {
        Assert.True(Map.TryGet("extension", out var rule));
        Assert.Equal(new[] { "kind" }, rule.Leading);
        Assert.Equal(NameRoles.Reference, rule.Name.Role);
    }

    [Fact]
    public void Lookup_ignores_case()
    {
        Assert.True(Map.TryGet("TABLE", out var rule));
        Assert.Equal(30, rule.Rank);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `ReferenceMap`, `NameRoles`, `InstallModes` do not exist.

- [ ] **Step 4: Write `ReferenceMap.cs`**

`src/FoBatchGen.Core/ReferenceMap.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace FoBatchGen.Core;

/// <summary>What a task's <c>name</c> means for a subcommand.</summary>
public static class NameRoles
{
    /// <summary>The name of the new object; it gets the prefix.</summary>
    public const string Define = "define";
    /// <summary>An existing object the command changes; rewritten only if the batch defines it.</summary>
    public const string Reference = "reference";
    /// <summary>Passed through unchanged (e.g. a report-extension pattern).</summary>
    public const string Literal = "literal";
}

/// <summary>How the server tells d365fo where to write.</summary>
public static class InstallModes
{
    public const string InstallTo = "install-to";
    public const string ApplyTo = "apply-to";
}

public sealed class NameRule
{
    public string Role { get; init; } = NameRoles.Define;
    public List<string> Kinds { get; init; } = [];
}

/// <summary>An option whose value is the name of a further new object (prefixed like a name).</summary>
public sealed class DefineRule
{
    public string Option { get; init; } = "";
    public List<string> Kinds { get; init; } = [];
}

/// <summary>
/// An option, or one part of its value, that names another object. With <see cref="Split"/> the
/// value is split and only <see cref="Segment"/> is considered; with <see cref="Subsplit"/> that
/// segment is split again and <see cref="Subsegment"/> picks the part (null = every part, -1 = last).
/// </summary>
public sealed class RefRule
{
    public string Option { get; init; } = "";
    public string? Split { get; init; }
    public int Segment { get; init; }
    public string? Subsplit { get; init; }
    public int? Subsegment { get; init; }
    public List<string> Kinds { get; init; } = [];
}

public sealed class CommandRule
{
    /// <summary>Tie-break order inside a stage: lower runs first.</summary>
    public int Rank { get; init; }
    public NameRule Name { get; init; } = new();
    public string Install { get; init; } = InstallModes.InstallTo;
    /// <summary>Option keys whose single value is passed as a positional argument before the name.</summary>
    public List<string> Leading { get; init; } = [];
    public List<string> Produces { get; init; } = [];
    public List<DefineRule> Defines { get; init; } = [];
    public List<RefRule> Refs { get; init; } = [];
}

/// <summary>Per-subcommand rules loaded from <c>reference-map.json</c>.</summary>
public sealed class ReferenceMap
{
    public const string AnyKind = "*";

    public static readonly IReadOnlySet<string> KnownKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "enum", "edt", "table", "view", "map", "query", "class", "form", "form-part", "menu-item",
        "menu", "tile", "privilege", "duty", "role", "entity", "configuration-key", "label-file",
        "resource", "report", "service", "service-group", "security-policy", "workflow",
        "workflow-approval", "workflow-task", "workflow-category", "kpi", "measurement",
        "number-sequence",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string D365foCommit { get; init; } = "";

    public Dictionary<string, CommandRule> Commands { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Types => Commands.Keys.Order(StringComparer.Ordinal).ToList();

    public bool TryGet(string type, [NotNullWhen(true)] out CommandRule? rule) => Commands.TryGetValue(type, out rule);

    public static ReferenceMap Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<ReferenceMap>(json, JsonOptions)
            ?? throw new InvalidDataException("reference-map.json is empty.");
        return new ReferenceMap
        {
            D365foCommit = parsed.D365foCommit,
            Commands = new Dictionary<string, CommandRule>(parsed.Commands, StringComparer.OrdinalIgnoreCase),
        };
    }

    public static ReferenceMap LoadEmbedded()
    {
        using var stream = typeof(ReferenceMap).Assembly.GetManifestResourceStream("FoBatchGen.Core.reference-map.json")
            ?? throw new InvalidOperationException("Embedded resource reference-map.json is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
```

- [ ] **Step 5: Write `reference-map.json`**

Written from the d365fo source at the pinned commit (`src/D365FO.Cli/Commands/Generate/*.cs`). `src/FoBatchGen.Core/reference-map.json`:
```json
{
  "d365foCommit": "640406b357b5fc9bc33ba499c8ec325c5b8a5c5f",
  "commands": {
    "configuration-key": { "rank": 0, "name": { "role": "define", "kinds": ["configuration-key"] }, "produces": ["configuration-key"],
      "refs": [ { "option": "parent-key", "kinds": ["configuration-key"] } ] },
    "label-file": { "rank": 0, "name": { "role": "literal" }, "produces": ["label-file"] },
    "resource": { "rank": 0, "name": { "role": "define", "kinds": ["resource"] }, "produces": ["resource"] },
    "workflow-category": { "rank": 0, "name": { "role": "define", "kinds": ["workflow-category"] }, "produces": ["workflow-category"] },
    "enum": { "rank": 10, "name": { "role": "define", "kinds": ["enum"] }, "produces": ["enum"] },
    "edt": { "rank": 20, "name": { "role": "define", "kinds": ["edt"] }, "produces": ["edt"],
      "refs": [ { "option": "extends", "kinds": ["edt"] }, { "option": "enum-type", "kinds": ["enum"] } ] },
    "number-sequence": { "rank": 25, "name": { "role": "define", "kinds": ["number-sequence"] }, "produces": ["number-sequence", "edt", "class"],
      "defines": [ { "option": "edt", "kinds": ["edt"] } ],
      "refs": [ { "option": "table", "kinds": ["table"] } ] },
    "table": { "rank": 30, "name": { "role": "define", "kinds": ["table"] }, "produces": ["table"],
      "refs": [
        { "option": "field", "split": ":", "segment": 1, "kinds": ["edt", "enum"] },
        { "option": "configuration-key", "kinds": ["configuration-key"] },
        { "option": "form-ref", "kinds": ["menu-item"] } ] },
    "map": { "rank": 35, "name": { "role": "define", "kinds": ["map"] }, "produces": ["map"],
      "refs": [
        { "option": "field", "split": ":", "segment": 1, "kinds": ["edt", "enum"] },
        { "option": "map-to", "split": ":", "segment": 0, "kinds": ["table"] } ] },
    "query": { "rank": 40, "name": { "role": "define", "kinds": ["query"] }, "produces": ["query"],
      "refs": [
        { "option": "ds", "kinds": ["table", "view"] },
        { "option": "join", "split": ":", "segment": 0, "kinds": ["table", "view"] } ] },
    "view": { "rank": 45, "name": { "role": "define", "kinds": ["view"] }, "produces": ["view"],
      "refs": [ { "option": "query", "kinds": ["query"] }, { "option": "configuration-key", "kinds": ["configuration-key"] } ] },
    "class": { "rank": 50, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "refs": [ { "option": "extends", "kinds": ["class"] } ] },
    "coc": { "rank": 60, "name": { "role": "reference", "kinds": ["*"] }, "produces": ["class"] },
    "event-handler": { "rank": 60, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "refs": [ { "option": "source-object", "kinds": ["*"] } ] },
    "sysoperation": { "rank": 60, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "defines": [
        { "option": "contract-name", "kinds": ["class"] },
        { "option": "service-name", "kinds": ["class"] },
        { "option": "controller-name", "kinds": ["class"] } ],
      "refs": [ { "option": "param", "split": ":", "segment": 1, "kinds": ["edt", "enum"] } ] },
    "business-event": { "rank": 60, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "defines": [ { "option": "contract-name", "kinds": ["class"] } ],
      "refs": [
        { "option": "payload", "split": ":", "segment": 1, "kinds": ["edt", "enum"] },
        { "option": "primary-table", "kinds": ["table"] } ] },
    "migration-script": { "rank": 60, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "refs": [ { "option": "source-table", "kinds": ["table"] }, { "option": "target-table", "kinds": ["table"] } ] },
    "runbase": { "rank": 60, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "refs": [ { "option": "dialog-param", "split": ":", "segment": 1, "kinds": ["edt", "enum"] } ] },
    "table-relation": { "rank": 65, "install": "apply-to", "name": { "role": "reference", "kinds": ["table"] } },
    "find-methods": { "rank": 65, "install": "apply-to", "name": { "role": "reference", "kinds": ["table"] } },
    "entity": { "rank": 70, "name": { "role": "define", "kinds": ["entity"] }, "produces": ["entity", "table"],
      "defines": [ { "option": "staging-table", "kinds": ["table"] } ],
      "refs": [
        { "option": "table", "kinds": ["table", "view"] },
        { "option": "computed-field", "split": ":", "segment": 2, "kinds": ["edt"] },
        { "option": "relation", "split": ":", "segment": 1, "kinds": ["entity"] } ] },
    "composite-entity": { "rank": 75, "name": { "role": "define", "kinds": ["entity"] }, "produces": ["entity"],
      "refs": [
        { "option": "root", "split": ":", "segment": 0, "kinds": ["entity"] },
        { "option": "embedded", "split": ":", "segment": 0, "kinds": ["entity"] } ] },
    "aggregate-entity": { "rank": 75, "name": { "role": "define", "kinds": ["entity"] }, "produces": ["entity"],
      "refs": [
        { "option": "measurement", "kinds": ["measurement"] },
        { "option": "measure", "split": ":", "segment": 3, "kinds": ["edt"] },
        { "option": "dimension", "split": ":", "segment": 4, "kinds": ["edt"] } ] },
    "form": { "rank": 80, "name": { "role": "define", "kinds": ["form"] }, "produces": ["form"],
      "refs": [ { "option": "table", "kinds": ["table", "view", "entity"] }, { "option": "lines-table", "kinds": ["table", "view", "entity"] } ] },
    "form-clone": { "rank": 80, "name": { "role": "define", "kinds": ["form"] }, "produces": ["form"],
      "refs": [
        { "option": "from", "kinds": ["form"] },
        { "option": "rebind", "split": "=", "segment": 0, "kinds": ["table", "view"] },
        { "option": "rebind", "split": "=", "segment": 1, "kinds": ["table", "view"] } ] },
    "datasource-method": { "rank": 90, "name": { "role": "reference", "kinds": ["form"] } },
    "control-method": { "rank": 90, "name": { "role": "reference", "kinds": ["form"] } },
    "report": { "rank": 90, "name": { "role": "define", "kinds": ["report"] }, "produces": ["report", "class", "table", "menu-item"],
      "defines": [ { "option": "dp", "kinds": ["class"] }, { "option": "tmp", "kinds": ["table"] } ],
      "refs": [ { "option": "extra-dataset", "split": ":", "segment": 1, "kinds": ["class"] } ] },
    "form-part": { "rank": 95, "name": { "role": "define", "kinds": ["form-part"] }, "produces": ["form-part"],
      "refs": [ { "option": "form", "kinds": ["form"] } ] },
    "report-extension": { "rank": 100, "name": { "role": "literal" }, "produces": ["class"],
      "refs": [
        { "option": "dp", "kinds": ["class"] },
        { "option": "tmp-table", "kinds": ["table"] },
        { "option": "report", "kinds": ["report"] },
        { "option": "controller", "kinds": ["class"] },
        { "option": "base-controller", "kinds": ["class"] } ] },
    "custom-service": { "rank": 100, "name": { "role": "define", "kinds": ["service"] }, "produces": ["service", "class", "service-group"],
      "defines": [ { "option": "class-name", "kinds": ["class"] }, { "option": "group-name", "kinds": ["service-group"] } ],
      "refs": [ { "option": "contract-param", "kinds": ["class"] } ] },
    "menu-item": { "rank": 100, "name": { "role": "define", "kinds": ["menu-item"] }, "produces": ["menu-item"],
      "refs": [
        { "option": "object", "kinds": ["form", "class", "report", "query"] },
        { "option": "enum-type", "kinds": ["enum"] },
        { "option": "config-key", "kinds": ["configuration-key"] },
        { "option": "query", "kinds": ["query"] },
        { "option": "linked-permission-object", "kinds": ["*"] } ] },
    "tile": { "rank": 110, "name": { "role": "define", "kinds": ["tile"] }, "produces": ["tile"],
      "refs": [
        { "option": "menu-item", "kinds": ["menu-item"] },
        { "option": "query", "kinds": ["query"] },
        { "option": "kpi", "kinds": ["kpi"] },
        { "option": "image", "kinds": ["resource"] },
        { "option": "configuration-key", "kinds": ["configuration-key"] } ] },
    "menu": { "rank": 120, "name": { "role": "define", "kinds": ["menu"] }, "produces": ["menu"],
      "refs": [
        { "option": "item", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["menu-item"] },
        { "option": "tile", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["tile"] },
        { "option": "menu-ref", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["menu"] },
        { "option": "configuration-key", "kinds": ["configuration-key"] } ] },
    "workflow": { "rank": 120, "name": { "role": "define", "kinds": ["workflow"] },
      "produces": ["workflow", "query", "class", "menu-item", "workflow-approval", "workflow-task"],
      "defines": [
        { "option": "approval-name", "kinds": ["workflow-approval"] },
        { "option": "task-name", "kinds": ["workflow-task"] },
        { "option": "document-class", "kinds": ["class"] },
        { "option": "document-menu-item", "kinds": ["menu-item"] },
        { "option": "submit-menu-item", "kinds": ["menu-item"] } ],
      "refs": [
        { "option": "table", "kinds": ["table"] },
        { "option": "category", "kinds": ["workflow-category"] },
        { "option": "query", "kinds": ["query"] } ] },
    "privilege": { "rank": 130, "name": { "role": "define", "kinds": ["privilege"] }, "produces": ["privilege"],
      "refs": [
        { "option": "entry-point", "kinds": ["menu-item", "service"] },
        { "option": "entry-object", "kinds": ["*"] },
        { "option": "data-entity", "kinds": ["entity"] } ] },
    "duty": { "rank": 140, "name": { "role": "define", "kinds": ["duty"] }, "produces": ["duty"],
      "refs": [ { "option": "privilege", "kinds": ["privilege"] } ] },
    "role": { "rank": 150, "name": { "role": "define", "kinds": ["role"] }, "produces": ["role"],
      "refs": [
        { "option": "duty", "kinds": ["duty"] },
        { "option": "privilege", "kinds": ["privilege"] },
        { "option": "sub-role", "kinds": ["role"] } ] },
    "security-policy": { "rank": 160, "name": { "role": "define", "kinds": ["security-policy"] }, "produces": ["security-policy"],
      "refs": [
        { "option": "constrained-table", "kinds": ["table"] },
        { "option": "policy-query", "kinds": ["query"] },
        { "option": "constrained", "split": ":", "segment": 0, "subsplit": "/", "kinds": ["table"] },
        { "option": "context-value", "kinds": ["role"] } ] },
    "systest": { "rank": 170, "name": { "role": "define", "kinds": ["class"] }, "produces": ["class"],
      "refs": [ { "option": "class", "kinds": ["class"] }, { "option": "table", "kinds": ["table"] } ] },
    "extension": { "rank": 180, "leading": ["kind"], "name": { "role": "reference", "kinds": ["*"] },
      "refs": [
        { "option": "field", "split": ":", "segment": 1, "kinds": ["edt", "enum"] },
        { "option": "privilege", "kinds": ["privilege"] },
        { "option": "duty", "kinds": ["duty"] },
        { "option": "item", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["menu-item"] },
        { "option": "tile", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["tile"] },
        { "option": "menu-ref", "split": ":", "segment": 0, "subsplit": "/", "subsegment": -1, "kinds": ["menu"] } ] }
  }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS, 9 tests (7 facts + 2 theory rows).

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add solution scaffold and per-command reference map

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 2: Request types and validation (`BatchSpec`)

**Files:**
- Create: `src/FoBatchGen.Core/BatchSpec.cs`
- Create: `tests/FoBatchGen.Tests/TestData.cs`
- Test: `tests/FoBatchGen.Tests/BatchSpecTests.cs`

**Interfaces:**
- Consumes: `ReferenceMap`, `CommandRule`, `NameRoles` (Task 1).
- Produces (namespace `FoBatchGen.Core`):
  - `sealed record TaskInput(string Type, string Name, Dictionary<string, JsonElement>? Options = null)`
  - `sealed record BatchRequest(string Model, string Prefix, IReadOnlyList<TaskInput> Tasks, bool DryRun = false)`
  - `sealed record ValidTask(int Index, string Type, string Name, IReadOnlyList<KeyValuePair<string, string>> Options, IReadOnlyList<string> Flags)` with `string Label` (`"tasks[{Index}] ({Type} {Name})"`) and `IReadOnlyList<string> Values(string option)`. `Type` is the lowercase catalog key; option keys have no leading `--`.
  - `sealed record ValidBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ValidTask> Tasks)`
  - `static class BatchSpec { const int MaxTasks = 500; static IReadOnlySet<string> ReservedOptions; static (ValidBatch? Batch, IReadOnlyList<string> Errors) Validate(BatchRequest request, ReferenceMap map); }` — on any error `Batch` is null and `Errors` lists every problem found.
  - Test helper (tests project, namespace `FoBatchGen.Tests`): `internal static class TestData { ReferenceMap Map; CancellationToken Ct; TaskInput T(string type, string name, string? optionsJson = null); ValidBatch Valid(string prefix, params TaskInput[] tasks); }`

- [ ] **Step 1: Write the test helper**

`tests/FoBatchGen.Tests/TestData.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

internal static class TestData
{
    public static readonly ReferenceMap Map = ReferenceMap.LoadEmbedded();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TaskInput T(string type, string name, string? optionsJson = null) =>
        new(type, name, optionsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson));

    public static ValidBatch Valid(string prefix, params TaskInput[] tasks)
    {
        var (batch, errors) = BatchSpec.Validate(new BatchRequest("ContosoFleet", prefix, tasks), Map);
        Assert.Empty(errors);
        return batch!;
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/FoBatchGen.Tests/BatchSpecTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class BatchSpecTests
{
    private static (ValidBatch? Batch, IReadOnlyList<string> Errors) Check(string model, string prefix, params TaskInput[] tasks) =>
        BatchSpec.Validate(new BatchRequest(model, prefix, tasks), Map);

    [Fact]
    public void Normalises_options_and_keeps_order()
    {
        var batch = Valid("CFlt", T("table", "Vehicle",
            """{"field":["A:B","C:D"],"size":20,"--label":"X","non-final":true,"disabled":false}"""));

        var task = Assert.Single(batch.Tasks);
        Assert.Equal(0, task.Index);
        Assert.Equal(
            new KeyValuePair<string, string>[] { new("field", "A:B"), new("field", "C:D"), new("size", "20"), new("label", "X") },
            task.Options);
        Assert.Equal(new[] { "non-final" }, task.Flags);
        Assert.Equal(new[] { "A:B", "C:D" }, task.Values("field"));
        Assert.Equal("tasks[0] (table Vehicle)", task.Label);
    }

    [Fact]
    public void Type_is_matched_without_regard_to_case()
    {
        var batch = Valid("", T("TABLE", "Vehicle"));
        Assert.Equal("table", Assert.Single(batch.Tasks).Type);
    }

    [Fact]
    public void Unknown_type_lists_the_supported_types()
    {
        var (batch, errors) = Check("M", "", T("tabel", "Vehicle"));
        Assert.Null(batch);
        var error = Assert.Single(errors);
        Assert.Contains("unknown type 'tabel'", error);
        Assert.Contains("table-relation", error);
    }

    [Fact]
    public void Reports_every_problem_at_once()
    {
        var (_, errors) = Check("M", "", T("tabel", "A"), T("table", "B", """{"out":"x"}"""), T("edt", "C", """{"size":{}}"""));
        Assert.Equal(3, errors.Count);
    }

    [Theory]
    [InlineData("out")]
    [InlineData("install-to")]
    [InlineData("apply-to")]
    [InlineData("output")]
    [InlineData("overwrite")]
    [InlineData("no-refresh")]
    public void Reserved_options_are_rejected(string option)
    {
        var (_, errors) = Check("M", "", T("table", "Vehicle", $$"""{"{{option}}":"x"}"""));
        Assert.Contains(errors, e => e.Contains($"option '{option}' is set by fo-batchgen"));
    }

    [Fact]
    public void Empty_and_oversized_task_lists_are_rejected()
    {
        Assert.Contains(Check("M", "").Errors, e => e.Contains("at least one task"));
        var many = Enumerable.Range(0, BatchSpec.MaxTasks + 1).Select(i => T("enum", $"E{i}")).ToArray();
        Assert.Contains(Check("M", "", many).Errors, e => e.Contains("maximum is 500"));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("My Model", "")]
    [InlineData("M", "1x")]
    [InlineData("M", "C-F")]
    public void Bad_model_or_prefix_is_rejected(string model, string prefix)
    {
        var (batch, errors) = Check(model, prefix, T("enum", "E"));
        Assert.Null(batch);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Empty_prefix_is_allowed() => Assert.Empty(Check("M", "", T("enum", "E")).Errors);

    [Fact]
    public void Object_names_must_be_aot_names_but_literals_are_free()
    {
        Assert.Contains(Check("M", "", T("table", "My Table")).Errors, e => e.Contains("not a valid AOT name"));
        Assert.Empty(Check("M", "", T("report-extension", "custom-design")).Errors);
    }

    [Fact]
    public void Extension_needs_exactly_one_kind()
    {
        Assert.Contains(Check("M", "", T("extension", "CustTable")).Errors, e => e.Contains("option 'kind' is required exactly once"));
        Assert.Empty(Check("M", "", T("extension", "CustTable", """{"kind":"Table"}""")).Errors);
    }

    [Fact]
    public void Nested_objects_and_nulls_are_rejected()
    {
        var (_, errors) = Check("M", "", T("edt", "E", """{"size":{"a":1},"label":null,"x":[true]}"""));
        Assert.Equal(3, errors.Count);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `BatchSpec`, `TaskInput`, `BatchRequest` do not exist.

- [ ] **Step 4: Write `BatchSpec.cs`**

`src/FoBatchGen.Core/BatchSpec.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FoBatchGen.Core;

/// <summary>One artifact as the client sends it.</summary>
public sealed record TaskInput(string Type, string Name, Dictionary<string, JsonElement>? Options = null);

/// <summary>A whole tool call.</summary>
public sealed record BatchRequest(string Model, string Prefix, IReadOnlyList<TaskInput> Tasks, bool DryRun = false);

/// <summary>A task that passed validation. Options keep their order; a repeated option appears once per value.</summary>
public sealed record ValidTask(
    int Index,
    string Type,
    string Name,
    IReadOnlyList<KeyValuePair<string, string>> Options,
    IReadOnlyList<string> Flags)
{
    public string Label => $"tasks[{Index}] ({Type} {Name})";

    public IReadOnlyList<string> Values(string option) =>
        Options.Where(o => o.Key == option).Select(o => o.Value).ToList();
}

public sealed record ValidBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ValidTask> Tasks);

public static partial class BatchSpec
{
    public const int MaxTasks = 500;

    /// <summary>Options fo-batchgen sets itself.</summary>
    public static readonly IReadOnlySet<string> ReservedOptions = new HashSet<string>(StringComparer.Ordinal)
    {
        "out", "install-to", "apply-to", "output", "overwrite", "no-refresh",
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex AotName();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex PrefixName();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex OptionName();

    public static (ValidBatch? Batch, IReadOnlyList<string> Errors) Validate(BatchRequest request, ReferenceMap map)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Model) || !AotName().IsMatch(request.Model))
            errors.Add($"model: '{request.Model}' is not a valid model name (letters, digits and _ only).");
        if (request.Prefix is null)
            errors.Add("prefix: required; use \"\" for no prefix.");
        else if (request.Prefix.Length > 0 && !PrefixName().IsMatch(request.Prefix))
            errors.Add($"prefix: '{request.Prefix}' must start with a letter and contain only letters, digits and _.");

        if (request.Tasks is null || request.Tasks.Count == 0)
        {
            errors.Add("tasks: at least one task is required.");
            return (null, errors);
        }
        if (request.Tasks.Count > MaxTasks)
            errors.Add($"tasks: {request.Tasks.Count} tasks given; the maximum is {MaxTasks}.");

        var valid = new List<ValidTask>();
        for (var i = 0; i < request.Tasks.Count; i++)
        {
            var input = request.Tasks[i];
            if (input is null)
            {
                errors.Add($"tasks[{i}]: task is null.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(input.Type) || !map.TryGet(input.Type, out var rule))
            {
                errors.Add($"tasks[{i}]: unknown type '{input.Type}'. Supported: {string.Join(", ", map.Types)}.");
                continue;
            }
            var type = input.Type.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(input.Name))
            {
                errors.Add($"tasks[{i}] ({type}): name is required.");
                continue;
            }
            var where = $"tasks[{i}] ({type} {input.Name})";
            if (rule.Name.Role != NameRoles.Literal && !AotName().IsMatch(input.Name))
                errors.Add($"{where}: name '{input.Name}' is not a valid AOT name (letters, digits and _ only).");

            var options = new List<KeyValuePair<string, string>>();
            var flags = new List<string>();
            foreach (var (rawKey, value) in input.Options ?? new Dictionary<string, JsonElement>())
            {
                var key = rawKey.StartsWith("--", StringComparison.Ordinal) ? rawKey[2..] : rawKey;
                if (!OptionName().IsMatch(key))
                {
                    errors.Add($"{where}: option '{rawKey}' is not a valid option name (lowercase, e.g. \"field\" or \"lines-table\").");
                    continue;
                }
                if (ReservedOptions.Contains(key))
                {
                    errors.Add($"{where}: option '{key}' is set by fo-batchgen and cannot be given.");
                    continue;
                }
                switch (value.ValueKind)
                {
                    case JsonValueKind.True:
                        flags.Add(key);
                        break;
                    case JsonValueKind.False:
                        break;
                    case JsonValueKind.String:
                        options.Add(new(key, value.GetString()!));
                        break;
                    case JsonValueKind.Number:
                        options.Add(new(key, value.GetRawText()));
                        break;
                    case JsonValueKind.Array:
                        foreach (var item in value.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String) options.Add(new(key, item.GetString()!));
                            else if (item.ValueKind == JsonValueKind.Number) options.Add(new(key, item.GetRawText()));
                            else errors.Add($"{where}: option '{key}': array items must be strings or numbers.");
                        }
                        break;
                    default:
                        errors.Add($"{where}: option '{key}' must be a string, number, boolean or array.");
                        break;
                }
            }

            foreach (var lead in rule.Leading)
            {
                if (options.Count(o => o.Key == lead) != 1)
                    errors.Add($"{where}: option '{lead}' is required exactly once for {type} (it is passed as the first argument).");
            }

            valid.Add(new ValidTask(i, type, input.Name, options, flags));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ValidBatch(request.Model, request.Prefix ?? "", request.DryRun, valid), errors);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS (all ReferenceMap and BatchSpec tests).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Validate batch requests

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---
### Task 3: Prefixing and reference rewriting (`NameExpander`)

**Files:**
- Create: `src/FoBatchGen.Core/NameExpander.cs`
- Modify: `tests/FoBatchGen.Tests/TestData.cs` (add `Expanded`)
- Test: `tests/FoBatchGen.Tests/NameExpanderTests.cs`

**Interfaces:**
- Consumes: `ValidBatch`, `ValidTask` (Task 2); `ReferenceMap`, `CommandRule`, `RefRule`, `NameRoles` (Task 1).
- Produces (namespace `FoBatchGen.Core`):
  - `sealed record ResolvedRef(string Option, string Name, IReadOnlyList<int> TargetTasks)` — a reference to an object defined by other task(s) of the batch (`Option` is `"<name>"` for a reference-role name).
  - `sealed record ExpandedTask(int Index, string Type, string Name, string Install, IReadOnlyList<string> LeadingArgs, IReadOnlyList<KeyValuePair<string, string>> Options, IReadOnlyList<string> Flags, IReadOnlyList<string> DefinedNames, IReadOnlyList<ResolvedRef> References)` with `string Label` = `"{Type} {Name}"`. `Options` excludes leading-positional keys.
  - `sealed record ExpandedBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ExpandedTask> Tasks)` with `IReadOnlyCollection<string> TouchNames` (every task's `Name` and `DefinedNames`, distinct, case-insensitive).
  - `static class NameExpander { static string ApplyPrefix(string prefix, string name); static (ExpandedBatch? Batch, IReadOnlyList<string> Errors) Expand(ValidBatch batch, ReferenceMap map); }`
  - Test helper: `TestData.Expanded(string prefix, params TaskInput[] tasks)` → `ExpandedBatch` (asserts no errors).

- [ ] **Step 1: Add the `Expanded` helper**

In `tests/FoBatchGen.Tests/TestData.cs`, add inside `TestData` after `Valid`:
```csharp
    public static ExpandedBatch Expanded(string prefix, params TaskInput[] tasks)
    {
        var (batch, errors) = NameExpander.Expand(Valid(prefix, tasks), Map);
        Assert.Empty(errors);
        return batch!;
    }
```

- [ ] **Step 2: Write the failing tests**

`tests/FoBatchGen.Tests/NameExpanderTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class NameExpanderTests
{
    private static ExpandedTask Find(ExpandedBatch batch, string type) => Assert.Single(batch.Tasks, t => t.Type == type);

    private static string[] Values(ExpandedTask task, string option) =>
        task.Options.Where(o => o.Key == option).Select(o => o.Value).ToArray();

    [Fact]
    public void Prefixes_new_object_names()
    {
        var table = Find(Expanded("CFlt", T("table", "Vehicle")), "table");
        Assert.Equal("CFltVehicle", table.Name);
        Assert.Equal(new[] { "CFltVehicle" }, table.DefinedNames);
    }

    [Fact]
    public void Never_adds_the_prefix_twice()
    {
        var batch = Expanded("CFlt", T("table", "CFltVehicle"), T("edt", "cfltCode"));
        Assert.Equal("CFltVehicle", Find(batch, "table").Name);
        Assert.Equal("cfltCode", Find(batch, "edt").Name);
    }

    [Fact]
    public void Empty_prefix_changes_no_names_but_still_links_references()
    {
        var batch = Expanded("", T("edt", "VehicleId"), T("table", "Vehicle", """{"field":["Id:VehicleId"]}"""));
        var table = Find(batch, "table");
        Assert.Equal("Vehicle", table.Name);
        Assert.Equal(new[] { "Id:VehicleId" }, Values(table, "field"));
        Assert.Equal(new[] { 0 }, Assert.Single(table.References).TargetTasks);
    }

    [Fact]
    public void Rewrites_references_to_objects_in_the_batch()
    {
        var batch = Expanded("CFlt",
            T("enum", "VehicleStatus"),
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle"}"""));

        // The field-name segment "VehicleId" stays; only the EDT segment is an object reference.
        Assert.Equal(new[] { "VehicleId:CFltVehicleId:mandatory", "Status:CFltVehicleStatus" }, Values(Find(batch, "table"), "field"));
        var form = Find(batch, "form");
        Assert.Equal(new[] { "CFltVehicle" }, Values(form, "table"));
        Assert.Equal(new[] { 2 }, Assert.Single(form.References).TargetTasks);
    }

    [Fact]
    public void Leaves_existing_objects_alone()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle", """{"field":["Vin:VIN"]}"""),
            T("edt", "Code", """{"extends":"SysGroup"}"""));
        Assert.Equal(new[] { "Vin:VIN" }, Values(Find(batch, "table"), "field"));
        Assert.Equal(new[] { "SysGroup" }, Values(Find(batch, "edt"), "extends"));
        Assert.All(batch.Tasks, t => Assert.Empty(t.References));
    }

    [Fact]
    public void A_reference_only_matches_objects_of_a_fitting_kind()
    {
        // The batch defines a table called Name; the EDT extends the standard EDT Name.
        var batch = Expanded("CFlt", T("table", "Name"), T("edt", "Code", """{"extends":"Name"}"""));
        Assert.Equal(new[] { "Name" }, Values(Find(batch, "edt"), "extends"));
    }

    [Fact]
    public void Matching_ignores_case_and_accepts_already_prefixed_references()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle"),
            T("form", "Vehicle", """{"table":"vehicle"}"""),
            T("privilege", "VehicleMaintain", """{"entry-point":"SomeMenuItem"}"""),
            T("duty", "VehicleMaintain", """{"privilege":"CFltVehicleMaintain"}"""));
        Assert.Equal(new[] { "CFltVehicle" }, Values(Find(batch, "form"), "table"));
        var duty = Find(batch, "duty");
        Assert.Equal(new[] { "CFltVehicleMaintain" }, Values(duty, "privilege"));
        Assert.Equal(new[] { 2 }, Assert.Single(duty.References).TargetTasks);
    }

    [Fact]
    public void Batch_names_outside_reference_positions_are_untouched()
    {
        var batch = Expanded("CFlt",
            T("table", "Vehicle", """{"field-group":["Vehicle:Vehicle"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle","caption":"Vehicle","field":["Vehicle"]}"""));
        var form = Find(batch, "form");
        Assert.Equal(new[] { "Vehicle" }, Values(form, "caption"));
        Assert.Equal(new[] { "Vehicle" }, Values(form, "field"));
        Assert.Equal(new[] { "Vehicle:Vehicle" }, Values(Find(batch, "table"), "field-group"));
    }

    [Fact]
    public void Defining_options_are_prefixed_and_can_be_referenced()
    {
        var batch = Expanded("CFlt",
            T("sysoperation", "Close", """{"contract-name":"CloseContract"}"""),
            T("class", "Helper", """{"extends":"CloseContract"}"""));
        var sysop = Find(batch, "sysoperation");
        Assert.Equal(new[] { "CFltCloseContract" }, Values(sysop, "contract-name"));
        Assert.Equal(new[] { "CFltClose", "CFltCloseContract" }, sysop.DefinedNames);
        Assert.Equal(new[] { "CFltCloseContract" }, Values(Find(batch, "class"), "extends"));
    }

    [Fact]
    public void Reference_role_names_are_rewritten_only_for_batch_objects()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("table-relation", "Vehicle"), T("find-methods", "CustTable"));
        var relation = Find(batch, "table-relation");
        Assert.Equal("CFltVehicle", relation.Name);
        Assert.Equal(InstallModes.ApplyTo, relation.Install);
        Assert.Equal("<name>", Assert.Single(relation.References).Option);
        Assert.Equal("CustTable", Find(batch, "find-methods").Name);
    }

    [Fact]
    public void Extension_kind_becomes_the_first_argument()
    {
        var batch = Expanded("CFlt",
            T("enum", "VehicleStatus"),
            T("extension", "CustTable", """{"kind":"Table","field":["Status:VehicleStatus"]}"""));
        var extension = Find(batch, "extension");
        Assert.Equal(new[] { "Table" }, extension.LeadingArgs);
        Assert.Equal("CustTable", extension.Name);
        var option = Assert.Single(extension.Options);
        Assert.Equal(new KeyValuePair<string, string>("field", "Status:CFltVehicleStatus"), option);
    }

    [Fact]
    public void Menu_items_inside_submenu_paths_are_found()
    {
        var batch = Expanded("CFlt",
            T("menu-item", "Vehicle", """{"object":"SomeForm"}"""),
            T("menu", "Fleet", """{"item":["Setup/Vehicle:Display","Other/CustTable"]}"""));
        Assert.Equal(new[] { "Setup/CFltVehicle:Display", "Other/CustTable" }, Values(Find(batch, "menu"), "item"));
    }

    [Fact]
    public void Two_definitions_of_one_object_are_rejected()
    {
        var (batch, errors) = NameExpander.Expand(Valid("CFlt", T("table", "Vehicle"), T("table", "vehicle")), Map);
        Assert.Null(batch);
        Assert.Contains("is defined twice", Assert.Single(errors));
    }

    [Fact]
    public void One_name_for_objects_of_different_kinds_is_fine()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("form", "Vehicle"), T("menu-item", "Vehicle"));
        Assert.All(batch.Tasks, t => Assert.Equal("CFltVehicle", t.Name));
    }

    [Fact]
    public void Touch_names_cover_new_and_changed_objects()
    {
        var batch = Expanded("CFlt", T("table", "Vehicle"), T("coc", "CustTable", """{"method":"insert"}"""));
        Assert.Contains("CFltVehicle", batch.TouchNames);
        Assert.Contains("CustTable", batch.TouchNames);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `NameExpander`, `ExpandedBatch`, `ExpandedTask` do not exist.

- [ ] **Step 4: Write `NameExpander.cs`**

`src/FoBatchGen.Core/NameExpander.cs`:
```csharp
namespace FoBatchGen.Core;

/// <summary>A reference to an object that other task(s) in the batch define.</summary>
public sealed record ResolvedRef(string Option, string Name, IReadOnlyList<int> TargetTasks);

public sealed record ExpandedTask(
    int Index,
    string Type,
    string Name,
    string Install,
    IReadOnlyList<string> LeadingArgs,
    IReadOnlyList<KeyValuePair<string, string>> Options,
    IReadOnlyList<string> Flags,
    IReadOnlyList<string> DefinedNames,
    IReadOnlyList<ResolvedRef> References)
{
    public string Label => $"{Type} {Name}";
}

public sealed record ExpandedBatch(string Model, string Prefix, bool DryRun, IReadOnlyList<ExpandedTask> Tasks)
{
    /// <summary>Names a journal entry written by this batch is expected to contain.</summary>
    public IReadOnlyCollection<string> TouchNames =>
        Tasks.SelectMany(t => t.DefinedNames.Append(t.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public static class NameExpander
{
    private sealed record Symbol(string Expanded, IReadOnlyList<string> Kinds, int Task);

    public static string ApplyPrefix(string prefix, string name) =>
        prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name : prefix + name;

    public static (ExpandedBatch? Batch, IReadOnlyList<string> Errors) Expand(ValidBatch batch, ReferenceMap map)
    {
        var errors = new List<string>();
        var symbols = new Dictionary<string, List<Symbol>>(StringComparer.OrdinalIgnoreCase);
        var labels = batch.Tasks.ToDictionary(t => t.Index, t => t.Label);

        void Define(string name, IReadOnlyList<string> kinds, ValidTask task)
        {
            var expanded = ApplyPrefix(batch.Prefix, name);
            if (symbols.TryGetValue(expanded, out var existing))
            {
                var clash = existing.FirstOrDefault(s =>
                    s.Task != task.Index
                    && string.Equals(s.Expanded, expanded, StringComparison.OrdinalIgnoreCase)
                    && s.Kinds.Intersect(kinds).Any());
                if (clash is not null)
                    errors.Add($"'{expanded}' is defined twice: {labels[clash.Task]} and {task.Label}.");
            }
            var symbol = new Symbol(expanded, kinds, task.Index);
            foreach (var key in new[] { name, expanded }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!symbols.TryGetValue(key, out var list)) symbols[key] = list = [];
                list.Add(symbol);
            }
        }

        // Pass 1: every name the batch defines.
        foreach (var task in batch.Tasks)
        {
            var rule = map.Commands[task.Type];
            if (rule.Name.Role == NameRoles.Define) Define(task.Name, rule.Name.Kinds, task);
            foreach (var definition in rule.Defines)
                foreach (var value in task.Values(definition.Option))
                    Define(value, definition.Kinds, task);
        }

        // Pass 2: prefix definitions, rewrite references.
        var expandedTasks = new List<ExpandedTask>();
        foreach (var task in batch.Tasks)
        {
            var rule = map.Commands[task.Type];
            var references = new List<ResolvedRef>();

            string Resolve(string option, string token, IReadOnlyList<string> kinds)
            {
                if (token.Length == 0 || !symbols.TryGetValue(token, out var candidates)) return token;
                var any = kinds.Contains(ReferenceMap.AnyKind);
                var matches = candidates
                    .Where(s => s.Task != task.Index && (any || s.Kinds.Intersect(kinds).Any()))
                    .ToList();
                if (matches.Count == 0) return token;
                references.Add(new ResolvedRef(option, matches[0].Expanded, matches.Select(m => m.Task).Distinct().ToList()));
                return matches[0].Expanded;
            }

            var name = rule.Name.Role switch
            {
                NameRoles.Define => ApplyPrefix(batch.Prefix, task.Name),
                NameRoles.Reference => Resolve("<name>", task.Name, rule.Name.Kinds),
                _ => task.Name,
            };

            var leading = rule.Leading.Select(key => task.Values(key)[0]).ToList();
            var defineOptions = rule.Defines.Select(d => d.Option).ToHashSet(StringComparer.Ordinal);
            var options = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in task.Options)
            {
                if (rule.Leading.Contains(key)) continue;
                if (defineOptions.Contains(key))
                {
                    options.Add(new(key, ApplyPrefix(batch.Prefix, value)));
                    continue;
                }
                var rewritten = value;
                foreach (var refRule in rule.Refs.Where(r => r.Option == key))
                    rewritten = RewriteValue(rewritten, refRule, (token, kinds) => Resolve(key, token, kinds));
                options.Add(new(key, rewritten));
            }

            var defined = new List<string>();
            if (rule.Name.Role == NameRoles.Define) defined.Add(name);
            foreach (var definition in rule.Defines)
                defined.AddRange(task.Values(definition.Option).Select(v => ApplyPrefix(batch.Prefix, v)));

            expandedTasks.Add(new ExpandedTask(
                task.Index, task.Type, name, rule.Install, leading, options, task.Flags,
                defined.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), references));
        }

        return errors.Count > 0
            ? (null, errors)
            : (new ExpandedBatch(batch.Model, batch.Prefix, batch.DryRun, expandedTasks), errors);
    }

    /// <summary>Applies one <see cref="RefRule"/> to an option value, resolving only the parts it names.</summary>
    internal static string RewriteValue(string value, RefRule rule, Func<string, IReadOnlyList<string>, string> resolve)
    {
        if (rule.Split is null) return Part(value);
        var parts = value.Split(rule.Split);
        if (rule.Segment >= parts.Length) return value;
        parts[rule.Segment] = Part(parts[rule.Segment]);
        return string.Join(rule.Split, parts);

        string Part(string segment)
        {
            if (rule.Subsplit is null) return resolve(segment, rule.Kinds);
            var sub = segment.Split(rule.Subsplit);
            for (var i = 0; i < sub.Length; i++)
            {
                var selected = rule.Subsegment switch
                {
                    null => true,
                    -1 => i == sub.Length - 1,
                    var n => i == n,
                };
                if (selected) sub[i] = resolve(sub[i], rule.Kinds);
            }
            return string.Join(rule.Subsplit, sub);
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Prefix names and rewrite references within a batch

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 4: Dependency stages (`Planner`)

**Files:**
- Create: `src/FoBatchGen.Core/Planner.cs`
- Test: `tests/FoBatchGen.Tests/PlannerTests.cs`

**Interfaces:**
- Consumes: `ExpandedBatch`, `ExpandedTask`, `ResolvedRef` (Task 3); `ReferenceMap` (Task 1).
- Produces: `sealed record Stage(int Number, IReadOnlyList<ExpandedTask> Tasks)` (numbers start at 1); `static class Planner { static (IReadOnlyList<Stage>? Stages, IReadOnlyList<string> Errors) Plan(ExpandedBatch batch, ReferenceMap map); }`.

- [ ] **Step 1: Write the failing tests**

`tests/FoBatchGen.Tests/PlannerTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class PlannerTests
{
    private static IReadOnlyList<Stage> PlanOk(params TaskInput[] tasks)
    {
        var (stages, errors) = Planner.Plan(Expanded("CFlt", tasks), Map);
        Assert.Empty(errors);
        return stages!;
    }

    private static string[][] Types(IReadOnlyList<Stage> stages) =>
        stages.Select(s => s.Tasks.Select(t => t.Type).ToArray()).ToArray();

    [Fact]
    public void Independent_tasks_share_one_stage_ordered_by_rank()
    {
        var stages = PlanOk(T("role", "R"), T("enum", "E"), T("table", "Tb"));
        var stage = Assert.Single(stages);
        Assert.Equal(1, stage.Number);
        Assert.Equal(new[] { "enum", "table", "role" }, stage.Tasks.Select(t => t.Type));
    }

    [Fact]
    public void A_feature_runs_in_dependency_order()
    {
        var stages = PlanOk(
            T("enum", "VehicleStatus"),
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
            T("form", "Vehicle", """{"table":"Vehicle"}"""),
            T("menu-item", "Vehicle", """{"object":"Vehicle"}"""),
            T("privilege", "VehicleMaintain", """{"entry-point":"Vehicle"}"""),
            T("duty", "VehicleMaintain", """{"privilege":"VehicleMaintain"}"""),
            T("role", "VehicleManager", """{"duty":"VehicleMaintain"}"""));

        Assert.Equal(
            new[]
            {
                new[] { "enum", "edt" }, new[] { "table" }, new[] { "form" }, new[] { "menu-item" },
                new[] { "privilege" }, new[] { "duty" }, new[] { "role" },
            },
            Types(stages));
        Assert.Equal(Enumerable.Range(1, 7), stages.Select(s => s.Number));
    }

    [Fact]
    public void A_task_waits_for_its_deepest_dependency()
    {
        var stages = PlanOk(
            T("privilege", "P", """{"entry-point":"SomeMenuItem"}"""),
            T("duty", "D", """{"privilege":"P"}"""),
            T("role", "R", """{"privilege":"P","duty":"D"}"""));
        Assert.Equal(new[] { new[] { "privilege" }, new[] { "duty" }, new[] { "role" } }, Types(stages));
    }

    [Fact]
    public void A_cycle_is_reported_with_the_tasks_involved()
    {
        var (stages, errors) = Planner.Plan(Expanded("CFlt",
            T("edt", "A", """{"extends":"B"}"""),
            T("edt", "B", """{"extends":"A"}"""),
            T("enum", "C")), Map);

        Assert.Null(stages);
        var error = Assert.Single(errors);
        Assert.Contains("cycle", error);
        Assert.Contains("tasks[0] (edt CFltA)", error);
        Assert.Contains("tasks[1] (edt CFltB)", error);
        Assert.DoesNotContain("enum", error);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `Planner`, `Stage` do not exist.

- [ ] **Step 3: Write `Planner.cs`**

`src/FoBatchGen.Core/Planner.cs`:
```csharp
namespace FoBatchGen.Core;

public sealed record Stage(int Number, IReadOnlyList<ExpandedTask> Tasks);

public static class Planner
{
    /// <summary>
    /// Puts each task one stage after its deepest dependency (longest path), so everything a task
    /// references is written — and indexed — before it runs. Within a stage, tasks run by rank, then
    /// by their position in the request.
    /// </summary>
    public static (IReadOnlyList<Stage>? Stages, IReadOnlyList<string> Errors) Plan(ExpandedBatch batch, ReferenceMap map)
    {
        var tasks = batch.Tasks.ToDictionary(t => t.Index);
        var dependsOn = tasks.Values.ToDictionary(
            t => t.Index,
            t => t.References.SelectMany(r => r.TargetTasks).Where(i => i != t.Index && tasks.ContainsKey(i)).ToHashSet());
        var dependents = tasks.Keys.ToDictionary(i => i, _ => new List<int>());
        foreach (var (task, deps) in dependsOn)
            foreach (var dep in deps)
                dependents[dep].Add(task);

        var waiting = dependsOn.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        var depth = tasks.Keys.ToDictionary(i => i, _ => 0);
        var ready = new Queue<int>(waiting.Where(kv => kv.Value == 0).Select(kv => kv.Key).Order());
        var placed = 0;
        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            placed++;
            foreach (var next in dependents[current])
            {
                depth[next] = Math.Max(depth[next], depth[current] + 1);
                if (--waiting[next] == 0) ready.Enqueue(next);
            }
        }

        if (placed < tasks.Count)
        {
            var stuck = waiting.Where(kv => kv.Value > 0).Select(kv => kv.Key).Order()
                .Select(i => $"tasks[{i}] ({tasks[i].Label})");
            return (null, new List<string>
            {
                $"Dependency cycle: these tasks are part of, or wait on, a cycle of references, so no order works: {string.Join(", ", stuck)}.",
            });
        }

        var stages = depth
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g => new Stage(
                g.Key + 1,
                g.Select(kv => tasks[kv.Key]).OrderBy(t => map.Commands[t.Type].Rank).ThenBy(t => t.Index).ToList()))
            .ToList();
        return (stages, Array.Empty<string>());
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Plan dependency stages

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 5: d365fo argument lists (`CommandBuilder`)

**Files:**
- Create: `src/FoBatchGen.Core/CommandBuilder.cs`
- Test: `tests/FoBatchGen.Tests/CommandBuilderTests.cs`

**Interfaces:**
- Consumes: `ExpandedTask`, `InstallModes`.
- Produces: `static class CommandBuilder { static IReadOnlyList<string> Build(ExpandedTask task, string model, string? applyToPath = null); static string Render(IReadOnlyList<string> args); }`. `Build` order: `generate <type> <leading...> <name> --<opt> <value>... --<flag>... (--install-to <model> | --apply-to <path>) --no-refresh --output json`. With `applyToPath` null an apply-to task gets the placeholder `<file of table NAME>` (dry run only).

- [ ] **Step 1: Write the failing tests**

`tests/FoBatchGen.Tests/CommandBuilderTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class CommandBuilderTests
{
    private static ExpandedTask Only(string type, ExpandedBatch batch) => Assert.Single(batch.Tasks, t => t.Type == type);

    [Fact]
    public void Builds_a_generate_command_that_installs_into_the_model()
    {
        var table = Only("table", Expanded("CFlt",
            T("edt", "VehicleId"),
            T("table", "Vehicle", """{"pattern":"main","field":["VehicleId:VehicleId:mandatory"],"label":"Vehicles"}""")));

        Assert.Equal(
            new[]
            {
                "generate", "table", "CFltVehicle", "--pattern", "main", "--field", "VehicleId:CFltVehicleId:mandatory",
                "--label", "Vehicles", "--install-to", "ContosoFleet", "--no-refresh", "--output", "json",
            },
            CommandBuilder.Build(table, "ContosoFleet"));
    }

    [Fact]
    public void Flags_follow_the_valued_options()
    {
        var cls = Only("class", Expanded("CFlt", T("class", "Helper", """{"non-final":true,"extends":"Object"}""")));
        Assert.Equal(
            new[] { "generate", "class", "CFltHelper", "--extends", "Object", "--non-final", "--install-to", "M", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(cls, "M"));
    }

    [Fact]
    public void Leading_arguments_come_before_the_name()
    {
        var ext = Only("extension", Expanded("CFlt", T("extension", "CustTable", """{"kind":"Table","field":"Flag:NoYes"}""")));
        Assert.Equal(
            new[] { "generate", "extension", "Table", "CustTable", "--field", "Flag:NoYes", "--install-to", "M", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(ext, "M"));
    }

    [Fact]
    public void Table_augmenting_commands_apply_to_a_file()
    {
        var find = Only("find-methods", Expanded("CFlt", T("find-methods", "CustTable")));
        Assert.Equal(
            new[] { "generate", "find-methods", "CustTable", "--apply-to", "/pkg/AxTable/CustTable.xml", "--no-refresh", "--output", "json" },
            CommandBuilder.Build(find, "M", "/pkg/AxTable/CustTable.xml"));
        Assert.Contains("<file of table CustTable>", CommandBuilder.Build(find, "M"));
    }

    [Fact]
    public void Unusual_characters_are_passed_unchanged()
    {
        const string label = "Kjøretøy & \"lastebil\" — 100%";
        var enumTask = Only("enum", Expanded("CFlt", T("enum", "Kind", $$"""{"label":{{System.Text.Json.JsonSerializer.Serialize(label)}}}""")));
        var args = CommandBuilder.Build(enumTask, "M");
        Assert.Equal(label, args[args.ToList().IndexOf("--label") + 1]);
    }

    [Fact]
    public void Render_quotes_only_arguments_that_need_it()
    {
        Assert.Equal(
            "d365fo generate enum CFltKind --label \"Kjøretøy & x\" --value A:0",
            CommandBuilder.Render(new[] { "generate", "enum", "CFltKind", "--label", "Kjøretøy & x", "--value", "A:0" }));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `CommandBuilder` does not exist.

- [ ] **Step 3: Write `CommandBuilder.cs`**

`src/FoBatchGen.Core/CommandBuilder.cs`:
```csharp
namespace FoBatchGen.Core;

public static class CommandBuilder
{
    /// <summary>The argument list for one task. Arguments are passed as a list, never through a shell.</summary>
    public static IReadOnlyList<string> Build(ExpandedTask task, string model, string? applyToPath = null)
    {
        var args = new List<string> { "generate", task.Type };
        args.AddRange(task.LeadingArgs);
        args.Add(task.Name);
        foreach (var (key, value) in task.Options)
        {
            args.Add("--" + key);
            args.Add(value);
        }
        foreach (var flag in task.Flags) args.Add("--" + flag);
        if (task.Install == InstallModes.ApplyTo)
        {
            args.Add("--apply-to");
            args.Add(applyToPath ?? $"<file of table {task.Name}>");
        }
        else
        {
            args.Add("--install-to");
            args.Add(model);
        }
        args.AddRange(["--no-refresh", "--output", "json"]);
        return args;
    }

    /// <summary>A readable command line for dry runs and messages.</summary>
    public static string Render(IReadOnlyList<string> args) => "d365fo " + string.Join(' ', args.Select(Quote));

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || "-_.:/\\=,@".Contains(c))
            ? arg
            : "\"" + arg.Replace("\"", "\\\"") + "\"";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Build d365fo argument lists

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 6: Running d365fo (`D365foRunner`, `Envelope`, `CliLocator`)

**Files:**
- Create: `src/FoBatchGen.Core/D365foRunner.cs`
- Test: `tests/FoBatchGen.Tests/EnvelopeTests.cs`, `tests/FoBatchGen.Tests/D365foRunnerTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces (namespace `FoBatchGen.Core`):
  - `sealed record CliResult(int ExitCode, bool Ok, JsonElement Data, IReadOnlyList<string> Warnings, string? ErrorCode, string? ErrorMessage, string? ErrorHint)` with `static CliResult Failure(int exitCode, string code, string message, string? hint = null)`. `Data` is `default` (ValueKind `Undefined`) when absent.
  - `interface ID365fo { Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct); }` — a caller cancellation throws `OperationCanceledException`; every other problem is a failed `CliResult`.
  - `static class Envelope { static CliResult Parse(int exitCode, string stdout, string stderr); }` — error codes it can produce besides d365fo's own: `NO_ENVELOPE`, `EXIT_CODE`, `UNKNOWN`.
  - `sealed class D365foRunner(string executable, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) : ID365fo` — adds `D365FO_FORCE_JSON=1`; codes `CLI_NOT_FOUND`, `TIMEOUT`.
  - `static class CliLocator { static string Resolve(Func<string, string?> getEnv); static TimeSpan Timeout(Func<string, string?> getEnv); }`

d365fo always prints one JSON envelope `{ "ok", "data", "warnings", "error": { "code", "message", "hint" } }` (exit 0 on success, 1 or 2 on failure) — on stdout, except argument-parsing errors such as an unknown option (`BAD_INPUT`, exit 2), which it prints on **stderr** with stdout empty. `Envelope.Parse` therefore reads stdout first, then stderr. It escapes non-ASCII as `\uXXXX`, so decoding stdout as UTF-8 is safe on Windows too.

- [ ] **Step 1: Write the failing tests**

`tests/FoBatchGen.Tests/EnvelopeTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Parses_a_success()
    {
        var r = Envelope.Parse(0, """{"ok":true,"data":{"path":"/p/A.xml"},"warnings":["w1"]}""", "");
        Assert.True(r.Ok);
        Assert.Equal("/p/A.xml", r.Data.GetProperty("path").GetString());
        Assert.Equal(new[] { "w1" }, r.Warnings);
        Assert.Null(r.ErrorCode);
    }

    [Fact]
    public void Parses_a_failure_with_code_message_and_hint()
    {
        var r = Envelope.Parse(2,
            """{"ok":false,"error":{"code":"BAD_INPUT","message":"Unknown option 'bogus'.","hint":"Run the command with --help to see the options it actually accepts."}}""",
            "");
        Assert.False(r.Ok);
        Assert.Equal(2, r.ExitCode);
        Assert.Equal("BAD_INPUT", r.ErrorCode);
        Assert.Equal("Unknown option 'bogus'.", r.ErrorMessage);
        Assert.StartsWith("Run the command", r.ErrorHint);
    }

    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException")]
    [InlineData("{not json}")]
    [InlineData("""{"data":{}}""")]
    public void Output_without_an_envelope_is_a_failure(string stdout)
    {
        var r = Envelope.Parse(1, stdout, "boom");
        Assert.False(r.Ok);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
        Assert.Contains("exited with code 1", r.ErrorMessage);
    }

    [Fact]
    public void Success_with_a_nonzero_exit_code_is_a_failure()
    {
        var r = Envelope.Parse(3, """{"ok":true,"data":{}}""", "");
        Assert.False(r.Ok);
        Assert.Equal("EXIT_CODE", r.ErrorCode);
    }

    [Fact]
    public void An_envelope_on_stderr_is_used_when_stdout_has_none()
    {
        // d365fo prints argument-parsing errors (e.g. an unknown option) on stderr.
        var r = Envelope.Parse(2, "", """{"ok":false,"error":{"code":"BAD_INPUT","message":"Unknown option 'bogus'."}}""");
        Assert.Equal("BAD_INPUT", r.ErrorCode);
    }

    [Fact]
    public void Text_before_the_envelope_is_ignored() =>
        Assert.True(Envelope.Parse(0, "note: warming up\n{\"ok\":true,\"data\":{}}", "").Ok);

    [Fact]
    public void Failure_without_error_details_gets_a_generic_code()
    {
        var r = Envelope.Parse(1, """{"ok":false}""", "");
        Assert.Equal("UNKNOWN", r.ErrorCode);
        Assert.NotNull(r.ErrorMessage);
    }
}
```

`tests/FoBatchGen.Tests/D365foRunnerTests.cs`:
```csharp
using System.Diagnostics;
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class D365foRunnerTests
{
    private static readonly bool Posix = OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    private static (string Exe, string[] Args) Sleep(int seconds) => OperatingSystem.IsWindows()
        ? ("powershell", new[] { "-NoProfile", "-Command", $"Start-Sleep -Seconds {seconds}" })
        : ("sleep", new[] { seconds.ToString() });

    [Fact]
    public async Task Missing_executable_is_reported()
    {
        var r = await new D365foRunner("/nonexistent/d365fo-missing", TimeSpan.FromSeconds(5)).RunAsync(["models", "list"], Ct);
        Assert.Equal("CLI_NOT_FOUND", r.ErrorCode);
        Assert.Contains("D365FO_CLI", r.ErrorHint);
    }

    [Fact]
    public async Task Program_without_json_output_is_a_failure()
    {
        var r = await new D365foRunner("dotnet", TimeSpan.FromSeconds(30)).RunAsync(["--version"], Ct);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
    }

    [Fact]
    public async Task Slow_program_is_stopped()
    {
        var (exe, args) = Sleep(30);
        var clock = Stopwatch.StartNew();
        var r = await new D365foRunner(exe, TimeSpan.FromSeconds(1)).RunAsync(args, Ct);
        Assert.Equal("TIMEOUT", r.ErrorCode);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Caller_cancellation_throws()
    {
        var (exe, args) = Sleep(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new D365foRunner(exe, TimeSpan.FromSeconds(60)).RunAsync(args, cts.Token));
    }

    [Fact]
    public async Task Child_gets_force_json_extra_environment_and_utf8()
    {
        Assert.SkipUnless(Posix, "uses sh");
        var env = new Dictionary<string, string> { ["FO_TEST_VALUE"] = "Kjøretøy" };
        var r = await new D365foRunner("sh", TimeSpan.FromSeconds(10), env).RunAsync(
            ["-c", "printf '{\"ok\":true,\"data\":{\"force\":\"%s\",\"extra\":\"%s\"}}' \"$D365FO_FORCE_JSON\" \"$FO_TEST_VALUE\""], Ct);
        Assert.True(r.Ok, r.ErrorMessage);
        Assert.Equal("1", r.Data.GetProperty("force").GetString());
        Assert.Equal("Kjøretøy", r.Data.GetProperty("extra").GetString());
    }

    [Fact]
    public async Task Child_does_not_inherit_the_servers_stdin()
    {
        Assert.SkipUnless(Posix, "uses cat");
        // cat reads stdin until EOF; with stdin closed it ends at once instead of hanging until the timeout.
        var r = await new D365foRunner("cat", TimeSpan.FromSeconds(10)).RunAsync([], Ct);
        Assert.Equal("NO_ENVELOPE", r.ErrorCode);
    }

    [Theory]
    [InlineData("/opt/d365fo", "/opt/d365fo")]
    [InlineData(null, "d365fo")]
    [InlineData("  ", "d365fo")]
    public void Locator_prefers_D365FO_CLI(string? value, string expected) =>
        Assert.Equal(expected, CliLocator.Resolve(name => name == "D365FO_CLI" ? value : null));

    [Theory]
    [InlineData("30", 30)]
    [InlineData("abc", 120)]
    [InlineData("0", 120)]
    [InlineData(null, 120)]
    public void Timeout_comes_from_FO_BATCHGEN_TIMEOUT_SECONDS(string? value, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CliLocator.Timeout(name => name == "FO_BATCHGEN_TIMEOUT_SECONDS" ? value : null));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `Envelope`, `D365foRunner`, `CliLocator` do not exist.

- [ ] **Step 3: Write `D365foRunner.cs`**

`src/FoBatchGen.Core/D365foRunner.cs`:
```csharp
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FoBatchGen.Core;

/// <summary>A parsed d365fo result. <see cref="Data"/> is default (Undefined) when absent.</summary>
public sealed record CliResult(
    int ExitCode,
    bool Ok,
    JsonElement Data,
    IReadOnlyList<string> Warnings,
    string? ErrorCode,
    string? ErrorMessage,
    string? ErrorHint)
{
    public static CliResult Failure(int exitCode, string code, string message, string? hint = null) =>
        new(exitCode, false, default, Array.Empty<string>(), code, message, hint);
}

public interface ID365fo
{
    /// <summary>Runs d365fo. Throws only when <paramref name="ct"/> is cancelled.</summary>
    Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

public static class Envelope
{
    /// <summary>Reads the envelope from stdout, or from stderr where d365fo prints argument-parsing errors.</summary>
    public static CliResult Parse(int exitCode, string stdout, string stderr) =>
        TryParse(exitCode, stdout) ?? TryParse(exitCode, stderr) ?? NoEnvelope(exitCode, stdout, stderr);

    private static CliResult? TryParse(int exitCode, string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) return null;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("ok", out var okElement)
                || okElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return null;

            var ok = okElement.GetBoolean();
            var data = root.TryGetProperty("data", out var d) ? d.Clone() : default;
            var warnings = root.TryGetProperty("warnings", out var w) && w.ValueKind == JsonValueKind.Array
                ? w.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            string? code = null, message = null, hint = null;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                code = Str(e, "code");
                message = Str(e, "message");
                hint = Str(e, "hint");
            }

            if (ok && exitCode != 0)
                return new CliResult(exitCode, false, data, warnings, "EXIT_CODE", $"d365fo reported success but exited with code {exitCode}.", null);
            if (ok)
                return new CliResult(exitCode, true, data, warnings, null, null, null);
            return new CliResult(exitCode, false, data, warnings,
                code ?? "UNKNOWN", message ?? "d365fo reported a failure without a message.", hint);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static CliResult NoEnvelope(int exitCode, string stdout, string stderr)
    {
        var output = (stdout + " " + stderr).Trim();
        if (output.Length > 500) output = output[..500] + "…";
        return CliResult.Failure(exitCode, "NO_ENVELOPE", $"d365fo exited with code {exitCode} and printed no JSON result. Output: {output}");
    }
}

public sealed class D365foRunner(string executable, TimeSpan timeout, IReadOnlyDictionary<string, string>? environment = null) : ID365fo
{
    public async Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // The MCP server talks JSON-RPC over its own stdin/stdout: the child must not touch either.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["D365FO_FORCE_JSON"] = "1";
        if (environment is not null)
            foreach (var (key, value) in environment)
                info.Environment[key] = value;

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return CliResult.Failure(-1, "CLI_NOT_FOUND", $"Could not start '{executable}': {ex.Message}",
                "Set D365FO_CLI to the full path of the d365fo executable, or put d365fo on PATH.");
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return CliResult.Failure(-1, "TIMEOUT",
                $"`d365fo {string.Join(' ', args.Take(2))}` did not finish within {timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} s and was stopped.",
                "Raise FO_BATCHGEN_TIMEOUT_SECONDS if the model is large.");
        }
        return Envelope.Parse(process.ExitCode, await stdout, await stderr);
    }
}

public static class CliLocator
{
    public static string Resolve(Func<string, string?> getEnv) =>
        getEnv("D365FO_CLI") is { } path && !string.IsNullOrWhiteSpace(path) ? path : "d365fo";

    public static TimeSpan Timeout(Func<string, string?> getEnv) =>
        int.TryParse(getEnv("FO_BATCHGEN_TIMEOUT_SECONDS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(120);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS (the two `sh`/`cat` tests are skipped on Windows).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Run d365fo and parse its result envelope

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---
### Task 7: The undo journal and the rollback decision

**Files:**
- Create: `src/FoBatchGen.Core/Journal.cs`
- Test: `tests/FoBatchGen.Tests/JournalTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces (namespace `FoBatchGen.Core`):
  - `sealed record JournalEntry(string Id, string Kind, string Name, string Model, string Target)`
  - `static class Journal { static IReadOnlyList<string> ListArgs(int limit); static IReadOnlyList<string> UndoArgs(int steps); static IReadOnlyList<JournalEntry> Parse(JsonElement data); }` — `ListArgs(n)` = `journal list -n <n> --output json`; `UndoArgs(n)` = `undo --steps <n> --output json`; `Parse` reads `data.entries[]` (most recent first).
  - `sealed record RollbackDecision(bool CanUndo, IReadOnlyList<JournalEntry> Entries, string? Reason)` with `int Steps => Entries.Count`.
  - `static class RollbackPlanner { static RollbackDecision Decide(string? anchorId, IReadOnlyList<JournalEntry> newestFirst, int limit, string model, IReadOnlyCollection<string> touchNames, IReadOnlyCollection<string> reportedPaths); }`

Facts from d365fo at the pinned commit: `journal list` returns `data.entries[]` newest first with `id, timestampUtc, command, targetType, kind, name, secondaryKey, model, operation, writePath, target, hasPreImage, isTombstone`; `undo --steps N` reverts and removes the newest N entries and returns `data.reverted`.

- [ ] **Step 1: Write the failing tests**

`tests/FoBatchGen.Tests/JournalTests.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class JournalTests
{
    private static JournalEntry E(string id, string name, string model = "ContosoFleet", string kind = "Table") =>
        new(id, kind, name, model, $"/pkg/{model}/{name}.xml");

    private static readonly string[] Touch = ["CFltVehicle", "CFltVehicleId", "CustTable"];

    private static RollbackDecision Decide(string? anchor, params JournalEntry[] newestFirst) =>
        RollbackPlanner.Decide(anchor, newestFirst, 2000, "ContosoFleet", Touch, Array.Empty<string>());

    [Fact]
    public void Parses_d365fo_journal_output()
    {
        using var doc = JsonDocument.Parse("""
            {"journalDirectory":"/x","count":1,"totalCount":1,"entries":[{"id":"6f50","timestampUtc":"2026-11-16T10:58:34+00:00",
             "command":"scaffold-write","targetType":"AotObject","kind":"SecurityRole","name":"FmTruckManager","model":"TestModel",
             "operation":"Create","writePath":"Disk","target":"/p/AxSecurityRole/FmTruckManager.xml","hasPreImage":false,"isTombstone":true}]}
            """);
        var entry = Assert.Single(Journal.Parse(doc.RootElement));
        Assert.Equal(new JournalEntry("6f50", "SecurityRole", "FmTruckManager", "TestModel", "/p/AxSecurityRole/FmTruckManager.xml"), entry);
    }

    [Fact]
    public void Parse_tolerates_missing_data() => Assert.Empty(Journal.Parse(default));

    [Fact]
    public void Builds_list_and_undo_arguments()
    {
        Assert.Equal(new[] { "journal", "list", "-n", "2000", "--output", "json" }, Journal.ListArgs(2000));
        Assert.Equal(new[] { "undo", "--steps", "3", "--output", "json" }, Journal.UndoArgs(3));
    }

    [Fact]
    public void Entries_above_the_anchor_belong_to_the_batch()
    {
        var d = Decide("a", E("c", "CFltVehicle"), E("b", "CFltVehicleId"), E("a", "Older"), E("0", "Oldest"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void An_empty_journal_before_the_batch_means_every_entry_is_ours()
    {
        var d = Decide(null, E("b", "CFltVehicle"), E("a", "CFltVehicleId"));
        Assert.True(d.CanUndo);
        Assert.Equal(2, d.Steps);
    }

    [Fact]
    public void Nothing_written_means_nothing_to_undo()
    {
        var d = Decide("a", E("a", "Older"));
        Assert.True(d.CanUndo);
        Assert.Equal(0, d.Steps);
    }

    [Fact]
    public void A_missing_anchor_blocks_the_undo()
    {
        var d = Decide("gone", E("b", "CFltVehicle"));
        Assert.False(d.CanUndo);
        Assert.Contains("not among the newest", d.Reason);
    }

    [Fact]
    public void A_full_journal_without_an_anchor_blocks_the_undo()
    {
        var d = RollbackPlanner.Decide(null, [E("b", "CFltVehicle"), E("a", "CFltVehicleId")], 2, "ContosoFleet", Touch, Array.Empty<string>());
        Assert.False(d.CanUndo);
    }

    [Fact]
    public void A_write_to_another_model_blocks_the_undo()
    {
        var d = Decide("a", E("c", "CFltVehicle"), E("b", "Other", model: "OtherModel"), E("a", "Older"));
        Assert.False(d.CanUndo);
        Assert.Contains("not from this batch", d.Reason);
        Assert.Contains("Other (OtherModel)", d.Reason);
        Assert.Equal(2, d.Entries.Count);
    }

    [Fact]
    public void A_write_with_an_unrelated_name_blocks_the_undo() =>
        Assert.False(Decide("a", E("b", "SalesTable"), E("a", "Older")).CanUndo);

    [Theory]
    [InlineData("CustTable_Extension")]
    [InlineData("CustTable.ContosoFleet")]
    [InlineData("CFltVehicleDP")]
    [InlineData("cfltvehicle")]
    public void Derived_names_count_as_ours(string journalName) =>
        Assert.True(Decide("a", E("b", journalName), E("a", "Older")).CanUndo);

    [Fact]
    public void Model_comparison_ignores_case() =>
        Assert.True(Decide("a", E("b", "CFltVehicle", model: "contosofleet"), E("a", "Older")).CanUndo);

    [Fact]
    public void A_path_a_task_reported_counts_as_ours()
    {
        var entry = E("b", "SomethingElse");
        var d = RollbackPlanner.Decide("a", [entry, E("a", "Older")], 2000, "ContosoFleet", Touch, [entry.Target]);
        Assert.True(d.CanUndo);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `Journal`, `JournalEntry`, `RollbackPlanner` do not exist.

- [ ] **Step 3: Write `Journal.cs`**

`src/FoBatchGen.Core/Journal.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace FoBatchGen.Core;

public sealed record JournalEntry(string Id, string Kind, string Name, string Model, string Target);

public static class Journal
{
    public static IReadOnlyList<string> ListArgs(int limit) =>
        ["journal", "list", "-n", limit.ToString(CultureInfo.InvariantCulture), "--output", "json"];

    public static IReadOnlyList<string> UndoArgs(int steps) =>
        ["undo", "--steps", steps.ToString(CultureInfo.InvariantCulture), "--output", "json"];

    /// <summary>Entries from <c>journal list</c> data, most recent first.</summary>
    public static IReadOnlyList<JournalEntry> Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<JournalEntry>();
        return entries.EnumerateArray()
            .Select(e => new JournalEntry(Str(e, "id"), Str(e, "kind"), Str(e, "name"), Str(e, "model"), Str(e, "target")))
            .ToList();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

public sealed record RollbackDecision(bool CanUndo, IReadOnlyList<JournalEntry> Entries, string? Reason)
{
    public int Steps => Entries.Count;
}

public static class RollbackPlanner
{
    /// <summary>
    /// Decides whether the entries written since <paramref name="anchorId"/> can be undone as a unit.
    /// They can only when every one is this batch's: same model, and a name containing one of the
    /// batch's names (commands journal derived names such as CustTable_Extension) or a target a task
    /// reported writing.
    /// </summary>
    public static RollbackDecision Decide(
        string? anchorId,
        IReadOnlyList<JournalEntry> newestFirst,
        int limit,
        string model,
        IReadOnlyCollection<string> touchNames,
        IReadOnlyCollection<string> reportedPaths)
    {
        var newer = new List<JournalEntry>();
        var anchorFound = anchorId is null;
        foreach (var entry in newestFirst)
        {
            if (anchorId is not null && entry.Id == anchorId)
            {
                anchorFound = true;
                break;
            }
            newer.Add(entry);
        }

        if (!anchorFound)
            return new RollbackDecision(false, newer,
                $"The journal entry recorded before the batch is not among the newest {limit} entries, so it is unclear which writes belong to this batch.");
        if (anchorId is null && newestFirst.Count >= limit)
            return new RollbackDecision(false, newer,
                $"The journal was empty before the batch but now has at least {limit} entries, so it is unclear which writes belong to this batch.");

        var paths = new HashSet<string>(reportedPaths, StringComparer.OrdinalIgnoreCase);
        var foreign = newer.Where(e => !Owned(e, model, touchNames, paths)).ToList();
        if (foreign.Count > 0)
            return new RollbackDecision(false, newer,
                "The journal contains writes that are not from this batch: "
                + string.Join(", ", foreign.Select(e => $"{e.Kind} {e.Name} ({e.Model})"))
                + ". Nothing was undone, so those writes are kept.");

        return new RollbackDecision(true, newer, null);
    }

    private static bool Owned(JournalEntry e, string model, IReadOnlyCollection<string> touchNames, HashSet<string> paths) =>
        string.Equals(e.Model, model, StringComparison.OrdinalIgnoreCase)
        && (touchNames.Any(t => t.Length > 0 && e.Name.Contains(t, StringComparison.OrdinalIgnoreCase))
            || (e.Target.Length > 0 && paths.Contains(e.Target)));
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Parse the undo journal and decide what a rollback may undo

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 8: Running a batch (`BatchExecutor`, `ArtifactPaths`)

**Files:**
- Create: `src/FoBatchGen.Core/ArtifactPaths.cs`
- Create: `src/FoBatchGen.Core/BatchExecutor.cs`
- Create: `tests/FoBatchGen.Tests/FakeD365fo.cs`
- Test: `tests/FoBatchGen.Tests/ArtifactPathsTests.cs`, `tests/FoBatchGen.Tests/BatchExecutorTests.cs`

**Interfaces:**
- Consumes: `BatchRequest`, `BatchSpec` (Task 2); `NameExpander`, `ExpandedBatch`, `ExpandedTask` (Task 3); `Planner`, `Stage` (Task 4); `CommandBuilder` (Task 5); `ID365fo`, `CliResult`, `Envelope` (Task 6); `Journal`, `RollbackPlanner` (Task 7); `InstallModes`, `ReferenceMap` (Task 1).
- Produces (namespace `FoBatchGen.Core`):
  - `static class ArtifactPaths { static IReadOnlyList<string> From(JsonElement data); }` — every string under a property named `path`, at any depth.
  - `sealed record TaskOutcome(ExpandedTask Task, int Stage, CliResult Result)`
  - `sealed record BatchFailure(string Step, string? Task, string Code, string Message, string? Hint)` — `Step` is `generate`, `refresh`, `cancelled` or `internal`; `Task` is `ExpandedTask.Label`.
  - `sealed record RollbackReport(bool RolledBack, int Reverted, IReadOnlyList<string> LeftBehind, string? Reason)`
  - `sealed record BatchResult(bool Ok, bool DryRun, string Model, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings, IReadOnlyList<Stage> Stages, IReadOnlyList<TaskOutcome> Completed, BatchFailure? Failure, RollbackReport? Rollback, long ElapsedMs)` — `Errors` non-empty means nothing was written (validation or preflight).
  - `sealed class BatchExecutor(ID365fo cli, ReferenceMap map) { const int JournalLimit = 2000; Task<BatchResult> RunAsync(BatchRequest request, CancellationToken ct); static IReadOnlyList<string> RefreshArgs(string model); }`
  - Test fake (tests project): `sealed class FakeD365fo : ID365fo` simulating `models list`, `journal list`, `undo`, `index refresh`, `get table` and `generate`, with `Journal` (oldest first), `Calls`, `Models`, `ExistingTables`, `Override`.

- [ ] **Step 1: Write the fake d365fo**

`tests/FoBatchGen.Tests/FakeD365fo.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;

namespace FoBatchGen.Tests;

/// <summary>
/// An in-memory d365fo: generate pushes a journal entry, undo pops entries, journal list reports them
/// newest first. <see cref="Override"/> can replace any answer (return null to fall through).
/// </summary>
internal sealed class FakeD365fo : ID365fo
{
    public sealed record Entry(string Id, string Kind, string Name, string Model, string Target);

    public List<Entry> Journal { get; } = [];
    public List<string> Calls { get; } = [];
    public List<(string Name, bool IsCustom)> Models { get; } = [("ContosoFleet", true)];
    public HashSet<string> ExistingTables { get; } = new(StringComparer.OrdinalIgnoreCase) { "CustTable" };
    public Func<IReadOnlyList<string>, CliResult?>? Override { get; set; }

    private int _nextId;

    public Task<CliResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Add(string.Join(' ', args));
        return Task.FromResult(Override?.Invoke(args) ?? Default(args));
    }

    public IEnumerable<string> Generates => Calls.Where(c => c.StartsWith("generate ", StringComparison.Ordinal));

    private CliResult Default(IReadOnlyList<string> a)
    {
        switch (a[0])
        {
            case "models":
                return Ok(new { count = Models.Count, items = Models.Select(m => new { name = m.Name, isCustom = m.IsCustom }) });
            case "journal":
                var limit = int.Parse(a[3]);
                var newest = Enumerable.Reverse(Journal).Take(limit)
                    .Select(e => new { id = e.Id, kind = e.Kind, name = e.Name, model = e.Model, target = e.Target });
                return Ok(new { count = Math.Min(limit, Journal.Count), totalCount = Journal.Count, entries = newest });
            case "undo":
                var steps = Math.Min(int.Parse(a[2]), Journal.Count);
                Journal.RemoveRange(Journal.Count - steps, steps);
                return Ok(new { reverted = steps });
            case "index":
                return Ok(new { modelsProcessed = 1 });
            case "get" when a[1] == "table":
                return ExistingTables.Contains(a[2])
                    ? Ok(new { table = new { name = a[2], sourcePath = $"/pkg/ContosoFleet/AxTable/{a[2]}.xml" } })
                    : Fail("NOT_FOUND", $"Table '{a[2]}' not found.");
            case "generate":
                var type = a[1];
                var name = type == "extension" ? a[3] : a[2];
                var at = a.ToList().IndexOf("--install-to");
                var model = at >= 0 ? a[at + 1] : Models[0].Name;
                var target = $"/pkg/{model}/{type}/{name}.xml";
                Journal.Add(new Entry($"e{++_nextId:D4}", type, name, model, target));
                return Ok(new { path = target });
            default:
                return Fail("BAD_INPUT", $"Unknown command '{a[0]}'.");
        }
    }

    public static CliResult Ok(object data, params string[] warnings) =>
        Envelope.Parse(0, JsonSerializer.Serialize(new { ok = true, data, warnings }), "");

    public static CliResult Fail(string code, string message, string? hint = null) =>
        Envelope.Parse(1, JsonSerializer.Serialize(new { ok = false, error = new { code, message, hint } }), "");
}
```

- [ ] **Step 2: Write the failing tests**

`tests/FoBatchGen.Tests/ArtifactPathsTests.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;
using Xunit;

namespace FoBatchGen.Tests;

public class ArtifactPathsTests
{
    [Fact]
    public void Finds_paths_at_any_depth()
    {
        // Shape of `generate report` output at the pinned d365fo commit.
        using var doc = JsonDocument.Parse("""
            {"report":{"path":"/m/AxReport/R.xml"},"dp":{"path":"/m/AxClass/RDP.xml"},
             "tmpTables":[{"path":"/m/AxTable/RTmp.xml"}],"menuItem":{"path":"/m/AxMenuItemOutput/R.xml"},"name":"R"}
            """);
        Assert.Equal(
            new[] { "/m/AxReport/R.xml", "/m/AxClass/RDP.xml", "/m/AxTable/RTmp.xml", "/m/AxMenuItemOutput/R.xml" },
            ArtifactPaths.From(doc.RootElement));
    }

    [Fact]
    public void Missing_data_has_no_paths() => Assert.Empty(ArtifactPaths.From(default));
}
```

`tests/FoBatchGen.Tests/BatchExecutorTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class BatchExecutorTests
{
    private const string Tail = "--install-to ContosoFleet --no-refresh --output json";
    private const string Refresh = "index refresh --model ContosoFleet --output json";

    private static BatchRequest Vehicle(params TaskInput[] extra) => new("ContosoFleet", "CFlt",
    [
        T("enum", "VehicleStatus", """{"value":["Active:0"]}"""),
        T("edt", "VehicleId", """{"base-type":"String"}"""),
        T("table", "Vehicle", """{"field":["VehicleId:VehicleId:mandatory","Status:VehicleStatus"]}"""),
        .. extra,
    ]);

    private static (FakeD365fo Fake, BatchExecutor Executor) Make()
    {
        var fake = new FakeD365fo();
        return (fake, new BatchExecutor(fake, Map));
    }

    private static Func<IReadOnlyList<string>, CliResult?> FailWhen(string type, string code = "WRITE_FAILED") =>
        a => a[0] == "generate" && a[1] == type ? FakeD365fo.Fail(code, $"{type} failed") : null;

    [Fact]
    public async Task Runs_stage_by_stage_and_refreshes_after_each_stage()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.True(result.Ok);
        Assert.Equal(
            new[]
            {
                $"generate enum CFltVehicleStatus --value Active:0 {Tail}",
                $"generate edt CFltVehicleId --base-type String {Tail}",
                Refresh,
                $"generate table CFltVehicle --field VehicleId:CFltVehicleId:mandatory --field Status:CFltVehicleStatus {Tail}",
                Refresh,
            },
            fake.Calls.Where(c => c.StartsWith("generate ") || c.StartsWith("index refresh")));
        Assert.Equal(3, result.Completed.Count);
        Assert.Equal(2, result.Stages.Count);
        Assert.Null(result.Failure);
        Assert.Null(result.Rollback);
    }

    [Fact]
    public async Task A_failed_task_rolls_back_every_write_of_the_batch()
    {
        var (fake, executor) = Make();
        fake.Override = FailWhen("table");

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Ok);
        Assert.Equal(new BatchFailure("generate", "table CFltVehicle", "WRITE_FAILED", "table failed", null), result.Failure);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Equal(2, result.Rollback.Reverted);
        Assert.Empty(result.Rollback.LeftBehind);
        Assert.Null(result.Rollback.Reason);
        Assert.Empty(fake.Journal);
        Assert.Contains("undo --steps 2 --output json", fake.Calls);
        Assert.Equal(Refresh, fake.Calls[^1]);
    }

    [Fact]
    public async Task Writes_from_before_the_batch_are_never_undone()
    {
        var (fake, executor) = Make();
        fake.Journal.Add(new FakeD365fo.Entry("old", "Table", "Older", "ContosoFleet", "/old.xml"));
        fake.Override = FailWhen("table");

        await executor.RunAsync(Vehicle(), Ct);

        Assert.Equal("old", Assert.Single(fake.Journal).Id);
    }

    [Fact]
    public async Task A_foreign_write_during_the_batch_blocks_the_undo()
    {
        var (fake, executor) = Make();
        fake.Override = a =>
        {
            if (a[0] == "generate" && a[1] == "edt")
                fake.Journal.Add(new FakeD365fo.Entry("x", "Table", "SalesTable", "OtherModel", "/other.xml"));
            return FailWhen("table")(a);
        };

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("not from this batch", result.Rollback.Reason);
        Assert.Contains("/pkg/ContosoFleet/enum/CFltVehicleStatus.xml", result.Rollback.LeftBehind);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("undo"));
        Assert.Equal(3, fake.Journal.Count);
    }

    [Fact]
    public async Task A_failed_undo_is_reported_with_the_files_left_behind()
    {
        var (fake, executor) = Make();
        fake.Override = a => a[0] == "undo" ? FakeD365fo.Fail("UNDO_FAILED", "disk locked") : FailWhen("table")(a);

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.False(result.Rollback!.RolledBack);
        Assert.Contains("disk locked", result.Rollback.Reason);
        Assert.Equal(2, result.Rollback.LeftBehind.Count);
    }

    [Fact]
    public async Task Cancelling_mid_batch_still_rolls_back()
    {
        var (fake, executor) = Make();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        fake.Override = a =>
        {
            if (a[0] == "generate" && a[1] == "edt") cts.Cancel();
            return null;
        };

        var result = await executor.RunAsync(Vehicle(), cts.Token);

        Assert.Equal("CANCELLED", result.Failure!.Code);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Equal(2, result.Rollback.Reverted);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task A_failed_refresh_rolls_back()
    {
        var (fake, executor) = Make();
        var refreshes = 0;
        fake.Override = a => a[0] == "index" && ++refreshes == 1 ? FakeD365fo.Fail("INDEX_LOCKED", "locked") : null;

        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.Equal("refresh", result.Failure!.Step);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task Apply_to_uses_the_file_of_a_table_made_earlier_in_the_batch()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(T("find-methods", "Vehicle")), Ct);

        Assert.True(result.Ok);
        Assert.Contains("generate find-methods CFltVehicle --apply-to /pkg/ContosoFleet/table/CFltVehicle.xml --no-refresh --output json", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("get table"));
    }

    [Fact]
    public async Task Apply_to_looks_up_an_existing_table()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt", [T("find-methods", "CustTable")]), Ct);

        Assert.True(result.Ok);
        Assert.Contains("get table CustTable --output json", fake.Calls);
        Assert.Contains("generate find-methods CustTable --apply-to /pkg/ContosoFleet/AxTable/CustTable.xml --no-refresh --output json", fake.Calls);
    }

    [Fact]
    public async Task Apply_to_an_unknown_table_fails_and_rolls_back()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle(T("find-methods", "NoSuchTable")), Ct);

        Assert.Equal("TARGET_NOT_FOUND", result.Failure!.Code);
        Assert.True(result.Rollback!.RolledBack);
        Assert.Empty(fake.Journal);
    }

    [Fact]
    public async Task An_unknown_model_is_rejected_before_writing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle() with { Model = "Nope" }, Ct);

        Assert.False(result.Ok);
        Assert.Contains("not in the d365fo index", Assert.Single(result.Errors));
        Assert.Contains("ContosoFleet", result.Errors[0]);
        Assert.Empty(fake.Generates);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("journal"));
    }

    [Fact]
    public async Task A_model_not_marked_custom_gives_a_warning()
    {
        var (fake, executor) = Make();
        fake.Models[0] = ("ContosoFleet", false);
        var result = await executor.RunAsync(Vehicle(), Ct);

        Assert.True(result.Ok);
        Assert.Contains("D365FO_CUSTOM_MODELS", Assert.Single(result.Warnings));
    }

    [Fact]
    public async Task A_dry_run_calls_nothing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(Vehicle() with { DryRun = true }, Ct);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Equal(2, result.Stages.Count);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Invalid_requests_call_nothing()
    {
        var (fake, executor) = Make();
        var result = await executor.RunAsync(new BatchRequest("ContosoFleet", "CFlt", [T("tabel", "X")]), Ct);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(fake.Calls);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `ArtifactPaths`, `BatchExecutor`, `BatchResult` and related records do not exist.

- [ ] **Step 4: Write `ArtifactPaths.cs`**

`src/FoBatchGen.Core/ArtifactPaths.cs`:
```csharp
using System.Text.Json;

namespace FoBatchGen.Core;

public static class ArtifactPaths
{
    /// <summary>Every string under a property named <c>path</c>, at any depth of a d365fo result.</summary>
    public static IReadOnlyList<string> From(JsonElement data)
    {
        var found = new List<string>();
        Walk(data, found);
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void Walk(JsonElement element, List<string> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("path") && property.Value.ValueKind == JsonValueKind.String)
                        found.Add(property.Value.GetString()!);
                    else
                        Walk(property.Value, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Walk(item, found);
                break;
        }
    }
}
```

- [ ] **Step 5: Write `BatchExecutor.cs`**

`src/FoBatchGen.Core/BatchExecutor.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;

namespace FoBatchGen.Core;

public sealed record TaskOutcome(ExpandedTask Task, int Stage, CliResult Result);

public sealed record BatchFailure(string Step, string? Task, string Code, string Message, string? Hint);

public sealed record RollbackReport(bool RolledBack, int Reverted, IReadOnlyList<string> LeftBehind, string? Reason);

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
    long ElapsedMs);

public sealed class BatchExecutor(ID365fo cli, ReferenceMap map)
{
    public const int JournalLimit = 2000;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public static IReadOnlyList<string> RefreshArgs(string model) =>
        ["index", "refresh", "--model", model, "--output", "json"];

    public async Task<BatchResult> RunAsync(BatchRequest request, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var (valid, specErrors) = BatchSpec.Validate(request, map);
        if (valid is null) return Rejected(request, specErrors, clock);
        var (expanded, nameErrors) = NameExpander.Expand(valid, map);
        if (expanded is null) return Rejected(request, nameErrors, clock);
        var (stages, planErrors) = Planner.Plan(expanded, map);
        if (stages is null) return Rejected(request, planErrors, clock);
        if (expanded.DryRun)
            return new BatchResult(true, true, expanded.Model, [], [], stages, [], null, null, clock.ElapsedMilliseconds);

        await _gate.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(expanded, stages, clock, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BatchResult Rejected(BatchRequest request, IReadOnlyList<string> errors, Stopwatch clock) =>
        new(false, request.DryRun, request.Model ?? "", errors, [], [], [], null, null, clock.ElapsedMilliseconds);

    private async Task<BatchResult> ExecuteAsync(ExpandedBatch batch, IReadOnlyList<Stage> stages, Stopwatch clock, CancellationToken ct)
    {
        var warnings = new List<string>();
        var preflightError = await PreflightAsync(batch.Model, warnings, ct);
        if (preflightError is not null)
            return new BatchResult(false, false, batch.Model, [preflightError], warnings, stages, [], null, null, clock.ElapsedMilliseconds);

        var anchor = await cli.RunAsync(Journal.ListArgs(1), ct);
        if (!anchor.Ok)
            return new BatchResult(false, false, batch.Model,
                [$"Could not read the d365fo journal before writing, so a rollback would be impossible: {anchor.ErrorCode}: {anchor.ErrorMessage}"],
                warnings, stages, [], null, null, clock.ElapsedMilliseconds);
        var anchorId = Journal.Parse(anchor.Data).FirstOrDefault()?.Id;

        var completed = new List<TaskOutcome>();
        BatchFailure? failure = null;
        try
        {
            foreach (var stage in stages)
            {
                foreach (var task in stage.Tasks)
                {
                    string? applyTo = null;
                    if (task.Install == InstallModes.ApplyTo)
                    {
                        var (path, lookupError) = await ResolveTableFileAsync(task, completed, ct);
                        if (path is null)
                        {
                            failure = Fail("generate", task, lookupError!);
                            break;
                        }
                        applyTo = path;
                    }

                    var result = await cli.RunAsync(CommandBuilder.Build(task, batch.Model, applyTo), ct);
                    if (!result.Ok)
                    {
                        failure = Fail("generate", task, result);
                        break;
                    }
                    completed.Add(new TaskOutcome(task, stage.Number, result));
                }
                if (failure is not null) break;

                var refresh = await cli.RunAsync(RefreshArgs(batch.Model), ct);
                if (!refresh.Ok)
                {
                    failure = Fail("refresh", null, refresh);
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failure = new BatchFailure("cancelled", null, "CANCELLED", "The batch was cancelled before it finished.", null);
        }
        catch (Exception ex)
        {
            failure = new BatchFailure("internal", null, "INTERNAL", ex.Message, null);
        }

        if (failure is null)
            return new BatchResult(true, false, batch.Model, [], warnings, stages, completed, null, null, clock.ElapsedMilliseconds);

        var rollback = await RollBackAsync(batch, anchorId, completed);
        return new BatchResult(false, false, batch.Model, [], warnings, stages, completed, failure, rollback, clock.ElapsedMilliseconds);
    }

    private static BatchFailure Fail(string step, ExpandedTask? task, CliResult result) =>
        new(step, task?.Label, result.ErrorCode ?? "UNKNOWN", result.ErrorMessage ?? "", result.ErrorHint);

    private async Task<string?> PreflightAsync(string model, List<string> warnings, CancellationToken ct)
    {
        var models = await cli.RunAsync(["models", "list", "--output", "json"], ct);
        if (!models.Ok)
            return $"d365fo is not usable: {models.ErrorCode}: {models.ErrorMessage}"
                + (models.ErrorHint is null ? "" : $" Hint: {models.ErrorHint}");

        var items = new List<JsonElement>();
        if (models.Data.ValueKind == JsonValueKind.Object
            && models.Data.TryGetProperty("items", out var array)
            && array.ValueKind == JsonValueKind.Array)
            items.AddRange(array.EnumerateArray());

        var match = items.FirstOrDefault(i => string.Equals(Str(i, "name"), model, StringComparison.OrdinalIgnoreCase));
        if (match.ValueKind == JsonValueKind.Undefined)
        {
            var known = items.Select(i => Str(i, "name")).Where(n => n is not null).Take(20);
            return $"Model '{model}' is not in the d365fo index. Known models: {string.Join(", ", known)}. "
                + "Check the name, or run `d365fo index refresh` if the model is new.";
        }
        if (!(match.TryGetProperty("isCustom", out var custom) && custom.ValueKind == JsonValueKind.True))
            warnings.Add($"Model '{model}' is not marked as custom in d365fo. Add it to D365FO_CUSTOM_MODELS.");
        return null;
    }

    private async Task<(string? Path, CliResult? Error)> ResolveTableFileAsync(
        ExpandedTask task, IReadOnlyList<TaskOutcome> completed, CancellationToken ct)
    {
        var created = completed.LastOrDefault(o =>
            o.Task.Type == "table" && string.Equals(o.Task.Name, task.Name, StringComparison.OrdinalIgnoreCase));
        if (created is not null && Str(created.Result.Data, "path") is { Length: > 0 } createdPath)
            return (createdPath, null);

        var lookup = await cli.RunAsync(["get", "table", task.Name, "--output", "json"], ct);
        if (lookup.Ok
            && lookup.Data.ValueKind == JsonValueKind.Object
            && lookup.Data.TryGetProperty("table", out var table)
            && Str(table, "sourcePath") is { Length: > 0 } sourcePath)
            return (sourcePath, null);

        return (null, CliResult.Failure(lookup.ExitCode, "TARGET_NOT_FOUND",
            $"Could not find the file of table '{task.Name}' for {task.Type}: {lookup.ErrorMessage ?? "d365fo get table returned no sourcePath."}",
            "Create the table earlier in the same batch, or check the name with `d365fo search table`."));
    }

    private async Task<RollbackReport> RollBackAsync(ExpandedBatch batch, string? anchorId, IReadOnlyList<TaskOutcome> completed)
    {
        // The caller may have cancelled; the rollback must still run.
        var none = CancellationToken.None;
        var reported = completed.SelectMany(o => ArtifactPaths.From(o.Result.Data))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var list = await cli.RunAsync(Journal.ListArgs(JournalLimit), none);
        if (!list.Ok)
            return new RollbackReport(false, 0, reported, $"Could not read the d365fo journal to roll back: {list.ErrorMessage}");

        var decision = RollbackPlanner.Decide(anchorId, Journal.Parse(list.Data), JournalLimit, batch.Model, batch.TouchNames, reported);
        var leftBehind = decision.Entries.Select(e => e.Target).Where(t => t.Length > 0)
            .Concat(reported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!decision.CanUndo) return new RollbackReport(false, 0, leftBehind, decision.Reason);
        if (decision.Steps == 0) return new RollbackReport(true, 0, [], null);

        var undo = await cli.RunAsync(Journal.UndoArgs(decision.Steps), none);
        if (!undo.Ok)
            return new RollbackReport(false, 0, leftBehind, $"d365fo undo failed: {undo.ErrorCode}: {undo.ErrorMessage}");

        var reverted = undo.Data.ValueKind == JsonValueKind.Object
            && undo.Data.TryGetProperty("reverted", out var r)
            && r.ValueKind == JsonValueKind.Number
            && r.TryGetInt32(out var n) ? n : decision.Steps;
        if (reverted < decision.Steps)
            return new RollbackReport(false, reverted, leftBehind, $"d365fo undo reverted only {reverted} of {decision.Steps} writes.");

        var refresh = await cli.RunAsync(RefreshArgs(batch.Model), none);
        return new RollbackReport(true, reverted, [],
            refresh.Ok ? null : $"All writes were undone, but `d365fo index refresh --model {batch.Model}` failed afterwards ({refresh.ErrorMessage}); run it by hand.");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Run batches stage by stage with full rollback

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 9: Result summary and the MCP server

**Files:**
- Create: `src/FoBatchGen.Core/Summary.cs`
- Create: `src/FoBatchGen/FoBatchGen.csproj`, `src/FoBatchGen/Program.cs`, `src/FoBatchGen/GenerateFoArtifactsTool.cs`
- Modify: `tests/FoBatchGen.Tests/FoBatchGen.Tests.csproj` (reference the server project), `FoBatchGen.slnx`
- Test: `tests/FoBatchGen.Tests/SummaryTests.cs`, `tests/FoBatchGen.Tests/GenerateFoArtifactsToolTests.cs`

**Interfaces:**
- Consumes: `BatchResult`, `BatchExecutor`, `ArtifactPaths` (Task 8); `CommandBuilder` (Task 5); `D365foRunner`, `CliLocator`, `ID365fo` (Task 6); `ReferenceMap` (Task 1); `FakeD365fo` (tests).
- Produces:
  - `static class Summary { const string BridgeWarningMarker = "bridge is not available"; static string ToJson(BatchResult result); }` — snake_case JSON; shapes: rejected `{ok:false, model, errors[], warnings?}`; dry run `{ok:true, dry_run:true, model, plan:[{stage, commands[]}]}`; success `{ok:true, model, stages, elapsed_ms, artifacts:[{type, name, paths[]}], warnings?:[{task?, message}]}`; failure `{ok:false, model, failed:{step, task?, code, message, hint?}, rollback:{rolled_back, reverted, left_behind?, reason?}, completed_before_failure, warnings?}`.
  - `[McpServerToolType] sealed class GenerateFoArtifactsTool(BatchExecutor executor)` with `const string ToolDescription` and tool `generate_fo_artifacts(string model, string prefix, List<TaskInput> tasks, bool dry_run = false)` returning `CallToolResult` (`IsError = !result.Ok`).

- [ ] **Step 1: Create the server project and reference it from the tests**

`src/FoBatchGen/FoBatchGen.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>FoBatchGen</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.4.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.15" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../FoBatchGen.Core/FoBatchGen.Core.csproj" />
  </ItemGroup>
</Project>
```

In `tests/FoBatchGen.Tests/FoBatchGen.Tests.csproj`, add to the `ProjectReference` item group:
```xml
    <ProjectReference Include="../../src/FoBatchGen/FoBatchGen.csproj" />
```

```bash
dotnet sln FoBatchGen.slnx add src/FoBatchGen/FoBatchGen.csproj
```

- [ ] **Step 2: Write the failing tests**

`tests/FoBatchGen.Tests/SummaryTests.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class SummaryTests
{
    private static BatchRequest Request(bool dryRun = false) => new("ContosoFleet", "CFlt",
    [
        T("edt", "VehicleId"),
        T("table", "Vehicle", """{"field":["VehicleId:VehicleId"]}"""),
    ], dryRun);

    private static async Task<JsonElement> RunAsync(FakeD365fo fake, BatchRequest request)
    {
        var result = await new BatchExecutor(fake, Map).RunAsync(request, Ct);
        return JsonDocument.Parse(Summary.ToJson(result)).RootElement;
    }

    [Fact]
    public async Task Success_lists_artifacts_and_collapses_bridge_warnings()
    {
        var fake = new FakeD365fo();
        fake.Override = a => a[0] == "generate"
            ? FakeD365fo.Ok(new { path = $"/m/{a[2]}.xml" }, "Metadata API unavailable: bridge is not available (set D365FO_BRIDGE_ENABLED=1).", $"Kjøretøy note for {a[2]}")
            : null;

        var json = await RunAsync(fake, Request());

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(2, json.GetProperty("stages").GetInt32());
        var artifacts = json.GetProperty("artifacts").EnumerateArray().ToList();
        Assert.Equal("CFltVehicleId", artifacts[0].GetProperty("name").GetString());
        Assert.Equal("/m/CFltVehicle.xml", artifacts[1].GetProperty("paths")[0].GetString());
        var warnings = json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("message").GetString()!).ToList();
        Assert.Single(warnings, w => w.Contains("bridge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Kjøretøy note for CFltVehicle", warnings);
        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.TryGetProperty("task", out var t) && t.GetString() == "table CFltVehicle");
    }

    [Fact]
    public async Task Failure_reports_the_step_error_and_rollback()
    {
        var fake = new FakeD365fo
        {
            Override = a => a[0] == "generate" && a[1] == "table" ? FakeD365fo.Fail("WRITE_FAILED", "Target exists", "pass --overwrite") : null,
        };

        var json = await RunAsync(fake, Request());

        Assert.False(json.GetProperty("ok").GetBoolean());
        var failed = json.GetProperty("failed");
        Assert.Equal("generate", failed.GetProperty("step").GetString());
        Assert.Equal("table CFltVehicle", failed.GetProperty("task").GetString());
        Assert.Equal("WRITE_FAILED", failed.GetProperty("code").GetString());
        Assert.Equal("pass --overwrite", failed.GetProperty("hint").GetString());
        Assert.True(json.GetProperty("rollback").GetProperty("rolled_back").GetBoolean());
        Assert.Equal(1, json.GetProperty("rollback").GetProperty("reverted").GetInt32());
        Assert.Equal(1, json.GetProperty("completed_before_failure").GetInt32());
    }

    [Fact]
    public async Task Rejected_requests_list_the_errors()
    {
        var json = await RunAsync(new FakeD365fo(), new BatchRequest("ContosoFleet", "CFlt", [T("tabel", "X")]));
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("unknown type", json.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task Dry_run_shows_the_commands_per_stage()
    {
        var json = await RunAsync(new FakeD365fo(), Request(dryRun: true));
        Assert.True(json.GetProperty("dry_run").GetBoolean());
        var plan = json.GetProperty("plan").EnumerateArray().ToList();
        Assert.Equal(2, plan.Count);
        Assert.Equal(
            "d365fo generate table CFltVehicle --field VehicleId:CFltVehicleId --install-to ContosoFleet --no-refresh --output json",
            plan[1].GetProperty("commands")[0].GetString());
    }
}
```

`tests/FoBatchGen.Tests/GenerateFoArtifactsToolTests.cs`:
```csharp
using System.Text.Json;
using FoBatchGen;
using FoBatchGen.Core;
using ModelContextProtocol.Protocol;
using Xunit;
using static FoBatchGen.Tests.TestData;

namespace FoBatchGen.Tests;

public class GenerateFoArtifactsToolTests
{
    [Fact]
    public void Description_lists_exactly_the_supported_types()
    {
        const string marker = "Supported types: ";
        var start = GenerateFoArtifactsTool.ToolDescription.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var listed = GenerateFoArtifactsTool.ToolDescription[start..].TrimEnd('.').Split(", ");
        Assert.Equal(Map.Types, listed);
    }

    [Fact]
    public async Task Returns_the_summary_and_flags_failures_as_errors()
    {
        var tool = new GenerateFoArtifactsTool(new BatchExecutor(new FakeD365fo(), Map));

        var ok = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("enum", "Status")], dry_run: false, Ct);
        Assert.False(ok.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(ok.Content)).Text;
        Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("ok").GetBoolean());

        var bad = await tool.GenerateFoArtifacts("ContosoFleet", "CFlt", [T("tabel", "X")], dry_run: false, Ct);
        Assert.True(bad.IsError);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: build FAILS — `Summary` and `GenerateFoArtifactsTool` do not exist.

- [ ] **Step 4: Write `Summary.cs`**

`src/FoBatchGen.Core/Summary.cs`:
```csharp
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FoBatchGen.Core;

/// <summary>The compact JSON the tool returns to the agent.</summary>
public static class Summary
{
    public const string BridgeWarningMarker = "bridge is not available";

    public const string BridgeNote =
        "The d365fo metadata bridge is not available, so raw scaffolds were written. "
        + "On the dev VM set D365FO_BRIDGE_ENABLED=1 to have files canonicalised by the Metadata API.";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Warning(string? Task, string Message);

    public static string ToJson(BatchResult result) => JsonSerializer.Serialize(Build(result), Options);

    private static object Build(BatchResult r)
    {
        if (r.Errors.Count > 0)
            return new
            {
                ok = false,
                model = r.Model,
                errors = r.Errors,
                warnings = NullIfEmpty(r.Warnings.Select(w => new Warning(null, w)).ToList()),
            };

        if (r.DryRun)
            return new
            {
                ok = true,
                dry_run = true,
                model = r.Model,
                plan = r.Stages.Select(s => new
                {
                    stage = s.Number,
                    commands = s.Tasks.Select(t => CommandBuilder.Render(CommandBuilder.Build(t, r.Model))).ToList(),
                }).ToList(),
            };

        var warnings = NullIfEmpty(CollectWarnings(r));
        var artifacts = r.Completed
            .Select(o => new { type = o.Task.Type, name = o.Task.Name, paths = ArtifactPaths.From(o.Result.Data) })
            .ToList();

        if (r.Ok)
            return new { ok = true, model = r.Model, stages = r.Stages.Count, elapsed_ms = r.ElapsedMs, artifacts, warnings };

        return new
        {
            ok = false,
            model = r.Model,
            failed = new
            {
                step = r.Failure!.Step,
                task = r.Failure.Task,
                code = r.Failure.Code,
                message = r.Failure.Message,
                hint = r.Failure.Hint,
            },
            rollback = r.Rollback is null
                ? null
                : new
                {
                    rolled_back = r.Rollback.RolledBack,
                    reverted = r.Rollback.Reverted,
                    left_behind = r.Rollback.LeftBehind.Count > 0 ? r.Rollback.LeftBehind : null,
                    reason = r.Rollback.Reason,
                },
            completed_before_failure = artifacts.Count,
            warnings,
        };
    }

    private static List<Warning> CollectWarnings(BatchResult r)
    {
        var list = r.Warnings.Select(w => new Warning(null, w)).ToList();
        var bridge = false;
        foreach (var outcome in r.Completed)
        {
            foreach (var message in outcome.Result.Warnings)
            {
                if (message.Contains(BridgeWarningMarker, StringComparison.OrdinalIgnoreCase))
                {
                    bridge = true;
                    continue;
                }
                list.Add(new Warning(outcome.Task.Label, message));
            }
        }
        if (bridge) list.Add(new Warning(null, BridgeNote));
        return list;
    }

    private static List<Warning>? NullIfEmpty(List<Warning> list) => list.Count > 0 ? list : null;
}
```

- [ ] **Step 5: Write the tool and the host**

`src/FoBatchGen/GenerateFoArtifactsTool.cs`:
```csharp
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
        + "For extension, options.kind (Table, Form, ...) is required. Do not pass out, install-to, apply-to, output, overwrite "
        + "or no-refresh. Run `d365fo generate <type> --help` in a terminal for each type's options. "
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
```

`src/FoBatchGen/Program.cs`:
```csharp
using FoBatchGen;
using FoBatchGen.Core;
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
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --project tests/FoBatchGen.Tests`
Expected: PASS.

- [ ] **Step 7: Check the server over stdio**

```bash
dotnet build src/FoBatchGen -c Release
printf '%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}' \
  '{"jsonrpc":"2.0","method":"notifications/initialized"}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list"}' \
  '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"generate_fo_artifacts","arguments":{"model":"M","prefix":"C","tasks":[{"type":"enum","name":"E"}],"dry_run":true}}}' \
  | (cat; sleep 3) | timeout 20 dotnet src/FoBatchGen/bin/Release/net10.0/FoBatchGen.dll 2>/dev/null
```
Expected: three JSON-RPC responses on stdout and nothing else — `tools/list` shows `generate_fo_artifacts` with `model`, `prefix`, `tasks` required and `dry_run` defaulting to false; `tools/call` returns text containing `"dry_run":true` and `d365fo generate enum CE --install-to M --no-refresh --output json`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "Add the MCP server and result summary

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---
### Task 10: Integration tests against the real d365fo

**Files:**
- Create: `scripts/build-d365fo.sh`, `scripts/build-d365fo.ps1`
- Create: `tests/FoBatchGen.IntegrationTests/FoBatchGen.IntegrationTests.csproj`
- Create: `tests/FoBatchGen.IntegrationTests/D365foSandbox.cs`
- Test: `tests/FoBatchGen.IntegrationTests/BatchIntegrationTests.cs`, `tests/FoBatchGen.IntegrationTests/SmokeTests.cs`
- Modify: `FoBatchGen.slnx`

**Interfaces:**
- Consumes: `BatchExecutor`, `BatchRequest`, `TaskInput`, `BatchResult`, `Summary`, `D365foRunner`, `ID365fo`, `CliResult`, `Journal`, `ReferenceMap`.
- Produces: `scripts/build-d365fo.sh` prints the path of the built `d365fo` executable on stdout; `D365foSandbox` (tests only).

The sample model comes from the pinned d365fo checkout (`.d365fo-cli/tests/Samples/MiniAot`, model `TestModel`, objects `FmVehicle`, `FmVehicleLine`, `FmServiceOrder`, `FmVehicleList` form, `FmVehicleService` class with method `run`, `FmVehicleQuery`, EDTs incl. `VIN` (reference table `FmVehicle`) and `FmMileage`). d365fo-cli has no LICENSE file, so the fixture is read from the checkout, not copied into this repo.

Facts from running every subcommand against that model on Linux (2026-11-17): 39 succeed with the options used in `SmokeTests` below; `datasource-method` and `control-method` change forms only through the metadata bridge (Windows dev VM) and fail here with `INSTALL_FAILED`; `resource` needs `--file-name` or `--source`.

- [ ] **Step 1: Write the build scripts**

`scripts/build-d365fo.sh`:
```bash
#!/usr/bin/env bash
# Clones d365fo-cli at the commit fo-batchgen is tested against, builds it, and prints the d365fo path.
set -euo pipefail
COMMIT=640406b357b5fc9bc33ba499c8ec325c5b8a5c5f
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DIR="$ROOT/.d365fo-cli"
if [ ! -d "$DIR/.git" ]; then
  git clone --quiet https://github.com/dynamics365ninja/d365fo-cli "$DIR"
fi
git -C "$DIR" fetch --quiet --depth 1 origin "$COMMIT"
git -C "$DIR" checkout --quiet "$COMMIT"
dotnet build "$DIR/src/D365FO.Cli" -c Release --nologo -v quiet >&2
echo "$DIR/src/D365FO.Cli/bin/Release/net10.0/d365fo"
```

`scripts/build-d365fo.ps1`:
```powershell
# Clones d365fo-cli at the commit fo-batchgen is tested against, builds it, and prints the d365fo path.
$ErrorActionPreference = 'Stop'
$Commit = '640406b357b5fc9bc33ba499c8ec325c5b8a5c5f'
$Root = Split-Path -Parent $PSScriptRoot
$Dir = Join-Path $Root '.d365fo-cli'
if (-not (Test-Path (Join-Path $Dir '.git'))) {
    git clone --quiet https://github.com/dynamics365ninja/d365fo-cli $Dir
}
git -C $Dir fetch --quiet --depth 1 origin $Commit
git -C $Dir checkout --quiet $Commit
dotnet build (Join-Path $Dir 'src/D365FO.Cli') -c Release --nologo -v quiet | Out-Host
Join-Path $Dir 'src/D365FO.Cli/bin/Release/net10.0/d365fo.exe'
```

```bash
chmod +x scripts/build-d365fo.sh
export D365FO_CLI="$(scripts/build-d365fo.sh)"
"$D365FO_CLI" --version || true
ls .d365fo-cli/tests/Samples/MiniAot/TestModel/TestModel
```
Expected: the last command lists `AxClass AxEdt AxEnum AxForm AxQuery AxTable`.

- [ ] **Step 2: Create the test project**

`tests/FoBatchGen.IntegrationTests/FoBatchGen.IntegrationTests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="xunit.v3" Version="4.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../../src/FoBatchGen.Core/FoBatchGen.Core.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln FoBatchGen.slnx add tests/FoBatchGen.IntegrationTests/FoBatchGen.IntegrationTests.csproj
```

`tests/FoBatchGen.IntegrationTests/D365foSandbox.cs`:
```csharp
using System.Security.Cryptography;
using System.Text.Json;
using FoBatchGen.Core;

namespace FoBatchGen.IntegrationTests;

/// <summary>A throw-away copy of d365fo's sample model with its own index, journal and config.</summary>
public sealed class D365foSandbox : IAsyncDisposable
{
    public const string Model = "TestModel";

    public static string? CliPath =>
        Environment.GetEnvironmentVariable("D365FO_CLI") is { Length: > 0 } path ? path : null;

    /// <summary>The d365fo-cli checkout made by scripts/build-d365fo.*, found by walking up to FoBatchGen.slnx.</summary>
    public static string? CheckoutPath
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (!File.Exists(Path.Combine(dir.FullName, "FoBatchGen.slnx"))) continue;
                var checkout = Path.Combine(dir.FullName, ".d365fo-cli");
                return Directory.Exists(checkout) ? checkout : null;
            }
            return null;
        }
    }

    public static string? SkipReason =>
        CliPath is null ? "Set D365FO_CLI to the d365fo executable (scripts/build-d365fo.sh prints it) to run integration tests."
        : CheckoutPath is null ? "Run scripts/build-d365fo.sh first: the tests use the sample model in .d365fo-cli."
        : null;

    public required string Root { get; init; }
    public required string Packages { get; init; }
    public required D365foRunner Cli { get; init; }
    public required BatchExecutor Executor { get; init; }

    private Dictionary<string, string> _snapshot = new();

    public string ModelDir => Path.Combine(Packages, Model, Model);

    public static async Task<D365foSandbox> CreateAsync(CancellationToken ct)
    {
        var root = Path.Combine(Path.GetTempPath(), "fo-batchgen-it-" + Guid.NewGuid().ToString("N")[..8]);
        var packages = Path.Combine(root, "Packages");
        CopyDirectory(Path.Combine(CheckoutPath!, "tests", "Samples", "MiniAot"), packages);
        var environment = new Dictionary<string, string>
        {
            ["D365FO_PACKAGES_PATH"] = packages,
            ["D365FO_CUSTOM_MODELS"] = Model,
            ["D365FO_CONFIG_DIR"] = Path.Combine(root, "cfg"),
            ["D365FO_INDEX_DB"] = Path.Combine(root, "cfg", "index.sqlite"),
            ["D365FO_HOME"] = Path.Combine(root, "home"),
            ["D365FO_BRIDGE_ENABLED"] = "0",
        };
        var cli = new D365foRunner(CliPath!, TimeSpan.FromMinutes(2), environment);
        await MustAsync(cli, ["index", "build", "--output", "json"], ct);
        await MustAsync(cli, ["index", "extract", "--output", "json"], ct);
        var sandbox = new D365foSandbox
        {
            Root = root,
            Packages = packages,
            Cli = cli,
            Executor = new BatchExecutor(cli, ReferenceMap.LoadEmbedded()),
        };
        sandbox.TakeSnapshot();
        return sandbox;
    }

    public static async Task<CliResult> MustAsync(ID365fo cli, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await cli.RunAsync(args, ct);
        if (!result.Ok)
            throw new InvalidOperationException($"d365fo {string.Join(' ', args)} failed: {result.ErrorCode}: {result.ErrorMessage}");
        return result;
    }

    public static TaskInput T(string type, string name, string? optionsJson = null) =>
        new(type, name, optionsJson is null ? null : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson));

    /// <summary>Remembers the content of every file under Packages.</summary>
    public void TakeSnapshot() => _snapshot = Hashes();

    /// <summary>Files added, changed or removed since <see cref="TakeSnapshot"/>.</summary>
    public IReadOnlyList<string> ChangesSinceSnapshot()
    {
        var now = Hashes();
        var changes = new List<string>();
        changes.AddRange(now.Keys.Except(_snapshot.Keys).Select(f => "added " + f));
        changes.AddRange(_snapshot.Keys.Except(now.Keys).Select(f => "removed " + f));
        changes.AddRange(now.Keys.Intersect(_snapshot.Keys).Where(f => now[f] != _snapshot[f]).Select(f => "changed " + f));
        return changes;
    }

    private Dictionary<string, string> Hashes() =>
        Directory.EnumerateFiles(Packages, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(Packages, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Write the batch tests**

`tests/FoBatchGen.IntegrationTests/BatchIntegrationTests.cs`:
```csharp
using System.Text.Json;
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.IntegrationTests.D365foSandbox;

namespace FoBatchGen.IntegrationTests;

public class BatchIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireD365fo() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");

    private static TaskInput[] TruckFeature() =>
    [
        T("enum", "VehicleKind", """{"value":["Light:0","Heavy:1"],"label":"Vehicle kind"}"""),
        T("edt", "TruckId", """{"base-type":"String","size":20}"""),
        T("table", "Truck", """{"pattern":"main","field":["TruckId:TruckId:mandatory","Kind:VehicleKind","Vin:VIN"]}"""),
        T("form", "Truck", """{"pattern":"SimpleListDetails","table":"Truck","caption":"Trucks","field":["TruckId","Kind","Vin"]}"""),
        T("menu-item", "Truck", """{"kind":"Display","object":"Truck","object-type":"Form","label":"Trucks"}"""),
        T("privilege", "TruckMaintain", """{"entry-point":"Truck","entry-kind":"MenuItemDisplay","access":"Delete"}"""),
        T("duty", "TruckMaintain", """{"privilege":"TruckMaintain"}"""),
        T("role", "TruckManager", """{"duty":"TruckMaintain"}"""),
    ];

    [Fact]
    public async Task A_feature_is_created_in_dependency_order_and_is_findable()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", TruckFeature()), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        Assert.Equal(7, result.Stages.Count);
        var table = await File.ReadAllTextAsync(Path.Combine(box.ModelDir, "AxTable", "FxTruck.xml"), Ct);
        Assert.Contains("AxTableFieldEnum", table);
        Assert.Contains("<EnumType>FxVehicleKind</EnumType>", table);
        Assert.Contains("<ExtendedDataType>FxTruckId</ExtendedDataType>", table);
        Assert.Contains("<ExtendedDataType>VIN</ExtendedDataType>", table);
        Assert.True((await box.Cli.RunAsync(["get", "table", "FxTruck", "--output", "json"], Ct)).Ok);
        var coverage = await MustAsync(box.Cli, ["security", "coverage", "FxTruck", "--type", "MenuItemDisplay", "--output", "json"], Ct);
        Assert.Contains("FxTruckManager", coverage.Data.GetRawText());
    }

    [Fact]
    public async Task A_failure_in_the_last_stage_rolls_back_everything()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var tasks = TruckFeature().Append(T("role", "Broken", """{"duty":"TruckMaintain","bogus":"x"}""")).ToArray();

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct);

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Failure!.Code);
        Assert.Equal("role FxBroken", result.Failure.Task);
        Assert.True(result.Rollback!.RolledBack, Summary.ToJson(result));
        Assert.Empty(box.ChangesSinceSnapshot());
        var search = await MustAsync(box.Cli, ["search", "any", "FxTruck", "--output", "json"], Ct);
        Assert.Equal(0, search.Data.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Running_a_batch_twice_fails_without_touching_the_first_run()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        TaskInput[] tasks = [T("enum", "VehicleKind", """{"value":["Light:0"]}"""), T("edt", "TruckId")];
        Assert.True((await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct)).Ok);
        box.TakeSnapshot();

        var second = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", tasks), Ct);

        Assert.False(second.Ok);
        Assert.Equal("WRITE_FAILED", second.Failure!.Code);
        Assert.True(second.Rollback!.RolledBack);
        Assert.Empty(box.ChangesSinceSnapshot());
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var before = await MustAsync(box.Cli, Journal.ListArgs(5), Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", TruckFeature(), DryRun: true), Ct);

        Assert.True(result.Ok);
        Assert.True(result.DryRun);
        Assert.Empty(box.ChangesSinceSnapshot());
        var after = await MustAsync(box.Cli, Journal.ListArgs(5), Ct);
        Assert.Equal(before.Data.GetRawText(), after.Data.GetRawText());
    }

    [Fact]
    public async Task Non_ascii_and_special_characters_reach_the_file()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);
        var options = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["value"] = new[] { "A:0" },
            ["label"] = "Kjøretøy & \"lastebil\"",
        });

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", [T("enum", "Kind", options)]), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        var xml = await File.ReadAllTextAsync(Path.Combine(box.ModelDir, "AxEnum", "FxKind.xml"), Ct);
        Assert.Contains("Kjøretøy &amp;", xml);
        Assert.Contains("lastebil", xml);
    }

    [Fact]
    public async Task Rolling_back_a_change_to_an_existing_table_restores_it()
    {
        RequireD365fo();
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx",
            [T("find-methods", "FmVehicle"), T("role", "Broken", """{"bogus":"x"}""")]), Ct);

        Assert.Contains(result.Completed, o => o.Task.Type == "find-methods");
        Assert.True(result.Rollback!.RolledBack, Summary.ToJson(result));
        Assert.Empty(box.ChangesSinceSnapshot());
    }

    [Fact]
    public void Every_option_in_the_reference_map_exists_in_d365fo()
    {
        Assert.SkipWhen(CheckoutPath is null, "Run scripts/build-d365fo.sh first.");
        var source = string.Concat(Directory
            .EnumerateFiles(Path.Combine(CheckoutPath!, "src", "D365FO.Cli", "Commands", "Generate"), "*.cs")
            .Select(File.ReadAllText));
        var map = ReferenceMap.LoadEmbedded();
        var options = map.Commands.Values
            .SelectMany(r => r.Refs.Select(x => x.Option).Concat(r.Defines.Select(d => d.Option)))
            .Distinct();
        foreach (var option in options)
            Assert.True(
                source.Contains($"CommandOption(\"--{option} ") || source.Contains($"CommandOption(\"--{option}\")"),
                $"--{option} is not a d365fo generate option");
    }
}
```

- [ ] **Step 4: Write the smoke tests**

`tests/FoBatchGen.IntegrationTests/SmokeTests.cs`:
```csharp
using FoBatchGen.Core;
using Xunit;
using static FoBatchGen.IntegrationTests.D365foSandbox;

namespace FoBatchGen.IntegrationTests;

/// <summary>One small batch per subcommand, run through the real d365fo against its sample model.</summary>
public class SmokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Subcommands that change forms only through the metadata bridge (Windows dev VM): checked by hand.</summary>
    public static readonly string[] BridgeOnly = ["datasource-method", "control-method"];

    private static readonly TaskInput Edt = T("edt", "Edt", """{"base-type":"String","size":10}""");
    private static readonly TaskInput Item = T("menu-item", "Item", """{"object":"FmVehicleList"}""");
    private static readonly TaskInput Priv = T("privilege", "Priv", """{"entry-point":"Item","entry-kind":"MenuItemDisplay","access":"Read"}""");
    private static readonly TaskInput Duty = T("duty", "Duty", """{"privilege":"Priv"}""");
    private static readonly TaskInput Entity = T("entity", "Entity", """{"table":"FmVehicle","public-entity":"FxEntity","public-collection":"FxEntities","all-fields":true}""");
    private static readonly TaskInput Category = T("workflow-category", "Category", """{"module":"Basic"}""");

    public static readonly Dictionary<string, TaskInput[]> Cases = new()
    {
        ["aggregate-entity"] = [Edt, T("aggregate-entity", "Agg", """{"measurement":"Measure","measure":"Cnt:Grp:Cnt:Edt"}""")],
        ["business-event"] = [T("business-event", "Event", """{"payload":"Vin:VIN"}""")],
        ["class"] = [T("class", "Class")],
        ["coc"] = [T("coc", "FmVehicleService", """{"method":"run"}""")],
        ["composite-entity"] = [Entity, T("composite-entity", "Composite", """{"root":"Entity"}""")],
        ["configuration-key"] = [T("configuration-key", "Key")],
        ["custom-service"] = [T("custom-service", "Service")],
        ["duty"] = [Item, Priv, Duty],
        ["edt"] = [Edt],
        ["entity"] = [Entity],
        ["enum"] = [T("enum", "Enum", """{"value":["A:0","B:1"]}""")],
        ["event-handler"] = [T("event-handler", "Handler", """{"source-kind":"Table","source-object":"FmVehicle","event":"OnInserted"}""")],
        ["extension"] = [T("extension", "FmVehicle", """{"kind":"Table","field":"Mileage:FmMileage"}""")],
        ["find-methods"] = [T("find-methods", "FmVehicle")],
        ["form"] = [T("form", "Form", """{"pattern":"SimpleList","table":"FmVehicle","field":"VIN"}""")],
        ["form-clone"] = [T("form-clone", "Clone", """{"from":"FmVehicleList"}""")],
        ["form-part"] = [T("form-part", "Part", """{"form":"FmVehicleList","caption":"Part"}""")],
        ["label-file"] = [T("label-file", "FxLabels", """{"entry":"Hello=Hello"}""")],
        ["map"] = [T("map", "Map", """{"field":"Vin:VIN","map-to":"FmVehicle"}""")],
        ["menu"] = [Item, T("menu", "Menu", """{"item":"Item"}""")],
        ["menu-item"] = [Item],
        ["migration-script"] = [T("migration-script", "Mig", """{"source-table":"FmVehicle"}""")],
        ["number-sequence"] = [T("number-sequence", "Num", """{"table":"FmVehicle"}""")],
        ["privilege"] = [Item, Priv],
        ["query"] = [T("query", "Query", """{"ds":"FmVehicle"}""")],
        ["report"] = [T("report", "Report", """{"field":"VIN"}""")],
        ["report-extension"] = [T("report-extension", "dataset", """{"dp":"FxReportDP","tmp-table":"FxReportTmp","dataset-accessor":"getFxReportTmp"}""")],
        ["resource"] = [T("resource", "Res", """{"file-name":"Logo.png"}""")],
        ["role"] = [Item, Priv, Duty, T("role", "Role", """{"duty":"Duty"}""")],
        ["runbase"] = [T("runbase", "Run", """{"dialog-param":"Vin:VIN"}""")],
        ["security-policy"] = [T("security-policy", "Policy", """{"constrained-table":"FmVehicle","policy-query":"FmVehicleQuery"}""")],
        ["sysoperation"] = [T("sysoperation", "Op", """{"param":"Vin:VIN"}""")],
        ["systest"] = [T("systest", "Test", """{"class":"FmVehicleService","method":"run"}""")],
        ["table"] = [Edt, T("table", "Table", """{"pattern":"main","field":["Code:Edt","Vin:VIN"]}""")],
        ["table-relation"] = [T("table", "Rel", """{"field":"Vin:VIN"}"""), T("table-relation", "Rel")],
        ["tile"] = [Item, T("tile", "Tile", """{"menu-item":"Item"}""")],
        ["view"] = [T("view", "View", """{"query":"FmVehicleQuery","field":"VIN:FmVehicle"}""")],
        ["workflow"] = [Category, T("workflow", "Wf", """{"table":"FmVehicle","category":"Category"}""")],
        ["workflow-category"] = [Category],
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void Every_subcommand_has_a_smoke_case_or_is_bridge_only() =>
        Assert.Equal(
            ReferenceMap.LoadEmbedded().Types,
            Cases.Keys.Concat(BridgeOnly).Order(StringComparer.Ordinal).ToList());

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Subcommand_runs_through_a_batch(string type)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? "");
        await using var box = await CreateAsync(Ct);

        var result = await box.Executor.RunAsync(new BatchRequest(Model, "Fx", Cases[type]), Ct);

        Assert.True(result.Ok, Summary.ToJson(result));
        Assert.Contains(result.Completed, o => o.Task.Type == type);
    }
}
```

- [ ] **Step 5: Run the integration tests**

Run: `D365FO_CLI="$(scripts/build-d365fo.sh)" dotnet test --project tests/FoBatchGen.IntegrationTests`
Expected: PASS — 7 batch tests, 1 coverage fact, 39 smoke cases. If a smoke case fails, read the `Summary.ToJson` message in the failure:
- a d365fo `BAD_INPUT` about a missing or wrong option → correct that case's options using `"$D365FO_CLI" generate <type> --help` run in a terminal (help needs a TTY), then rerun;
- an error that only the Windows metadata bridge can fix (`INSTALL_FAILED … bridge is not available`) → move the type from `Cases` to `BridgeOnly` and add it to the README's manual checklist.
Never loosen an assertion to make a case pass.

Also run: `dotnet test --project tests/FoBatchGen.IntegrationTests` with `D365FO_CLI` unset.
Expected: the d365fo tests are reported as skipped with the "Set D365FO_CLI" reason; `Every_subcommand_has_a_smoke_case_or_is_bridge_only` passes.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Add integration and smoke tests against the pinned d365fo

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```

---

### Task 11: README, setup and the dev VM checklist

**Files:**
- Create: `README.md`

**Interfaces:**
- Consumes: everything above (documents it).
- Produces: user documentation.

- [ ] **Step 1: Write `README.md`**

`README.md`:
````markdown
# fo-batchgen

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
  listed in `D365FO_CUSTOM_MODELS`. Tested against d365fo commit `640406b357b5fc9bc33ba499c8ec325c5b8a5c5f`.
- On the dev VM: `D365FO_BRIDGE_ENABLED=1`, so files are written through the Metadata API and
  `datasource-method` / `control-method` work.

## Install

```powershell
dotnet publish src/FoBatchGen -c Release -o C:\tools\fo-batchgen
```

## Configure your AI client

The server reads the same environment variables as `d365fo`, plus:

| Variable | Meaning | Default |
|---|---|---|
| `D365FO_CLI` | Full path of the `d365fo` executable | `d365fo` on `PATH` |
| `FO_BATCHGEN_TIMEOUT_SECONDS` | Time limit for each `d365fo` call | `120` |

Claude Code (`.mcp.json` in your X++ repo):
```json
{
  "mcpServers": {
    "fo-batchgen": {
      "command": "C:\\tools\\fo-batchgen\\FoBatchGen.exe",
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
    "fo-batchgen": {
      "type": "stdio",
      "command": "C:\\tools\\fo-batchgen\\FoBatchGen.exe",
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
- `table-relation` and `find-methods` edit an existing table; fo-batchgen finds its file.
- `dry_run: true` returns the stages and exact `d365fo` commands without writing.

On failure the result names the failing task, d365fo's error and hint, and whether the rollback
succeeded. If the d365fo journal shows writes that are not from the batch (for example you ran
`d365fo` by hand at the same moment), nothing is undone and the written files are listed instead.

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
dotnet test --project tests/FoBatchGen.Tests
D365FO_CLI="$(scripts/build-d365fo.sh)" dotnet test --project tests/FoBatchGen.IntegrationTests
```

Integration tests run the pinned `d365fo` against a copy of its sample model and are skipped when
`D365FO_CLI` is not set. When moving to a newer `d365fo`, change the commit in
`scripts/build-d365fo.*` and `src/FoBatchGen.Core/reference-map.json`, then run both test projects.
````

- [ ] **Step 2: Run every test once more**

Run: `D365FO_CLI="$(scripts/build-d365fo.sh)" dotnet test`
Expected: all unit and integration tests pass; no skips other than platform-specific ones.

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "Document setup, usage and the dev VM checklist

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01Qaf9wWyewpEDfRRtokGm3q"
```
