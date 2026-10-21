# Native sites

Prefer a shared APIShared service; use the registry for hooks APIShared doesn't
cover yet. When several mods need the same native interception point, the way to
avoid the conflict is a shared service that owns the one hook and publishes events or
extension points with ordering, veto and replacement rules. The registry is a
supplementary safeguard and diagnostic, not an alternative to such a service.

`APIShared.NativeSites.NativeSiteRegistry` is a process-wide, cooperative registry of
native sites in CrusaderDE.dll: which mod writes, chains, calls or observes which
bytes. A mod registers a span before it installs a hook there and gets either an
acceptance or a refusal that names the existing owner, mode and span, instead of a
silent double detour. Any mod can ask who owns a site. APIShared registers its own
sites when it installs them.

A refusal identifies a conflict; it does not resolve it. The refused feature stays
unavailable, and which side is refused can depend on registration order. A conflict
that keeps recurring is fixed by a shared service with ordering, veto and replacement
rules, not by the registry. An accepted overlap (`Compatible`, `SameEntryChain`) is not
a compatibility guarantee either: it only means the spans do not overlap in a way that
corrupts bytes. Whether two hooks behave correctly together is still untested.

## When to use which

| Situation | Use | Examples |
|---|---|---|
| APIShared already serves the point | The shared service; never a competing hook | Market price queries (`MarketPriceEvents`), gatehouse timing, audio, AIV build step, gate routing |
| A second real owner needs the same point | Add or extend a shared APIShared service with explicit ordering, veto, replacement or exclusive-ownership rules | 0xE2610, detoured by both a naval mod and BugfixesAndQoL; 0x18E9A0, detoured by one mod, called by another and hooked by the Script Extender; 0xDB650, whose body holds APIShared's gate-routing spans |
| A unit-specific AI or FSM route with exactly one consumer | Registry; the algorithm stays in the consumer mod | A custom unit's update, selection or controller detour (for example 0x174100, 0x1969B0, 0x16E440) |
| A mid-function patch with a private register contract | Registry; a broker would need a one-off contract for one mod | CALL-site patches feeding one controller, a formation context hook (0xC0942), a terrain inline patch (0x182EE6) |

A private site stops being private as soon as the registry shows a second real owner
on it. That is the signal to propose a shared service for it.

Audience: authors of mods that hook native code, and maintainers changing an
APIShared native service. Contracts are on the declarations in
[NativeSiteContracts.cs](../src/Core/NativeSites/NativeSiteContracts.cs) and
[NativeSiteRegistry.cs](../src/Core/NativeSites/NativeSiteRegistry.cs); the
[compiled example](../examples/ThirdPartyMod/NativeSiteExample.cs) shows the workflow.

## Usage

1. Declare a hard or soft `BepInDependency` on `APIShared_Serp`, so APIShared binds the
   image and registers its eager sites before your `LibraryLoaded` handler runs.
2. In that handler, before you install anything, call
   `NativeSiteRegistry.Register(ownerGuid, name, spans)` with every span the hook
   group writes or relies on. Spans are RVAs in CrusaderDE.dll, half-open
   `[Rva, Rva + Length)`. Register the full displaced or written range, not only the
   patch instruction. The group is atomic: all spans are recorded or none is.
3. If `result.Succeeded` is false, keep Vanilla behavior and log `result.Reason` once.
   `result.Conflict` is the existing registration; `result.FailedSpanIndex` is your span.
4. Install the hook. If the installation fails before publication, call
   `NativeSiteRegistry.Unregister(ownerGuid, name)`. Published hooks stay registered.
5. To learn who owns a site, call `TryGetWriter(rva, out registration)` or
   `Query(rva, length, buffer)`. `Registrations` and `Refusals` are immutable snapshots
   for diagnostics.

Registering the identical group again returns `AlreadyRegistered`. Registering a
different span set under a name you already used is `Invalid`; unregister first.

## Modes

| Mode | Meaning | Typical use |
|---|---|---|
| `Exclusive` | Writes the span and cannot share it | Entry detour that refuses an occupied entry, inline or context hook, byte or code-immediate patch |
| `ChainableEntry` | Function-entry detour through a backend that layers over another detour at the same entry and always calls through | Only when your backend really chains; most native hooks are `Exclusive` |
| `CallOnly` | Calls the function through its live entry, accepts whatever detours run there, writes nothing | Calling a game function from managed code |
| `Observe` | Reads the span and relies on its bytes staying as validated, writes nothing | Pristine prologue or function hashes, decoded CALL sites, trampoline continuations, immediates read as data |

## Overlap rules

The rules only describe byte overlap. A compatible result is not a compatibility
guarantee, and a conflict result does not choose a winner that keeps both features.
The rules apply between registrations of different owners whose spans overlap.
Adjacent spans never overlap. An owner's own registrations never conflict with each
other. `NativeSiteRegistry.GetOverlapRule` returns the same table.

| | Exclusive | ChainableEntry | CallOnly | Observe |
|---|---|---|---|---|
| **Exclusive** | Conflict | Conflict | Compatible | Conflict |
| **ChainableEntry** | Conflict | Same entry only | Compatible | Conflict |
| **CallOnly** | Compatible | Compatible | Compatible | Compatible |
| **Observe** | Conflict | Conflict | Compatible | Compatible |

Why:

- **Exclusive with Exclusive.** Two writers of one span cannot both be right: an
  inline or context hook has no chaining, so overlapping spans corrupt each other,
  and almost every entry detour in this ecosystem checks live bytes and refuses an
  occupied entry, so the second installer disables itself in an order that depends
  on plugin load order.
- **Exclusive with ChainableEntry.** The exclusive owner refuses an occupied entry or
  breaks when its bytes change; a layering detour over it changes exactly those bytes.
- **Exclusive or ChainableEntry with Observe.** The observer validated original bytes
  that the writer replaces. A pristine prologue or whole-function check is the
  common case.
- **Anything with CallOnly.** A call through the live entry runs whatever the writer
  installed, exactly like every native caller of that function, and no byte is
  shared. Refusing here would turn "I call this function" into a false conflict.
  A caller that also validates the function's bytes registers those bytes as
  `Observe` and gets the Observe rules.
- **ChainableEntry with ChainableEntry.** Layered detours share one entry. Both spans
  must start at the same RVA; a patch that starts inside another patch's bytes
  corrupts it, so an overlapping chainable span with a different start conflicts.
- **Observe with Observe.** Readers do not affect each other.

### Chain order

`Sequence` records the process-wide registration order; `SharedWith` lists the
earlier chain members in that order and `TryGetWriter` reports the first one. The
call order of layered detours is decided by the hooking backend, not by the
registry. Hooks installed from `Awake` or `LibraryLoaded` register and install in
BepInEx plugin order (GUID order, then dependency edges); lazily installed hooks
follow runtime order. A chain member must therefore call through and must not
depend on its position. Adding a dependency edge can change the order. An allowed
chain says only that the entry patches do not corrupt each other; whether the layered
detours agree on arguments, results and side effects is not checked. Two mods that
both need to change one entry's behavior belong in a shared service.

## Expected original bytes

A span may carry the SHA-256 (64 hexadecimal digits) of its bytes before the owner
changes them. When APIShared has bound the image (`IsImageBound`), registration
hashes the live bytes and refuses a mismatch with `UnexpectedBytes`; the
registration then has `OriginalBytesVerified = true`. This detects a foreign hook
that never registered, or a different game build. Register before you patch:
after your own patch the live bytes no longer match. Before the image is bound,
the hash is recorded but not checked, and image bounds are not enforced.

## Thread, timing and lifetime

- Every member is thread-safe. Writes are serialized; reads use an immutable
  snapshot without locks. `Query`, `TryGetWriter`, `Registrations` and `Refusals`
  allocate nothing.
- Register and query at startup or install time only. Nothing in the registry runs
  on a hooked path, and it adds no per-call cost to any hook.
- Registrations live for the process. `Unregister` exists only to roll back an
  installation that was never published.
- Results and registrations are immutable and may be retained.

## Diagnostics

Conflicts and unexpected bytes are recorded in `Refusals` (the first 256). After
native initialization APIShared logs one summary line on the next Unity main-thread
turn, after the startup handlers of that frame:

```text
NATIVE_SITES: 212 site registrations by 3 owners (APIShared_Serp=120, ...), 1 capability reservations, image bound (0x...); refusals: none.
```

The line is a warning when refusals exist and lists up to ten of them. Every later
refusal is logged once as `NATIVE_SITES refusal: ...`.

## APIShared's own sites

APIShared registers its sites under `APIShared_Serp` immediately before each hook
transaction and releases them only when that transaction was not published. The
spans are built from each service's native contract constants in
[ApiSharedNativeSites.cs](../src/Core/NativeSites/ApiSharedNativeSites.cs).

| Group | Spans | When |
|---|---|---|
| `Audio.UnitContext` | Exclusive 0x2960/15, 0x2990/15, 0x1A41E0/14; Observe the 91 catalog CALL sites (5 bytes each) | LibraryLoaded |
| `Gatehouse.PermanentHooks` | Exclusive 0xB7B70/75 (distance), 0xB7BBB/37 (decision); Observe 0xB7C32/7 (human-delay prefix, ends where the Fixes hook at 0xB7C39 begins) | Native publication, for the parts whose live bytes validated |
| `Gatehouse.Automation` | Exclusive 0xB7A4E/17, 0xB7BF4/15, 0xC53D5/18, 0xC54A0/16, 0xD5835/15, 0xB79E2/18; Observe 0xC5300/15 | First automation resolver |
| `Diagnostics.AivBuildStep` | Exclusive 0x51790/17 (the instruction-aligned prologue any patch of up to 14 bytes displaces) | Native publication |
| `Economy.MarketPrices` | Exclusive 0xCEB10/10, 0xCEB90/10; Observe the rest of each 31-byte helper | First market registration |
| `Pathfinding.AssassinPath` | Exclusive 0xD9C40/10, 0xD9F0C/16, 0xD9F1C/15, 0xE19D8/18, 0xE19F9/23, 0xE19C0/14 | First Assassin path registration |
| `Pathfinding.AssassinAttack` | Exclusive 0x16CD70/10 | First attack guard |
| `Pathfinding.AssassinRetargetEntry` | Observe 0x122800/24 | Attack guard with NativeRetarget |
| `GateRouting.Movement` | Exclusive 0x1232E0/6, 0xDC3C0/6, 0x113BC0/10, 0x8F251/29, 0x8F1BF/14, the 11 direction filters and 3 tactical filters | Provider's `InstallMovement` |
| `GateRouting.Comparisons` | Exclusive 0xE2705/20, 0xE3024/20 | Provider's `InstallComparisons` |
| `GateRouting.Descent` | Exclusive 0x185D55/20; Observe 0x185934/4 | Provider's `InstallDescent` |
| `Buildings.RepairQuote` | Observe 0xD51A0/17 (validated entry that the repair quote calls) | First repair capability acquisition |

A conflict with another owner's earlier registration fails that APIShared service
closed through its existing path (capability diagnostic `Conflict` or its install
exception), exactly as an occupied entry already does. No APIShared site overlaps
another APIShared feature's site.

Capability reservations are separate: when a consumer applies gatehouse timing or
distance origin, it reserves the logical values APIShared serves through its own
hooks. These reservations keep their pre-registry rules (one owner per value,
compared only with each other) and appear in `Registrations` with `CapabilityId`
set. They never conflict with site registrations, because the hooked span itself
is registered by APIShared.

## Limits

- **Diagnostic, not resolution.** A refusal leaves the refused feature off and can
  depend on registration order. Recurring conflicts need a shared service.
- **No behavioral check.** Accepted overlaps mean no byte overlap that corrupts code,
  nothing more.
- **Cooperative.** The registry sees only what is registered. It does not see
  Script Extender's own native hooks, hooks of plugins that do not use it, other
  backends or managed hooks. It does not verify that a registrant installed
  anything, and it does not protect memory. A refusal is advice the registrant
  follows; nothing stops a mod that ignores it.
- **One module.** Spans are RVAs of the loaded CrusaderDE.dll. A registration does
  not carry a game-build hash; each mod still validates its own binary.
- **Script Extender spans** are not mirrored by APIShared. They should come from
  the Script Extender itself.
- **Data tables** written through public table APIs (for example the pathfinding
  table replacements) are not registered as APIShared sites; the same tables are
  written by the Script Extender's own API.

## Implementation

The public facade is a static class over `ApiSharedRuntime.ProcessInstance.NativeOwnership`,
the [owner registry](../src/Core/Internal/Native/NativeOwnershipRegistry.cs) that audio
and the gatehouse used before. It holds two layers under one lock: site registrations
in RVA space and the earlier capability reservations in the caller's address space.
Each change publishes a new immutable array plus a read-only view; readers take it
with one volatile read. Overlap checks are linear in the number of registrations
and run only at registration time.

The first `LibraryLoaded` step binds the image (base, length, live-byte reader).
Absolute capability reservations are converted to RVAs for publication once bound.

Measured after warm-up, 0 bytes allocated per query call: `Query`, count-only
`Query`, `TryGetWriter` and `Registrations` over 200 registrations on CoreCLR
(`tests/Core.Tests/NativeSiteRegistryTests.cs`), and the public facade on the
.NET Framework 4.8.1 test host (`tests/APISharedTests/NativeSiteRegistryTests.cs`).
Neither host is the game's Mono runtime; no in-game measurement exists yet.
