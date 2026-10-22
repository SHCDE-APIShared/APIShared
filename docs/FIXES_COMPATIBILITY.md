# Fixes wheat-sale startup compatibility

## Behavior and limits

APIShared has an optional `fixes` BepInEx dependency. With the audited Fixes
installation, it corrects the wheat-sale inline hook's player-ID load once during
synchronous plugin initialization. Fixes remains optional; no public API, new
consumer dependency, simulation callback or configuration switch is introduced.
Fixes' settings and per-player flags retain their meaning.

The incorrect load uses the saved caller RDI as a flag-array index. A managed
detour can legitimately keep a profile pointer there, exposing an out-of-range
read. The correction loads the actual player argument instead. This protects
all callers reaching this particular hook, without changing their calling code.

Only `CORRECTED` and `ALREADY-CORRECT` confirm the fully validated stub.
`NOT-NEEDED` means Fixes or its wheat hook was absent; it is not a protection
claim. `REFUSED/UNCONFIRMED` means no confirmed protection, including errors after
a write. Every outcome includes a timestamp with milliseconds. Other APIShared
services continue. Unsupported native/game/backend contracts, unavailable startup
evidence or memory access failures cause refusal, never a relaxed signature search
or later retry. Since 2026-10-22 no component has an exact-version, maximum-version,
whole-file-hash or fixed-address gate here: neither the game, Fixes, Script Extender,
BepInEx, Unity, RedBird, Iced nor MonoMod. Loaded/disk module identities must agree,
and the actual code must satisfy the derived contract below, so updates keep the
correction working while that contract holds and refuse it when it does not.

This is a deliberately narrow exception to permanent-hook immutability: one
validated displacement byte of one known foreign stub, before normal game
initialization. No disk DLL is changed. There is no rollback, teardown or
map/settings-dependent executable mutation. The Fixes hook and its native state
remain owned by Fixes. APIShared's native-site registry describes module RVAs;
it does not misrepresent this dynamically allocated foreign stub as an owned RVA.

## Startup safety and lifecycle

`APISharedPlugin.Awake` calls `FixesWheatStartup.Initialize` before publishing
APIShared services. The installed Chainloader sorts soft dependencies as well as
hard dependencies, then synchronously calls `ManagerObject.AddComponent`.
During Awake its PluginInfo entry exists but Instance has not yet been assigned;
`_initialized` is true and `_loaded` is false. The latter becomes true only after
the plugin loop. Fixes' existing instance must already be published.

The actual stack must contain APIShared Awake inside Chainloader.Start inside
`UnityEngine.Application..cctor`, in that order. This is the entry patched by
the audited BepInEx preloader; a config setting alone would not prove the actual
call path. Missing/inlined stack evidence is conservatively refused.
The active MonoMod ILHook body of Chainloader.Start is equivalent evidence when
the audited backend's live registry maps that exact original method to the active
Detour.Target. Pinned runtime handles identify its stack frame; generated names
are never accepted as proof. Unregistered method copies are rejected. This
allows the dependency bootstrap's transformed Start without bypassing the stack,
startup-state, null-singleton, one-attempt or lock requirements. Director
and GameMap singleton fields must still be CLR-null (not Unity destroyed-null).
No singleton property is evaluated. The known managed image initializes
`EngineInterface.threadLock` as an object; reflection verifies that private field.
Its other static initialization allocates the selected-chimp array and sets a
boolean. Director initializes null/state fields and its default culture; GameMap
initializes a null instance and tilemap size. These initializers do not enter
native game/map execution.
The caller must not already own it, and a nonblocking Monitor.TryEnter must
succeed. Startup is rechecked under the lock and immediately before writing.

Native AI can run from tick, load and editor paths. Vanilla run/load wrappers
use this lock, but EditorChangeMap_* calls to DLL_SetMapEditorParam do not.
Therefore acquiring the lock in a menu or delayed LibraryLoaded callback is
insufficient. The pre-game synchronous startup checks are mandatory. Arbitrary
foreign code calling native functions from unsynchronized startup worker threads
is outside this contract; no process-wide thread suspension is attempted.

The one-attempt flag is static. No work survives as a callback on the short-lived
plugin component, no new JSON is used, and startup cleanup does not undo the
correction. Binary/source public compatibility is preserved. Behavioral
compatibility changes: APIShared now initializes after Fixes, and that hook reads
the actual player ID. Consumers must still wait for the existing APIShared
readiness/capability contracts; no public timing guarantee is added.

## Native validation and write

Derived contract (2026-10-22, `Compatibility/FixesWheatAnalysis`):

- The hook site is the target of Fixes' own `c_game_ai_sell_item_handler_hook`; it
  must lie in an executable section of the loaded CrusaderDE.dll. The backend's
  displaced snapshot must equal the disk bytes at that site and decode to complete,
  branch-free, non-RIP-relative and stack-neutral instructions. The live entry is the
  backend's `jmp qword [rip+0]` to the stub plus NOP padding.
- The seller is the function whose x64 unwind entry (`.pdata`) contains the site,
  following chained fragments to the primary entry. Fragments may only save
  registers; no frame register is allowed. The stack allocation comes from the
  unwind codes and must equal the decoded prolog, which must store the untouched EDX
  (second argument, the player) in its entry home slot `+0x10` exactly once. No
  instruction of the seller or its fragments may write that slot or take its
  address. The live prolog must equal the disk prolog (no foreign entry detour).
- The Fixes stub is decoded up to its absolute jump back to `site + displaced`,
  which must be preceded by the displaced instructions. It must contain balanced
  push/pop pairs, exactly one stack load, a 32-bit `mov reg,[rsp+disp8]`, and that
  register must index `cmp byte [flags+reg],0` after `mov flags, imm64` with the
  actual `wheatSaleCategoryFlagArray` address (at least 9 flags). The required
  displacement is the stub's pushed bytes plus allocation plus `0x10`; only that one
  operand byte is written, and only when it differs.

The audited installation and its earlier exact byte contracts remain documented in
[UpdateToNewDLL](../UpdateToNewDLL.md#fixes-wheat-sale-startup-correction) as observations.
The paragraph below records the state before 2026-10-22.
The tested installation uses Fixes 1.26.5 and Script Extender 2.14.2. These are
observations, not accepted-version limits. The installed Fixes emitter still
loads the wrong stack slot. An emitter already using the audited correct slot is
also supported and must cause no write. Unity byte-loaded Assembly-CSharp and
UnityEngine.CoreModule use their canonical GameRootPath images, including
SHA-256, full assembly name and loaded/disk MVID, rather than their empty Location.
The native module is obtained from its loaded module handle and hashed at that
module's path. Game and backend identities are hash-bound; Fixes/Extender module
provenance uses full assembly name and disk/loaded MVID agreement. The Fixes
static HookHandle and NativeFlagArray are obtained by
exact typed reflection; no Fixes assembly reference is linked.

Validation covers the backend's installed state, resolved/target address,
displaced byte count and original snapshot; live entry jump and padding; Vanilla
prolog; flag-array length/address; and all 75 stub bytes, including both branches,
push/pop pairs, displaced instructions and absolute return. Only the flag pointer,
return address and the two explicitly allowed displacement values vary. The
first two must match their independently verified objects/addresses.

VirtualQuery requires committed, readable ranges and executable code ranges,
without guard pages or region crossings. VirtualProtect changes only the page
containing the operand, with the exact previous protection restored in finally;
FlushInstructionCache follows. The complete stub is read back and compared with
the original snapshot except for the single permitted byte. Entry/prolog are
revalidated. Restoration/cache errors never report success and never trigger a
second write or undo. The code has no later hot-path overhead.

## Integration review and pending acceptance

Recheck on 2026-10-17: the installed Fixes 1.26.5 emitter and the complete
production identity preflight pass the targeted integration tests with Extender
2.14.2. The generated 61-byte body matches the audited template. A private native
fixture executes the corrected stub through its full continuation for players
1..8, flags off/on, non-wheat goods and three unrelated old-stack-slot values;
the production memory writer performs the one operand correction. File-path tests
cover byte-loaded assemblies, missing/changed files and loaded/disk MVID mismatch.
This is desktop fixture evidence, not game/save/editor acceptance. The earlier
prepared/pending notes below retain their original implementation-stage context.

The existing APIShared market hooks at 0xCEB10/0xCEB90 and AIV hook at 0x51790
do not overlap 0x3F0C9..0x3F0DC. Gatehouse validation already allows the distinct
Fixes hook beginning at 0xB7C39; its observed human-delay prefix ends there.
Fixes declares only its Extender hard dependency, so the new edge creates no
APIShared/Fixes cycle in the audited installation. Existing binary/live-site
guards remain in force for all APIShared services after the order change.

BugfixesAndQoL already depends on APIShared and optionally on Fixes. Its stone
reserve hook at 0x3F156 (20 displaced bytes) is disjoint, derives player context
independently and needs no additional adapter or minimum-version change.

Prepared Core.Tests cover full-byte rejection, exact one-byte correction,
idempotence, denied writes, post-write corruption, players 1..8, arbitrary saved
RDI values, flags off/on and late/missing/reordered startup evidence. The stack
fixture checks addressing; it does not execute the generated stub. Prepared
integration tests use the real BepInEx sorter/compiled dependency and reject an
unknown binary. No tests requiring compilation have been run for this change.

Still required before release: compile and run these suites; validate the actual
RedBird fixture/stub execution and OS protection/cache failure paths; test real
startup with Fixes absent, disabled, enabled, already corrected and unsupported;
verify synchronous stack evidence on Unity Mono; repeat save loads and fresh
process starts past tick 70, including test_dusty.sav, with the cooperating mods;
check editor startup and all existing APIShared capabilities after the order
change. No build, installation or gameplay success is claimed by source checks.
