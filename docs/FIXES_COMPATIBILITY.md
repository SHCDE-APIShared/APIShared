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
services continue. Unknown binaries, unavailable startup evidence or memory
access failures cause refusal, never a relaxed signature search or later retry.

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
call path. Missing/inlined stack evidence is conservatively refused. Director
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

Exact identities and byte contracts are in [UpdateToNewDLL](../UpdateToNewDLL.md#fixes-wheat-sale-startup-correction).
The native module is obtained from its loaded module handle and hashed at that
module's path. Loaded managed identities are checked by SHA-256 and disk/loaded
MVID agreement. The Fixes static HookHandle and NativeFlagArray are obtained by
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
