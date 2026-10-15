# Native entry brokers

Two CrusaderDE.dll entries have more than one real owner: the PCL reachability query
at 0xE2610 (BugfixesAndQoL, a naval plugin, and callers that only call it) and the tile
refresh helper at 0xD90D0 (BugfixesAndQoL and a landscape plugin). Every owner detours
the entry itself and refuses an occupied one, so whichever installs second loses its
feature. The brokers own each entry once and let every consumer register ordered
callbacks instead.

Audience: authors of mods that detour or call these two entries, and maintainers
changing them. Contracts are on the declarations in
[NativeBrokerContracts.cs](../src/NativeBrokers/NativeBrokerContracts.cs),
[PclReachabilityBroker.cs](../src/NativeBrokers/PclReachabilityBroker.cs) and
[TileRefreshBroker.cs](../src/NativeBrokers/TileRefreshBroker.cs); the
[compiled example](../examples/ThirdPartyMod/NativeBrokerExample.cs) shows the workflow.

| Broker | Entry | Phases | Call view |
|---|---|---|---|
| `APIShared.NativeBrokers.PclReachabilityBroker` | 0xE2610 | Ordered Pre (may override) and Post (may replace the result) | `ref PclReachabilityCall`: manager, player, two PCL IDs, mode, result |
| `APIShared.NativeBrokers.TileRefreshBroker` | 0xD90D0 | Ordered Post | `TileRefreshCall` copy: manager, tile Y, tile ID |

## Usage

1. Declare a soft `BepInDependency` on `APIShared_Serp`, and a soft one on every older
   consumer that still detours the entry itself (today `BugfixesAndQoL_Serp`), so that
   consumer installs first and the broker refuses instead of displacing it.
2. From your `LibraryLoaded` handler or later, call `TryRegister(ownerGuid, id, order, ...)`.
   The first successful registration patches the entry. Keep the returned handle.
3. If `TryRegister` returns false, keep your previous path and log `reason` once. `State`
   says why: `Pending` (too early), `ForeignOwner` (another detour or registered writer owns
   the entry), `Unavailable` (unsupported build or failed installation).
4. Override in Pre with `call.TryOverride(value)`; replace in Post with `call.TrySetResult(value)`;
   read the original's own value with `call.OriginalResult`.
5. Dispose the handle to unregister. Use `CallOriginal` for Vanilla without consumers.

Do not install your own detour on 0xE2610 or 0xD90D0 while the broker serves them, and
do not pin their entry bytes as `Observe` in the native-site registry (that conflicts with
the broker's `Exclusive` claim). A call-only user registers `CallOnly`.

## Contract

**Installation and fast path.** Nothing is patched before the first successful
registration. Then the entry holds one NativeX64 Absolute detour for the process
lifetime; its stub reads a native demand byte. With no active registration it jumps
straight to the trampoline: no managed transition, lock or allocation. Otherwise it
enters one managed dispatcher, which runs every consumer and the original once. Disposing
the last registration returns the entry to the zero-registration path; it is never
unpatched.

**Ordering.** Ascending `order`, then ordinal owner GUID, then ordinal registration ID,
independent of when each registered. Pre callbacks run in that order until one overrides;
the overriding call skips the original and every later Pre. Post callbacks always run, in
the same order, and see `IsOverridden`, `OriginalCalled`, `OriginalResult` and the current
`Result` (including replacements by earlier Posts). Observe in Post, not in Pre.

**Arguments.** The native arguments are read-only. The two PCL arguments are named by
position (`FirstPcl`, `SecondPcl`): APIShared's gate-routing ABI and BugfixesAndQoL name
their roles differently, and the roles are not audited here.

**Thread.** Callbacks run synchronously on the thread that called the native function,
usually the game's simulation or input thread. Do not block, log per call or call Unity
APIs there. Registration and disposal are thread-safe from any thread.

**Recursion.** A call made from inside a callback through the live entry, directly or by
native code the callback calls, dispatches again, exactly as with a private detour.
`CallOriginal` reaches the original without consumers: the trampoline once installed, the
live entry before that (which still runs a foreign detour while the broker is refused).

**Exceptions.** A throwing callback's override or result replacement is rolled back, the
failure is counted (`CallbackFailures`) and reported on the first, second, fourth, ...
failure of that registration, and the other callbacks and the native caller continue.

**Unregistration.** `Dispose` is idempotent. After it returns, no new callback of that
registration starts; a callback already running on another thread may finish. Disposing
during a dispatch skips that registration's remaining callbacks of the same call.

**Foreign owners.** At the first registration the broker claims the entry in the
[native-site registry](NATIVE_SITES.md) with the expected hash of the displaced bytes,
then compares the live bytes itself. If another owner registered the span, or the live
bytes are not the audited prologue (another detour), the broker never patches: it becomes
`ForeignOwner`, logs `NATIVE_BROKER_REFUSED` once, the registry records the refusal
(`NativeSiteRegistry.Refusals`: `Conflict` or `UnexpectedBytes`), and every `TryRegister`
fails. The refusal is terminal for the process.

## Migration notes

These are notes for each consumer's maintainer; nothing here changes those mods.

### Naval plugin (ByzantineNaval, 0xE2610)

Today `ShipNativeNavigation` adds a `Connected` detour at 0xE2610 to its navigation
transaction and verifies the entry's 16 prologue bytes first. Its callback answers 1/0
while its `[ThreadStatic]` input scope is active and calls the original otherwise. Under
the broker that is one Pre: `if (inputDepth > 0) call.TryOverride(reachable ? 1 : 0);`, no
Post. Remove 0xE2610 from the transaction and from the `Verify` list only when
`TryRegister` succeeded; otherwise keep today's detour as the fallback. Add soft
dependencies on `APIShared_Serp` and `BugfixesAndQoL_Serp`, and touch the broker types
only from a method that runs after checking that APIShared is present and new enough.
The return type is `int` (EAX), not `long`.

### Mounted hybrid (call-only 0xE2610)

`NativeWorld` calls 0xE2610 through its live entry, and `RequireCallable` accepts a
foreign entry detour after pinning the image bytes. The broker's installed entry is
`jmp [rip+0]` with an absolute target, two NOP filler bytes and an untouched continuation
from +16, which that classifier accepts as a foreign entry detour (read from its source,
not executed here). No change is needed. Optional: register the call as `CallOnly` in
the native-site registry (compatible with the broker), and call
`PclReachabilityBroker.CallOriginal` where the hybrid wants Vanilla without other
consumers' overrides; through the live entry it sees them, as with any foreign detour today.

### LandscapeThemes (0xD90D0)

The released observer (`TerrainPlacementObserver`, release snapshots up to
`tree_identity_r5`; the current `runtime/src` has no 0xD90D0 hook) detours the entry,
records the cell with `AcceptTerrainCell(y, id)` and then calls the original. The broker
is Post only: the same call moves after the original. `AcceptTerrainCell` records the
cell in a managed map and queues a later redraw, so the order looks irrelevant, but that
is read from source and not confirmed in game. Keep the `Guard` of the second, unrelated
site 0x707DB; drop the 0xD90D0 guard and detour when `TryRegister` succeeded.

### BugfixesAndQoL (0xE2610 and 0xD90D0)

Its 0xE2610 detour calls the original first and then may replace the result (ladder
attack fix, dig and fill work regions, direct cursor moves, gate-routing observation).
That is one Post: start from `call.OriginalResult`, return early when
`call.OriginalCalled` is false (another consumer overrode the call), and replace with
`TrySetResult`. Its direct calls of the original trampoline become
`PclReachabilityBroker.CallOriginal`. Its 0xD90D0 observer becomes a `TileRefreshBroker`
Post. Both detours sit in atomic transactions with other hooks; when a broker
registration fails, keep the existing detour for that entry in the transaction as the
fallback, so the opt-in never loses a feature. Its exact-prefix checks for these two
entries must not run once it uses the broker, because the broker owns those bytes.
BugfixesAndQoL hard-depends on APIShared 0.7.0: raising that minimum to the first release
with the brokers makes an older APIShared refuse to load it, while a runtime check keeps
the old path; that is the maintainer's choice.

## Version skew

| Combination | 0xE2610 | 0xD90D0 | Checked offline |
|---|---|---|---|
| New APIShared, no consumer registers | Pristine, nothing patched | Pristine | Yes: `BrokerPatchesOnlyAtFirstRegistration...` |
| Old BugfixesAndQoL, old naval, new APIShared | Today's behavior: the second detour refuses | Today's behavior | Yes, by the above: no registration, no patch |
| Old BugfixesAndQoL first, migrated naval (soft dependency) | BugfixesAndQoL keeps its detour; broker `ForeignOwner`, refusal recorded; naval's fallback detour refuses as today, so naval's sea reachability stays off | LandscapeThemes alike, if migrated | Refusal path yes (`ForeignDetourIsRefused...`); load order no |
| Migrated naval first without the soft dependency, old BugfixesAndQoL | Broker installs; BugfixesAndQoL's live-prefix check then refuses its 0xE2610 transaction, and per its log text the ladder attack fix, building commands and direct FillMoat staging stay Vanilla | A migrated LandscapeThemes registering first does the same to BugfixesAndQoL's 0xD90D0 connectivity-observer transaction; its fallback there is not audited | Patched bytes yes (`ProductionContractHolds...`); BugfixesAndQoL's reaction read from its source only |
| New BugfixesAndQoL and migrated naval, new APIShared | Both served: naval's Pre overrides inside its input scope; BugfixesAndQoL's Post skips overridden calls | Both Posts run in order | Dispatch semantics yes; game behavior no |
| New BugfixesAndQoL or migrated naval, old APIShared | Must keep today's detour (runtime check), or not load (raised minimum version) | Same | No: depends on the consumer's gate |
| Mounted hybrid with the broker installed | Calls run through the broker; classified as a foreign entry detour | n/a | Entry bytes yes; the hybrid's classifier not executed |

Load order: the soft dependency orders BepInEx plugin loading. That the Script Extender
then runs `LibraryLoaded` handlers in subscription order, and that each consumer
subscribes in `Awake`, is not verified offline.

## Limits

- Two entries only, both bound to the audited CrusaderDE.dll hash
  `FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`; any other build is
  `Unavailable`.
- The broker cannot coexist with a consumer that keeps its own detour on the same entry:
  whichever patches first owns the entry. Migration needs each consumer's opt-in.
- Calls from inside callbacks dispatch again; a consumer that must not recurse guards
  itself (as the naval plugin's input scope does) or uses `CallOriginal`.
- Callbacks cannot change the native arguments.

## Implementation

[NativeBrokerSite](../src/NativeBrokers/NativeBrokerSite.cs) owns one entry. At the first
registration it claims the span through `ApiSharedNativeSites` (Exclusive over the
displaced bytes with their SHA-256, Observe over the continuation instruction), checks the
live bytes, and creates a NativeX64 Absolute detour whose intermediary is
[NativeBrokerBridge](../src/NativeBrokers/NativeBrokerBridge.cs): the same demand guard as
the audio bridges (`mov rax, demand; cmp byte [rax], 0; jne active; mov rax, trampoline;
jmp rax`), then a tail jump into the rooted managed dispatcher with the caller's frame, so
stack argument five and the return address are the native caller's. RAX is the only
scratch register.

0xE2610 displaces 16 bytes (`push rbp; push r12; push r13; push r14; lea rbp,[rsp-0x888]`)
and resumes at `sub rsp,0x988`; 0xD90D0 displaces 15 bytes (three `mov [rsp+n],reg`) and
resumes at `push rdi`. Neither span has a RIP operand or branch. The backend re-encodes the
displaced instructions (it drops the redundant REX prefix of 0xE2610's `push rbp`), so
validation compares the trampoline instruction by instruction, then its jump to the
boundary, the published `jmp [rip+0]` patch with NOP padding, and the untouched
continuation. The order is create, publish the trampoline to the dispatcher, then write
the patch (issue #28); the demand byte is still zero while the patch is written, so a
call landing then runs the trampoline. A published detour that fails validation keeps
demand zero and the broker reports `Unavailable`.

The [dispatchers](../src/NativeBrokers/NativeBrokerDispatch.cs) read one immutable
snapshot (separate Pre and Post arrays sorted at registration) with one volatile read,
build the call view on the stack and check each handle's active flag before invoking it.
Registration and disposal serialize on one lock, publish a new snapshot, and only then
raise or clear the demand byte, so no active registration meets a false zero. The dispatch
methods are emitted without `.locals init` (`SkipLocalsInit`): the .NET Framework JIT
otherwise zeroes their frame with `rep stos`, which measured 6 to 8 ns per call.

### Measurements

`tests/APISharedTests/NativeBrokerTests.cs`, `BrokerCostAgainstAHandWrittenDetourOnCopiedCode`:
private pages hold small functions whose first bytes are the audited prologue and
continuation of each entry (the body is a stand-in that counts calls). A native loop calls
the entry 20,000 times per sample (one managed-to-native transition per sample), 31 samples
after two warm-up rounds, alternating mode order, at raised priority. Consumers are no-op
callbacks with one static flag check; the hand-written detour is one RedBird Absolute detour
whose managed callback checks the same flag and calls the original, as consumers write them
today. 0 bytes allocated per call in every mode.

| Mode (ns per call, min / median) | 0xE2610 | 0xD90D0 |
|---|---|---|
| Vanilla, unhooked | 1.6 / 1.7 | 1.3 / 1.7 |
| Hand-written single detour | 26.0 / 27.1 | 21.5 / 22.2 |
| Broker, 0 registrations | 2.3 / 2.3 | 2.1 / 2.1 |
| Broker, 1 consumer (Pre for 0xE2610, Post for 0xD90D0) | 31.9 / 33.2 | 27.3 / 28.4 |
| Broker, 2 consumers | 34.2 / 36.1 | 28.4 / 30.0 |
| Broker, 8 consumers (Pre only / Post only) | 46.9 / 49.2 | 43.0 / 44.5 |
| Broker, 8 consumers with Pre and Post | 64.3 / 66.6 | n/a |

One of three consecutive runs; the others were within about 3 ns per mode. Each private
detour costs one native-to-managed transition (about 20 to 25 ns here); the broker pays one
for all consumers. One consumer costs about 6 ns more than a single private detour, each
further consumer about 2 ns, where a second private detour (if the two could coexist) would
add a second transition.

Limits: desktop CLR 4.8.1 on the test host, not the game's Unity Mono, whose transition and
JIT costs differ; a stand-in function body; no in-game frame-time measurement.
