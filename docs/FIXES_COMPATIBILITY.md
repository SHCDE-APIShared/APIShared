# Fixes wheat-sale startup compatibility

## Behavior and limits

APIShared optionally loads after Fixes and repairs supported wheat-sale inline
hooks once during synchronous pre-game initialization. The legacy stack-load
variant receives its derived one-byte operand correction. The division-based
variant observed in Fixes 1.29.1 receives a corrected prefix inside the existing
stub allocation. No public API, dependency requirement, setting or later callback
is introduced. Fixes retains its hook, settings, flag storage and per-player flags.

The legacy load indexed the flag array using saved caller RDI. The newer emitter
has three separate defects: R9 has already become the previous good's stock when
the seller reaches wheat; unsigned division puts the remainder in RDX, which is
then used as the flag index; and restoring saved RCX discards the Resource category.
The correction obtains the true player argument from its independently derived
seller stack slot and retains the intended category result.

Only CORRECTED and ALREADY-CORRECT confirm the complete supported contract.
NOT-NEEDED means Fixes or its hook was absent. REFUSED/UNCONFIRMED confirms no
protection, including after a write whose protection/cache/read-back checks failed.
Unknown implementations remain unchanged and other APIShared services continue.
The error gives the global Fixes option to disable and requires a fresh process.
No config, disk DLL or per-player flag is rewritten automatically.

The 2026-10-26 user-authorized exception extends the previous single-operand
exception to one bounded prefix write in this one foreign hook, exclusively during
the existing verified synchronous startup. It permits no other repatching, retry,
rollback, teardown write or map/settings-dependent code mutation.

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

The hook handle supplies the actual site, original displaced snapshot and stub.
The loaded/disk module identities must agree. No exact version, whole-file hash
or fixed address is an acceptance gate. The actual code determines support:

- The site must be executable, its live jump must target the handle's actual stub,
  and its displaced snapshot must equal disk code and decode into complete,
  branch-free, position-independent, stack-neutral instructions.
- PE unwind information and decoded seller prolog derive the frame and player
  home slot; writes overlapping any of its four bytes, indexed stack writes and
  stack-address aliases are refused. The live prolog
  must equal disk code. Existing startup/lock/provenance checks remain mandatory.
- Decode at most 256 bytes through the absolute continuation to site + displaced.
  The displaced instructions must immediately precede that return.
- Reassemble each supported complete prefix using the actual stub address, flag
  pointer, flag-array length, derived stack slot and installed Extender enums/layout.
  Compare every byte, including branches, register operations and constant operands.
  A plausible stack load or recognizable flag access alone never suffices.
- A supported legacy prefix may vary its stack displacement; only its single
  displacement byte changes. Already-correct legacy code receives no write.
- The supported division prefix is replaced with a bounded stack-load prefix.
  It preserves RDX/R8, tests wheat, loads EDX from player-home + 16, rejects
  unsigned indices at or beyond the flag-array length, tests that player's flag,
  and sets ECX to Resource only when enabled. RCX is deliberately not restored.
  R9 is untouched and no division is performed.
- The replacement must fit the existing prefix. Pad the remaining prefix bytes
  with NOPs; retain displaced instructions and the return at their original
  addresses. The same validated replacement is recognized without writing again.

The observed 1.29.1 emitter has a 61-byte prefix and a 94-byte complete stub.
The corrected prefix uses 47 bytes with the current frame/flag length, leaving
14 NOP bytes. These are dated observations, not fixed runtime capacities.

The internal analysis returns full before/after snapshots and exactly one allowed
write range. Before writing, recheck startup, native entry/prolog and the full
snapshot. The memory owner accepts equal nonempty expected/replacement ranges of
at most 256 bytes, within one committed readable executable VirtualQuery region.
It compares the preimage before and under writable protection. Only that range is
copied; finally restores the exact original protection and synchronizes the same
instruction-cache range, even if restoration fails. Verify protection and read
back the replacement, then compare the complete snapshot and corrected contract.

Failures after the write boundary report unconfirmed protection. They never
trigger a second correction or attempt to restore published executable code.
Production always uses real Win32 protection/cache operations; a narrow internal
OS-call seam lets the existing private-memory tests cover errors and exceptions.
There is no new game hook, executable allocation or managed simulation work.

## Current verification and acceptance

The existing integration test invokes a private copy of the installed Fixes
emitter with only its flag-address provider substituted. It applies production
analysis/writing and executes the corrected machine code in private memory.
Historical wrong/correct stack emitters and the current division emitter are
covered, including players 1..8, independent flags, non-wheat goods, unrelated old
stack slots/R9 stocks, invalid indices for the bounded prefix, stack/GPR/XMM
sentinels, replayed original instructions and repeat no-op behavior.

Negative cases cover changed prefix instructions/padding, flag pointers, return
and displaced bytes, truncated/unsupported bodies, changed preimages, denied
protection, region crossings and protection/cache/read-back failures. Source
guardrails retain single startup ownership and one bounded native writer.

These are offline/native-fixture guarantees, not Unity or real-peer acceptance.
Fresh-process startup, repeated save loading, editor, actual AI wheat trades and
matching-build host/client behavior still require gameplay acceptance. No successful
offline build or menu startup substitutes for these cases.

## Historical integration notes

The following records describe the earlier implementation and its pending work
at that time. They do not override the current contract and verification above.

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
