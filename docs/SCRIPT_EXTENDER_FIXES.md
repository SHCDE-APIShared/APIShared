# Script Extender fixes

APIShared corrects selected Script Extender and bundled-dependency defects automatically.
Consumers need no registration; this page describes what changes and which consumer
contracts follow from it.

**Contents**

- [Related replacements](#related-replacements)
- [Scope](#scope)
- [Save archive context](#save-archive-context)
- [Lord media and custom AI slots](#lord-media-and-custom-ai-slots)
- [Lord script diagnostics](#lord-script-diagnostics)
- [Lobby mismatch dialog](#lobby-mismatch-dialog)
- [Popularity display](#popularity-display)
- [UU-ImGUI shutdown](#uu-imgui-shutdown)
- [Interpolation event reuse](#interpolation-event-reuse)
- [Configuration](#configuration)
- [Testing](#testing)


## Related replacements

For explicitly called alternatives, see
[Script Extender replacements](../src/ScriptExtenderReplacements/DOCUMENTATION.md).
That separate public area provides corrected pathfinding-table access with the
SE method signatures; migrate those calls explicitly rather than expecting an
automatic correction of existing SE calls.

## Scope

APIShared includes targeted corrections for Script Extender and bundled dependencies,
implemented in `src/ScriptExtenderFixes`. They work without SerpsMods or a consumer
registration. They do not replace the Extender and do not change existing public
APIShared capability contracts.

## Save archive context

The archive-context correction fixes the shared Script Extender context before mod
callbacks run. Synchronized native saves (including autosaves before the first manual
save) receive `SaveContext.IsSaveFile=true` and `IsMapEditorSave=false`.
Their `FilePath` is null: the Extender's retained path can still name the source map,
and the synchronized Chore does not supply a managed output filename. Consumers must
continue accepting an unavailable path, as specified by the Extender context contract.
Ordinary export saves, map/editor writes and Trail templates retain their original
factory context. Multiplayer-save loads use `bMultiplayerSave`; editor loads use
`LoadingEditorMap`, so their mod-data callbacks also receive the correct classification.
The correction adds no public API or save format. The corrected flags apply to every
handler, including third-party save-only records; use the explicit context scope rather
than assuming autosaves are maps. Multiplayer saves written by APIShared before 0.20.0
can contain only a handler's map record. They load with `IsSaveFile=true`; handlers
that branch on it should accept a map-format record from a save (for example, fall
back to the map interpretation when save-only fields are absent). Unsupported managed/native contracts log a failure and preserve the
original save path; they do not force record writes. See the native update contract
in [UpdateToNewDLL](../UpdateToNewDLL.md#synchronized-save-archive-context).

This preserves the existing modsettings policy: eligible saved host settings form
the selected base, then Trail author rules apply; personal/client values stay current.
A missing or invalid settings record forces the existing current-host fallback and
is reported as such, independently of the user's preferred checkbox state. Records
missing from such saves cannot be recovered.

## Lord media and custom AI slots

The Lord media correction shares APIShared's single process-lifetime managed hook on the internal
resolver `GameAIManagerAPI.TryGetVideoAndAudio` with packaged Lord presentation: packaged claims
answer first, then the correction, then the Script Extender. The hook needs the validated
signature; the correction additionally needs the audited output-initialization, registration
lookup and logging-only miss branch and stays inactive if they differ. Null/empty names and unregistered Lords return false
with empty outputs, quietly retaining the original game media paths. These are normal
for Vanilla Lords using a custom `.lordjson` AIC and for Vanilla-style custom packages
without `lordmeta.json`. A generic native AI slot does not imply custom media.
Registered Lords retain the original lookup, clip selection, subtitle effects, results
and exceptions; an omitted optional message category does not produce a new warning.

After managed mission initialization (including saves and editor starts), a separate
Unity-thread lifecycle observer checks occupied custom AI slots against Vanilla's
public Lord catalog and SE's registry. `SE_LORD_MEDIA` warns only when a used,
unambiguously identified package has `lordmeta.json` but is not registered. This can
happen because Vanilla accepts packages with valid AIC/AIV data independently of SE's
registration: an absent `info.json` prevents registration, and invalid SE metadata can
abort it without removing the Lord from Vanilla's catalog. Warnings identify the
display/internal names, all affected player slots and package folder, distinguish a
missing `info.json` from an otherwise unexplained registration failure, and suggest
checking the package and earlier registration messages. They do not assert missing
media files or successful fallback playback. Each package/cause is reported once per
session; a later map or save is checked afresh. Stale queued sessions are ignored.
Packages without SE metadata remain quiet. Video/audio files are not inspected.

Neither diagnosis nor logging affects the media callback; no file/UI access occurs
there. An incompatible resolver skips only this correction with an explanatory
installation warning and technical contract details. No consumer registration is
needed; this correction does not reconstruct lost Lord identities or recover media.

## Lord script diagnostics

The Lord script diagnostics in `ScriptExtenderFixes/LordScripts` only log. Script
Extender runs a custom Lord's `init.lua` per player slot by the slot's local Lord name,
only while its local `AllowScriptedLords` option is on, and checks only `NetworkMode: 1`
packages when a player joins a lobby. Once per real multiplayer match, APIShared logs
`SE_LORD_SCRIPTS` with every scripted Lord (player, package GUID and version,
NetworkMode, a short `init.lua` hash and the local option) so players' logs can be
compared, and a warning when a scripted Lord's package is client-side or unregistered,
or when scripts are disabled locally. Matches without scripted Lords log nothing.
Nothing is blocked, sent or changed; the Lord author is responsible for NetworkMode 1.

## Lobby mismatch dialog

The lobby mismatch correction in `ScriptExtenderFixes/LobbyMismatch` replaces only
the audited popup call inside SE's `GameNetworkAPI.RejectLobby`. It installs once
at managed startup, survives Unity's startup cleanup, and needs no consumer registration.
The original rejection, logging, exception boundary, Multiplayer overlay and OK callback
are retained. Changed signatures or popup-call structures skip this correction with an
explicit `SE_LOBBY_MISMATCH` warning; no other APIShared service is disabled.

The larger dialog shows at most two differences with bounded names and versions,
identifies the side missing a mod, and always points to the client's
`BepInEx\LogOutput.log`. Additional differences are announced in the dialog and
the complete detected list (names, GUIDs and both version states) is logged once per
rejected attempt, independently of development diagnostics. Diagnostic comparison
uses SE's lobby metadata and asset-over-plugin precedence, case-insensitive GUIDs,
ordinal version matching and client-only exemptions; it never makes the join decision.
Missing/invalid metadata or capture failures are explicitly reported instead of claiming
a complete comparison. The original SE reason remains in the log and is shown when
no complete explanatory comparison is available. The localized title "Different mods"
describes mod-list differences without claiming an incompatibility diagnosis. Text uses
the [APIShared catalogs](THIRD_PARTY_GUIDE.md#apishared-interface-language). Consumer lobby-chat warnings are not affected.

## Popularity display

The separate world-popup correction keeps the campfire's floating mask consistent
with its displayed popularity snapshot: red below 40, neutral from 40 through 59,
and green from 60. Vanilla initializes the mask before installing the new number,
then caches that first mask in a Unity sprite child. APIShared normalizes only the
popularity popup's mask argument before that cache is created. Other floating
icons, digits, simulation state and player identities retain their original paths.
This permanent managed hook validates the loaded public method and its IL argument
mapping; the native thresholds are hash-bound. It requires no consumer registration.

The popularity display correction reads the 41 direct SE popularity modifiers after
successful `GuiChecks` execution during an active mission. It automatically replaces
only contributions whose effective SE value differs from Vanilla, including values
set by third-party mods. It updates report/building text, totals and direction icons;
it never changes simulation data, current popularity, SE stacks or building identity.
No consumer registration, new setting or network packet is needed. Restoring a SE
value to Vanilla automatically removes that correction. In particular, the existing
zero-population Vanilla report discrepancy stays untouched when its SE value is Vanilla.
The separate coverage/overpopulation thresholds are outside this correction's scope.

The implementation in `ScriptExtenderFixes/Popularity` validates the audited native
and managed game fingerprints before registering. Unknown report implementations
are skipped independently. Mission observers and the GUI publisher survive startup
cleanup; ending a mission closes only the logical gate. Snapshots are read from the
current perspective, including paused checks and ration/tax previews. Public text
setters retain Vanilla labels; signed church/cathedral sums never enter the identity
byte. Overflow leaves the original display intact and produces a throttled error.
One `SE_POPULARITY_DISPLAY` confirmation is logged per corrected session.

The independent `tests/PopularityDisplay.Tests` suite covers all 41 modifiers, engine
boundary callbacks and allocation checks. In the warmed .NET test runtime, effective
value capture and calculation allocate no per-call objects; UI text output still
creates strings. This is not a Unity frame-time or multiplayer performance claim.
The original testmod correction was accepted in-game; the transferred implementation
still needs its own game/editor/save-load and real host/client acceptance.

## UU-ImGUI shutdown

The shutdown fix addresses an intermittent Alt+F4 crash in the UU-ImGUI API managed
DLL and its `cimguiaio.dll` bundled with the Script Extender (audited: UU-ImGUI 1.6.7
in SHCDE-SE 2.14.1). Compatibility is determined by the features the fix uses, not
by file hashes or the version label: the eight methods with their signatures and
IL/native-call contracts, loaded/disk module identity for the metadata it reads,
and a loaded `cimguiaio.dll` identical to the one shipped beside the managed DLL.
Unity Mono needs the MethodBuilder backend for these native call signatures: a
permanent managed compiler hook routes only copies of the eight methods, including
internal original backups, through that backend. Before the first UU-ImGUI hook, a
private probe hook proves that the installed MonoMod uses this routing for edited
bodies and backups and that the generated thiscall `calli` keeps every argument and
the result. Other method generation and process-wide MonoMod settings keep their
existing behavior. Binaries without these contracts are skipped with a
`SE_IMGUI_SHUTDOWN_FIX` log reason; other services remain available. Recheck the
shutdown semantics after upstream updates.

## Interpolation event reuse

For visual interpolation, SE creates a new `UnitUnityVisualInterpolateEventArgs` for every
visible unit and frame before raising `UnitR3EventHooks.OnUnitUnityVisualInterpolate`.
APIShared avoids that allocation only for receivers whose handlers provably cannot retain
or forward the arguments. Before the first unknown or retaining receiver actually runs,
the notification is promoted to a fresh object. All subsequent receivers of that notification
receive the same fresh object, preserving callback mutations and allowing it to be retained.
R3 operators and custom observers are treated as unknown. Newly registered receivers are
checked at their actual delivery; short-lived subscriptions cannot bypass this boundary.

The implementation copies the audited R3 delivery loop into a private, non-generic stand-in.
It does not patch `Subject<T>.OnNext`, avoiding effects on other events through Mono's
shared generic code. R3 subscription versions, ordering, disposal and error handling remain
in that loop. No callback runs under an added lock. Nested hook calls and other threads use
fresh arguments. A finally wrapper balances notification state even after an exception;
there is no per-frame recovery callback. Unknown method contracts or existing patches of
the audited dispatch methods decline the optimization and keep SE's original path.

## Configuration

BepInEx writes the settings to `BepInEx/config/APIShared_Serp.cfg`:

    [ScriptExtenderFixes]
    EnableImGuiShutdownFix = true
    EnableInterpolateEventReuse = true

Both fixes default to enabled and take effect after restarting the game.
The existing interpolate setting remains supported; setting it to false retains the original path.
Neither option is a network setting and no live unpatching occurs.

At the first close, menu exit, or quitting notification, a terminal shutdown gate
stops new ImGui work. Subsequent Present/Resize callbacks call the existing native
trampolines directly. Already admitted callbacks may finish; the fix never frees
their contexts. Window messages continue to the saved original WndProc, and its
stored predecessor cannot be replaced with the ImGui hook itself. Context destruction,
native hook removal, and native DLL unloading are suppressed during shutdown;
the operating system reclaims resources at process exit. The existing managed
`OnDestroyed` notification and context clearing in `ProcessExit` are preserved.
Rendering, input and normal menu callbacks before shutdown retain their behavior.

## Testing

Fixes must be tested with controlled competing callbacks and installed-game checks.
An official upstream correction must be inspected before updating support or
retiring a workaround. Do not install a competing patch for the same owned methods.
