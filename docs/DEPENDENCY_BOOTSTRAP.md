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

This pair-gated companion does not repair a failed initial Script Extender deployment of APIShared.
The separate [Linux first-install patcher](WORKSHOP.md#linux-paths-and-first-install-repair)
works without APIShared and has no dependency on this companion or its pair manifest.

The dependency-index correction itself requires no consumer registration or changed dependency declaration. It only adds eligible missing providers to a separate local check dictionary. The optional early-plugin observer below shares the existing loader boundary independently of this SE correction.
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
disable the companion. New Workshop installations wait until the companion is active
again. Recovery of an already interrupted installation still runs before plugins can
load. The switch is sampled once in Finish. Configuration changes
take effect on a complete restart. No hook is undone during Unity cleanup.

## Initialization and diagnostics

Finish attaches a static listener and initializes diagnostics. Before that, Finish runs
the [Workshop installation pass](WORKSHOP.md#installation-preloader-swap), which
recovers interrupted swaps and applies staged Workshop installations while nothing in
`BepInEx/plugins` is open yet. The pass holds a process-lifetime lease in the game
directory. A second instance of that installation stops before mod discovery. Staged
changes require active mode and an existing APIShared plugin directory. A recovery
conflict stops the launch before it can load an incomplete installation. The
`APIShared.WorkshopInstall` log source reports outcomes, along with the
`_APIShared/I/result.txt` file consumed by the APIShared plugin. Runtime type
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
pair contract and activation decisions; `Diagnostics` owns hashes and bounded trace output; `Workshop`
(`bootstrap/src/Workshop/WorkshopInstallPass.cs`) owns the Workshop installation pass and its
log source and compiles the linked `src/Workshop/Install/WorkshopSafeInstall.cs` from the
runtime tree unchanged. Hooks stay beside
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
against a local fixture that it removes after each run, on success and on failure;
Core.Tests exercises the actual map validator.
No test invokes Chainloader.Start, installs runtime hooks, or starts Steam/the game.
The Workshop installation pass is covered by the installation-core tests listed under
[Workshop internal implementation](WORKSHOP.md#interrupted-update-regression).

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
renamed competing bootstraps still reject activation. The bootstrap creates no diagnostic files;
the Workshop installation result file is installation state, not tracing.


## Early plugin activation observers

The public `APIShared.DependencyBootstrap.EarlyPluginActivation` contract lives in
`APIShared.DependencyBootstrap.dll`, not `APIShared.dll`. A consumer preloader
companion calls `TryRegister(ownerGuid, id, targetGuid, callback, out registration,
out reason, order)` in its `Finish()`. Reference the bootstrap assembly with
`Private=false`; ship neither an APIShared copy nor a second loader hook. Keep
consumer runtime correction code deferred until the callback; Finish must not
initialize Unity, MonoMod, native hooks or plugin instances. A complete matching
bootstrap/runtime pair and the supported loader contract are prerequisites.

Registration is process-lifetime and closes at the selected-winner snapshot,
including when no consumer was registered. Accepted means Pending, not ready.
The returned object exposes the explicit state/reason: unselected owner/target,
previous owner failure, unavailable loader/pair, skipped target, completed callback
or isolated callback failure. There is no late registration, replay, cancellation,
retry or veto. Startup completion retires targets that never reached AddComponent.
A callback returning does not prove the target's subsequent Awake succeeded.

The existing APIShared Chainloader hook dispatches synchronously after the real
loader selected and loaded the target assembly and immediately before its ordinary
AddComponent(Type). Observers run in ascending order, then ordinal owner GUID and
registration ID. They execute outside registry locks. Exceptions and even failed
diagnostics cannot escape to the loader or prevent the next observer. The legacy
SE/dependency-shadow callback still runs afterwards. No new native hook is involved.

Context contains immutable selected owner/target GUID, plugin version, absolute
assembly path, entry type and selected inventory, plus the actual loaded Type,
assembly full name and module MVID. Target type and loaded path must match the
selected metadata. These facts identify the selected plugin assembly; they do not
certify every package asset or guarantee a selected owner will later load. An owner
already in the loader's failure set is refused. Consumers remain responsible for
validating correction-specific IL/ABI/native contracts and dependency readiness.

An unknown loader, missing or disabled companion/runtime, invalid pair, missed
selection, or late registration leaves the observer unavailable. Do not repair an
already published native hook or retry from ordinary plugin Awake. AssemblyLoad
alone is not equivalent: an already loaded assembly need not produce another event,
and that event precedes the loader's exact PluginInfos assignment. The service uses
the existing selected-plugin AddComponent boundary instead.

Source-only staging and desktop tests do not prove Unity-Mono early compilation,
startup ordering or game behavior. Those require explicit integrated startup tests
with the same selected package pair before release. No consumer-specific correction
or mod identifier belongs in this service.


### Deferred declarations in a normal consumer DLL

`EarlyPluginActivationProviderAttribute` is an additive alternative to a separate
consumer preloader companion. Put the assembly attribute in the normal consumer
DLL, referencing `APIShared.DependencyBootstrap.dll` with `Private=false`:

```csharp
[assembly: APIShared.DependencyBootstrap.EarlyPluginActivationProvider(
    "Example.Consumer", "early-observer", "Example.Target",
    "Example.EarlyProvider", "BeforeTarget", order: 0)]

namespace Example
{
    internal static class EarlyProvider
    {
        public static void BeforeTarget(
            APIShared.DependencyBootstrap.EarlyPluginActivationContext context)
        {
            // Validate feature-specific managed/native contracts here.
            // No Unity objects, plugin initialization, or future-Awake assumptions.
        }
    }
}
```

The owner GUID must name an actual selected plugin entry **in this same DLL**.
A host cannot lend its selected identity to an unselected payload in another DLL.
Several selected plugin entries may share a DLL; declaration ownership remains
explicit. Target GUID must also be selected. Names are CLR full type/method names,
not assembly-qualified strings. Provider must be declared on that type as a public,
static, nongeneric method returning void and accepting exactly the public context.
The type must be nongeneric and its actual Module must be the inspected manifest
DLL, with matching path and MVID; linked netmodules cannot supply provider code.
The inspected manifest DLL containing a module initializer is refused. Consumer static initializers and the complete provider call graph remain
the consumer's responsibility: keep them early-safe and independently audit them.

Discovery reads Cecil metadata only from actual Chainloader winner paths. It does
not load consumers, enumerate plugin directories or execute custom attributes.
No consumer is eagerly loaded ahead of the target boundary. Declarations share
owner-local IDs and deterministic ordering with existing preloader registrations.
An existing preloader registration wins an ID conflict. Same-DLL owner validation
precedes duplicate grouping: an invalid declaration in another DLL cannot poison a
legitimate owner-local ID. Duplicate declarations from that legitimate owner DLL
are all rejected. Existing TryRegister signatures, deadline,
callback ordering, failure isolation and lifetime remain unchanged.

At the existing BeforePlugin target boundary, the wrapper verifies the selected
owner's complete hard-dependency closure, inclusive minimum versions, absence of
hard cycles and currently known loader failures. Selection is required; plugin
Awake or already-loaded assemblies are **not** required. This deliberately permits
a target that precedes the owner's dependencies in the normal startup order.
It does not promise that the owner or any dependency will later Awake successfully.
A soft dependency remains optional; the target is selected separately.

The wrapper checks selected module metadata and then calls Assembly.LoadFile on
the absolute selected owner path. Actual location, assembly identity and MVID must
match discovery. It binds/prepares the static delegate, checks again and invokes
once outside registry locks. For names relevant to the owner/target and their selected hard closures, each
actually loaded assembly must match a complete selected path/identity/MVID record.
Different selected GUIDs may legitimately use the same simple name at different
versions/paths; these are matched independently. Unrelated loaded modules do not
globally gate this provider. Unknown wrong-path/MVID loads for relevant names remain
refused, and the actual owner LoadFile result is pinned to its own record. Future
unloaded dependencies remain unloaded by the validator. Normal resolver policy is retained: no global resolver is installed.
It checks loaded identities again after callback completion. An arbitrary consumer
can trigger further loads inside its own body; these checks cannot retroactively
undo a consumer's effects. They do not preauthorize those loads or prove how arbitrary future/transitive
assembly references will resolve. Early providers
must avoid such unvalidated side effects and validate their own early call graph.
MVID is a module identity, not a cryptographic package or IL-integrity certificate.

Rejected metadata/binding yields Unavailable for that declaration; an exception in
consumer code yields CallbackFailed. Diagnostics never format foreign exceptions
and cannot stop the next observer or original AddComponent. Missing/duplicate
metadata is logged and grants no callback. Declarations have no public registration
object: a consumer needing readiness should retain its own successful callback flag
and leave its feature inactive if the callback was absent. Never repair a missed
early boundary by patching an already published native hook from ordinary Awake.
The declaration does not guarantee Unity/plugin initialization or enable a feature.

The production-source bootstrap fixture emits small independent managed consumer
assets with Cecil and exercises actual discovery, the existing broker, LoadFile and
delegate binding. It covers selection/closure/version/failure, identity changes,
invalid declarations/signatures/module initializers, ordering/conflicts, once-only
execution and isolated errors. The test driver removes assets after the executable
exits because Framework may retain file locks. Desktop tests do not replace real
Unity-Mono startup/provider coexistence acceptance for the exact delivered pair.
