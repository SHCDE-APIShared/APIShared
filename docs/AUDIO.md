# Situational audio

This guide is for mod authors using shared sound rules and contributors maintaining
Audio. Consumer settings remain outside APIShared.

- [Public contracts](#public-audio-contracts): registration, context and custom clips.
- [Native source events](#native-source-events): exact-unit attribution and sound replacement before the native record.
- [Integration](#situational-sound-rules): initialization and live controls.
- [Consumer policies](#consumer-filters-repeat-limits-and-other-sound-policies): filters, repeat limits and examples.
- [Internal implementation](#internal-implementation): permanent hooks and clip lifetime.
- [Demand and measurements](#audio-demand-and-dispatch-cost): implementation cost and test limits.

## Public audio contracts

Requires APIShared **0.8.0** or newer; declare this minimum version in both plugin
and manifest dependencies when using these audio contracts.

`APIShared.Audio.SoundEvents.TryRegister(ownerGuid, registrationId, pre, post,
out SoundRegistration handle, out string reason, int order = 0)` registers a
process-owned native-record playback observer. Register after APIShared managed
startup on Unity. Callback order is numeric order, ordinal owner GUID, then ID;
callbacks execute synchronously on the Unity playback thread without replay.
The handle's `SetEnabled` is atomic and affects the next invocation snapshot;
in-flight Pre/Post ownership remains paired. No published hook is disposed.

Pre provides an immutable `SoundContext` and owner-private `State`.
`MultiplyVolume` composes finite nonnegative factors after Vanilla spatial
calculation, without clamping ordinary gain to one. `ReplaceSound` accepts only
validated Vanilla effect-table IDs, **not** Extender registered clip/play-list
IDs. Last valid sound and panorama replacements win. `SetPan` accepts [-1,1].
`SkipOriginalFunction` suppresses only this presentation call and is sticky.
An invalid mutation or callback exception rolls back that callback's mutations,
veto and State; other contributors continue. Arguments freeze after Pre.
Recursion inside Pre/Post bypasses notifications. Original exceptions propagate
and have no Post. Post reports submission or suppression; it cannot prove
actual audible output, since Vanilla throttling, variants and channel policy
still run downstream. Gain zero still submits a call; suppression does not.

### Coverage, situations and evidence

Coverage is the two audited `SFXManager.playSound` sites in
`GameMap.processTestMap` record kind16. UI, speech, music, ambient and arbitrary
direct sound calls are outside this contract. Explicit records expose no source
tile; spatial records expose tile coordinates. No live unit or player ID is
invented. `ArabianIdleSwordDraw` identifies type75/state1/sound180/spatial flag0
through the audited native scope and transported negative marker. The source catalog additionally covers 91 direct soldier calls (military support
included). Attribution uses original native return addresses, not the unit's state
after emission. `UnitType` and `SourceKey` identify the snapshot provenance;
there are no unit/player IDs. Unknown or unsafe origins remain `Unknown`.
`SituationEvidence` is source-specific: `Unknown`, `Inferred`, `NativeAudited`,
`InGameConfirmed`. `SituationConfirmed` is true only for the last value.
The Arabian draw entry is confirmed; the other interpretations are inferred,
including currently unconfirmed Arabian sheathing reachability. Enum values stay
stable when an individual source is confirmed. A named context alone does not
imply in-game confirmation.

### Availability and diagnostics

`PlaybackAvailable`/`PlaybackDiagnostic` report managed availability;
`ArabianIdleContextAvailable`/`ContextDiagnostic` independently report native
context support. Missing native context leaves generic sound rules usable.
Unsupported binaries, occupied entries and changed IL fail closed. Only the
current audited managed/native/backend identities are supported. Callback
failures are counted and logged at bounded intervals. Settings and consumer
policies remain with consumers; no global sound defaults change.

See [audio contracts](../src/Audio/SoundContracts.cs),
[event broker](../src/Audio/SoundEvents.cs) and
[consumer example](../examples/ThirdPartyMod/SoundExample.cs).

`SupportedSituations` reports the currently available contexts, including
Unknown. Native unit-layout checks are separate from managed playback checks.
The shared contract tests live in APISharedTests (`SoundEventsTests` and
`AudioNativeContractTests`).

### Preloaded custom clips

`SoundClips.TryLoadModClip(owner, id, path, completed, out handle, out reason,
baseVolume)` reads the owner's indexed resource through the Extender;
`TryLoadMapClip` reads the current archive without mod fallback.
`TryImportExtenderClip` captures a borrowed `play_list` clip reference once on
Unity. Integer IDs can shift afterward without retargeting that handle. Import
while the intended ID still exists. All operations are deferred through the
Extender's permanent dispatcher. Success means queued, not decoded; check
`IsReady`/`Diagnostic` or the completion callback. Resources must be indexed first.

Handles use ordinal owner/registration identity, separate from native effect IDs.
`TryReplaceClip(handle)` returns false without changes for unavailable handles.
`Target.Kind` and `Target.Clip`/`EffectId` distinguish the final namespace;
Pre/Post `SoundId` is -1 for custom targets. Last valid target wins. Factors,
panorama, suppression and callback rollback compose under the same callback contract. Destroyed clips
fall back to the last valid Vanilla effect (initially the original effect).

OGG and the Extender decoder's PCM16, 44100 Hz mono/stereo WAV are supported.
Custom playback uses computed gain × explicit `BaseVolume`, then the existing
MyAudioManager global SFX/master gains and channel selection. It traverses the
Extender's downstream clip override hook; Vanilla variants and effect throttles
apply only to Vanilla effect targets. Post proves submission, not audibility.
No files or decoder calls occur in sound callbacks.

Owned mod clips remain until process exit. Map clips invalidate synchronously
at unload Pre; destruction is deferred on Unity. An invalid map handle can be
registered again on a later map under the same identity and base volume.
Imported clips are conservatively map-scoped and never destroyed by APIShared.
Load-completion callback failures are isolated and counted separately through
`SoundClips.CallbackFailures`. No per-sound detailed logging is enabled in APIShared.

Map clip lifetime also observes load/save-load/start Pre, because the installed
Extender suppresses unload notifications inside map startup. Register map clips
or imports after OnPostLoad Post; registrations during earlier loading phases
may correctly become invalid at a subsequent startup boundary. All publisher
callbacks isolate errors so the Extender cannot repeat Vanilla on an exception.
Map-lifetime failure closes map clips only and is exposed by MapDiagnostic;
process clips and generic events remain independent.

### Native source events

Not in packages up to 0.9.1; check the package you require.
`APIShared.Audio.SoundSourceEvents.TryRegister(ownerGuid, registrationId, pre,
out SoundRegistration handle, out string reason, int order = 0)` registers a
process-owned observer `void SoundSourceHandler(ref SoundSourceArgs args)` on the
positional wrappers Audio already owns. Registration works from any thread and
before the native context is ready; calls begin once it is. Callbacks run
synchronously on the thread executing the native game tick, inside the wrapper
and before its original, ordered by order, ordinal owner GUID, then ID, without
replay. Do not call Unity APIs or block there. `SetEnabled` affects subsequent
calls. Sound calls made from inside a callback are not published again. A throwing
callback's replacement is rolled back and counted in `CallbackFailures`; later
callbacks still run.

Only validated sources are published; every other wrapper call passes unchanged.

| Sources | Acting unit (`UnitId`, `UnitGlobalId`, `UnitType`) | `TargetUnitId` / `TargetGlobalId` | Validation | Evidence |
| --- | --- | --- | --- | --- |
| The 91 soldier catalog entries (`UnitNN.SourceNN`) | Native current unit | null | Unit type, tile and (0x1700D2) state, as for playback context | Per source, as in the catalog |
| `MeleeKill.Source01`..`04`, situation `MeleeKill` | Attacker argument of the melee damage routine | Victim argument | Attacker type selects this cue in the routine's jump tables; victim health 0; sound at the victim tile (Source04: attacker tile) | `NativeAudited`; needs in-game confirmation |

`SoundSourceArgs` is a value type passed by reference; dispatch allocates nothing.
It exposes `OriginalSoundId`, the current `SoundId`, `TileX`/`TileY`, `Kind`,
`Situation`, `SituationEvidence`, `SituationConfirmed`, `SourceKey` and the identity
members above. Game IDs are one-based UnitAccess IDs and global IDs are
`GameUnit.r_GlobalId`, read at the call: immediate values, not lifetime guarantees.
No player ownership is inferred. `TryReplaceSound(effectId)` selects a Vanilla
effect-table ID in 0..`MaxEffectId` (10000) and returns false otherwise; the last
successful replacement wins. Vanilla checks the table downstream (`playSound`
ignores IDs outside it). Never pass an Extender clip/play-list ID.

The replacement is written into the native record, so Vanilla plays it and
`SoundEvents` sees it with Unknown context: the replaced effect is no longer the
audited source. `MeleeKill` is reported only here; Unity playback records of kill
cues stay Unknown and `SoundContext` still carries no unit ID. `Available` and
`Diagnostic` report the native context; `MeleeKillAvailable` and
`MeleeKillDiagnostic` report kill attribution, which fails independently.

Kill cues per attacker type, from the jump tables at 0x199652 and 0x1992E2:

| Attacker types | Victim-tile cue (Source01; Source02/03 for swords) | After a victim cry | Special victim (Source04, attacker tile) |
| --- | --- | --- | --- |
| 3, 84 | 240 | 242 | 242 |
| 55 | 240 or 241 | 242 | 242 |
| 22, 24, 70, 72, 74, 76, 82 | 118 or 153 | 187 | 187 (not 22) |
| 25, 78 | 240 or 241 | 186 | 186 |
| 26, 85 | 116 or 151 | 185 | 185 |
| 27, 28, 73, 75, 80, 81, 83 | 119 (Source02) or 154 (Source03) | 188 | 188 |

The variant follows bit 0 of the victim's word at GameUnit+0x380. A victim cry is a
cue the routine already played for victim types 17, 21, 55, 56, 57, 63 or
(conditionally) 64. A special victim has the native byte at GameUnit+0x328 set and
gets only the attacker-tile cue; its meaning is not audited. Other attacker types
and every death voice reaching the same CALLs stay unpublished. Hurt and order cues
are not attributed: their native call paths were not audited for this catalog.

`ContextRejections` counts known sources whose unit lookup/type/coordinate/state
checks did not match. The first rejection logs bounded native diagnostics; that
record remains Unknown without disabling other catalog entries. Custom clip
playback passes force=false; Vanilla-specific forced effect channels remain part
of the effect path, while global gains/channel selection remain shared.

## Situational sound rules

Register a stable owner/ID pair through `APIShared.Audio.SoundEvents` after
APIShared managed startup. Keep the returned `SoundRegistration` rooted and
change `SetEnabled` when your setting changes. Filter `args.Context` before
mutating it; use `MultiplyVolume(0.25f)` for a quarter of the spatially computed
gain. `SoundSituation.Unknown` is an explicit absence of audited attribution.
Do not install competing native sound or GameMap hooks.

See the [audio contract](#public-audio-contracts) and
[example](../examples/ThirdPartyMod/SoundExample.cs). For custom audio, preload
through `SoundClips`, retain the handle, and use `TryReplaceClip` in Pre. Keep files
in the mod's private resource root (for example `Sounds/chime.wav`); loading is
deferred and requires the Extender's resource index. Never pass an Extender clip
ID to `ReplaceSound`. A map handle can be rebound on a later map; load it only
after the desired archive is available.

### Exact-unit cue replacement

To change a cue for specific units, register `SoundSourceEvents` once at startup,
keep the handle rooted and match `Situation`, `UnitType` and your own identity set
(for example `UnitGlobalId`, plus `TargetGlobalId` for kill cues), then call
`TryReplaceSound`. Keep the callback short and thread-safe; it runs on the native
game-tick thread. Do not detour the wrappers 0x2960/0x2990 yourself: Audio owns
them exclusively and a second detour makes one of the two refuse to install. See
the [contract](#native-source-events) and the `OnSource` part of the
[example](../examples/ThirdPartyMod/SoundExample.cs).

A named situation may be provisional. Filter `Context.SituationEvidence` or
`SituationConfirmed` if your rule requires accepted gameplay evidence. Use
`SourceKey` plus `UnitType` when acceptance is specific to one source/type.
The Arabian example rule matches its confirmed source. Military
support units are included; worker/animal contexts remain outside this catalog.

### Consumer filters, repeat limits and other sound policies

These are consumer rules built with the existing Audio contracts, not extra
native hooks or hard-coded APIShared settings. Keep their state process-rooted;
use mission start/end/initialization notifications to reset session-local state.
Local presentation preferences do not require synchronized gameplay messages.

| Policy | Inputs and action | Boundary |
| --- | --- | --- |
| Unit-type-specific sounds | Filter nullable `Context.UnitType` together with `Situation`; then change gain, target, pan or suppression. | A type is not a live unit ID or player owner. Reject missing types for a specific-type rule. |
| Exact-unit cues | Register `SoundSourceEvents`; match `Situation`, `UnitType` and a consumer-owned `UnitGlobalId` set; `TryReplaceSound`. | Native thread, attributed sources only; effect IDs, not clips. The Unity-stage context of a replaced call is Unknown. |
| One exact animation/source | Match `SourceKey`, `UnitType`, original effect ID and, if needed, evidence. | The same situation name can describe several sources with different evidence. |
| Confirmed-only rules | Require `SituationConfirmed`, or select an explicit evidence threshold. | An inferred action name does not prove its gameplay meaning; confidence applies to the individual source/type. |
| Repeat/burst reduction | Use a monotonic consumer clock and a shared or keyed interval; veto too-frequent requests with `SkipOriginalFunction`. | Original native creation/RNG remain unchanged. Admission is not audible output. |
| Quiet modes and accessibility | Multiply spatial gain by a user factor, including zero; suppress selected noisy situations or substitute a clearer cue. | Zero still submits playback; suppression skips it. Do not promise dialogue/music coverage. |
| Situation-specific themes | Preload a mod/map clip, retain its handle and select it in Pre; alternatively replace a validated Vanilla effect. | Clip handles and effect IDs are separate namespaces. An unavailable clip leaves the valid Vanilla path available. |
| Alternative clip variants | Select among already loaded handles by an owner-local cycle or RNG. | Avoid changing native/gameplay RNG; no decoding, asset lookup or file IO in Pre/Post. |
| Tile/region policies | Filter available `TileX`/`TileY`, calculate a bounded regional factor, or choose a cue by area. | Coordinates are tiles, not durable unit identity. Missing spatial coordinates must be handled explicitly. |
| Panorama/accessibility overrides | Set finite panorama in [-1,1], for example centered presentation. | Default is Vanilla panorama. This changes balance, not unit position or navigation. |
| Several independent mods | Use stable owner/registration IDs and explicit order; factors multiply and the last valid target/pan replacement wins. | Veto is sticky; each failed callback's argument mutations roll back. |
| Statistics and diagnostics | Observe Post, preserve owner-private `State`, count suppression/submission, original/final targets and evidence. | Post proves submission or suppression, not that channels, throttling or hardware produced an audible sound. |
| Profiles and live controls | Atomically publish validated settings and use registration `SetEnabled`; reset local timers on policy/session changes. | Keep callbacks and subscriptions rooted. Do not toggle/dispose shared hooks. |

For example, the following part of a Pre callback restricts a rule to the
Arabian swordsman's confirmed idle sword draw:

```csharp
if (args.Context.Situation != SoundSituation.ArabianIdleSwordDraw ||
    args.Context.UnitType != 75 || !args.Context.SituationConfirmed)
    return;
args.MultiplyVolume(0.25f);
```

A global repeat window is useful for groups of noisy idle soldiers: the first
matching request is admitted, subsequent requests within the interval are
suppressed, and suppressed requests do not extend the window. A per-type or
per-source window can preserve more variation, but requires bounded consumer
storage. Do not call a tile/source key a per-unit limiter: the context exposes
no durable unit ID (native source events expose identity only for their attributed
sources, before the record exists). If an earlier contributor has already vetoed a request,
leave its veto and avoid consuming your window. A later contributor can still
veto an admitted request. Pre admission therefore measures attempts, not sound
actually heard. Post-based accounting may be used for submitted requests; even
that cannot observe downstream Vanilla throttling/channel rejection.

Use `Stopwatch.GetTimestamp()` and `Stopwatch.Frequency` for real elapsed time.
Document whether pauses count and when changing settings resets the window.
The current test consumer uses one window for all matching situation/type
requests, 0–60000 ms (0 disables it), elapsed time including pauses, and resets
on settings changes and mission start/end/initialization. All unit types and no
repeat limit are the defaults, preserving the Arabian 25% rule. Its filters and
limit operate locally through the same permanent sound-event registration.

Consumer dictionaries, counters, RNG and timers are external side effects;
APIShared's callback rollback restores event arguments, not arbitrary consumer
state. Avoid throwing after reserving a window, isolate diagnostic logging and
bound any state accumulated from coordinates or arbitrary keys. Do not use this
API to schedule new gameplay, replace arbitrary UI/speech/music calls, change
pitch/seek/clip duration, or infer player ownership and live unit state from the
context. Such features need their own audited contracts. Workers and animals
remain outside the named-source catalog; generic effect/volume rules can still
handle Unknown requests intercepted by the audited GameMap playback path.

## Internal implementation

Audio owns one permanent GameMap IL hook, two NativeX64/Absolute wrapper
intermediaries (15 bytes each) and one spatial-emitter detour (14 bytes) with its
own intermediary. Every intermediary first tests a native demand byte.
Original native return addresses
select a hash-bound 91-entry soldier catalog; a negative marker carries only
source/effect provenance through the 21-short records and existing buffers.
No native pointer or late unit lookup crosses into Unity playback. UnitAccess
validates the emitting unit's snapshot type/position before marking; state
changes before a call do not defeat source attribution. Unrelated sources stay
Unknown. Evidence is per source/type; generic playback remains available when
native attribution fails. Hooks, bridges, delegates and publishers are permanent
roots with logical activation only.

Native source events reuse the wrapper path: attribution runs before the original,
callbacks see a stack-only `SoundSourceArgs`, and the final sound ID is forwarded.
With source demand alone the scope ThreadStatics stay untouched; a replaced call
is never marked.

### Melee kill attribution

Kill cues are CALLs to wrapper 0x2960 at return addresses 0x199957, 0x199823,
0x199837 and 0x199319 inside the melee damage routine 0x199110 (rcx unit base,
edx attacker, r8d victim, r9d damage). They are reached only after the victim's
health was written as zero (0x19925C, branch 0x19931E). The current-unit global is
not used: instant-kill callers (0x1AD64, 0x1ADD4, 0x198F84) pass units that need
not be the one whose update runs. Instead the bridge passes the caller's RBP and
entry RSP: the routine writes RBP only by `movsxd rbp,r8d` (0x199124), so RBP is the
victim, and the attacker offset `attacker*0x490` sits in the caller-home slot
[RSP+0x70] written only at 0x1991D5, which is [entry RSP+0x78] at the wrapper.
Script Extender detours the routine's 20-byte prologue for its melee-damage event;
the trampoline keeps the frame layout, so only the body after the prologue
(0x199124..0x19999D) is hash-checked live. A mismatch disables kill attribution and
leaves the soldier catalog running. Attacker and victim are then validated through
UnitAccess, the attacker type against the effect's jump-table group, the victim's
zero health and the sound tile.

SoundClips queues owner-private resources, map archives and borrowed Extender
clip captures on the permanent Extender dispatcher. API handles are independent
of integer effect/play-list IDs. Preloaded custom targets call public playSFX,
preserving global/channel behavior and Extender overrides. No file loading
occurs in the playback path. Map generation changes invalidate handles before
later playback; old owned clip references are detached before deferred Unity
destruction. Mod clips persist and imported clips remain foreign-owned.

### Native bridge validation

Audio source capture owns wrapper ranges 0x2960..0x296F and
0x2990..0x299F plus emitter 0x1A41E0..0x1A41EE. Absolute patch validation
checks displaced bytes, target, entry and trampoline continuation before and
after publication. Each permanently rooted Win64 bridge starts with
`mov rax,demand; cmp byte [rax],0; jne active; mov rax,trampoline; jmp rax`, so a
zero byte reaches the original with untouched argument registers, RSP and return
address (RAX is scratch: neither displaced prologue reads it first). The wrapper's
active path reserves 0x38 aligned stack bytes and passes the original return
address, the entry RSP and the caller's RBP as arguments five to seven; the
emitter's active path tail-jumps into its rooted managed thunk. Validation decodes
both bridges. Copied-code tests execute the bridges and original displaced
prologues with the byte at zero and one; production functions/random choices still
run exactly once. Catalog and kill sites verify CALL rel32 targets.

The concrete hashes, addresses, resolution policy and binary-update checklist
remain in [UpdateToNewDLL](../UpdateToNewDLL.md). General ownership and update
rules are in [native compatibility](NATIVE_COMPATIBILITY.md).

## Audio demand and dispatch cost

Audio alone supplies a cached demand provider to the shared interception broker.
Successful registration attaches its handle to a SoundDemand: one for playback
(`SoundEvents`) and one for native source events. Rare activation and
attachment writes acquire handle -> demand locks in a fixed order. Enabling
publishes the count before the enabled flag; disabling clears the flag before
reducing the count. Readers use a volatile count and never enumerate handles.
An overlapping update may conservatively report demand, but cannot publish a
false zero for an enabled handle. Begin still captures actual active observers,
so such conservative positives cannot notify disabled registrations. Other
services retain dynamically evaluated activation predicates.

The IL playback entry has no captured lambda. Its production guard returns to
Vanilla before context creation and uses a value-type playback sink thereafter.
Callback-backed tests enter the same dispatch core. Internal target reads use
the clip reference directly; the public immutable SoundTarget behavior is kept.
Audio args hold one typed rollback checkpoint while each Pre callback runs and
release its saved clip on Freeze. The broker allocates owner-State storage only
when a Pre has a matching Accepted/Post receiver. Static factories receive
explicit context rather than capturing invocation objects. Public args and
results are not pooled and can still be retained by observers.

Permanent native bridges remain installed without audio demand, but a dormant call
no longer enters managed code. SoundNativeDemand keeps two bytes in process-lifetime
unmanaged memory: the wrapper byte is native context ready AND (playback OR source
demand); the emitter byte is ready AND playback demand. Every transition that can
change them is owned and republishes both under one lock: a count change in either
SoundDemand (registration attach, `SetEnabled`) and `SoundContextPolicy.SetReady`.
Enabling raises the byte before the handle's enabled flag; disabling clears the flag
first, so no enabled handle meets a zero byte. Behind the guard the managed
callbacks still check demand before touching ThreadStatic scope state. One-time
confirmation markers first read their state and only the first contender needs an
atomic write.

### Audio dispatch measurements

Measured on the local 64-bit .NET Framework 4.8.1 test host, Release compilation,
with 2000 warm-up calls and 20000 measured calls per sample. Context construction
is outside the matrix; the original playback is a static no-op, callbacks only
multiply by one or observe. Timing is indicative and uses the median of five
samples; scheduling/GC vary. Allocation assertions
are strict for dormant paths; timing has no pass/fail threshold or FPS implication.

| Registrations / callbacks | Bytes per call | ns per call |
| --- | ---: | ---: |
| 0 | 0 | 27.3 |
| 1 / Pre | 144 | 184.0 |
| 1 / Post | 232 | 216.1 |
| 1 / Pre + Post | 264 | 257.6 |
| 16 / Pre | 144 | 721.0 |
| 16 / Post | 1552 | 857.4 |
| 16 / Pre + Post | 1704 | 1447.7 |
| 16 / all disabled | 0 | 66.2 |

The matrix measures the general registry scenarios. Separate tests clone the
actual compiled IL entry and production guard, replace only Unity's original
sound call with a recorder, and
retain the confirmation path and real audio demand cache. Both no active demand
and 16 disabled handles allocate 0 bytes per call after warm-up (about 39/44 ns
in this run). The production guard has no allocation opcode before its original
return; the outer IL callback has none either. Unity/Mono rendering, real native
transition cost, asset decoding and hardware output are outside these measurements.

### Native bridge measurements

Production hooks (`SoundRuntime.CreateHooks`: real bridges, demand bytes,
OnWrapper/OnEmit, RedBird trampolines) on private executable copies of the real
wrapper 0x2960 and emitter 0x1A41E0, with the manager LEA and emitter CALL
relocated. A native loop calls the copied wrapper 20000 times per sample, one
managed transition per sample; each call appends exactly one record. 21 samples per
mode, modes alternated within a run, three runs alternated with the develop
baseline 49e60dc (same harness with a test-only hook-construction seam). The value
is the minimum over all samples; medians were within 20 % of it. Both sides allocate
0 bytes per call in every mode. 64-bit .NET Framework 4.8.1, not Unity Mono.

| Wrapper call (wrapper + emitter) | develop ns | branch ns |
| --- | ---: | ---: |
| Unhooked copy (reference) | 2.96 | 2.88 |
| No active registration | 144.65 | 4.39 |
| Source events only, unattributed origin | n/a | 128.95 |
| Playback events, unattributed origin | 206.22 | 226.23 |

Dormant cost drops from two managed transitions per call to one byte test and two
jumps (+1.5 ns over the unhooked copy). With playback demand the branch is about
20 ns slower per call: two more bridge arguments, the source-demand read and the
emitter guard. Source dispatch itself, measured separately with stack-only args and
a replacing callback, is 0 bytes and about 67 ns for one and 177 ns for 16
registrations. Attributed calls (UnitAccess lookups, catalog checks) need live
units and are not measured off-game; nor is Mono's transition cost.

RedBird's thread-suspending patch destabilises the desktop CLR test host (a later
GC fails with an internal runtime error, reproduced with one private detour), so
private-copy tests pass `suspendThreads: false` to `NativeContract.Backend`. No
other thread can execute those copies. Production hooks keep the default.
