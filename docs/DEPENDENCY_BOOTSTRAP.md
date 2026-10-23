# Early dependency-index correction

The transferred APIShared package passed the targeted Windows/Unity-Mono acceptance
on 2026-10-17 with Script Extender 2.14.2: the active correction preserved three
genuine control errors while resolving the eligible nested dependency. A second
start with only the orphaned bootstrap installed no hooks and preserved all four
original errors. Menu/startup and post-cleanup evidence were recorded for both runs.
These results cover the tested binaries, not subsequent source changes or other
platforms. Ordinary release readiness requirements still apply.

## Purpose and compatibility

BepInEx discovers nested plugin DLLs before Script Extender checks its direct-child
asset inventory. A selected nested plugin may therefore be reported as missing.
APIShared's ordinary Awake runs too late to correct this check. The independent
`APIShared.DependencyBootstrap.dll` companion supplies the earlier entry point.

No public capability, consumer registration or changed dependency declaration is
required. Existing APIShared public types and methods remain unchanged. The new
behavior only adds eligible missing providers to a separate local check dictionary.
It never overwrites original entries, registers assets, reorders plugins or filters
the original compatibility result. An unknown BepInEx binary or failed SE contract validation preserves
the original check. Later successful plugin Awake is not guaranteed.

## Package and activation

The fixed paths are `BepInEx/plugins/APIShared_Serp/APIShared.dll`,
`BepInEx/patchers/APIShared.DependencyBootstrap.dll`, and
`BepInEx/plugins/APIShared_Serp/dependency-bootstrap-pair.json`.
The schema-1 manifest binds GUID, version and both SHA-256 hashes. It is generated
after both DLLs and inventoried within the existing plugin-relative inventory.
Only the exact additional bootstrap path is accepted by APIShared ZIP/map validators;
general consumer packages retain their existing protected-root restrictions.
Historical packages without the companion and pair manifest remain readable.

A complete pair and an audited loader are required. The actual selected APIShared
winner must match the runtime path, version and hash. The initially read manifest
is hash-bound too. Ownership and the complete pair are rechecked at selection and
immediately before publishing the local index. A failed check revokes correction
without removing any published hook. Duplicate/renamed own
bootstraps and the known dependency diagnostic probe block hook installation.
Unrelated patchers are not removed. Unknown or unreadable patcher metadata refuses
this optional correction rather than guessing ownership.

Create `BepInEx/config/APIShared.DependencyBootstrap.disabled` before starting to
disable the companion. The switch is sampled once in Finish. Configuration changes
take effect on a complete restart. No hook is undone during Unity cleanup.

## Initialization and diagnostics

Finish only attaches a static listener and initializes diagnostics. Runtime type
resolution, pair validation and hook preparation start at the synchronous BepInEx
`Chainloader ready` message. Neither Initialize nor Start is called manually, and
no BepInEx initialization flag is changed. Both transformed method copies are
compiled and JIT-prepared before their hooks are installed, without executing them.

The chain observer captures selected winners and known failures. If the selected
APIShared pair is rejected, a published observer stays rooted but cannot install
the corrective SE hook. All callbacks and diagnostics contain their own failures.
Only genuine warnings and errors are written to the normal BepInEx log, with
millisecond timestamps. Successful startup, selected inventories, phase markers
and compatibility-result dumps are silent. No separate trace file or optional
verbose mode is provided. Internal package/index checks and exception containment
remain active; desktop delegate generation never substitutes for Mono-JIT evidence.

## Development and manual removal

Build the current working tree with `build.bat /workspace /nopause /noinstall`. Outputs and build evidence remain inside
this checkout. The build checks the actual runtime pair contract and package/removal
tools. Development use is permitted by user selection; runtime and lifecycle
acceptance remain separate from a successful build.

An orphaned bootstrap may remain in the patcher directory after plugin removal.
At the later ready event, an absent APIShared plugin directory causes a silent
return before runtime resolution, pair reads, method transformation or hooks.
BepInEx still loads the bootstrap and its diagnostic listener. Access errors,
linked paths or a file occupying the directory are not treated as removal.
If the directory exists, a missing runtime DLL or manifest still rejects activation
with a diagnostic; the original loader and dependency check continue unchanged.
A mixed/reinstalled partner must pass the complete pair and
selected-winner checks on a fresh process before correction can activate.
The user's accepted removal policy is to retain this inert file or remove it
manually. No parallel deletion worker is required.

Automatic update/unsubscribe cleanup and self-repair are intentionally absent.
A sequential partial update leaves correction inactive; equal versions do not
guarantee that Script Extender will reinstall an incomplete package. Repair requires
a complete verified package and a closed game. Do not deploy the new bootstrap
alongside the active diagnostic probe for an acceptance run.

`tools/Remove-DependencyBootstrap.ps1 -GameDir <game> -PackageRoot <verified-package>`
previews removal of exactly the bootstrap DLL. Only `-Apply` performs deletion;
`-WhatIf` is supported. The complete known package provides the expected hash.
External management, a running target game, links, changed bytes, competing local
pair manifests or pending SE staging refuse deletion. The file is rechecked just
before deletion. Runtime DLL, pair manifest and unrelated patchers remain untouched.
This manual tool does not infer Steam subscription ownership and is not an automatic
unsubscribe handler; the operator must review ownership before applying it.

## Source ownership

The standalone project is `bootstrap/APIShared.DependencyBootstrap.csproj`, outside
`src` and its runtime compile glob. Within `bootstrap/src`, `EarlyEntry` owns Finish
and the one-shot ready listener; `DependencyCorrection` owns provider evaluation,
local index replacement and their permanent hook owner; `Package` owns the fixed
pair contract and activation decisions; `Diagnostics` owns hashes and bounded trace output. Hooks stay beside
the correction they implement. Folder moves do not change assembly/type identity.

Packaging helpers remain beside the existing Workshop/build helpers in `tools`;
validation drivers remain in `tools/Validation`. The dedicated test project compiles
the same source files. Source fingerprints and permanent-hook checks traverse the
bootstrap source folders recursively and exclude generated build outputs.

## Tests and remaining acceptance

`tools/Validation/Test-DependencyBootstrap.ps1` compiles the actual bootstrap sources
into an isolated desktop executable: early type boundaries, listener behavior,
real DMD transformations, stack/control-flow preservation, negative contracts,
inventory rules, original SE comparisons and partial-copy pair checks.
`Test-DependencyBootstrapPackage.ps1` exercises actual ZIP and manual-removal code
against retained local fixtures; Core.Tests exercises the actual map validator.
No test invokes Chainloader.Start, installs runtime hooks, or starts Steam/the game.

The transferred assembly's Windows installation, active Unity-Mono startup and
orphan behavior passed the targeted acceptance above. Interrupted-copy behavior
is covered by offline pair tests; a real APIShared update is not thereby certified.
Automatic bootstrap removal is optional housekeeping, not a prerequisite for dependency correction.
Linux/Proton requires separate validation. Existing public consumer compatibility checks remain
required; this feature does not grant broader filesystem rights to consumer mods.

## Probe evidence and transfer limits

The independent probe passed a real Windows Extender A-to-B package replacement
on 2026-10-17, including both executable partners, pair binding and restart.
Both subsequent original version checks reported the package current. The synthetic
Workshop-list adapter and its trial settings are not part of APIShared.
This evidence supports the transfer design; it does not certify these different
assemblies, automatic removal or Steam/Proton deployment. APIShared's separate
active/orphan startup acceptance is described above.

Native helper DLLs in patcher packages are distinguished by their PE/CLR header
before managed assembly metadata is inspected. Invalid/unreadable images and
renamed competing bootstraps still reject activation. The bootstrap creates no diagnostic files.
