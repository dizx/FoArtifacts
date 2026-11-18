**Title:** fix(generate): canonicalise form XML before the bridge, and report the file a bridge create wrote

## Summary

With `D365FO_BRIDGE_ENABLED=1`, `generate form --install-to` saved forms that had lost part of their content, and the model then failed to build. Bridge creates also reported no file path. This PR fixes both:

1. **Forms are canonicalised before they reach the Metadata API.** `generate form` now hands the bridge XML in contract member order, as every other generate command already does.
2. **Bridge creates report their file.** The result's `path` and the journal entry's `target` now name the file the provider wrote, as a disk write does.

## Problem

Repro on a dev VM with the bridge on:

```powershell
$env:D365FO_BRIDGE_ENABLED = '1'
d365fo generate form MyVehicle --pattern=SimpleListDetails --table=MyVehicle --field=VehicleId --field=Status --install-to MyModel
d365fo build --model MyModel
```

The build fails:

```
FormPatternValidation Error: AxForm/MyVehicle/Design/Controls/DetailsTabs/TabPageOverview/ColumnsMode:
Property 'ColumnsMode' on control ... must have value 'Fill' per pattern 'Fields and Field Groups'.
```

The saved `AxForm` is missing more than `ColumnsMode`. Both tab pages are saved with empty `<Controls />`, so `OverviewGroup`/`GeneralGroup`, their fields and their data groups are gone. The same command with the bridge off writes a complete form that builds, and that form round-trips through `d365fo validate metadata` with nothing dropped. The honesty report did not flag the loss.

Bridge creates have a second, smaller problem. Each one returned `"source": "bridge"` with no `path`, and journaled no `target`. A caller could not tell which file the command had created, or check after `undo` that the file was gone.

## Root cause

The bridge reads the XML with `DataContractSerializer`, which skips a member that arrives out of contract order without raising an error.

- `GenerateInstaller.Emit` (XDocument) runs `ContractNamespaceApplier` and `ContractOrderCanonicalizer` before the bridge and disk paths split. Its comment explains why: so the bridge does not strip properties.
- `GenerateInstaller.EmitString`, which only `generate form` uses, passed the raw template string straight to `EmitCore`.
- The disk path happened to be correct anyway, because `ScaffoldFileWriter.Write(string)` canonicalises in `CanonicalizeIfNeeded`. So only forms written through the bridge were hit.

Of the form patterns, **SimpleListDetails, DetailsMaster, ListPage, TableOfContents and Workspace** produced out-of-order XML. In ListPage, for example, `AllowEdit` arrives after `AllowDelete` and would be dropped as well. SimpleList, Lookup and Dialog were already in order. DetailsTransaction is not covered by the new test, because it needs a lines table.

## Changes

- `EmitString` canonicalises once, with `ScaffoldFileWriter.CanonicalizeIfNeeded` (now public), before the paths split. The bridge, the disk writer and the honesty report all see the same document.
- After a bridge create, the file is resolved the way a disk write's target is (`<model folder>\<subfolder>\<name>.xml`, via `ResolveModelFolder`). It is reported in the result, recorded with `RecordArtefact` and stored in the journal entry's `TargetPath`, but only when the file exists there; otherwise nothing is claimed. `undo` still replays bridge entries through the bridge, because it dispatches on `WritePath`, not on the target.
- New test seam: `GenerateInstaller.SaveViaBridge`, an internal delegate that defaults to `BridgeGate.TrySaveObject`. It lets a test see the exact XML handed to the provider, which no file on disk records. Happy to change this if you prefer another shape.

## Tests

New `GenerateFormBridgeContractOrderTests`, in the `EnvIndexDb` collection, uses a temp model and index and needs no D365 installation:

- `Xml_handed_to_the_bridge_is_in_contract_order`: a theory over 8 form patterns. It asserts that canonicalising the XML sent to the bridge changes nothing. Before the fix, 5 of 8 cases fail; after it, all 8 pass.
- `Bridge_create_reports_the_file_it_wrote`: when the provider writes the file, the result's `path` and the journal `target` name it.
- `Bridge_create_reports_no_path_it_cannot_see`: when no file is there, neither is claimed.

Full suites: `D365FO.Core.Tests` 1789 passed, 7 skipped; `D365FO.Cli.Tests` 1058 passed.

## Verified on a dev VM

Windows Server 2022, Visual Studio 18, xppc 7.0.7996.43, bridge on:

- The SimpleListDetails form above, plus enum, EDT, table, menu item and security objects, were generated into a custom model. `d365fo build --model` gave **0 errors, 0 warnings**, where it had 2 FormPatternValidation errors before.
- Every bridge-created object now reports its file.
- `d365fo undo` of the bridge entries, now journaled with a target, still deletes them through the bridge.
