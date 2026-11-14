# Native binary update contracts

**Contents**

- [Shared health initialization](#shared-health-initialization)
- [Authenticated Chore context and submission](#authenticated-chore-context-and-submission)
- [Spectator input control boundaries](#spectator-input-control-boundaries)
- [Player-view identity: common save writer and raw Chore submission](#player-view-identity-common-save-writer-and-raw-chore-submission)
- [Synchronized-save archive context](#synchronized-save-archive-context)
- [Native field knowledge](#native-field-knowledge)
- [AI market sell policies](#ai-market-sell-policies)
- [Fixes wheat-sale startup correction](#fixes-wheat-sale-startup-correction)
- [Pathfinding table replacements](#pathfinding-table-replacements)
- [Group-move exclusion mark](#group-move-exclusion-mark)
- [Audio](#audio)
- [Explicit gate-routing backend](#explicit-gate-routing-backend)
- [Assassin reconstruction connection rule](#assassin-reconstruction-connection-rule)
- [Registered native sites](#registered-native-sites)
- [Workshop staging cleanup](#workshop-staging-cleanup)
- [Rounded fear-popularity SE adapter](#rounded-fear-popularity-se-adapter)
- [Plague standing heights](#plague-standing-heights)
- [Shared visible-map entry and pause-menu actions](#shared-visible-map-entry-and-pause-menu-actions)
- [Managed Script Extender and game targets of Lord presentation and move diagnostics](#managed-script-extender-and-game-targets-of-lord-presentation-and-move-diagnostics)
- [Enemy-unit proximity and Peace Time](#enemy-unit-proximity-and-peace-time)
- [Native unit-pair exclusion rules](#native-unit-pair-exclusion-rules)
- [Huntable prey reservation word](#huntable-prey-reservation-word)
- [Hunter native sites](#hunter-native-sites)
- [Hunter owner gates](#hunter-owner-gates)
- [Line of sight](#line-of-sight)

## Shared health initialization

Owner: `Health.HealthNativeObserver` and `Health.HealthRuntime`; public lifetime
contract: [shared HP](docs/HEALTH.md). Reference native SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.

The common unit type initializer at RVA `0x19A240` has ABI
`void(void* unitManager, int gameId, int chimpType)` (RCX/EDX/R8D).
Reference prologue, 26 bytes:
`48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 56 48 83 EC 20`.
The first two five-byte instructions store RBX/RBP in caller shadow space.
Only the productive RedBird NativeX64 Indirect scheme is allowed: ten displaced
bytes, continuation at +10. Validate full reference/live bytes, scheme, target,
chain depth, FF25 pointer, NOP padding, relocated instructions, trampoline return
and untouched continuation through `NativeIndirectEntry`. The private backend
fixture exercises this contract; it does not modify game memory. Full feature
audit found no incoming targets inside the displaced prefix, and local Extender
and Fixes sources do not own this entry. The native-site registry reserves this
prefix and observes the remaining prologue before publication.

The original always runs exactly once. Its callback only queues a verified game/
global/type identity, never applies HP. Lord creation at `0xC23C0` applies profile
HP after this initializer returns; completed simulation ticks therefore process
queued units. Recruitment/worker/disband paths can reuse both slot and GlobalId,
including the same type, so every observed initialization starts a fresh generation.
Save-loaded generations must be imported before issuing a new serial. Existing
building-spawn Post events similarly queue buildings until outer integration.
The startup-prepared transaction and callback remain rooted for process lifetime;
published hooks are logically dormant when unsupported or without enabled demand
and without current-mission history.

An available observer also remains logically demanded while the current mission
owns health history, even if every policy handle is disabled. A cached flag keeps
archive lookups out of the native observer guard. These notifications preserve
generation provenance only: completed dormant objects are recorded without HP
writes or policy evaluation. The save callback also completes ready dormant
provenance before an early resave. Unfinished native initialization and reciprocal
tower links retain their normal wait conditions. A dormant or `None` unit retires
an old applied proxy target while preserving both original bases; generations
already completed under an active policy retain their frozen target. A proxy
completed while all policies are disabled is never rewritten retroactively.

Required interop layouts are hash-bound and checked before publication:
GameUnit stride `0x490`, current/max `0x3C4/0x3C8`, percentage `0x2D0`, blocks
`0x34`; GameBuilding stride `0x32C`, signed current/unsigned max `0x10C/0x10E`.
Siege tower unit type58 and proxy building69 use reciprocal ID/global pairs at
unit `0x334/0x364` and building `0x110/0x114`. Proxy deployment at `0x15ECE0`
may happen much later. Both native damage directions (`0x7EB00`, `0x15ECE0`)
multiply unit maximum and building health in signed Int32; retain their arithmetic
and jointly bound the pair, including future proxy creation. Unit HP-percentage
readers use signed `current * 100`; building repair at `0xD5030` multiplies current
HP by positive wood/stone costs. These establish the public limits and repair
cost-dependent bound, rather than the storage types alone.

Load normalizer `0x19C300` can replace healthy unit HP with type/owner defaults;
new-map normalizer `0x197EF0` does likewise. Saved original bases, including Lord
profiles and Vanilla Enemy HP, must survive these resets. Building spawn at
`0xB47E0` uses type-table health at VA `0x1802E7510`, without Enemy HP. Existing
full-health building normalizers `0xCA110/0xCA5F0` are distinct from repair.
Do not infer bases by dividing current health or periodically enforce targets.

On an updated DLL audit all callers/writers, outer completion boundaries, load
normalizers, cache readers, both tower damage directions and repair arithmetic.
Recheck actual installed Extender field widths/offsets and hook ownership. Unknown
hashes or conflicting entries fail only this service closed. Real new-game,
save-loading, spectator, editor exclusion and matching host/client acceptance
remain necessary beyond the static and private-memory fixtures.

## Authenticated Chore context and submission

Owner: `Networking.NativeChoreContext` / `NativeChoreDispatch` / `NativeChoreTransport`. Reference native
SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
No native hook is installed; SE's opcode-106 handler stays in place.

The ChoreManager base is supplied by SE's public resolved `ChoreManagerVA`, must
equal module `+0x8574320` for the reference image and must fit the complete accessed
range inside the loaded image. The exact base is independently confirmed by
enqueue, receive and execution; an in-range but different resolver value is rejected. Offsets are
current-slot `0x84CC8`, phase `0x84CCC`, slot base `0xB0BF8`, stride `0x500`,
executing source `0x36FA4C`. Slot tick/source/opcode/command ID are at 0/4/8/12.
Only phase 0, opcode 106, slot 0..499 and identical slot/execution sources 1..8
are admitted, with a second consistency read. Neither a packet's PlayerId nor
the current native view establishes issuer identity. Native `0x1F9A0` is
`mov eax,edx; ret` in the reference image; roster remapping occurs in managed
multiplayer before `ReceiveChore`.

Native field reads are bound to SE's loaded
`DispatchReceivedScriptExtenderPayload(byte[]) -> void` by a permanent managed
hook. Its loaded IL must decode the two-byte packet ID at offset zero, allocate
and copy precisely the remaining body from offset two, and call `HandleRawPacket`
once with an absent Steam sender; unknown helpers/mutations fail initialization.
Before typed deserialization, verify the blob against native slot parameters
`+17` (four-byte length) and `+21` (blob), with manager payload size `+0x84CD4`
and transfer cursor `+0x370BF8` both equal to length plus four. Scope the captured
issuer/slot/command/tick and packet ID on the dispatch thread, restoring outer
scopes in `finally`; invalid nested calls must not inherit them. Receivers require
the exact packet ID and unchanged native tuple before and after consumer validation.
Do not invoke consumer activation/payload/authority predicates before this context
is verified; rejected Steam/worker deliveries must not run game-state callbacks.
Unrelated registered packet IDs avoid native reads and keep SE's dispatch order and
behavior. No native patch is installed, and the managed hook is never torn down.

Raw submission binds SE's installed `SendScriptExtenderChorePayload(byte[]) -> bool`
and checks its loaded IL calls: one native queue delegate invocation, engine
monitor Enter/Exit, and only the audited manager/log/string/pointer helpers.
The lock object must be loaded once from `EngineInterface.threadLock`, retained
in the same local for Enter and Exit, and the queue must lie in its protected
try/finally. The taken flag cannot escape or be reassigned after initialization.
The bound native queue must still target module `+0x23990`. Compare the SE queue
delegate with a reference of the same delegate type created from that audited
address using `Marshal.GetDelegateForFunctionPointer`; neither delegate is invoked.
Do not compare `GetFunctionPointerForDelegate(queue)` with the native address:
the installed Unity/Mono runtime returns a marshaling-wrapper address even for
a correctly bound native delegate. Same-target delegate equality accepts the
native binding and rejects another target, managed replacements and multicast
delegates. The staging byte
field and private engine monitor are validated reflection bindings, not direct
publicized-assembly accesses. Recheck their mutable monitor/queue bindings before
each send. The outer wrapper holds that same monitor while
checking real local identity against the native enqueue view and clears staging
in `finally`. It never calls the public Steam-fallback sender. Regular Chore channels do not use
this path.

Before enqueue, inspect the next available native slot under that same monitor.
`0x20950` starts after the cursor at manager `+0x370BF4`, wraps over 500 entries
and accepts state zero or signed-byte state >=10 at slot `+9`. After 500 failed
checks it still returns an active slot, which `0x23990` overwrites. The authenticated
sender therefore refuses a full queue. This bounded event-time check grants no
global peer capacity reservation: a protocol on top of the channel needs its own bounded
in-flight native-packet window, including peer acknowledgements and other traffic.
Do not burst a complete logical reassembly budget into Vanilla's 500 slots.

There is no unknown-image layout fallback: source attribution needs the entire
receive/schedule/dispatch chain, and a matching function prologue cannot establish
these data offsets. Unsupported images or changed SE method contracts disable
only authenticated delivery/submission and report an independent initialization
failure. Re-audit `0x23F10`, `0x1F7E0`, `0x1F9A0`, `0x23990`, the field helper
`0x1F5F0`, native/managed receive and SE's opcode-106 pack/unpack/dispatch before
admitting another image. This is a transport prerequisite, not control permission.

## Spectator input control boundaries

Owner: `PlayerControl.ControlInputNative` (installer) with `ControlInputBridge`
(stubs), registry group `Players.ControlInput`; the actor cell is written only by
`LocalInputControl` through `PlayerPerspectiveAPI.TrySetSpectatorControl`. Reference
native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
Startup only validates the pristine blocks; both boundaries are installed together on the
first `PlayerPerspectiveAPI.TryRegisterSpectatorControl` (inside the LibraryLoaded startup
window without thread suspension, otherwise only outside a running mission with
suspension). Hash-bound; any other image skips the feature (one Info line) and leaves
Vanilla unchanged. Without a registered consumer nothing is installed. Actor zero
follows both complete original blocks. This grants full Vanilla input of the chosen
player; it is not a rights/permission service.

Vanilla zeroes the native view for a spectator at two places before map input:

1. Within `DLL_RunTick` at `0x86680`, PE unwind end `0x86F72`, span
   `[0x868F6,0x86900)` contains `41 0F 45 C4` (CMOVNE EAX,R12D) and
   `89 05 70 D4 85 08` (MOV [RIP+disp32],EAX). The destination is native view
   RVA `0x88E3D70`. Incoming EAX/EBX hold the view, R12D is zero, and flags come
   from the spectator comparison at `0x868DC`; the intervening MOVs do not alter
   them. Continuation `0x86900` is `45 0F B6 C6` (MOVZX R8D,R14B). The full
   current-image function's direct branches/calls and baseline xrefs have no entry
   inside the ten-byte span. RunTick restores EBX at `0x86927` after the call to the
   input dispatcher `0x8C5F0`.
2. Inside the dispatcher `0x8C5F0`, a nested re-dispatch (taken when the input state
   `[0x1860AD56C]` was nonzero before Vanilla's deselection `0x897F0`) loads
   EBX=[view] at `0x8D10B`, compares the spectator flag at `0x8D115` and executes
   `0F 45 C7` (CMOVNE EAX,EDI; EDI=0 since `0x8D0C8`), `45 33 C9` (XOR R9D,R9D) and
   `89 05 3C 6C 85 08` (MOV [view],EAX at `0x8D12E`) before the recursive call
   `0x8D15B`; `0x8D16D` restores EBX. NativeX64 Indirect displaces exactly the six
   bytes `[0x8D128,0x8D12E)`; the unchanged store then writes the stub's EAX. Within
   the whole dispatcher no direct branch targets `0x8D129..0x8D133`; the flags are
   dead after the span (XOR R8D,R8D at `0x8D134` precedes the call). No SE or Fixes
   source references either span.

These boundaries are not a whole foreign-command audit. Managed building placement
and demolition (`EditorDirector` -> `PlaceMapperItem`/`DeleteBuilding`) pass
`EditorDirector.ActivePlayerID`, which `DLL_MapAction` temporarily writes as the view;
they do not use these boundaries.

The intermediaries preserve flags, R11 and stack; they use no managed callback or
SIMD scratch. Zero demand, or a clear spectator comparison (ZF set, preserved from
`0x868DC`/`0x8D115`), follows the complete relocated original, so a stale actor can never
change a non-spectator's input. For a spectator, nonzero demand requires an actor in 1..8
equal to EBX; any other value, including suspended -1, yields zero for the input scope. The dispatcher form also performs the displaced
XOR R9D,R9D on its own branches. UI selection or a packet cannot grant input merely
by setting a view. Switching players first runs Vanilla's deselection (GameAction
`CloseTroopsPanel` -> `0x897F0`: local marking clear `0x186C50`, queued opcode-15
unselect via `0x19A5B0`/`0x23990` with the current view as source) while the old
view is current. Whether that path releases every tribe reservation (`+0x2A`) is
statically unproven; it is identical to an ordinary Vanilla deselection.
The stubs load the actor cell once per decision, so a concurrent change cannot mix
two actor states; every actor write also holds the engine monitor, which serializes
`DLL_RunTick`. A suspended actor (-1) writes view 0 in the blocked branch.

For NativeX64 Indirect/10 and Indirect/6, validate both prepared and installed forms,
pointer slot, padding and generated stub destinations. The relocated RunTick MOV's
bytes necessarily change: decode its operand and require the same native destination.
Require the exact CMOVNE forms, the XOR R9D form and a trampoline jump to +10/+6; the
dispatcher continuation must decode as the native-view store. A separately owned E9 or
FF25 hook starting exactly at the continuation is allowed; the pristine continuation is
still checked. Isolated executable-page fixtures exercise the real backend/factory
(dispatcher: the production options), relocated store, flags/stack/R9 and both branches.
No game hook is installed by these fixtures.

No unknown-image fallback is established: these short CMOV/MOV/XOR blocks cannot
identify the surrounding spectator/input lifetime, view storage or later command
effects. Updates must re-audit both spans, the dispatcher's nested branch conditions,
`0x897F0` and the view cell before admitting another image.

## Player-view identity: common save writer and raw Chore submission

Owner: `PlayerViewIdentityRuntime` (src/Players/ViewIdentity) with `PlayerViewSaveScope`, registry
group `Players.ViewIdentity`. Reference native SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`. Both adapters are installed
during the existing startup window and protect the original participant identity while an
eliminated participant shows a foreign view. Unsupported hashes or an unavailable writer/send
protection disable only foreign eliminated-participant views; original-spectator views and
spectator input control do not depend on them. The local view cell must match the
Extender-resolved address at module RVA `0x88E3D70` (`NativeViewCell.Rva`) and remain inside
the image. Usage and the lease model: [player views and control](docs/PLAYER_CONTROL.md#implementation).

**Common save writer.** The writer at `0x52B0` has Win64 signature
`ulong (nativeManager, sectionTable, byte mapSave)`. Its three current callers
are `0x13030`, `0x816B0` and `0x86F80`. The table at RVA `0x336B20`, row 103,
serializes four bytes from the native view; a foreign view must not replace the
original participant's saved identity. The only admitted detour is NativeX64 Indirect/10,
replacing `48 89 5C 24 10 48 89 74 24 18`, two five-byte register-save MOV instructions.
Continuation `0x52BA` starts `55 57` (push rbp/rdi). The complete PE unwind range
ends at `0x5AD8`; direct branches/calls and current xrefs have no interior entry.
Validate pristine/live bytes, prepared span and original target before publication;
after publication verify the indirect pointer slot, padding, displaced bytes and
exact `+10` trampoline return. SE's later append hook at `0x5A4C` stays intact.
Installation is permitted only in the startup patch window, using its non-suspending
backend; there is no lazy or late installation.

**Raw Chore submission.** The process-lifetime managed raw-send hook targets the installed
internal static `GameNetworkAPI.SendScriptExtenderChorePayload(byte[]) : bool`, resolved and
validated by `NativeChoreTransport.ResolveSubmissionMethod` (loaded-IL, engine monitor and
native-queue contract). The authenticated transport enters the identity scope before its
raw view/actual-player check; the raw hook covers direct Extender Chore sends too. No native
queue hook is installed. Re-audit the entire queue `0x23990`: slot source `+4` and command ID
`+12` use the local view before serialization `0x23800`, and the immediate branch may
synchronously execute a handler. Protect the full original submission rather than rewriting
a header afterwards. Preserve an already-queued result even if restoration fails; protection
rejection must not return the false result that SE uses to select its Steam fallback.

**Scopes and ownership.** Both adapters serialize the lease check and original operation with
the same engine monitor, including unbound calls; known Vanilla writer callers already hold
that monitor, and the boundary prevents a concurrent lease acquisition from bypassing
protection. Unbound original callbacks retain the scope depth, preventing reentrant view
acquisition before a Save or immediate Chore execution has completed. The guard changes only
the runtime-owned native view cell during the scope, restores it in `finally`, and suppresses
stale restoration after a lease release or world replacement, including replacement inside a
throwing setter before the writer runs. Lease transitions recheck the world after adapter
reads. The central lifecycle's private native-world identity survives metadata refreshes but
is invalidated before load/unload observers; repeated loads get new identities even within
one attempt. Failed restoration retains the identity protection and blocks the dependent view
until recovery. Stale captured failures must neither fault a replacement owner nor reset its
deduplication state; the common writer's error has one coordinated drain. Dormant Save/send
fixtures verify the monitor boundary and zero managed allocations after warm-up.

**Native network identity reads.** The same lease also supplies one process-lifetime
participant data cell to four pure-native read substitutions. These do not enter managed
code or temporarily change the report viewpoint. All sites are prepared and validated
before publishing the group, and all must be available before admitting a foreign
eliminated-participant view. The admitted backend is NativeX64 `Indirect` (minimum six
bytes), with these complete single-instruction displaced spans:

| Purpose | RVA / exclusive end | Original instruction | First unchanged continuation |
|---|---|---|---|
| Timing participant | `0x869D2` / `0x869D8` | `8B 15 98 D3 85 08`, `mov edx,[rip+nativeView]` | `49 8B CE`, `mov rcx,r14` |
| Heartbeat progress | `0x27FEB` / `0x27FF2` | `48 63 8B 50 FA 36 00`, `movsxd rcx,[rbx+0x36FA50]` | `8B 05 60 DF 63 03`, `mov eax,[rip+tick]` |
| Heartbeat acknowledgement | `0x27FFF` / `0x28006` | `48 63 8B 50 FA 36 00`, `movsxd rcx,[rbx+0x36FA50]` | `8B 05 4C DF 63 03`, `mov eax,[rip+tick]` |
| Heartbeat timestamp | `0x28019` / `0x28020` | `48 63 8B 50 FA 36 00`, `movsxd rcx,[rbx+0x36FA50]` | `89 84 8B 78 D0 14 00`, `mov [rbx+rcx*4+0x14D078],eax` |

`DLL_RunTick` (`0x86680`) forwards the timing participant through `0x24E80` to
`0x1F9B0`, where local progress is compared with the registered humans' progress. The
heartbeat producer (`0x27F30`) writes self progress, acknowledgement and time at manager
`+0x14D030/+0x14D054/+0x14D078` indexed by the participant. Its manager `+0x36FA50`
aliases native view RVA `0x88E3D70`; reading a report viewpoint here incorrectly changes
the self index. The `DAT_188575204` gate is connection pause, written by
`DLL_ConnectionPause` (`0x81360`), not the spectator or defeat flag. Preserve this gate,
manager role flag `+0x9A0`, declared local identity `+0x8A8`, participant registration,
received-peer indexes and authenticated sending unchanged.

Validate every pristine instruction and continuation, the timing RIP target, prepared
and installed spans, indirect entry and exact trampoline continuation. Full surrounding
function branches and current xrefs have no entry inside these four displaced spans.
The dormant cell-zero branch executes the original relocated read; active values retain
MOV's EDX zero-extension or MOVSXD's RCX result semantics. Save/restore flags and the
scratch register on every branch; neither branch may allocate, call managed code, write
the native view or change other registers, SIMD state or stack position. Publish/clear
the data cell only with the exact current lease under the engine monitor, and retain
all published code/data until process exit. Unsupported hashes have no fallback: re-audit
timing, heartbeat, transport/role identity and native-view aliases before admitting another
image. Executable private-memory fixtures cover the four entries and continuations;
they do not establish live host/client pacing acceptance.

No unknown-image fallback: the generic writer prologue cannot establish writer identity,
section-table semantics, argument ABI or native viewpoint storage. Re-audit the full
writer/section flow and callers, the preparation helpers `0xB8220`/`0x186040`, archive
append, native-world/lifecycle invalidation, positive local identity, nested callbacks and
Vanilla load/remapping before admitting a new image. These fixtures do not establish
live-game Save acceptance.

## Synchronized-save archive context

Owner: `ScriptExtenderFixes.SynchronizedSaveRuntime`, registry group
`Savegames.SynchronizedSaveContext`. Native reference SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
The Chore 39 handler at RVA `0x13030` is a no-argument, void Win64 function;
both its serialize and execute phases are enclosed in a thread-local scope with
`finally` cleanup. Only the execute phase calls the synchronous writer `0x52B0`.
The factory observes that scope at SE's existing archive append hook (`0x5A4C`);
APIShared does not patch or claim that existing SE site.

The only admitted backend is NativeX64 `Indirect`, six displaced bytes:
`48 89 5C 24 08` (`mov [rsp+8],rbx`, five bytes), `57` (`push rdi`, one byte).
Resume at `0x13036`: `mov eax,0x1D40`, then `call 0x1A6990` (`chkstk`) and
`sub rsp,rax`. The 19-byte pristine prefix and live entry/continuation are checked;
prepared and installed hooks validate scheme, span, target, pointer slot, relocated
bytes and trampoline continuation. Full 1102-byte body branch/call and xref audit
found no incoming target inside `(0x13030,0x13036)`. Local SE, Fixes and APIShared
site inventories contain no competing entry owner. Never undo a published hook.

No unknown-image fallback is admitted: the whole Chore phase/protocol and synchronous
writer relationship were audited for this image, not established by a prologue
pattern alone. Unknown hashes fail this correction closed and retain original
saving; their save-only data may still be omitted. Re-audit scheduler `0x1F6E0`,
Chore dispatch/phase, `0x13030`, all `0x52B0` callers (`0x13030`, `0x816B0` Trail
template export, `0x86F80` DLL_SaveSaveGame) and SE's append/factory contracts before
adding an image. In particular `0x52B0` argument 3 being zero does not prove a
gameplay save, since Trail templates use that value too.

The three managed transformations validate the loaded IL before publication:
one `SaveContext(bool,bool,string)` constructor before mod-data dispatch in
`PrepareMapArchiveAndGetBytes`, one Boolean field assignment before archive load
in each of `OnLoadMap` and `OnLoadSave`. The latter use the public event-argument
getters; no private game member is accessed. All transformations are generated in
a dry run first. Partial publication stays rooted; the native scope is enabled
only after the complete managed group is ready. Tests execute the actual backend
and the transformed factory/SE dispatch with real appended ZIP records. Unity
timer/metadata serialization and real multiplayer execution need game acceptance.

## Native field knowledge

Owner: `src/NativeKnowledge`. This area installs no hook and resolves no executable
target. The captured field bindings belong exclusively to native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
Offsets, scalar widths, record extents and evidence are authoritative in
`NativeFieldCatalog.Data.cs`; the [field reference](src/NativeKnowledge/FELDKATALOG.md)
explains each identity. Only bindings explicitly marked read-verified are admitted.

The existing hash initialization publishes one immutable process reader. Unknown
hashes remain unknown and yield `UnsupportedImage`; hash failure leaves acquisition
unavailable. There is no layout/pattern fallback: no independently established
semantic migration anchors are provided for these bindings. Future image support
requires a separate full field/root/stride/width/ID-origin audit; add a hash binding
without silently changing a published identity. Extender header/source names alone,
recovered layouts and historical similarity never authorize migration.

Tribe target slices +0x622/+0x626 deliberately do not follow named source-field
boundaries. Their native stores +0x64C/+0x650 derive through the audited +0x2A
origin; original machine sites are RVA 0x11FC2C/0x11FC38. Revalidate those stores,
paired identity consumers and full record extent when adding support. No private
managed member or executable-memory mutation is involved. Caller-supplied spans
still require valid object identity, owning thread and immediate lifetime.

The weapon-workshop pair uses two signed 16-bit words in the `0x32C` building
record. Current goods `nkf-000093` sit at public `GameBuilding+0x28E` (native
displacement `0x2EA`), and next goods `nkf-000094` at public `+0x290` (native
displacement `0x2EC`). The native formula adds `buildingId * 0x32C` to the
manager base before the displacement. The public view includes the additional
`0x5C` header. `BuildingProductionFieldLayout` compares the exact hash, field
encoding and compiled Extender layout during workshop initialization. Revalidate
the single and bulk writers `0xD5870` and `0xC52B0`, the allocator `0xB47E0`, and
the copy and delivery consumers in `0x12D230`, `0x13AAD0` and `0x13BDD0` before
adding another binding. The catalog grants raw reads. A workshop writer also
requires its own validated identity and simulation contract.

## AI market sell policies

Owner: `Economy.AiMarketSell`, `AiMarketSellNativeRuntime` and
`AiMarketSellAssembly`. Reference SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
Unknown hash, changed layout/backend, occupied bytes, ambiguous pattern or wrong
displacement refuses the entire service. No late installation retry. No AIC edits.

| Site/data | Audited contract and update check |
| --- | --- |
| 0x3F1F3..0x3F202 | 15 bytes `488D0516D06203448B8C2890F01200`: LEA RAX manager, MOV R9D total stock. Replay once before callbacks/floor. EDX final reserve, R8 player*0x160F, RDI good, RBP player*0x583C+good*4. Next `443BCA7F16` compares signed stock and reserve. |
| 0x29723..0x29732 | 15 bytes `8BD7488D2DE42A6403418BF0488BCD`: EDX=EDI, RBP manager, ESI=R8D, RCX=RBP. Input EDI one-based player, R8D good, EBX/R9D amount after negative rejection. Set EBX and R9D together; replay once on continuation. |
| 0x29732 | `E859540A00` price call 0xCEB90 before credits 0x2974D/58/5F and removal 0xB7D80. |
| 0x2978B | Empty-sale exit `488B6C2440488B5C2448488B7424504883C4305FC3`; complete prologue has already run. No pricing, credits, removal or presentation on this exit. |
| 0x379B2A0 + player*0x583C + good*4 | Signed aggregate stock. GamePlayerResources stride 0x583C, null/ale/gold offsets 0x4D0/508/50C; eGoods null=0, ale=14, gold=15, Count=25. Gold base 0x379B2DC is harness evidence, not a new production write. |

At the reference hash check exact site bytes first. On mismatch scan executable PE
sections for the exact fingerprint and reject duplicates; a moved match cannot
establish relocated data/layouts, so remains refused. Validate comparison, price
call and epilogue separately. Re-audit the full 0x3EE10 decision loop, scheduler
0x2AE40/controller 0x3F270, .lordjson/AIC copy 0x87350, table 0x2CEC90 (20 entries,
eight categories) and the complete 0x29700 booking/removal chain on updates.
Check whole-function branch targets, all xrefs and external entries for BOTH entire
15-byte spans; no interior entry is allowed. Adjacent owners: Fixes 0x3F0C9..DC,
stone reserve 0x3F156..16A, price 0xCEB90 and diagnosis 0x29700..09 remain separate.

Actual RedBird X64InlineHook probes and prepared/published hooks must displace
exactly 15 bytes. Backend/Iced identities are checked in the runtime; update only
after inspecting new backend behavior (minimum 14, whole instructions, FF25/0
pointer jump and final NOP). Native state is aligned int32 demand 1/2/4/8. Safety
paths preserve flags/scratch state; reverse callbacks additionally preserve volatile
GP/XMM0..5 and shadow space. Re-run the productive machine harness, including
recursive consumption before the final stock re-read and inactive Vanilla parity.

Publication uses APIShared's shared root/latch before any Enable attempt. A partial
write or failed post-validation retains all code/data and zero demand until process
exit. Never dispose, retry, release site ownership or free reachable state afterward.
Only unpublished failures clean up. Tests and public limits:
[AI market sell policies](docs/AI_MARKET_SELL.md).

## Fixes wheat-sale startup correction

**2026-10-26 contract extension:** the supported division-based emitter receives
one bounded in-place prefix replacement during the same verified startup window.
The full prefix must match a freshly assembled contract; its replacement loads
the player from the derived stack home, bounds the flag index and retains ECX's
category result. Existing allocation, native jump, displaced instructions and
continuation retain their addresses. Observed Fixes 1.29.1: 61-byte prefix,
94-byte complete stub, corrected body 47 bytes plus 14 NOPs. These lengths are
observations; fitting and full pre/post validation are mandatory. No version or
hash bypass, late retry or rollback is introduced. Legacy one-byte repair remains
supported. Existing native execution and memory/error tests cover both forms.
The historical identities and exact layouts below remain historical evidence;
see the current [contract](docs/FIXES_COMPATIBILITY.md#native-validation-and-write).

Owner: internal `Compatibility/FixesWheatStartup`; foreign hook ownership stays
with Fixes. See [startup safety, behavior and test limits](docs/FIXES_COMPATIBILITY.md).

**Since 2026-10-22 none of the hashes, versions, RVAs or byte templates below is a
runtime condition.** Site, seller frame and player displacement are derived from the
actual code ([derived contract](docs/FIXES_COMPATIBILITY.md#native-validation-and-write));
the values below are the audited installation and serve as test observations
(`FixesWheatAnalysisTests`). After a game update, check that the derivation still
succeeds; a refusal names the violated rule. The pre-2026-10-22 text follows unchanged.

| Audited binary | SHA-256 |
| --- | --- |
| CrusaderDE.dll | FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2 |
| Assembly-CSharp.dll | BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789 |
| UnityEngine.CoreModule.dll | E468E19789D608B41E2C7FE4F367C7082FECE7D1883F568E75870F6C5384CC86 |
| fixes.dll (1.26.4.0) | 6A4ED4F492812BDA9342766AC432954F483732CC864C4CAAD67F9172A4F9E5BA |
| fixes.dll (1.26.5.0) | 47D77AC11585423AA2B054873C11EC3AF888CF92E955A0BA376F24BA1B7ECE2A |
| BepInEx.dll | 8255B28902886085C578B9E427D3073C97002DB85176D2090CDEDA90EF14CE70 |
| BepInEx.Preloader.dll | 55D3895351A9D16B63B6F35F1C01B44AC650979E853D0BD3A442B92A082AF64F |
| SHCDESE.dll | E5D78FDF2EA9701336C398F7D29410F206D141FC01AA8099F30C824E6AB8A0B3 |
| SHCDESE.dll (2.14.2.0) | 577F9AAB5DE504BE0BFA2193C7DEB734B26076ECBBAF0A408FC9ABC4CC5E7175 |
| RedBird.Abstractions.dll | 339CECE116F66F59ECC7DA5E9A5C0B2DFE24F4E7A5E7EFC005864FA77C027A1E |
| RedBird.Core.dll | EE4B036C486077F7D8898AE054F124EDDE73E6AE1A059E8991C361E2B720D29F |
| RedBird.X64.dll | E085B83B2B87B5D2142C980090D8D9EC616A90A443BD125C17B41E0BB47A44EB |
| Iced.dll | CC915D1C2E7BE60933C5B129842CA3D08D1FE3CDA6C6D6ADE8824DCDDE16191F |
| MonoMod.RuntimeDetour.dll | 40E49BB314391CD7BDDC2644F8553EEBA92C194B940836B103DF16955C464E0C |
| MonoMod.Utils.dll | 9D1495F147AC93C4F81F84538C1A326E8F8A6AEFC78D6289D798F3CE1162C5E9 |

Fixes/SHCDESE rows record audited installations, not runtime version/hash allowlists.
Their full loaded/disk assembly identities and MVIDs must agree. The native
game rows remain pinned to the audited machine contracts. The RedBird and Iced
rows record the audited installation only: the AI market sell and plague-height hooks
prove the backend contract at run time (displaced span, generated stub bytes, jump form)
instead of pinning those files. The MonoMod rows likewise record the audited
installation only: the UU-ImGUI shutdown fix proves at run time, with a private
ILHook probe, that edited bodies and original backups use its scoped compiler and
that a thiscall calli keeps every argument. Unsupported hook bytes and startup
evidence are always refused.

The 2026-10-17 installed-binary recheck records the two updated identities above.
Fixes 1.26.5 still emits the same
wrong `rsp+0x50` player load; the installed RedBird/Iced, BepInEx, preloader,
Unity and game files retain their audited hashes. The integration fixture invokes
a private copy of the actual installed Fixes emitter with a test flag-array address
and compares all emitted bytes; it never constructs the Fixes runtime or installs
a foreign hook. Live validation still verifies the entire 75-byte stub and return.

Unity may byte-load Assembly-CSharp and UnityEngine.CoreModule with empty
Assembly.Location values. The wheat guard and popularity display resolve the
relevant installed images from BepInEx's
GameRootPath. SHA-256, full assembly identity and loaded/disk MVID must agree;
hashing and metadata inspection share one read-only file handle. Missing or
different files remain unsupported; an empty Location alone is not a failure.

MonoMod loader-body contract: ILHook._Map[originalStart] must contain a Context
whose Method is originalStart and whose Detour is valid/applied with the same
Method. Its exact Target, normalized through GetIdentifiable's pinned-handle map,
may identify the current frame. No DMD name/prefix is trusted. Registry mismatch,
multiple loader frames or wrong surrounding Awake/Application ordering refuse
the correction. Recheck this private read-only lookup when updating MonoMod;
it never installs, refreshes, disposes or undoes a hook.

Seller 0x3EE10 begins `89 54 24 10 48 89 4C 24 08 48 83 EC 58`:
player EDX saved at entry RSP+0x10, then stack allocation 0x58. Fixes pushes RDX
and R8, making the correct load displacement 0x10+0x58+8+8 = 0x78.
The incorrect 0x50 refers to saved caller RDI, not a player argument.
The native scheduler 0x2AE40 passes the profile in RCX and player in EDX to
0x3F270; that controller's market/alternation branches tail-jump to purchase
0x3EC90 or seller 0x3EE10. The seller iterates goods, and the wheat hook precedes
reserve evaluation and the actual sale at 0x29700. Non-wheat and disabled-flag
paths retain the original category. Enabled wheat uses category 6 (Resource).

Fixes site 0x3F0C9 displaces 19 bytes, returning to 0x3F0DC:
`45 8B 9A C4 00 00 00 49 8D 04 38 48 8D 2C 85 00 00 00 00`.
The installed entry must be `FF 25 00 00 00 00 <stub u64>` plus five NOPs.
Resolve the stub from the actual `Fixes.Detours.FixesAIDetours` static
`c_game_ai_sell_item_handler_hook` (HookHandle<X64InlineHook>), not by scanning.
Its `_originalBytesBackup`, resolved/target address, installed state and
DisplacedByteCount must agree. `wheatSaleCategoryFlagArray` must be a
NativeFlagArray of length 9; its Address must match the stub operand.

Full stub layout (hex offsets):

| Offset | Required instruction/data |
| --- | --- |
| 00 / 01 | push rdx / push r8 |
| 03 / 07 | cmp rdi,9 / jne +27 |
| 09 | mov edx,[rsp+50] or already-correct [rsp+78]; only byte +0C may change |
| 0D / 0F | push r8 / mov r8,flagArrayAddress (u64 at +11) |
| 19 / 1E / 20 | cmp byte ptr [r8+rdx],0 / pop r8 / je +27 |
| 22 | mov ecx,6 |
| 27 / 29 | pop r8 / pop rdx |
| 2A..3C | complete original 19-byte instruction sequence |
| 3D / 43 | FF 25 00 00 00 00 / absolute continuation u64; total 75 bytes |

Scope/search/fallback: exact known module hash plus the exact foreign object and
complete live bytes; no pattern search, migrated RVA or unknown backend fallback.
The live Vanilla prolog is also checked, so a foreign seller-entry detour is
refused. Missing hook with intact original entry needs no correction. Conflicting
flags/handles, failed/prepared hooks or altered return bytes are refused.

Update checklist: audit BepInEx soft-dependency sorting, AddComponent/Awake and
_loaded publication; preloader Application..cctor injection; engine private lock
and all tick/load/editor paths (editor calls are not universally locked); Fixes
immediate LibraryLoaded installation and process-lived native state; RedBird
handle, backup, stub generation and entry padding; complete seller/flag dataflow.
Then update identities, source/byte/behavior tests and real backend/game tests
together. Do not whitelist a newer version solely by name. No builds or gameplay
acceptance have been performed for this implementation.

## Pathfinding table replacements

Native SHA-256: FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2.
Profiles: RVA 0x322540, 90 Int32 entries. Permissions: classes 1..6 start at
RVA 0x32BDB0 and occupy 540 Int32 entries; each row is 0x168 bytes / 90 entries.
Class-zero table base is 0x32BC48. Native reader 0x181E00 loads the unit's signed
16-bit type at manager + gameId*0x490 + 0x6E6 and uses `(class*0x5A + type)*4`.
Direct profile readers 0x18E1E0, 0x196280 and 0x198ED0 use type-indexed Int32s.
Their symbol names remain candidate-level; these address calculations were
confirmed in the complete native bodies and disassembly. The former two feed
movement eligibility/search; the latter branches on the active unit's profile.

SE 2.14.1 commit 5908e1f12437deb7ef5f5b1dc2d64e301178fba3 uses enum count 89
as storage length and row stride. Keep the 89-value enum contract distinct from
90-slot native storage. Re-audit complete readers, table extents, SE public
address resolution and enum values on updates. No mutable canonical-value test
may reject legitimate changes from foreign mods during initialization.
The replacement installs no hook and performs no initialization writes.
See [replacement contracts and migration](src/ScriptExtenderReplacements/DOCUMENTATION.md).

Concrete native reference and resolution contracts belong here. General update
policy is in [native compatibility](docs/NATIVE_COMPATIBILITY.md); consumer and
implementation details are in the relevant feature guide.

## Group-move exclusion mark

Native SHA-256: FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2 (audit 2026-11-09).
`Commands.GroupMoveExclusion` writes the 16-bit field at `GameUnit+0x428` (Script Extender
`N000000D8`; raw slot address `0x1867E8E84 + id*0x490`). It installs no hook and resolves no RVA.

- **Reader:** only the group move `0x11B520`. Each of its three member loops admits a member only
  when this field is 0, next to AliveState 2, death marker `+0x29C` 0 and the other admission
  tests. A skipped member keeps its order and AI state; the function still returns 1.
- **Writers:** the complete Ghidra Xref export lists 20 references to the field, all in
  `0x11B520` (read), `0x11DD10` and `0x1245B0` (AI order routines: 0, then 1 by their own criteria,
  for every living member right before their order) and `0x126020` (0 for every member with
  AliveState 2 and `+0x29C == 0`). The decompilation shows no pointer-relative access to it.
  Callers of the writers are AI routines only; `0x3D840` and `0x3E070` call `0x126020` immediately
  before their plain `0x11B520`. Tribe create/assign/unassign (`0x119C00`, `0x11D370`, `0x123EA0`),
  the player move (`0x196100`) and the unit spawn path (`0x17FEF0`, `0x19A240`) do not write it.
- **Helper:** writes 0 for one unit passing `UnitAccess.IsReallyAlive` (the predicate of `0x126020`).
  `Initialize` requires the audited hash and checks `GameUnit` size `0x490`, `N000000D8` UInt16 at
  `0x428`, `r_AliveState` (Int16 enum, `IsAlive` = 2) at `0x88` and `r_IsKilledByProjectile` UInt16
  at `0x29C`; any difference leaves it unavailable without a write.

On a native update, recheck the field's Xref set, the three member loops of `0x11B520`, the AI
writers and Vanilla's clear-before-move callers, then the layout above. The field has no second
semantic anchor, so the helper stays hash-bound. Usage and multiplayer rules are in the
[integration guide](docs/THIRD_PARTY_GUIDE.md#ordering-units-taken-from-ai-groups).

## Audio

The audio implementation lives under src/Audio; this is a native-update checklist,
not a consumer setting. Native provenance and fixed catalog/field offsets remain
hash-bound; a matching prologue alone cannot authorize a new game binary.

Native SHA-256: FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2.
Managed SHA-256: BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789.
NativeX64 SHA-256 (audited installation, not a runtime pin): 0843DD4C381A3E77DD6D8B51D5CCF95465B3FB49BFDF2980F39A212D774AADB0.
The runtime requires the loaded RedBird NativeX64 backend 1.5.0 or newer and then
validates the prepared and published Absolute hooks (displacement, trampoline
prologue, continuation, patch form and bridge code) before readiness.
NativeContract holds the exact executable-section patterns and prologue bytes.
Targets: spatial wrapper 0x2960, flagged wrapper 0x2990, emitter 0x1A41E0.
Resolve the reference RVA first; if its pattern no longer matches, attempt a unique
executable-section fallback even on a matching hash. Log the method and actual
RVA. A moved target cannot be accepted against the old catalog/global layout;
context stays inactive. This is deliberate because original caller return PCs,
current-unit global 0x9302C4, native slot/stride and record contracts have no
independent proven cross-version semantic fallback.

Wrappers displace 15 bytes (4+3+8); continuation moves R8D to R9D. Emitter
displaces 14 (4+3+4+3); continuation writes X. Only NativeX64 Absolute is allowed.
Validate actual backend Scheme, target, displacement, zero PointerSlot, hook entry,
FF25 absolute patch, padding, copied prologue and trampoline continuation before
and after publication. Each rooted intermediary first tests its native demand byte
and tail-jumps to the trampoline when it is zero; RAX must stay unread by both
displaced prologues. The wrapper intermediary then reserves 0x38 aligned Win64
stack bytes, forwards the entry RSP (pointing at the original return PC) as
argument five and spills the caller RBP at entry RSP-0x10; the emitter
intermediary tail-jumps to its managed thunk.
Execute both on copied full wrappers/emitter with the byte at zero and one after
every backend change. No published hook may be reverted.

Melee kill cues are bound to the melee damage routine 0x199110: re-derive the
frame-setup hash 0x199124..0x1991D9 (from the end of the 20-byte prologue through the
attacker spill), the victim RBP write (`movsxd rbp,r8d`, 0x199124) and the only
attacker spill (`mov [rsp+0x70],r10`, 0x1991D5, read as entry RSP+0x78 at the
wrapper), the four CALL sites (returns 0x199957, 0x199823, 0x199837, 0x199319), the
zero-health branch and the attacker-type jump tables at 0x199AD4/0x199AF0 and
0x1999A0/0x1999B8 against SoundSources.Kills. Recheck that Script Extender's melee
patches (entry detour, context hook at 0x199444) and other installed hooks stay
outside the pinned range and the CALL sites, and that their trampolines keep RBP,
RSP and [rsp+0x70]. A mismatch disables kill attribution only.

Audit all 91 source calls, allowed dynamic effect selections, type and coordinate
origin, pre-emission state changes, incoming edges, pending copies, all six pools
and slot reuse. Negative markers encode source/effect only; unknown sources stay
Unknown. Check GameUnit game IDs (1-based), manager+0x65C+id*0x490,
public slot1 manager+0xAEC, type+0x8A, state+0x2BC and X/Y+0xC0/+0xC2.
Revalidate the exact real processTestMap IL and public playSound/playSFX members,
private effect/clip table reflection and Extender decoder/resource publishers.
Update evidence per source/type; do not promote all units from one hearing test.

Map clips invalidate at unload/load/save-load/start Pre and should register after
OnPostLoad Post. Extender intentionally suppresses unload during native startup.
Callbacks must isolate exceptions to avoid its fallback repeating Vanilla. Imported
clips are borrowed references, not retained integer IDs. Custom playback uses
public playSFX with force=false, retaining global gains and channel selection;
Vanilla-specific forced effect channels/variants/throttles stay in the effect path.

Recheck Script Extender and the canonical Fixes clone for entry overlap and managed
playSFX overrides. Current inspected SE 2.14.1 and Fixes 1.25.1 do not own these
three native entries. Run standalone validations, all suites and consumer checks,
then game acceptance for new clip audibility, inferred actions and map transitions.
Errors deactivate the affected optional context or map-clip capability while
Vanilla, generic sound events and independent API services remain available.

## Explicit gate-routing backend

Native SHA-256: FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2.
Consumer/lifetime contracts: [Gate routing](docs/GATE_ROUTING.md).
Exact bytes and continuations: `src/Pathfinding/GateRoutes/Native/EnemyGatePathfindingNativeDefinition.cs`.

- Capturer comparisons: 0xE2705 and 0xE3024, 20 bytes each. Execute the displaced
  MOVSXD/IMUL/CMP once, transport secured inequality via saved R11b, TEST only
  after callback stack cleanup, restore scratch flag-neutrally before native JE.
  Never use Context Rflags as the decision transport. Recordstride 0x204;
  current R9 record PCL offsets +0x1C/+0x20/+0x1D0, not the previous record.
- Cursor: 0x8F251/29 bytes (DB650 call at 0x8F269), 0x8F1BF/14 bytes
  (E2610 result followed by TEST/LEA). Preserve player/unit context and original
  instructions. Consumer owns reference/policy validation and cache policy.
- Direction filters: 0xD9EA6/14, 0xDA783/18, 0xDACB2/18, 0xDB242/17,
  0xF31A8/15, 0xF33F5/14, 0xDB857/17, 0xDB947/17, 0xDBA36/17,
  0xDBB26/17, 0xDC536/16. Validate live tile/direction/register roles and all
  native branches against complete containing functions on any update.
- Tactical filters: 0xF1910/17 -> reject 0xF1A17; 0xEF1F0/17 -> 0xEF2D4;
  0xEEAD0/18 -> 0xEEC1B. Scope layouts/masks are permanent data only.
- Function detours: 0x1232E0/6, 0xDC3C0/6, 0x113BC0/10. NativeX64 Indirect
  only, no following jumps/calls, chaining refused. Probe the same backend and
  validate scheme, actual displacement, FF25/padding, pointer slot and hook entry
  before publication. Do not substitute an X64InlineHook probe.
- Closed-gate descent: 0x185D55/20, allowed continuation 0x185934 begins TEST R9w.
  Consumer must validate the consumed direction (index is already incremented),
  identity, enabled climbing and roof-to-ground transition. Complete original
  clear remains the default; all downstream native step checks are retained.

Recheck full search/build/reconstruction/step/clear chains, simulations with
restoration, incoming edges, all live GPR/SIMD/flags and stack alignment. Existing
builder/attack/building/command/Assassin function detours remain their current
owners; do not install competing entries. The byte checks permit no unknown
native layout. Execute generated stubs with the installed RedBird backend after
updates. Acquisition and legacy policy registration do not install hooks; explicit
installation is permanent, setting/map changes only change logical data.

## Assassin reconstruction connection rule

Owner: `Pathfinding/Assassin/AssassinPathAPI` (site index 4 of
`AssassinPathNativeDefinition`), audited against
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.

- Hook 0xE19C0/14 (X64InlineHook): `test byte [r14+r15+0x312620],al` (8) /
  `jne short 0xE1A1F` (2) / `test r12b,1` (4). Reached only after `cmp ecx,3`
  at 0xE19BB, i.e. E1640 mode 3, which F4930 passes when path manager +0x88 != 0
  (0xF4B5A). E32B0 (mode 0) and F4930's second call (mode 1) never reach it.
- Pattern fallback: the 24 bytes up to the 0xE19D8 site, unique in executable sections.
  Incoming edges into 0xE19C1..0xE19CD are refused by `ValidateIncoming` over E1640.
- Data: neighbor offsets 0x405EDB0 `[y * 8 + direction]`, connections 0x51890D0
  (byte per tile), direction masks 0x312620 (must equal `1 << direction`, checked
  before publication). Live at the site: al = current connections, rdx = row y,
  r8d = current tile, r14 = direction, r15 = image base.
- Semantics: D9C40 expands u->v by `conn[u] & mask[dir]` (0xD9EA6) and the physical
  step DCE60 accepts either endpoint; E1640 descends target->start checking only
  `conn[current]`. While policy flag 1 or 2 is set the stub also accepts
  `conn[neighbor] & mask[(dir + 4) & 7]` and jumps to 0xE1A1F (stamp/distance check
  unchanged, no flag dependency there). Otherwise `test r12b,1` is replayed last.
  Without the rule, every unit leaving its own gatehouse over a one-way drawbridge
  failed reconstruction and ran DAA50/E49D0/C3E50/DE6A0 (~19 ms each, 2026-10-20,
  8-lord save: 112 failures vs. 10 without the gatehouse rule).
- Update checks: re-derive the mode dispatch, register liveness at 0xE19C0, the
  neighbor/connection layouts and the D9C40 edge direction; execute the generated
  stub against the copied E1640 body (`AssassinReconstructionConnectionTests`).

The weighted owner may additionally correct stockpile-only climb eligibility through
`SetWeightedReconstructionEnabled(owner, enabled, ignoreGoodsyardWallFlag: true)`.
The existing two-argument overload retains its prior behavior. Policy bit 4 is
effective only together with weighted bit 1; direct-gatehouse bit 2 is independent.
Only the existing endpoint stub at `0xE19F9/23` changes its optional branch.
The ordinary connection stub at `0xE19C0` and endpoints `0xE19D8/18`,
`0xD9F0C/16`, `0xD9F1C/15` retain their semantics.

- At endpoint site 3, `r8` is the current tile and `rcx` is the neighbor derived
  at `0xE19DE..0xE19ED`. `r9d` contains only IsWall (masked at `0xE178F`).
  The stub preserves flags and `rax/r10/r11`, bounds-checks both tiles and reads
  complete flags from existing grid `0x48F71B0` (uint per tile, 320800 entries).
- The opt-in branch requires at least one endpoint with `IsWall` (`0x100`) and
  without `GoodsyardRelated` (`0x2`), verified against installed Extender enums.
  Stockpile connection tiles use `0x102`; those bits alone cannot establish a
  climb, but a real wall at the other endpoint remains eligible. Both ordinary
  connection checks precede this site. The original raw-wall test is replayed
  after acceptance, and search/surface/building checks retain their own contracts.
- This uses the existing hash-bound flag-grid layout and adds no data grid,
  address resolver, callback or hook. Unknown native images retain the shared
  Assassin initialization failure path.
- Update checks: re-derive both endpoint tile registers and execute the generated
  stub with ordinary walls, stockpile-only walls, mixed endpoints, policy
  defaults/resets and independent gate bits.

## Registered native sites

Every APIShared native site above and in the other services is registered in the
[native-site registry](docs/NATIVE_SITES.md#apishareds-own-sites) from the same
constants (`src/Core/NativeSites/ApiSharedNativeSites.cs`). When an RVA, displaced
length or validated range changes, the registered span follows the constant; update
the expected spans in `tests/APISharedTests/NativeSiteRegistryTests.cs` and the table
in the guide. The AIV detour claims 0x51790/17, the instruction-aligned prologue any
patch of up to 14 bytes displaces; re-derive it if that prologue changes.

## Workshop staging cleanup

Managed contract, not native: Script Extender
`MapModManager.TryStageModUpdate(string, MapArchive)` returning bool, every return
intercepted. Consumer behavior: [Workshop package cleanup](docs/WORKSHOP.md#workshop-package-cleanup).

Compatibility depends on the loaded nonpublic instance method, its exact signature,
managed body and return sites, followed by dry IL transformation and compilation.
Neither runtime installation nor build validation uses an exact SE version or a
whole-assembly hash allow-list. Only the declared minimum SE version is required.
A failed technical check leaves this optional callback uninstalled and logs its
specific reason; independent services remain available.

When updating the Extender:

1. Review changes to `MapModManager`, `MapArchive`, `ModInfo` and staging/updater
   semantics: the bool result, `_SE/.staging/BepInEx/plugins/<GUID>` and
   `_SE/.staging/delete_list.txt` applied after exit and before staged files copy.
2. Run `Verify-WorkshopContracts.ps1 -GameDir <game> -ExtenderDir <extender>`
   and APISharedTests `WorkshopHookTests` with `-p:ExtenderDir=<extender>`.
   `InstalledExtenderPassesTheCleanupInstallerGate` runs the same signature,
   managed-body, transformation and compilation checks as runtime initialization.
   Negative fixtures cover changed signatures, missing bodies and unusable IL.
3. Run the full `build.bat /nopause /noinstall` with the installed dependencies.
   A successful check is evidence for those contracts, not a complete gameplay test.

The development dependency bootstrap follows the same SE policy for
`GameAssetModManager.PrepareAllAndCheckCompatibility`: signature/body validation
and a dry transformation/compilation precede installation. Pair ownership and the
BepInEx binary gate remain separate requirements; SE hashes are diagnostic only.

The optional [Linux first-install patcher](docs/WORKSHOP.md#independent-linux-first-install-patcher)
has separate managed launcher and attempt-counter contracts. When updating the
Extender, run its [independent build and checks](CONTRIBUTING.md#building-and-checking-the-optional-proton-patcher)
as well; the ordinary APIShared build does not build or validate that patcher.

## Campfire popularity popup

Native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
The popup is a Unity Fly, not a Noesis WGT_Popularity or book/report widget.
Producer `0xB5690` selects campground projectile type 41 (SE
`ProjectileType.EconomyIndicator2`) and calls spawn `0x9B2B0`. Spawn immediately
dispatches its updater through `0x2DA9C0` at `0x9BA06`. Only after spawn returns
does `0xB5845` store the campground owner's current popularity divided by 100 into
projectile offset `0xD8`. Updater `0x9AC20` reads that same snapshot and writes mask
frame `0xD4`: 130 below 40, 129 below 60, otherwise 128. The snapshot stays fixed
for the popup lifetime; subsequent popularity changes create a later popup.

Packet builder `0x1A13C0` exports `0xD4` and `0xD8` as the 21-short record's
infoType and infoData fields. Managed `GameMap.addUpdateFly` creates the icon and
digits once, and its existing-object branch never refreshes them. Consequently
the first render can retain the old red mask beside the new number 98.

`PopularityWorldPopupRuntime` owns one permanent managed IL hook. The loaded
public instance method must return void and accept fourteen parameters, all Int32
except the sixth (Single). Semantic IL guards require exactly one direct store
from IL arguments 12, 13 and 14 into Fly.gameObjectType, infoType and infoData,
respectively. A dry transformation precedes publication. Only argument 13 is
normalized, only for type 41, using the native snapshot thresholds. No native
memory, popup number, cache, child sprite, original branch or player identity is
changed. Unknown native/managed contracts skip this optional correction.
`PopularityDisplay.Tests` checks thresholds and unrelated icons; APISharedTests
transforms and emits the actual installed method, preserving its original body,
and executes a boundary fixture to verify first-call output and unchanged arguments.
The unchanged Unity body cannot be JIT-prepared in the standalone .NET host because
of its Unity ECalls. Actual Mono/game acceptance remains separate.

## Rounded fear-popularity SE adapter

`Economy/FearPopularityRuntime` installs a permanent managed IL hook, without a new
native address or overlapping native span. The native ABI retains its audited SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
SE has no version or hash allowlist. Loaded/disk assembly identity and MVID must agree;
that establishes provenance without imposing a version ceiling.

Discover the calculation by the public SE coefficient getter and contribution-field
write across all module types, independent of generated type/method names. Exactly one
calculation owner must be identifiable; ambiguity is an error. Analyze its CFG, stack,
locals and ref/out pointer assignments. Every coefficient access must lead through
ManagedValue<int>.GetValue and multiplication with the signed Int32 fear field to the
32-bit contribution field of the same resource pointer. Accept any positive number of
multiplication sites, local/stack spills, supported joins, NOPs and either operand order.
Preserve instruction identities when replacing multiplication with the corresponding
two-int-to-int helper, retaining branch targets and the downstream sum/source selection.

All sites are validated before edits. Coefficient escapes, changed arithmetic/types,
unresolved resource aliases, double patches and unsupported exception-region flows
refuse only this optional service, with a reason. Never accept a new SE merely because
its name/version matches, and never reject it solely for a different file hash. For a
future unsupported layout, extend the semantic proof and positive/negative fixtures;
do not replace a hash or remove a failing safety assertion just to make it pass.

Native flow: scheduler 0xD1AD0 -> updater 0xCB090 (eligibility, fixed-mode and small-
population bypasses retained) -> SE contribution calculation -> remaining timed event
penalties -> final 0..10000 clamp/history/speech. Report producer 0x19D960 independently
uses the signed fear level; public report correction consumes the same rounded result.
The fixed-level contribution API selects absolute report points at
this same boundary. It does not alter the IL/native site, signed input provenance,
worst-source ordering or hook lifetime; unlisted levels retain the original SE path
unless an earlier applicable registration supplies a contribution.
Native game/report ABI changes still require their own audit. CLI fixtures validate
rewriting and JIT preparation; actual Mono/game and multiplayer acceptance are separate.

## Plague standing heights

Reference image: SHA-256 FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2.
Revalidated 2026-10-23 against the installed DLL and installed RedBird/Iced binaries.
The RedBird/Iced backend is not pinned by file hash: before publication every prepared
stub must equal the production generator plus the continuation jump, assembled at the
stub address, besides the displacement and jump-form checks.
The native service is hash-bound: matching individual instructions cannot establish its
manager, field, grid and table contracts on a different image. Unsupported images
disable only this capability. There is no unverified pattern-only layout fallback.

### Native flow and sites

Natural D17D0 and catapult impact 9C730 create Disease (22) through 9B2B0. Slots
use E8-byte records and sentinel 0. 9F960 advances movement; 9C730 visits the native
tile-linked unit lists (head grid 4C559B0, next-unit manager offset 8FA), including
roof occupants. Its center/eight-neighbor/outer-ring classifications feed 192750.
That function takes unit manager, one-based unit ID, projectile slot and radius.
The shared hooks relax only surface/building eligibility and compare standing planes;
type immunity table 32BC40, damage 400/200/150, player resistance, HP/death and health
bar updates remain native. Existing Extender projectile-damage entry events remain intact.
Treatment A0470, aging/fade 9A080 and terminal/removal A0540/9F2D0 are unmodified.

| Behavior | RVA / displaced bytes | Continuations |
|---|---|---|
| Cloud standing-plane update | 9FDB8 / 32 | 9FDD8; slot EDX, projectile manager RBX, image RSI |
| Disease eligibility | 192809 / 26 | accepted 192861, rejected 1929D0, original 192823; unit stride RSI, unit manager R15, projectile stride R13 |
| Eight neighbors | 9CB6F / 26 | accepted 9CB89, rejected 9CB9D; projectile stride R13 |
| Outer ring | 9CC3D / 26 | accepted 9CC57, rejected 9CC6A; projectile stride R14 |

Full containing functions and hash-matching baseline Xrefs establish no external
entry into the spans. Verify pristine bytes, every instruction boundary, outgoing
branches and actual RedBird displacement; compare own live spans before committing.
Pristine context.Memory is a snapshot, not evidence of live bytes. C07C0 and its
jump tables through C087F must also remain unmodified live.

### Data and ABI

Image-relative data: projectile manager 2F7A680; byte height grid 4DDD350,
uint properties 48F71B0, signed-short building IDs 4B6AA50 (320800 tiles).
Building manager 64CCBB0 has 1..3999 IDs, stride 32C, type +12E, base height +148;
the corresponding GameBuilding header is 5C, fields +D2/+EC. Unit manager 67E8400
has stride 490 and header 65C; signed base/additive height +712/+714 are unit
fields +B6/+B8. Current unit tile is manager+unitStride+72C (unit field +D0), the
same address used by the displaced original query. Validate current Extender sizes,
offsets and actual manager bases before installation.

Source-plane helper follows 184FD0 precedence: stairs raw+12; building-backed
wall raw+40; bare wall raw; IsBuilding raw+2; Elevated building base+C07C0;
otherwise raw. Unit-specific movement/tunnel offsets are not applied to clouds;
the actual target's signed stored heights remain authoritative. Visual cloud
height is standing plane+20. Invalid tiles/buildings reject affected damage and
retain the original visual update, with one error marker.

C07C0 clobbers only RAX/RCX/RDX/flags, with no SIMD or mutable-memory effects.
Helpers preserve RBX and Windows x64 alignment/shadow space. Wrappers preserve all
live volatile/nonvolatile GPRs, SIMD and flags; cloud update restores original
RAX/RCX output semantics. Neighbor hooks retain arithmetic and the alive comparison.
Repeated original blocks use Iced instructions with cleared identity IPs, retaining
absolute branch targets. Decode and execute emitted code with the installed backend.

Target building identity takes precedence over wall/stair flags. The emitter uses
Extender eStructs for keep variants/doors, gates and their pieces, defensive towers,
editor prototypes and collapsed tower variants. Mobile siege towers are not included.
Unrecognized elevated surfaces replay Vanilla exclusions; disabled categories do the
same. The source plane still follows its surface independently of target policy.

The aligned qword at state+64 contains four unsigned 16-bit tolerance+1 lanes in
Walls/Gates/Towers/Keeps order; zero means no active owner. One Volatile.Write
publishes the complete aggregated policy. Each category uses its largest active
owner tolerance, ground the largest overall; zero snapshot replays all four original
blocks. No per-candidate managed callback, allocation, new RNG or global scan.

### Ownership, publication and verification

APIShared claims all four spans in its native site registry, prepares the complete
transaction with InstallOnCommit=false, validates handles and uses NativeHookPublication
before the first executable write. Partial/failed publication permanently retains the
candidate, helper, data and claims with a zero policy; no late retry or rollback.
Private helper memory is written RW once and changed to RX before publication.
Only unpublished candidates can be rolled back. Published hooks never use Unity
teardown; OnTick is a durable, isolated support-diagnostic subscriber.

Contracts and composition: docs/THIRD_PARTY_GUIDE.md. Tests in
tests/PlagueHeight.NativeTests execute production generators and the complete native
damage routine, backend displacement, all 16 masks, signed tolerance boundaries,
configured tolerances, live surface changes, independent owners and ABI preservation.
The normal build driver runs them against the selected installed references.
Workspace baseline audit: _inspect/PlagueHeightAudit.py (not a runtime dependency).
Public consumer example: examples/ThirdPartyMod/PlagueHeightExample.cs.

Prior user game evidence covers cloud-edge movement, wall demolition and tower collapse.
New category UI, real multiplayer and full save/treatment roundtrips require game
acceptance; isolated execution tests do not establish these outcomes.

## Shared visible-map entry and pause-menu actions

Audited image SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
`0x41D60`: Win64 void `(map, left, right, top, bottom, zoom)`, arguments 5/6 at
entry RSP+0x28/+0x30. The first ten bytes are
`48 89 5C 24 10 48 89 6C 24 18`; both MOV instructions save nonvolatile registers
in home slots. Indirect/10 resumes at `0x41D6A`; validate pointer-slot jump, NOP
padding, displaced bytes and resume target. APIShared exclusively owns this entry;
consumers register observers rather than detours. The demand bridge tail-jumps
without changing stack arguments, using only volatile RAX/flags. Its instructions
and targets are validated after publication. Published failed hooks stay rooted
with zero demand. `VisibleMapRenderTests` executes the production bridge and tests
both demand paths, all six arguments, exact original count and cleanup failures.
The complete original render function and its terminal drawing-list cleanup at
`0x41D10` remain intact. Rendering temporarily changed spectator visibility must
always be restored by consumer cleanup; the service makes no player changes.

No unknown-image fallback is admitted for `0x41D60`: the ten-byte prologue is a
generic register-save sequence that does not identify this render function, and the
six-argument contract (including the two stack arguments read by consumers) was
audited only for this image. Other hashes fail registration closed with a diagnostic;
Vanilla rendering and unrelated services stay active. Re-audit the argument meaning,
callers and the `0x41D10` cleanup before admitting a new image.

Managed presentation (ESC menu): APIShared hooks only the public
`HUD_IngameMenu.Init()` of the real Assembly-CSharp and observes
`MainViewModel.PropertyChanged(Show_HUD_IngameMenu)`. After the original `Init`, it
moves the existing `LeftPanel` into its own scrollable surface and reparents all nine
named Vanilla buttons (`ButtonSave`, `ButtonLoad`, `ButtonWorkshop`, `ButtonOptions`,
`ButtonHelp`, `ButtonRestartMission`, `ButtonQuitMission`, `ButtonExitStronghold`,
`ButtonResume`) into row grids or a collapsed parking panel; their commands, bindings
and style are unchanged. Re-audit on a managed/UI update: `LayoutRoot` (Grid),
`LeftPanel` (StackPanel) and the nine buttons as its direct children, the style
`BTN_SH_GlowSDisable`, the XAML patch adding `APISharedMenuTooltipStyle` to
`UserControl.Resources`, `PropEx` text attachments, and that `Init()` resets
`LayoutRoot.Height` (currently 400 before mode-specific changes) on every opening.
A foreign layout that moved a button keeps the menu unchanged and reports the failure.
Popup sizing reads Unity screen height only on the UI thread; its opt-in disables the
competing SE scaler only for that popup.

## Managed Script Extender and game targets of Lord presentation and move diagnostics

These are managed hooks; they claim no native site. Re-audit them after a Script
Extender or managed game update.

- `PackagedLordPresentation` (installed with the first registration; a failure
  rejects that registration, already installed hooks pass through without entries):
  `OnScreenText.getComputerName(int,int)`, `Platform_Multiplayer.getPlayerName(int,bool,bool)`,
  `CustomisationFileManager.GetCustomLordText(string,int)` (IDs 34..41 are the nickname
  titles), and the SE `GameAIManagerAPI` members `IsSupportedCustomLord`,
  `TryGetDisplayName`, `TryGetLordTitle`, `TryGetLordDetails`, `TryGetFace`,
  `TryGetJoinAudio`, `TryGetLeaveAudio`, `TryGetCurrentSpeechText`, `TryGetLuaInitPath`
  and, through the single owner `LordMediaResolverRuntime`, the internal `TryGetVideoAndAudio`
  (packaged claims first, then the missing-identity correction). Portraits use the private
  `GameAssetManagerAPI.TryLoadTexture(string, out Texture2D)` through reflection.
  Clip selection must keep using `DeterministicRandom.Next(0, count)` exactly where SE does.
- `CoopLordRestartPresentation`: IL edit of `Platform_Multiplayer.StartGame(MultiplayerSetupData,
  FileHeader, int coopTrailID, int coopMissionID)`; requires exactly one call to
  `HUD_IngameMenu.RestartMPInfo.encode()` and inserts the population before it.
  Vanilla fills restart Lord names only when `coopTrailID == 0`.
- `MoveCommandFailureRuntime`: IL edit of SE `GameTribeManagerAPI.IssueMoveHereCommand(int,int,int,bool,int,TribeMoveType)`;
  requires exactly two `LogHelper.Error(string,string,string)` branches and one native
  dispatch, with the tribe-lookup branch before and the result branch after the dispatch;
  only the latter is labelled as a native failure. A mismatch logs `SE_MOVE_FAILURE_CONTEXT`
  and keeps the original wrapper.

## Enemy-unit proximity and Peace Time

Reference native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.

- The existing Script Extender owns live entry `0xEE640`; APIShared only subscribes
  to its Pre event. There is no additional native hook or overlapping reservation.
- The shared private predicate copies exactly `0xEE640..0xEE835` (502 bytes), body
  SHA-256 `1D2DDB19C37B3EC505D426272421E57FE4336CAA2BF9FA80BE6F1143282C565D`.
  Every original instruction and internal branch identity is retained except the
  audited category distance CMP at `0xEE748`; four RIP-relative data anchors are
  reencoded as absolute loads. A seventh Win64 argument supplies civilian radius
  squared; the original six arguments, stack layout and military radius remain.
- Data contract: enemy count `0x379CE58 + player*0x583C`, word IDs
  `0x38C19E8 + player*20000`, paired global IDs `0x38ED908 + player*40000`,
  sentinel-relative unit base `0x67E8A5C + gameId*0x490`. All original identity,
  coordinate, death/state and Assassin/Bedouin filters must remain byte-equivalent.
  Fixed layouts have no speculative unknown-build fallback.
- The native loop uses strict squared Euclidean `< radius*radius`. Public supplied
  radii are nonnegative and at most 46340. Unsupported raw inputs retain the
  original path; independent valid policy vetoes still apply.
- `PeaceTimeState` reads the native active byte at `0x38722DC`, anchored by the
  RIP-relative load at `0xCA874` in updater `0xCA870`. Native initializer `0xCA900`,
  archive `0x15C60`, scenario reconstruction `0x100530`, and alternate updater
  `0xD1E50` remain authoritative. Expiry occurs when current tick exceeds end;
  the reader must not replace this with its own comparison or timer.
- Validate all consumed bytes against the pristine load context before preparing
  pointers. Never interpret a byte/address from another native hash using these
  fixed layouts. A failed query validation disables radius replacement only; a
  failed peace read disables peace-dependent consumers only.
- Revalidate the installed RedBird AllocateStub/WriteStub cache-flushing and
  process-lifetime allocation contract and execute the copied native tests after
  an SDK/native update. No executable rewrite/free after publication.
- Review the full candidate-producer and placement/action callers, native ABI,
  category enum values, complete exclusions, save/load/world invalidation and
  separate building/wall/Keep checks on updates. Retain the independent native
  fixtures and repeat game/editor/save and matching-build multiplayer acceptance.

The offline expiry fixture validates the complete 138-byte `0xCA870` body hash
`FF0C43799C8C79DAB6071437A44818457D5D42BC341CF448B59CA657DF8EB19B` before
executing a private relocated copy. Only external display/remove calls at
`0x1A2980`/`0x1A42B0` and the sound tail jump at `0x2940` become private RET
stubs. Timer reads, comparisons and active-flag writes remain original. Review
all three external edges and R11 liveness when updating this fixture.

## Ally transfer adapter

Reference native hash: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
`Economy.AllyTransfers` uses `0xD7AD0` (585-byte gift primitive) and `0xC5AD0`
(319-byte capacity query); complete byte digests and all relevant branches are in
[the transfer contract](docs/ALLY_TRANSFERS.md). Native globals, record layouts,
capacity semantics and notification quantities remain hash-bound: a prologue
pattern cannot establish them on a new binary, so unknown hashes fail closed.

The Script Extender already owns the gift entry. Do not install another detour or
require live pristine bytes at `D7AD0`. Validate the full pristine native bodies,
the Extender hook/delegate target and its loaded wrapper IL. The managed adapter
replaces exactly the wrapper's one native Original delegate Invoke, retaining the
surrounding mutable Pre, skip branch and Post. Recheck argument order, final getters,
exception paths, recursive calls and opaque return preservation on Extender updates.
The capacity query stays unpatched. Failure disables this optional service and
leaves the existing Extender/native behavior active; no late native hook retry.

## Mapper selection camera broker

Reference native SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
`Presentation.MapperSelectionCameraEvents` owns the public managed
`EngineInterface.StartMapperItem(int) : int` hook. Its loaded signature and
`EngineInterface.threadLock` private static object contract are checked before
initialization. A permanent native bridge is required before camera policies are
accepted; consumers do not install competing camera or mapper hooks.

Vanilla start export `0x87830` calls dispatcher `0x8BD60`. Its family branches
validate the first-family entry via `0xCC140` using the current native view, then
converge at CALL `0x8BF96` (`E8 85 9F FB FF`) to `0x45F20`; its return address is
`0x8BF9B`. Families are stores 0x34, tradepost 0x4D, Bedouin stockade 0x4F,
granary 0x50, armoury 0x51, wooden/stone barracks 0x56/0x57 and engineers/tunnelers
guild 0x58/0x59. Two compound-building branches additionally check their current
footprint completion. Camera inputs are building begin tile plus one on each axis.
The complete start dispatcher, camera helper, pending-target writer and clamp were
audited: 575 decoded instructions matched the installed image. Their internal
function labels remain candidate names; the stated operands/control flow are
instruction-backed facts for this reference image.

Camera helper `0x45F20` is twenty bytes:
`49 63 C0 4C 8D 04 40 42 03 94 81 6C E1 61 00 E9 EC A2 00 00`.
The only admitted backend is NativeX64 Indirect. Its six-byte minimum rounds to
the two complete instructions `[0x45F20,0x45F27)` (3-byte MOVSXD RAX,R8D and
4-byte LEA R8,[RAX+RAX*2]). Continuation is ADD EDX,[RCX+R8*4+0x61E16C], then
tail JMP `0x50220`. Full function control flow/xrefs have no inner entry into the
seven-byte span. Validate the pristine complete function and mapper CALL, live
exclusive entry/caller, exact prepared/installed Indirect/7 scheme, pointer slot,
entry padding, generated intermediary bytes and trampoline continuation. An
adjacent hook beginning at the continuation is permitted. Current local Extender,
canonical Fixes, APIShared and BFQoL sources have no competing site here.

Do not place a standard context hook directly at the mapper CALL: its fourteen-byte
minimum would include another switch entry at `0x8BFA0`, targeted by the computed
jump at `0x8BE82`. The function-entry intermediary instead checks one private data
cell and the incoming native return address. Only nonzero scoped demand AND exact
mapper return `0x8BF9B` returns without camera work. Every other call tail-jumps
through the complete original trampoline. Preserve all flags, R11 and stack on both
branches; no managed callback, SIMD change or native-view write occurs there.

`0x45F20` translates the tile through the camera manager's table and tail-calls
`0x50220`, which writes pending target +0x7C/+0x80/+0x84, native camera offsets
+0x98/+0x9C and clamp-derived +0xA0/+0xA4 through `0x4B270`. Serializer `0x19D960`
exports the pending target into PlayStateReturnData +0x554/+0x556/+0x558 and clears
its XY qword afterwards. The managed report may therefore override a Unity-only
postfix later. Clearing only pending XY is insufficient and can discard another
legitimate camera request. Suppression skips the producer and all these camera
side effects, retaining mapper selection and every placement validation path.

Scoped demand is changed only under the existing engine monitor, restored in finally
before Post, and cleared on lifecycle invalidation. Nested scopes restore the parent
only in the same native world. Partial publication retains code/data roots with zero
demand; native or managed initialization failure prevents policy registration. No
unknown-hash fallback is supplied: this small generic helper cannot establish the
camera fields, shared mapper caller or return-address meaning on another image.
Re-audit the entire chain and registry on updates. Source tests cover ordering,
private State, original/callback exceptions, recursion, world replacement and dormant
allocation behavior. The executable private-memory harness uses the real Indirect
backend and matching/nonmatching callers, checking every GPR/XMM, flags and stack.
Those fixtures do not establish live camera framing or host/client game acceptance.

## AI military admission and quota accounting

The supported image is SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
Re-audit the full role/admission/order/fold/build/load flow, not only entry bytes.
Public behavior and limits are documented in [AI military admission](docs/AI_MILITARY.md).

- [Native call bindings](src/Units/AiMilitary/Native/AiMilitaryNativeBindings.cs)
  contain all call-only entry contracts. Keep live SE/Fixes detours in the call
  path. The direct allocator is `119D60`; the Extender public creation wrapper
  instead covers `119C00`. These are distinct operations.
- [Native world/order ownership](src/Units/AiMilitary/Native/AiMilitaryNativeObservers.cs)
  owns Indirect entry detours at `87D0` (raw restore), `122B40` (strategic dispatch)
  and `10DF60` (global task-list build). Preserve exact ABI, process roots and
  inactive native gates. No competing consumer hook is allowed at these entries.
- [Quota contracts](src/Units/AiMilitary/Quota/AiQuotaNativeContract.cs) identify
  the instruction-complete spans for fold capture and recruitment comparisons.
  The native real-count vector must remain unchanged. Contributions must come
  from the same completed fold consumed by each comparison, including role-22
  group/global-identity validation and no-demand folds.
- [Engineering ownership](src/Units/AiMilitary/Engineering/AiMilitaryEngineeringRuntime.cs)
  covers the six builder entries `A65A0/A8E40/A93C0/A9A30/AA030/AA630`.
  Preserve historical ALL-contributor reduction, delayed product/crew identity
  checks and exact Fixes replacement scope. The optional private Fixes method
  is validated by signature, installed assembly identity and its delete/create IL.
- [Effective-original broker](src/Units/AiMilitary/Orders/AiMilitaryOrderBroker.cs)
  augments existing SE managed wrappers. Pre mutations precede native receipts;
  Post cannot manufacture admission/allocation success. Verify every transformed
  wrapper against the installed Extender, including reentrant world changes with
  reused unit and group IDs/global IDs. Never restore old route/group snapshots
  into a newer world.

The mapper is a group spawn: `93750` dispatches to `118CA0`, resolves a group through
`DE0C0`/`119C00`, calls `17FEF0` and then `11D370`, while the new unit still needs
initialization. Invocation capture must prevent this early mapper group admission;
quarantining only after the next native tick is too late. Map-load quarantine must
similarly precede initial AI/fold execution, after the entire archive is decoded.

Raw restore success is int 1. The audited zero-result failure branches are:
`85496 -> 8549B -> 85B4D -> 85B74`,
`94B15 -> 94B1A -> 96EBE -> 96F1F`, and
`1006D7 -> 1006DC -> 102550 -> 10256A`.
They reach their ordinary failure epilogues before AI initialization. Recheck all
three complete caller paths before allowing the early participant to return zero
for a failed protection transaction. Keep that failure sticky through retries and
authoritative over mutable legacy Post results.

Layout checks distinguish serialization origins from public record pointers:
native unit serialization starts at public GameUnit minus four bytes; tribe
serialization starts at raw row plus `28`, public GameTribe at plus `2A`.
The AIC field named `r_AILordMinusOne` is actually one-based on this image.
Role zero, unmanaged `65535`, unit roles and player storage slots have distinct
meanings. Nonpositive cover/reserve/wall group counts retain their base slot.

Role-22 `2ECD0` gates both recruitment and building on a physical population below
20; `14AED0` retires above 20. Additional classification changes neither bound.
Admission reserves the currently missing regular two-engineer pool without
claiming unrestricted future construction capacity.

Run `AiMilitary.NativeTests` with the canonical DLL, `AiRecruitOrders.Tests`,
`AiMilitary.AttackTests`, `MapRecordReadiness.Tests`, and the affected framework,
save-context, API compatibility and consumer tests. Verify real normal/DevDiag
Spectator builds separately; temporary diagnosis must stay absent from normal
assemblies. Fixtures establish native ABI/branch and managed behavior contracts,
not actual editor/game/save or real host/client acceptance.

## Worker production bonus

Owner: `APIShared.Economy.WorkerProductionBonusNativeRuntime`; reference native SHA-256 `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`. The complete site list, original bytes, signature windows, continuations and work-completion entries are maintained in `src/Economy/WorkerProductionBonusNativeDefinition.cs`; instruction generation and footer register maps are in `WorkerProductionBonusNativeAssembly.cs`.

The calculation block is RVA `0x18D976..0x18D987` inside the existing Extender-owned `0x18D940` yield helper. Seven field-worker gates follow successful arrival and precede storage checks; six workshop gates follow production/animation and precede initial storage lookup. Ox checks `0x132A09` and `0x132C17` protect reduced positive loads; reserve check `0x1320B0` protects the separate driver path. Render `0x18D2D6` rejects negative extra-yield display without altering stored carry.

The fear channel uses that same calculation span. Native owner comes from the
Extender-resolved UnitId's unit record, and selects the signed player fear level
at `image + 0x379CF04 + owner * 0x583C`. Recheck the `0xCD850` reader and
productivity writer and the independent fear-table reader in `0x180560` when
qualifying a new native image. This data layout is hash-bound and has no fallback.
The existing original-actor context limits the override to actual output. Check
the two Baker calls separately and retain Miller's separate flour-output path.
Negative fear uses the existing extended row contract for reduced Ox provenance
on the original carrying unit, including when Pre redirects native bonus state.

Layout accesses (unit manager, current unit, native mode, stride and worker/footer register contracts) remain hash-bound. A signature match alone does not admit a new layout. Known hashes use reference-byte validation; any allowed fallback must resolve a unique executable-section site and validate the full window, branches and continuation. Unknown layouts fail closed. Validate every site against the immutable load image and own live span, then prepare and validate every actual RedBird displacement before publication. Preserve adjacent ImprovedHunters hooks, particularly Tanner find at `0x13ED8B`; an original-image signature anchor is not a claim over its entire window.

On a game update, re-audit all producer/caller and completion paths, both Ox departure thresholds, driver reserve, render consumer, signed carry, save section 3015, Chore56, checksum and slot clearing. Re-run native execution fixtures and live save/editor/host-client acceptance. Do not enable negative percentages after partial initialization.

All non-calculation guards require the process-only readiness DWORD at coefficient+4. Publish it only after the final installed-hook validation; a partial installation retains coefficient50 and readiness0. At the audited completed-output phases, ready zero-cargo completion always runs the normal footer: an Extender Pre callback may redirect UnitId and leave no negative-extra marker on the physical producing unit. Positive cargo keeps the original route. Ox/reserve guards additionally recognize saved negative extra and coordinated sidecar batch provenance even with coefficient50 or native mode0. History uses the current producer for Ox, RBX's Ox for driver reserve and original RAX's record for rendering. Save metadata and raw unit bytes must be restored before simulation resumes; consumers must publish admission at the corresponding pre-simulation lifecycle checkpoint.

### Production sidecar, unit resynchronization and critical saves

The additional owners are `WorkerProductionStateNativeRuntime`,
`WorkerProductionCriticalSaveRuntime` and `WorkerProductionSaveUiRuntime`.
Their field layouts remain bound to the same full native hash above. Unknown
hashes have no layout fallback and leave this dependent production service
unavailable. On an update, repeat the complete pack/measure/unpack, checksum,
save-caller and managed-continuation audit before accepting a new identity.

The private, permanently allocated sidecar has 10000 rows of 32 bytes (slot0 is
an empty sentinel, game IDs1..9999). It does not enlarge a native unit record or
change any Extender field type. Offsets are GlobalID u32@0, current native kind
u16@4, batch flags u16@6, fraction numerator u64@8, denominator u64@16,
schema u32@24 and reserved u32@28. Nonempty rows use schema1, reserved0,
reduced `0 <= numerator < denominator <= Int64.MaxValue`; a zero fraction with
provenance is0/1, and no fraction/provenance is all-zero. Fraction identity is
slot plus GlobalID: profession changes preserve credits, normalize the stored
kind and discard old-role batch flags. Save/pack normalization uses a copy;
saving one peer must not mutate simulation state. Marker flags describe the
producing operation, not final cargo after native bonuses or Extender Post.

`15710` is the sole Chore56 pack/unpack/measurement owner. Its NativeX64
Indirect entry displaces two five-byte MOVs; the next five-byte MOV remains
verified. Native payload2172 becomes2204 only in a world admitted to the
extended protocol. Mode2 measures without moving payload; mode1 changes its
own native mode before packing, so capture the original mode. Mode0's native
skip gate remains authoritative. The original helper transfers ID4, the
native record0x490 (origin at unit manager+658), and path1000, then the owner
appends/restores exactly one validated row32. Native manager RVA8574320,
mode85F8FEC, payload length85F8FF4, transfer unit86C132C, cursor+370BF8,
unscheduled body+84CD8 and skip+9A0 remain hash-bound. Check the180000-byte
body limit before the original helper copies anything; its own overflow
logger runs after copying and cannot provide this protection.

The managed `EngineInterface.ReceiveChore(int, byte[], int)` wrapper validates
the whole incoming unit packet under the real engine monitor before its
single native invocation. Extended plain56 packets require their exact2212
bytes, unscheduled tick0, a valid slot/row/native identity, and a denominator
dividing the admitted common precision. Compressed125/56 is rejected before
native decompression; the audited native sender23800 never compresses56.
Any invalid tail faults and quarantines the world without calling Original;
it must never partially restore a native record before validating its sidecar.

Checksum span22149..22159 is16 bytes: MOV EDX,[RSP+A0], ADD EDI,ECX,
MOVSXD RCX,[RBX+36FA50]. The generator mixes the canonical effective row into
EDI before the original category ADD at22159 and unit store2216F. Empty rows
are a strict no-op. R12 is the one-based unit ID. Native identity accesses are
origin+6F0 GlobalID and+6E6 kind; public GameUnit starts at origin+65C, whereas
the transferred record starts at+658. Do not confuse record GlobalID+98 with
wire-body GlobalID+9C (the latter includes ID4). Revalidate the entire caller
loop, category/unit writes, no interior incoming edge, backend displacement,
generated bytes, installed jump, register/flag/stack preservation and both
legacy/extended branches. Native execution fixtures run this generator through
the actual category and unit stores, including profession-change normalization.

### Weapon workshop ingredient amounts

For consumer registration and gameplay limits, see
[weapon workshop recipes](docs/WEAPON_PRODUCTION.md).

`WeaponProductionNativeDefinition` supplies a separate optional ingredient hook
cohort for each workshop. Validate the original spans, their fallthroughs and
all bypass targets. For the Fletcher and Poleturner, trace the per-trip stock
search, post-drop local threshold, and latched debit. Preserve the native
drop-off notification and reuse local surplus when the next charge can start
without a stockpile trip. For iron, distinguish insufficient raw stock from an
actual finished lot on the state-6 recovery path. Keep the original `0xB6100`
call and check that it accepted the signed debit. For Tanner, trace the state-1
threshold, state-9 three-hide grant, state-6 debit, and state-5 yield.

The Tanner's multi-hide admission also checks the live original yield call at
`0x13EBF7`. Revalidate the check against the current ImprovedHunters hook on a
new image. The hook can choose a meat delivery using the remaining cowhide
count, so its presence disables multi-hide recipes. Preserve the default
adapter and the one-hide recipes if the extra ingredient cohort is unavailable.

### Weapon workshop repair transport

The existing ReceiveChore and Chore56 owners also handle weapon production. The
optional `Economy.WeaponProductionTransfer` cohort owns the building packet and
checksum spans. Enable the world demand byte only after both groups and the
frozen peer plan are ready. The dispatch table at `0x2C7A30` maps opcodes 54, 55,
56 and 57 to `0x15170`, `0x15200`, `0x15710` and `0x158B0`. The first two are
repair start and completion. `0x251F0`'s repair loop sends the unit and building
records and the `0x26D30` planning path measures them. `0x23800` emits the eight
byte header and uncompressed body. The managed receive boundary rejects a
compressed extended record, wrong frame length, nonzero scheduled tick or invalid
sidecar before native record transfer. The validation uses the transferred native
identity, since the local record can have a different identity before repair.

- Chore57 at `0x158B0` transfers ID4 and the native building record `0x32C` from
  `buildingManager + id * 0x32C + 0x5C`. Indirect/6 overwrites the five-byte stack
  MOV and one-byte PUSH. The first original instruction after the span is
  `48 83 EC 30`. Prolog signature `48895C2408574883EC3048630527375E08`.
- In a weapon workshop world, Chore57 appends the building row16 (832-byte body,
  840-byte frame). Chore56 keeps the existing row32 and appends the worker row20
  (2224-byte body, 2232-byte frame). The building body has native GID at `4+D8`
  and type at `4+D2`. The unit body has GID at `4+98` and type at `4+8E`. Modes
  0/1 restore/pack and mode 2 only measures. Advance the cursor and commit the
  validated row only after the original native transfer consumed its exact body.
- Building CRC span `0x22236..0x22246` has bytes
  `488DB62C03000003D048638350FA3600`. RBP is the one-based slot and EDX is the
  per-building checksum before category add at `0x2224D` and slot store at
  `0x22256`. Compare the row GID to manager+`id*0x32C+0x134`. The earlier
  temporary zero at `0x22201` is relative to RSI at public record `+0x10`.
  It clears public `+0x2FC`; the native CRC includes both production words.
- Unit CRC uses the existing `0x22149` span with the new row fold after the
  previous amount fold. Compare GID at manager+`id*0x490+0x6F0` and actual
  profession at `+0x6E6` to the expected role. Stale rows do not contribute.
  The generators fold each matching canonical row before both the per-slot
  and category checksum stores and preserve flags and scratch registers.

Revalidate dispatch/send/measurement/restore routes, frame sizes, loaded layouts,
both patch backends, full incoming-control-flow inventory, emitted stub bytes and
the same save/peer plan before supporting another image. Private execution tests
exercise the actual checksum continuation stores. Live repair still requires a
real host/client acceptance run with a forced building and worker mismatch.

The retire pass of lost workshop workers (`WeaponProductionRuntime.RetireLostActors`) relies on
three Vanilla facts; recheck them for another image: the regular checksum runs only on map
ticks divisible by 200 (`0xD1AD0`, call at `0xD1DA9`), the Script Extender tick hook in the same
function runs before that call, and the repair recompute sends no report. The retire interval
must divide that checksum interval; the source enforces the current values at compile time.

### Production state archives

Versioned record `APIShared_WorkerProduction`, known entry `state`, uses the
existing simulation-relevant save/map archive. Its payload is WPS1, schema1,
capacity10000 and row size32 (four little-endian u32 fields), then the entire
row table. Prepared decodes only private bytes. The existing AI-owned87D0
restore detour invokes a narrow internal participant list after all raw
sections restore and before the first simulation fold; no second87D0 hook
exists. Validate the whole candidate and native identities before publishing
rows. Admission/actual restore must compile the same saved denominators and
rule digest. Missing/invalid required support cannot silently become legacy.
The participant is connected only after the root mission observer is installed;
an optional root initialization failure must not change unrelated Vanilla loads.
87D0 is a file restore (7430 path, binary read open), not an in-memory snapshot.
Its three direct callers are853F0,94350 and100530. Ordinary save/editor load853F0
calls87D10 first, which sets scene3665ED8 to40. This scene is not a serialized
section;71670's CDE60 fold requires7E50, which accepts only scenes12/14/16,
so that route cannot dispatch182B00 before returning. Other map routes need
precise mode admission before the direct fold: initflag366A0F8=-1 is not a
pause (CDE60 normalizes it to0 and can dispatch workers). The engine-run gate
alone does not cover this direct initializer.
Legacy CRC/Chore56 remains exact when no base channel or sidecar is required;
the chosen world protocol is never downgraded mid-mission.

The common native file writer52B0 is reached by86F80 (managed normal/editor/
quicksave),13030 (synchronized save), and816B0 (trail restart-info). Its ABI is
`ulong(archive pointer, section-table pointer, map-save byte)`, success1/failure0.
The existing `PlayerViewSaveScope` exclusively owns its Indirect/10 detour and
caches the exact native WriterCall once. Inside the existing engine monitor and
participant-identity protection, WriteOriginal routes through the production
transaction before that cached native Original. The production owner installs
no second writer detour or52B0 span; its write/close cohort requires the published
save scope and remains dormant after partial publication. Preserve the outer
identity restoration/result/error boundary, including failed and throwing writes.
The native writer has no existing temporary-file transaction: production freezes
a validated snapshot in that protected scope before Original, then redirects only
the native destination slot to a short sibling temporary path. Native path
object30D0F30, selector+FB0 (RVA30D1EE0), slot1 UTF-16 address31C74B6 and
capacity1001 characters derive from7430/A850; all three callers select1.
Save and restore the complete slot and selector in finally. The Extender's
logical SaveContext keeps its original path, including synchronized null-path
and trail restart-info source-map contracts. Do not alter map name/header or
restart-info globals merely to redirect the physical write.

The same publication cohort owns native binary write1DDB94 and close1DBB20,
both Indirect/10 with two five-byte MOVs and18-byte entry contexts. Write ABI
is `i32(i32 descriptor, pointer, u32 count)`; close is `i32(i32 descriptor)`.
All46 static52B0 writes and the Extender ZIP append use1DDB94. The first write
after its sole successful open is the four-byte native header; bind that file
descriptor only while this thread is inside the critical52B0 Original. Other
descriptors and all noncritical calls retain Original.1DDC2C/1DDD4C return the
actual binary byte count or-1;1DBBB8/1DB978/1DBD14 return0 on close success,
-1 on failure. Record incomplete/error writes and close failures persistently
for this transaction, even if a later ZIP append succeeds. Require an observed
successful close before replacement. Revalidate the whole52B0 writer call list,
the SE resolved write target, entry boundaries and both trampoline contracts.

After Original, require all native writes and close to have succeeded, this
exact capture to have completed successfully and
read the actual temporary archive back: its versioned record must contain the
current immutable payload byte for byte. Only then commit with APIShared's
existing atomic file replacement helper. Capture failure, skipped/stale data,
append omission, native short/error write, close failure or replacement failure
returns0 and preserves the old target. This verifies actual I/O results and the
sidecar payload; it does not introduce a parser for every compressed section.

The managed continuation transforms are bound to loaded IL rather than an
Assembly-CSharp file hash: `EditorDirector.SaveSaveGameOrMap` skips dirty/name
success writes on a newly failed critical transaction; the existing
`SaveLoadDialogEvents` owner guards `HUD_LoadSaveRequester.RunOKAction` before
remembered names and keeps the same request retryable. Both concrete workshop
save call sites (upload preparation and post-upload local publication) stop
before preview/upload or .data/success continuations. No managed save-and-quit
chain exists in the audited assembly; editor exit confirmation is a separate
scene change. These guards do not change ordinary Vanilla failure semantics.

Validate the complete IL evaluation stack at every injected return and branch
join. In the audited post-upload closure, a duplicated workshop pathname remains
on the stack after the void save call; the critical-failure branch must discard
it, while the success branch retains it for the subsequent `.data` publication.
Normalize short branches before insertion and reject unaudited exception regions.
Generate and force JIT compilation of every transformed body in Unity Mono before
publishing any hook: desktop generation alone did not detect the nonempty-return
stack. Desktop fixtures substitute only the native-save/UI boundaries for the
executable regression, because actual Unity ECalls cannot JIT on desktop CLR.

## Huntable prey reservation word

Owner: `Wildlife.HuntablePreyNativeContract` and `Wildlife.HuntablePreyRuntime`; public
contract: [huntable prey](docs/HUNTING.md). Reference native SHA-256:
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`. No hook, patch or
executable target: the service writes one data field and reads the Extender's
`OnUnitHunterQueryTarget` event.

- Field: signed 16-bit Hunter lease at public `GameUnit +0x448`, native displacement
  `0xAA4` from `manager + id*0x490`. The public pointer starts `0x65C` into that address,
  the same relation as AI state `0x918`/public `0x2BC` and target global `0x9F8`/public `0x39C`
  (checked against the compiled Extender layout in `HuntablePreyTests`).
- Anchors compared byte for byte in the unmodified load image at initialization:
  `0x12FE63` `66 47 89 B4 28 A4 0A 00 00` (Hunter state 0 writes lease 2),
  `0x156203` `0F B7 84 33 A4 0A 00 00` and `0x156213` `66 89 84 33 A4 0A 00 00` (deer/goat
  decrement), `0x18AFFD` `66 83 BB AC 01 00 00 00` (query requires 0; loop base `mgr+0xD88`),
  `0x184E76` `3B 13 0F 8C` (the dispatcher `0x182B00`'s per-type update loop, table call at
  `0x18410C`, runs while the ID is below the manager's first DWORD, the Extender's
  `r_NextUnitId`; the release visits only that ID range). The Extender's unit manager must be
  module + `0x67E8400` with `GameUnitArray` at `0x65C`.
  Other mods hook `0x12FE63`; the comparison never reads live code.
- Further audited writers (renewal every Hunter update, lease 2 in r14): `0x130044`,
  `0x130259`, `0x130338`, `0x13064F`; further reader: corpse search `0x18B640` at `0x18B6ED`.
  The same word stores the war-dog kennel slot (`0xB47E0`, read at `0x1671BB`) and an Engineer
  flag (`0x14AED0`); both types are rejected as prey. Full access list:
  `_inspect/CrusaderDE-Native-Baseline/sem/FBCB9319/knowledge/HUNTER_PREY_RULES.md` in the
  maintainer workspace.
- Fallback: none. Any other hash, a different `GameUnit` size or any anchor mismatch leaves
  the service unavailable; registrations then fail with the reason and only deer and goat stay
  huntable. Before adding a hash, repeat the complete access audit of the word, including all
  unit types that reuse it, and check that Vanilla still renews the lease on every Hunter update.

## Hunter native sites

Owner: `Wildlife.HunterNativeSites` (generators in `Wildlife.HunterNativeAssembly`, executed by
`tests/WorkerProductionBonus.NativeTests/HunterSiteTests.cs`); users: `Wildlife.HuntablePreyRuntime`
(target policies, meat yields) and `ScriptExtenderFixes.HunterEventIdentityRuntime`. Reference native
SHA-256 `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; site group
`Wildlife.HunterSites`. Two RedBird `X64InlineHook`s with custom generators, purely native, published
together and never unpatched. Any other hash, a snapshot or live byte difference, a changed
continuation (a foreign `jmp` at the continuation is accepted), a backend span other than the audited
length or a generated stub that differs from the expected bytes leaves both unavailable; target
policies, meat yields and the event-id correction are then refused or inactive. No pattern fallback.

- Query entry `0x18AF50`, 14 bytes `48 89 5C 24 18 55 56 57 41 54 41 55 41 56`, continuation
  `41 57 48 83 EC 40`. ABI `0x18AF50(rcx = unit manager, edx = querying unit id)`, six callers
  (`0x12FDBC`, `0x12FF7E`, `0x130521`, `0x1305C7`, `0x1306DD`, `0x1941B4`). The stub stores EDX and
  `[rsp]` (the caller's return address) in a private slot, using RAX and R10, which are free at a
  function entry, then replays the bytes. A return address of `0x1941B9` marks the presentation-only
  query of `0x1940C0` (idle speech of the selected unit). No inbound branch into the span; the
  Script Extender's hook at `0x18AFE6` is located from the function start by its own scan at
  Extender startup, before APIShared publishes.
- Corpse arrival `0x1306AE` in HunterUpdate `0x12FC70` state `0xB`, 15 bytes
  `66 42 83 BC 31 E6 06 00 00 2C B8 E8 03 00 00`, continuation `66 42 89 84 31 20 09 00 00`.
  Registers: RBX = Hunter id (loaded from the current unit `0x9302C4` at `0x12FC8C`), RDI = Hunter id
  * 0x490, R14 = unit manager (`0x130695`), RCX = corpse id * 0x490, EAX = corpse global, just compared
  with the Hunter's target at `0x1306A4`. Behind a demand byte the stub writes Hunter global
  (`[rdi+r14+0x6F0]`), corpse global and corpse type (`[rcx+r14+0x6E6]`) to a 10,000-entry table, saving
  R10/R11; the flags are dead at entry because the replayed `cmp` sets them. Every butchered animal
  passes it exactly once; no inbound branch into the span.
- The meat-pickup correction reads the dispatcher's current unit `0x9302C4` (same constant as
  `WorkerProductionBonusNativeDefinition.CurrentUnitRva`); the Extender's pickup hook at `0x12FD7C`
  runs inside HunterUpdate, where that value is the Hunter. The query correction writes the backing
  field `<HunterUnitId>k__BackingField` of `UnitHunterQueryTargetEventArgs`, validated on a probe
  instance at startup.
- Before adding a hash: re-audit all six query callers and their arguments, the state-`0xB` arrival
  (registers, the global-id check before it, every path into state 2), the yield call `0x1309FE`, and
  the Extender's two hooks (stack shift and registers) for the installed Extender and RedBird.

## Hunter owner gates

Owner: `Wildlife.HunterOwnerGates` (generators in `Wildlife.HunterNativeAssembly`, executed by
`tests/WorkerProductionBonus.NativeTests/HunterOwnerGateTests.cs`). Reference native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; site group
`Wildlife.HunterOwnerGates`. Three RedBird `X64InlineHook`s, purely native, installed together on the
first huntable-prey registration with own-player prey (only before the first mission,
`NativeInlinePatchWindow`) and never unpatched. They read one 65,536-byte
per-type table (set at mission activation, cleared at its end). Any other hash, snapshot or live byte
difference (for example an older ImprovedHunters that patched the same spans), a changed continuation,
a backend span other than 14 bytes, a stub that differs from the expected bytes, or an arrow gate block
whose SHA-256 differs from `C3BCA2C9â€¦AE225F` refuses the install; the registration is then Unsupported.

- Query `0x18AFD8` `66 83 BB F6 FD FF FF 00 0F 85 F7 00 00 00` (`cmp word[rbx-0x20A],0; jne 0x18B0DD`),
  continuation `0F B7 83 EE FD FF FF` (the Script Extender's type hook starts there). RBX = candidate
  field `+0x8F8` (type at -0x212, owner at -0x20A), R13 = manager + Hunter id * 0x490. Free: RAX, R8-R11,
  RDI; flags dead. An owned candidate passes when its type is flagged and its owner equals `[r13+0x6EE]`.
- Explicit attack `0x18EBDA` `41 39 84 89 3C DF 7E 03 0F 84 BF 00 00 00` (team table `0x37EDF3C` via R9 =
  image; `je 0x18ECA7`), continuation `41 83 FC 06`. R12D = attacker type, RCX/R13 = target/attacker
  owner, target type at `[r8+rbp+0x6E6]`. Free: RAX, R11; flags dead. Same team passes only for a Hunter
  on a flagged type with equal owners.
- Arrow `0x192AA7` `43 39 84 19 3C DF 7E 03 44 0F B7 5C 24 78`, continuation `75 49` (reads ZF). CX =
  projectile type, R8W/R10W = projectile/victim owner, R12 = victim type, R13 = projectile offset, R11 =
  image until the replayed `movzx`; source unit at `[r11+r13+0x2F7A736]`, projectile height at
  `[r11+r13+0x2F7A6D0]` (both read by the gate block `0x192B00`, whose hash pins them). RDI is saved around
  the table read. An allowed hit repeats the dead and height gates (miss `0x1929D0`) and continues at
  `0x192B93`, behind the local "under attack" warning; every other case restores ZF = 1.
- Corpse removal (in the corpse-arrival stub of [Hunter native sites](#hunter-native-sites)): a type flagged
  in a second per-type table gets alive state 3 at `[rcx+r14+0x6E4]`; the unit pass `0x182B00` then calls
  removal `0x1869C0`. Re-audit HunterUpdate state 2 (`0x13072E`-`0x130814` reads no corpse field) before
  adding a hash.
- Before adding a hash: repeat the register-liveness audit of all three spans, the team-table and
  projectile-record layout, and check that no other plugin patches the spans.

## Herds

Owner: `Wildlife.HerdRuntime` (layout in `Wildlife.HerdNativeContract`, flight generator in
`Wildlife.HerdNativeAssembly`, executed by `tests/WorkerProductionBonus.NativeTests/HerdFlightGateTests.cs`;
layout, calm marks and route edits in `tests/APISharedTests/HerdTests.cs`). Reference native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; no pattern fallback, an unknown image
leaves herds unavailable. Static audit of 2026-11-08.

- Records: tribe manager `0x7CC6720` (the `lea rcx` at `0xCE272` passes it to the herd maintenance
  `0x1261F0` and the dispatcher `0x117520`), 4500 records of `0x688` bytes, id 0 reserved. The Extender's
  `GameTribe` starts `0x2A` after a raw record; `TryValidateLayout` checks its named fields against raw
  owner `+0x2C`, global id `+0x34`, state `+0x40` (2 in use), leader `+0x5A`, count `+0x5C`, member bits
  `+0x60` (`0x271` words by unit id), waypoints `+0x5B4`, route index `+0x5DC`, route count `+0x5DE`.
  Unnamed: kind `+0x42` (dispatcher `0x1175D9`, written by the spawn at `0x11E202`) and flight `+0x5F4`
  (deer update `0x124F75`; set to 1 on a threat, counted up and reset; rabbits use it alike at `0x125E60`).
- Kinds by the dispatcher's two jump tables: deer `0xD` -> `0x124E80`, lion `0xE` -> `0x1259D0`, rabbit
  `0xF` -> `0x125D00`, camel `0x10` -> `0x124890`, goat `0x28` -> `0x125290`, hyena `0x29` -> `0x1256A0`,
  crocodile `0x2A` -> `0x124B90`. Other kinds are troop groups.
- Spawn `0x11E150(manager, art, x, y, height)`, entry `44 89 4C 24 20 44 89 44 24 18 89 54 24 10 53 55 56
  57 41 54 41 55 41 57 48 83 EC 50`: claims a record (`0x119C00`), refuses x or y above `0x31F`, an unused
  tile and tile mask `0x4A5014B1`, then creates the animals (`0x17FEF0`, `0x11D370`) and returns the herd id
  or 0. Arts: deer `0xDC` (9-12), lion `0xDD` (3-4), rabbit `0xDE` (14-21), camel `0xDF` (2-5), goat `0xE2`
  (4-5), hyena `0xE3` (6-9), crocodile `0xE5` (1); `0xE0`, `0xE1`, `0xE4` are no herds. Animals above the
  wanted number get alive state 3; `0x1261F0`, every tick before the dispatcher, removes members whose state
  is not 1, 2 or 4 (`0x123EA0`) for every record in use.
- Routes `0x127E60(grid, herd, reach, mode)`, Indirect detour of 10 bytes (`mov [rsp+0x20],rbx; mov
  [rsp+0x18],r8d`), continuation `55 56 57 41 54 41 55 41 56 41 57 48 83 EC 70`; no branch in the function
  targets `[0x127E60,0x127E74)`. Callers: `0x124890`, `0x124E80`, `0x125290`, `0x1256A0`, `0x1259D0`,
  `0x125D00` (rabbits also `0x129010`). It zeroes index and count (`0x1283D6`, `0x1283E1`) before its only
  early exit (`0x128931`) and appends waypoints (`0x128A20`). Callers either test `count > 0` or walk to
  `route[index]` without it (`0x1249F2`, `0x124FBE`, `0x125829`, `0x125B6D`), so an emptied route
  gets the leader's tile as its first waypoint.
- Flight gates `0x124F20` (deer) and `0x125330` (goats), 18 bytes `8B 7C 08 28 03 7C 08 48 44 8B 7C 08 40 44
  8B 64 08 44`, continuation `66 44 89 AB F8 05 00 00`; both functions are byte-identical but for the type
  constants. No branch targets the spans. RBP = herd id (`movsxd rbp, edx`), EDI = threat sum, read once at
  `0x124F71` and written again before any other read; RAX and RCX are written again on both paths behind the
  span; flags dead. Sector grid `0x3395E10` (`lea rcx` at `0x124EFA`), 80 by 80 sectors of 10 tiles, stride
  `0xA0`, `+0x28` predators, `+0x48` units and fleeing deer and goats (`0x126810`).
- Before adding a hash: repeat the dispatcher, spawn-art and route-reader audit, the register liveness of
  both flight spans, the raw field map against the Extender's `GameTribe`, and check that the Fixes mod
  (it hooks `DeerUpdate` `0x156130`, not these functions) and other plugins leave the spans alone.

## Corpse lifetimes

Owner: `Wildlife.CorpseLifetimeNative` (generator and arithmetic in `Wildlife.CorpseLifetimeNativeAssembly`,
executed by `tests/WorkerProductionBonus.NativeTests/WildlifeEconomyNativeTests.cs`; catalog combination in
`tests/APISharedTests/HuntablePreyTests.cs`). Reference native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; no pattern fallback (the whole HuntablePrey
service is bound to this image), an unknown image leaves corpse lifetimes unavailable. Static audit of 2026-11-14.

- Fading branch (AI state `0x6E`, raw `+0x918`) of the rabbit `0x157810`, camel `0x158020` and chicken `0x162CB0`
  updates: the fade byte `+0x986` (signed, read by the render export at `0x43C21`) and the counter `+0x920`
  (signed word) grow by 1 per update, chickens by 2 (`0x16346B`/`0x163477`/`0x16347F`/`0x16349F`); one
  `mov ecx, 0x20` gives both the fade cap (`cmp byte,cl; cmovg`) and the counter threshold (`cmp word,cx; jle`);
  above it the update writes alive state 3 (`0x157F02`, `0x1584FE`, `0x1634B6`) and the unit pass deletes the
  corpse. Outside `0x6E` the fade byte counts down again (`0x1578CA`). Rabbits and camels reset the counter on
  entering `0x6E` (`0x157ECD`, `0x1584A2`); the chicken entries `0x163426`/`0x163456` do not.
- Camel span `[0x1584BE,0x1584D1)` `FE 84 2B 86 09 00 00 B9 20 00 00 00 38 8C 2B 86 09 00 00`, continuation
  `0F B6 84 2B 86 09 00 00 0F 4F C1`; chicken span `[0x16346B,0x16347F)` `FE 84 2B 86 09 00 00 B9 20 00 00 00
  66 FF 84 2B 20 09 00 00`, continuation `FE 84 2B 86 09 00 00 38 8C 2B 86 09 00 00`. Anchors `0x1584B8`
  (`cmp si,0x6E; jne`), `0x163465` (`cmp di,0x6E; jne`), `0x1584EB` and `0x1634A7` (counter compare). No direct
  branch and no table entry in the image targets the span interiors; RCX is dead before the replaced `mov`, flags
  are recomputed by the following compare (camel) or are dead (chicken). Stubs replay the span with `mov rcx,cell;
  mov ecx,[rcx]` for the constant; the cell holds `0x20` (Vanilla) unless a catalog asks for longer.
- Rabbit: the constant at `0x157EDC` (`FE C2 B9 20 00 00 00 3A D1` from `0x157EDA`) is the Script Extender's
  `GameGlobalsManager.RabbitDespawnTickTime` (`B9 ? ? ? ? 3A D1`); APIShared validates that the Extender's
  site is that instruction with original value 32 and only places an owner-bound `SetOverride`, never its own
  patch. Script Extender and Fixes AOBs, and the workspace mods, do not touch the two camel/chicken spans
  (scan of 2026-11-14); an older ImprovedHunters wrote the same two immediates directly.
- Encoding: ticks * step; at or below `0x20` stays `0x20`; otherwise rounded up to `256 * n + 8` (cap 8 keeps a
  long-lived corpse visible, as ImprovedHunters' former 1800 did), at most `0x7F08`; public limit 16000 ticks.
- Writes happen only in `HuntablePreyRuntime.Activate`/`EndWorld`, which `WorkerProductionRuntime` calls under the
  engine monitor (`EngineInterface.threadLock`) before a mission's first admitted tick and at its end.
- Before adding a hash: repeat the fading-branch audit of all three updates (both meanings of the constant,
  counter start, deletion), the span register/flag liveness, the Extender's rabbit pattern, and the render use of
  `+0x986`.

## Build-menu availability

Owner: `Buildings.BuildMenuAvailabilityRuntime` (generator `Buildings.BuildMenuAvailabilityAssembly`, executed
against Vanilla's own function body by `tests/WorkerProductionBonus.NativeTests/WildlifeEconomyNativeTests.cs`).
Reference native SHA-256 `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; no pattern fallback.
Static audit of 2026-11-14.

- `0xFFC70(record rcx, mapper edx)`, a leaf without stack use. Callers: export `DLL_IsMapperAvailable` `0x84A20`
  (`8B D1 48 8D 0D 07 2D 24 03 E9 42 B2 07 00`, record `0x32C7730`; managed `EngineInterface.IsMapperAvailable`
  under `threadLock`, result `> 0`, used by `MainViewModel.CanPlaceMapper`, `HUD_Main.SetupNewBuildScreen`,
  `KeyManager.Update`, `GameMap.processTestMap`), `0xC2030` (mappers `0x60`/`0x61`, `!= 0`) and the UI state
  producer `0x19D960` from `DLL_RunTick` (recruitment mappers `0x123`, `0xBE`..`0xC2`, `0x166`). No simulation,
  AI or command path asks it.
- Branches: game type `0x3665F28` (0 campaign, 1 map editor, 2 freebuild, 3 skirmish, 4, 6 siege editor) with
  special tables for 2/3 and the trail switches `0x87EE2F0`/`0x87EE2F4`; network mode `0x8574B90` (0 single
  player, 99 skirmish, 1 network): Hunter's hut `0x4E` off without deer/goats at map start (`0x37EF97C`), dog cage
  `0x138` off with NoDogs (`0x37EF964`, BugfixesAndQoL NOPs `0xFFD98`); game type 4 refuses `0x34`,
  `0x3D`..`0x40`, `0x4E`, `0x4F`, `0x15B`, `0x15C`, `0x179`..`0x17B`; final branch `0xFFF92`
  (`81 FA B7 01 00 00 0F 87 E6 FD FF FF 48 63 C2 41 0F BF 84 40 2C 02 00 00 C3`) returns the mission's signed
  availability word `record + 0x22C + mapper * 2` for mappers below `0x1B8`.
- Entry span `[0xFFC70,0xFFC7E)` `8D 82 4E FF FF FF 4C 8B C1 83 F8 01 76 14` (the backend displaces exactly 14),
  continuation `83 FA 35 74 0F`. No branch or table entry targets the span interior. The stub reads one byte per
  mapper (0..511): 0 replays the entry and continues; Veto returns 0; Allow calls a replayed copy (RSP 8 mod 16 at
  the entry, frame `0x38`), keeps a result above 0, else returns 1 unless the mission word for a mapper below
  `0x1B8` is 0 or below. Clobbers only RAX, R8, R10 and flags, all free at a function entry.
- Before adding a hash: repeat the caller list, every branch for the documented mappers, the record argument of
  the export and the final table branch.

## Chicken population

Owner: `Wildlife.ChickenPopulationRuntime` (generator `Wildlife.ChickenPopulationNativeAssembly`, executed by
`tests/WorkerProductionBonus.NativeTests/WildlifeEconomyNativeTests.cs`). Reference native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; no pattern fallback. Static audit of 2026-11-14.

- `0xD29E0(player manager)`, reached only from the scheduled tick step `0xB4` of `0xD1AD0` (`0xCDE60`,
  `DLL_RunTick`). Returns at once in game types 1 and 6. Loops players 1..8 (`ebx`, `rdi` = manager + `0x1345D0`
  + player * `0x583C`), active when `[rdi-0x140] > 0` and `[rdi] > 0`. Target from `[rdi+0x1EC8]` (probably the
  food stock; meaning of it and of the divisor `[rdi+0x1FAC]` not confirmed): 0
  below 1, 1 below 10, 2 below 20, 3 below 30, else `min(10, food / max(1, 2 * [rdi+0x1FAC]) + 3)`; 100 while
  `0x3665FE0` is set (`cmovne eax,r13d`, R13D = 100). Compared with the player's count `[rdi+0x2048]`: below,
  `0xBF510` picks a random bakery (17), mill (34) or granary (19) of the player (game random numbers) and
  `0x17FEF0` spawns a chicken (type `0x3E`) there; above (`jge` at `0xD2BA7` on the same flags), the first unit of
  the player with type `0x3E` gets AI state `0x6E` (fading, also when it already fades). The writer of the
  count was not traced.
- Span `[0xD2AB4,0xD2AC5)` `83 3D 25 35 59 03 00 41 0F 45 C5 3B 87 48 20 00 00`, continuation `0F 8E DC 00 00 00`;
  the four `jmp 0xD2AB4` at `0xD2A72`..`0xD2A93` enter at the span start, nothing targets its interior. Anchors
  `0xD2A07`, `0xD2A10`, `0xD2A16`, `0xD2A34`, `0xD2BA7`, `0xD2BFA`. Live in the loop: R9D `0x6E`, R10D 10, R12..R15,
  RBP 1; RCX, RDX, R8 and R11 are written before any read on both paths; flags are recomputed by the replayed
  compare. The stub replays the first two instructions, calls the managed callback only while the demand dword is
  set (saving RCX, RDX, R8..R11 and XMM0..5, dynamic 16-byte alignment) and replays the compare.
- Before adding a hash: repeat the target formula, the player loop registers, both branches on the compare's
  flags and the caller chain to `DLL_RunTick`.

## Line of sight

Owner: `Sight.LineOfSightNative` (public facade `Sight.LineOfSight`, guide
[docs/LINE_OF_SIGHT.md](docs/LINE_OF_SIGHT.md); executed by
`tests/WorkerProductionBonus.NativeTests/SightDispatchTests.cs`, managed rules in
`tests/APISharedTests/SightTests.cs`). Reference native SHA-256
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; no pattern fallback,
an unknown image leaves the service unavailable. Static audit of 2026-11-14 (Vanilla
facts in the baseline `knowledge/LINE_OF_SIGHT.md`). Calls only, no hook; site group
`Sight.LineOfSightCalls` (CallOnly `0xA06F0`/1, `0x9E350`/1).

- Wrapper `0xA06F0`, validated as all 156 bytes (`48 8B C4 48 89 58 08 ... 41 5C C3`): forward
  `0x9E350(ctx, start, end, 0)`, reverse `0x9E350(ctx, end, start, 0)` only after a zero result.
  Both `call rel32` resolve to `0x9E350` (`0xA0743`, `0xA0768`).
- Core `0x9E350`, entry `44 89 44 24 18 89 54 24 10 48 89 4C 24 08 53 55 56 57 41 54 41 55 41 56
  41 57 48 83 EC 68`. Its only non-stack store is `[ctx+0xC]` (eight stores); the eighth
  argument (`[rsp+0xE8]` after the prologue) switches all obstacle checks off when not zero,
  so `Directional` always passes 0. Tiles are `x / 8`, `y / 8` through the row table
  `0x402FF2C` and the tile manager `0x405EDB0` without bounds checks: the facade rejects
  points outside the map diamond and heights beyond +/- 100000.
- Rule and eye-height anchors (exact bytes): `0x18B052` `83 C1 1A` (target +26), `0x18B070`
  `83 C1 1E` (viewer +30), `0x18B0A2` `E8 49 56 F1 FF FF C8 3D AF 01 00 00 77 1F` (visible
  `1..0x1B0`), `0x18E9DE` `C7 44 24 50 1E 00 00 00` (viewer default +30), `0x18ED3C` `83 C1 1A`,
  `0x18ED6A` `E8 81 19 F1 FF 8B D0 85 C0 0F 8E EB 00 00 00` (visible above 0). Unit fields are
  raw record origin `0x65C` relative: `+0x70E/+0x710/+0x712/+0x714` = `GameUnit +0xB2/+0xB4/+0xB6/+0xB8`,
  all read signed.
- Before adding a hash: repeat the wrapper and core audit (stores, eighth argument, result
  values, bounds), both callers' offsets and rules, and check that no Script Extender or Fixes
  hook changes the wrapper or core semantics.

### Sight-blocking buildings

Owner: `Sight.SightBlockingBuildingsNative` (public `Sight.SightBlockingBuildings`, generator
`Sight.SightNativeAssembly`). Same reference hash, no pattern fallback. Site group
`Sight.BuildingHeightDispatch`: Exclusive `0x6BA0C`/17; Observe `0x6B9F1`/27 (image-base `lea`
and type switch), `0x6BA1D`/5 (jump) and `0x6BAB4`/88 (case and type tables). Installed on the
first registration inside `NativeInlinePatchWindow`, never unpatched.

- Helper `0x6B990(tiles, tile, mode)`; callers only the sight core (`mode = 1`, six calls) and the
  projectile routine `0x9C730` (`mode = 0`, `0x9CD70`, `0x9CDD5`).
- `0x6B9F1` `4C 8D 05 08 46 F9 FF` (`lea r8`, image base); `0x6B9F8` `4E 0F BF 9C 11 2E 01 00 00 41 8D
  43 F9 83 F8 47 77 37 48 98` (type word `+0x12E`, types `7..78`).
- Span `0x6BA0C` `41 0F B6 84 00 C4 BA 06 00 41 8B 8C 80 B4 BA 06 00` (two table loads, 17 bytes, the
  RedBird probe must displace exactly 17); continuation `0x6BA1D` `49 03 C8 FF E1`. No direct
  branch targets `(0x6BA0C, 0x6BA1D)`. RCX is free before the second load, flags are dead until
  `add rcx,r8`; R8-R11, RBX, RSI, RDI are untouched by the stub.
- Case targets `0x6BAB4` = `0x6BA3D, 0x6BA30, 0x6BA22, 0x6BA41`; case 0 `0x6BA3D` `85 F6 75 56`
  (sight mode adds nothing), case 3 `0x6BA41` `47 8B 8C 98 60 8C 2E 00 EB 4C` (height table
  `0x2E8C60`, type 7 = 40). Type bytes `0x6BAC4`: case 0 only for type 7 (`STRUCT_HUNTERS_HUT`),
  which `IsExemptInVanilla` derives from the validated load image.
- Before adding a hash: repeat the helper audit (all callers and modes, case table, register and
  flag liveness of the span, incoming branches), rerun the native test and check that no other
  owner hooks `0x6B990`.

## Native unit-pair exclusion rules

The attack target rule entry is pinned to FBCB9319 at RVA `0x1867A0`.
The Indirect detour displaces 8 bytes (instructions of lengths 5 and 3).
The next instruction is `48 8B D9`. The full verified prefix includes the
remaining original prologue through `0x1867B2`. Callers at `0x18795C`,
`0x188ACE` and `0x18EBFF` pass manager, attacker slot, target slot and a
byte mode. Nonzero excludes. The live entry must be pristine.

The manager at `0x67E8400` uses the `0x65C` one-based sentinel and
`0x490` unit stride. Controller, unit type and global identity use the
verified Extender fields. The optional peace gate uses the existing validated
native flag anchor. No pattern fallback is allowed because the manager binding,
unit layout and complete caller contract are image-specific. Revalidate the
three callers, entry and continuation, live ownership, old QoL compatibility
and all disjoint QoL body patches for a new image.

## Centered gatehouse distance

For native SHA-256 `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`,
the existing hook at `0xB7B70` replaces 75 bytes ending at `0xB7BBB`.
`RBX` is the one-based building ID times the native building stride and
`RBP` is the module base. The grid-width DWORD is at
`[RBX + RBP + 0x64CCD04]`, corresponding to `GameBuilding.r_OccupyTileGridSize`
at +0xF8 in the typed record. The native coupling caller `0xC5300`
uses the same field. The begin coordinates arrive sign-extended in
`R15D`/`R12D`. The selected access tile fields at `0x64CCD0A/C`
are independent of the footprint and are excluded from the calculation.

The centered branch reads both unit coordinates before clobbering `EDX`.
Editor placement `0x118CA0` supplies `tile * 8` to `0x17FEF0`, which
adds four before writing unit world X/Y. Tile bounds start at `tile * 8`.
The footprint center is `begin * 8 + size * 4` on each axis.
It returns the same Chebyshev distance representation in `R8D`.
An unrecognized grid size or inactive centered origin takes the displaced
Vanilla path. The gatehouse distance/timing layout remains hash-bound.
On an unvalidated build or incomplete rule initialization the managed
range query declines to skip candidates. Re-run the private-buffer native
distance and gate/bridge backend tests after any layout or code change.
## Vanilla Lord message reference catalog

Owner: `Presentation/VanillaLordMessages`; read-only, no native hooks. Reference
native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
The full enqueue/presentation audit is retained in the workspace native baseline's
`knowledge/LORD_MEDIA_LOOKUP.md` and `knowledge/NOTIFICATION_LAST_FRAME.md`.

The native wrapper at RVA `0x103AA0` obtains its original speech Lord from the
AIC record. The read-only catalog resolves three complete-instruction anchors:

- `0x103ADC`: original AIC indexing followed by `lea r15,[rip+disp32]` at
  `0x103AEA`. Its resolved target must be module RVA zero. The adjacent built-in
  limit is 29, crosschecked against the Extender Lord enum.
- `0x103B22`: generic message formatting indexes the audio table using
  `message * 0xF0`, then loads `[rbx+r15+audioRva]` at `0x103B38` and
  `[rbx+r15+videoRva]` at `0x103B45`.
- `0x103BA3`: the built-in branch computes `message * 30 + lord`, independently
  loads `[r15+rcx*8+videoRva]` at `0x103BB8` and
  `[r15+rcx*8+audioRva]` at `0x103BC3`, and computes localized text as
  `(lord-1)*34 + message` in text section `TEXT_SKIRMISH_SPEECH` (221).

The reference table RVAs are `0x316450` (audio) and `0x318340` (video), but
production derives them from both addressing forms and rejects disagreement.
All anchors must occur in one ordered executable-code neighborhood; original
pointer tables and NUL-terminated ASCII filenames must remain inside mapped
non-executable PE sections. The snapshot's relocated pointers are interpreted
relative to the actual module base. All 29 built-in Lords and message categories
1 through 33 are copied to immutable managed references once; announcements,
custom-AIC slots and nickname pseudo-categories are not catalog references.
The active Extender speech/video loaders continue to select language and overrides.

Known identities validate reference bytes without scanning. A missing/changed
reference takes a unique executable-section semantic pattern fallback. Unknown
identities use the same fallback with independent stride, base, localization and
pointer validations. Resolved anchor RVAs are cached under
`BepInEx/cache/APIShared/lord-messages/<full-native-hash>-<APIShared-version>.bin`.
A cache hit revalidates complete anchor patterns and addressing; an invalid cache
is ignored and rescanned. No caller-provided RVA or absolute pointer is accepted.
Validation failure disables only Vanilla references and reports the precise cause.

For a game update, re-audit both wrapper branches, AIC original-type mapping,
full table bounds, the two addressing forms, section layout, original filenames,
localization stride and enums. Execute `VanillaLordMessageTests` against the
canonical installed DLL. These are static source-reference checks; they do not
establish live decoding, audible playback or multiplayer acceptance.

### Lord presentation record protocol

The shared Lord message broker separately supports the exact audited native hash
above. Its managed IL contract validates the loaded `GameMap.processTestMap`
consumer, but that alone cannot prove the native producer's record grouping.
`0xFEE50` supplies audio-present in the fourth argument of `0x1A2850` and
video-present in its fifth argument; the emitter writes those booleans to words
13 and 14 of the kind-20 record. A complete Lord group then contains channel-1
speech at gain 100, a kind-22 control record, optional audited frontend
music/ambient/old-video-stop controls, and a nonlooping kind-21 video start
(value zero, speech-wait one). The native queue/presentation paths at
`0x103290`, `0x102A70`, `0xFE570` and `0xFEE50` and their relevant callees form
this protocol's closed audited controlflow; the existing Extender interior
speech-path relocation preserves it.

An unknown native identity disables the presentation broker even if the separate
Vanilla catalog's table-pattern fallback succeeds. Those table anchors prove
addressing and bounds, not this whole emission/controlflow contract. Extending
broker support requires a fresh feature audit of the complete producer and
queue/lifecycle chain, buffer transport, header flags, signed text selectors,
allowed intervening records and media flags. The consumer continues the original
managed path without changing the records when the protocol or any individual
group is unsupported. No speculative group protocol is inferred from old RVAs.

## Unit voices

This section owns the binary update contract for `Audio/UnitVoices`. General
consumer behavior, resources and callback contracts belong in `docs/AUDIO.md`.
The values below describe the audited binary, not a promise about future games.

Reference SHA-256:

`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`

Audit observation: 2026-11-11. Source: the canonical installed `CrusaderDE.dll`,
the current semantic baseline selected through `CURRENT.json`, its complete
decompiler export and raw Ghidra xrefs. The corresponding Script Extender source
identity in the baseline is commit
`4014ca67c96974a5e6d697665baf5351cbfebde6`, tree
`372b43a9e9d614075c3b827d3df2a2346ec356fa`.

### Native interception and ABI

Every target uses the existing RedBird NativeX64 Absolute backend with the
audited minimum backend version and actual prepared/published hook validation.
Callbacks, delegates, bridges, trampolines and guard storage remain rooted until
process exit. Per-source activation modifies private data bytes, never code.

| Source | Entry RVA | Displaced bytes | Function bytes | First continuation |
|---|---:|---:|---:|---:|
| `Troop.Dispatcher` | `0x3BC0` | 16 | 1244 | `0x3BD0` |
| `Engineer.SpecialActions` | `0x3770` | 14 | 300 | `0x377E` |
| `Civilian.Selection` | `0x97DA0` | 15 | 426 | `0x97DAF` |
| `Recruitment.European` | `0xD5B40` | 15 | 671 | `0xD5B4F` |
| `Recruitment.Bedouin` | `0xD5DE0` | 15 | 585 | `0xD5DEF` |
| `Recruitment.Arabian` | `0xD6290` | 15 | 649 | `0xD629F` |
| `Recruitment.Support` | `0xD6030` | 15 | 594 | `0xD603F` |
| `Recruitment.Tunneler` | `0xD6830` | 15 | 365 | `0xD683F` |
| Common speech output | `0xEDF0` | 14 | 179 | `0xEDFE` |

`UnitVoiceNativeContract.Targets` contains the complete original byte sequence
for each span. Whole-function disassembly and the hash-bound xref export contain
no direct incoming call or jump into any open displaced interval. Calls and data
references to the entry itself are permitted. Runtime requires original and live
entry bytes to match before preparing the corresponding hook. An occupied source
entry disables only that source; common identity, layout or output failure
disables native capture as a whole.

All nine audited functions return `void` and have at most four integer/pointer
arguments. A shared Win64 four-register delegate forwards RCX/RDX/R8/R9 unchanged;
extra arguments are ignored by the lower-arity original functions. No floating
point parameters or fifth stack arguments exist in these signatures. Source
callbacks save and restore a primitive thread-local capture frame around exactly
one original invocation. Native code does not invoke public mod predicates.

The dormant guard uses RAX as scratch and tail-jumps to the original trampoline.
The active branch tail-jumps to the rooted callback. The audited entries do not
consume incoming RAX or flags before replacing them. The engineer prologue
contains `cmp edx,39; ja 0x389B`; its relocated branch must preserve that absolute
target and both stack outcomes. Dispatcher and civilian prologues contain
RIP-relative reads; validation checks the relocated addresses. The output
prologue ends with `cmp dword ptr [rcx+0x28],0`, whose flags are consumed later;
the original instruction is replayed in the trampoline.

The current implementation deliberately fails closed for an unknown native hash.
Entry matching alone cannot recover the shared globals, native voice-table
layouts, selected-civilian identity or the transport copy contract. There is no
semantic data-layout fallback for these dependencies. A new hash requires a
feature audit and an updated contract, not changing the accepted hash to silence
a check. Existing SFX audio remains independent.

### Native source meaning

`0x3BC0` receives the requested unit type and situation. The requested type is
different from the selected voice bank: siege devices can use engineer voices,
and Monk selects Monk or Temple Guard according to religion. The dispatcher also
routes engineer actions to `0x3770` and Bedouin healer/sapper/demolisher actions to
`0x38A0`. The surrounding dispatcher scope survives those calls.

The direct dispatcher callers are `0x11C3F0`, `0x11C490`, `0x11C530`,
`0x11C580`, `0x11C5D0`, `0x11C620`, `0x11CA70`, `0x11CC30`, `0x11D190`,
`0x143400`, `0x191250`, `0x1918A0`, and `0x195E30`. These cover selection,
movement, attack, moat excavation, disband and special acknowledgements. The
native table positions used for public action interpretation are 0,6,9,10,11,
17,21,25,41; position42 normalizes to21 on the ordinary path. Attack-group names
remain inferred rather than claiming in-game confirmation.

`0x3770` is additionally called directly by `0x8C5F0`, `0x90CD0`, `0x11C650`,
`0x191250`, `0x1918A0`, and `0x199B70`. Direct special actions do not reliably
identify one requested unit type, so that nullable field remains unknown. A nested
healthy engineer scope retains the outer requested type. If the engineer source
is unavailable, its special-table assets cannot be relabelled as a healthy outer
dispatcher source.

Recruitment scopes capture the originally requested type, while known success
assets classify the actual acknowledgement. Their original functions retain all
resource/capacity checks and cooldowns. Error calls from those same functions
use Genie or adviser assets and are excluded by the unit cue catalog. The
confirmation may reuse a previously stored type for its voice; the requested
type and actual voice-bank fields intentionally remain separate.

`0x97C70` calls the civilian selection routine `0x97DA0`. Its selected unit ID
comes from RVA `0x67E839C` and is passed unchanged as a one-based ID to the captured
`GameUnitManagerAPI` through `UnitAccess`. The unit's type and actual owner are
read while the existing engine monitor is held. The selected comments are chosen
by `0x18CE60` and `0x1940C0`, based on work and settlement state. Male/female
Peasant voice banks do not imply a Peasant unit type. Native early gates exclude
Lords/Ladies and several non-speaking types; the API does not create voices for
them or interpret combat grunts as speech.

The requester is the current native view at RVA `0x88E3D70`. It is not promoted
to an independently verified owner for dispatcher or recruitment scopes. Only
the civilian selected-unit path supplies a direct owner snapshot. No live unit
pointer, ID or claimed identity crosses to Unity. Player IDs are one-based and
unknown values remain nullable.

The unit manager and validated engine-monitor object are captured at startup.
The callback requires `Monitor.IsEntered` on that same monitor; it never acquires
a competing engine lock or constructs a lazy manager. The installed SDK fields
are public and validated at runtime: type `+0x8A`, owner `+0x92`, global ID
`+0x94`, with record stride `0x490`.

### Voice tables and managed routes

The catalog is generated from the audited native tables, independently of the
user's language-folder inventory:

| Table | Reference RVA | Contract |
|---|---:|---|
| Ordinary banks | `0x2AD298` | 42 situation rows, stride `0x150`, 27 pointer columns |
| Ordinary counts | `0x2AD290` | Variant counts at each `0x150` row |
| Ordinary counters | `0x2AD370` | Per-bank counters inside each row |
| Arabic engineer ordinary bank | `0x2B0EB0` | 42 pointers |
| Engineer special actions | `0x2B09B0` | 40 rows, stride32; count, European/Arabic pointers, counter |
| General table unit subset | `0x2B1000` | Only indices65â€“76, strings at stride1000 |
| Male civilian comments | `0x2D7E50` | 126 slots at stride32, including intentional Null slots |
| Female civilian comments | `0x2D8E50` | Same layout |

The general-table unit subset is `Healer_Heal1â€“4`, `Sapper_Sap1â€“4`, and
`Demolisher_Sap1â€“4`. The remaining general table and arbitrary calls to generic
speech helpers are adviser/dialogue paths outside this API.

The observation contains 919 distinct non-Null native asset names. That count is
a dated measurement, not a public constant. Original managed routing is generated
separately from the actual `SFXManager` speech arrays in their initialization
order. In this observation, 29 native intent names have no managed speech-folder
entry. Their folder remains null and Vanilla stays silent for them
(`UnitVoiceContext.OriginalIsSilent`). An explicit replacement is played: a Vanilla
cue through its own route, a resource through the folder of the original's voice
bank. Native intent observers can still see an attributed attempted request.

Recheck for another image: the civilian guard skips the managed callback when the
selected civilian (`0x67E839C`) is zero or equals the previous selection
(`0x2D9E58`); `0x97DA0` compares them at `0x97E09`, stores at `0x97E11` and reaches
its only speech output (`0x3B20`) only when they differ. The Script Extender
also edits `EngineInterface.CopyPlayStateStruct` and the speech loader's
`<LoadClip>` `MoveNext` with IL hooks. Our edits there run through `SafeIlHook`;
after an Extender update, confirm that the copy hook and all five speech edits
still report `IsApplied`.

### Shared filename transport and original behavior

Sound manager base: RVA `0x88E72D0`. Its speech filename starts at `+0x41A6`,
global RVA `0x88EB476`, with 128 transport bytes.

The four native writer paths are `0xEDF0`, `0xEF20`, `0xF070`, and `0xF0F0`.
The latter three are unrelated speech/message paths and are not hooked here.
`0x19D960` copies 128 bytes to the returned play-state structure at `+0x600` and
then clears all 128 source bytes. `EngineInterface.CopyPlayStateStruct` reads up
to NUL, bounded by 128 bytes. `0x1A4270` appends record kind18 containing channel
and volume, but no independent filename or source identity. Every record 18 of one
`PlayState` uses that state's single final `speechFileName`.

The API therefore preserves Vanilla's last-writer coalescing. It does not replay
earlier per-request files that Vanilla overwrote. Internal ASCII tokens contain
the catalog ordinal and an emission sequence, fit the existing speech buffer,
and are introduced only at the common output argument. Generator counters,
original filename comparisons and busy-channel checks have already run.

EDF0 can reject before copying: negative manager state`+0x28` while channel 3 is
busy, or native mode 4. A new pending snapshot is published only after the actual
native filename equals its token. A rejected candidate leaves the older pending
snapshot intact. Untagged EF20/F070/F0F0 overwrites cannot inherit the snapshot,
even if they reuse an identical original filename.

The copy hook restores every valid internal token to an original filename before
the `PlayState` is published and attaches the matching immutable source via a
`ConditionalWeakTable`. The token's ordinal still restores the original if the
snapshot was lost. Multiple record 18 calls can share this binding; consumption
does not remove it. End/disable transitions stop capture but retain resolution
for already emitted tokens. Public invocation IDs are created per managed record,
separately from the internal emission sequence.

### Update and acceptance checklist

1. Verify the canonical DLL hash and baseline provenance. Re-audit every changed
   generator, alternative route, writer, copy and terminal playback path.
2. Derive each source function and span anew, checking incoming branches,
   original instruction boundaries, RIP operands, conditional targets and actual
   backend displacement. Never accept a new enum value or layout based on a name.
3. Rebuild catalog metadata from native tables; independently compare every
   managed folder against the real `SFXManager` constructor arrays. Preserve Null
   and unsupported-route behavior.
4. Check local Extender/Fixes source and live entry bytes for overlap. Preserve
   per-source failure isolation and the mandatory common pipeline boundary.
5. Run the catalog and native tests: all real prologue preparations, relocation
   checks, dormant/active guard execution, engineer conditional outcomes, original
   call counts, independent source rejection, token restoration, foreign writers
   and shared-state coalescing.
6. Complete actual game/editor and, separately, matching host/client acceptance:
   ordinary and special orders, recruitment, civilian comments, viewpoint changes,
   save/load, pause and speech-volume groups. Offline copied code does not establish
   audibility, Unity timing, actual startup-cleanup survival or multiplayer results.

The copied-prologue tests never execute the unrelocated game-body references.
Guard execution uses safe private native fixtures; the real engineer entry is
executed only with both continuations replaced by private stack-correct endpoints.

## Actual production goods receipts (2026-11-13)

Reference native SHA-256: `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
`ProductionGoodsReceivedNativeDefinition` owns only final storage calls: Wood12CA01/16,
Hunter130AF4/16, Pitch1338D5/16, Hops135998/16, Apple1368E4/22, Cattle137396/22,
Iron14E011/16, Wheat134A35/16, Miller138272/16, Baker1393BC/16, Ox132E3A/22,
QuarryGrunt1324F1/22, Brewer13A514/16, and standalone Tanner13EF38/16.
Every block ends in the direct call to `B6100`; the preceding argument loads and
normal continuation remain unchanged. Full owning-handler instruction packets and
all native xrefs have no interior target. Source bytes and continuation instructions
are pinned in the definition; unknown hashes fail closed because worker provenance,
manager layouts and exact source classification have no independent fallback.
The seven-argument native tail relay preserves the native result and stack ABI.
Demand zero jumps directly to the existing live Extender entry, without a receipt
managed callback. Demand one enters the shared receipt callback, whose managed
Original adapter measures the effective tuple and actual original mutation.
Weapon-workshop sources reuse the existing recipe Deposit owners after their
filters even when recipe demand is disabled. Tanner's standalone span is skipped
when ImprovedHunters is discovered; its permanently installed meat and Vanilla
branches both call the shared relay and confirm publication. Failed external
publication keeps receipt availability false rather than installing a competing hook.
No source hooks are removed or rewritten after publication; logical demand controls
observation. Native fixture tests execute both relay branches with all seven inputs
and the full64-bit result, validate actual backend displacement and generated source
bytes, and exercise recipe-independent delivery. These are private native fixtures;
actual game/new/load/editor and real multiplayer acceptance remain separate.
