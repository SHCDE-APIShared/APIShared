# Early dependency-index correction (development)

This optional companion is under development. The transferred APIShared build has
not yet passed a Unity-Mono startup or a real package update/removal test. The
isolated probe previously demonstrated the algorithm on one audited installation;
that is not acceptance of this assembly. Releases and installation through the
development build driver are blocked until those checks are complete.

## Purpose and compatibility

BepInEx discovers nested plugin DLLs before Script Extender checks its direct-child
asset inventory. A selected nested plugin may therefore be reported as missing.
APIShared's ordinary Awake runs too late to correct this check. The independent
`APIShared.DependencyBootstrap.dll` companion supplies the earlier entry point.

No public capability, consumer registration or changed dependency declaration is
required. Existing APIShared public types and methods remain unchanged. The new
behavior only adds eligible missing providers to a separate local check dictionary.
It never overwrites original entries, registers assets, reorders plugins or filters
the original compatibility result. Unknown binaries and failed validation preserve
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
winner must match the runtime path, version and hash. Duplicate/renamed own
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
The standalone trace is `BepInEx/APIShared.DependencyBootstrap.trace.log` with
millisecond timestamps and `ASDB` markers. `PAIR_VERIFIED`, `PAIR_SELECTED`,
`CORRECTION_APPLIED`, `ORIGINAL_RESULT` and `CHAIN_COMPLETE` distinguish acceptance
phases; desktop delegate generation never substitutes for Mono-JIT evidence.

## Development and manual removal

Build with `build.bat /nopause /noinstall`. Outputs and build evidence remain inside
this checkout. The build checks the actual runtime pair contract and package/removal
tools. The release gate `bootstrap/DEVELOPMENT-ONLY` is itself a build input; removing
it requires reviewed runtime and lifecycle evidence, not merely another build.

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
pair contract; `Diagnostics` owns hashes and bounded trace output. Hooks stay beside
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

Before release: independently test the transferred assembly in Unity Mono, menu and
post-cleanup behavior; implement and audit automatic ownership-aware post-exit
cleanup; test Windows installation, interrupted updates and removal. Linux/Proton
requires separate validation. Existing public consumer compatibility checks remain
required; this feature does not grant broader filesystem rights to consumer mods.
