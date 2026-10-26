# Native compatibility

The runtime's current reference native SHA-256 is
`FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2`.
The authoritative supported hash, function hashes, bytes, addresses and layouts are
the runtime catalogs. This document explains their purpose; it does not override them.

## Ownership and validation

| Service | Native contract / owner |
|---|---|
| Building repair | Validated native callable functions; no competing detour |
| Gatehouse distance/timing/automation | APIShared owns the coordinated intervals in the shared gatehouse functions |
| AIV build-step observation | APIShared owns the detour at reference RVA `0x51790` |
| HUD and recruitment | Shared managed/native presentation hooks and registration brokers |
| Market buy/sell queries | APIShared owns reference RVAs `0xCEB10` and `0xCEB90`; fixed-image validation and NativeX64 Indirect detours |
| Additional route preferences | Managed Pre/Post registration; native command hooks belong to the participating publisher |
| Assassin selection | Script Extender owns its function detour; APIShared uses its audited integration points |

Every native site APIShared installs or validates for these services is registered
under `APIShared_Serp` in the public [native-site registry](NATIVE_SITES.md) when the
service installs it; the spans are listed there and built in
`src/Core/NativeSites/ApiSharedNativeSites.cs`. A conflict with another owner's earlier
registration fails that service closed like an occupied entry.

Capability failures remain independent. Hash-bound catalogs, live-byte validation
and the existing unique-pattern resolution policy determine support. Resolvers that
already require the supported hash accept a pattern match at its audited reference
RVA without rescanning the mapped image (`CompiledBytePattern.FindUniqueAtReference`;
the full scan proved uniqueness for that hash and costs about 70 ms at startup); any
other bytes keep the full unique scan. A pattern
match is not permission to bypass function, layout, overlap or backend validation.
Game or Extender version numbers alone are not evidence of native compatibility.

## Hook backend contracts

The single audited exception is the [Fixes wheat-sale startup correction](FIXES_COMPATIBILITY.md):
one validated displacement byte for the legacy prefix, or one fully validated
prefix replacement for the division variant, during synchronous pre-game
initialization under the engine lock. The user-authorized extension of 2026-10-26
retains the allocation, native entry, displaced code and return. It permits no
general repatching, later retry or teardown undo.

Published hooks remain installed for the process lifetime. Activation changes are
logical gates; inactive callbacks preserve the original behavior. Installation
rollback is limited to a prepared candidate before the first patch activation attempt.
An activation attempt is irreversible even when it throws: the backend may already
have made executable code reachable. Installation success is not the publication boundary.

A native call can enter a detour as soon as its patch is written, while RedBird's
default commit attaches the detour handles only after writing every patch. Detours
whose callbacks read a handle or its original therefore commit with
`InstallOnCommit = false`, publish that handle, and only then write their patches
through `NativeHookPublication.Enable` (issue #28). Before the first activation
attempt, a shared publication state permanently roots the transaction, handles and
callback owner. A later activation or validation failure stops installation without
disabling, disposing or unpublishing anything, and retains the native-site claims.
Only a candidate that has never attempted activation may be disposed and release
its claims. Full feature readiness follows all patch writes and post-write checks;
until then, and after failure, callbacks preserve the original path. Subsequent
requests report the retained failure instead of retrying a partial installation.
Gate-routing providers must retain their supplied buffers even when installation
throws; published callbacks may still access them for the rest of the process.

RedBird's NativeX64 detour backend suspends every other process thread around each
patch (about 60 ms per detour). APIShared-owned detours skip that suspension only
inside `NativeStartupPatchWindow`: the synchronous run that raises
`CrusaderLibrary.LibraryLoaded`, on the thread that started APIShared's handler,
until the next Unity main-thread turn. This covers APIShared's own startup detours
and shared detours that consumers request from their own `LibraryLoaded` handlers.
No simulation, AI, pathing, market or unit-speech code has run at that point, and
the Script Extender writes its own detours there without suspension. Every other
installation keeps suspension; scheme, jump and chaining options never change.

RedBird inline-hook size is a minimum: actual displacement is instruction-aligned
and can exceed the requested size. Function detours have a separate scheme-dependent
contract. Validate the real backend, complete displaced range, incoming branches,
continuation and ABI before publishing. Context register masks must preserve live
registers; SIMD state and flags need separate verification. Do not assume a context
wrapper preserves the original flags. Keep assembler labels valid and decode emitted
stubs, not only their intended pseudo-code.

The gatehouse centered-distance replacement loads both unit coordinates before
using `cdq`, because `cdq` overwrites EDX and would otherwise destroy the live record
offset. Timing and automation share coordinated ranges; changes must preserve their
non-overlap and the original inactive paths. Existing backend tests execute productive
stubs on isolated buffers, never on another running game's executable pages.

Market queries validate each complete 31-byte function before installation. Only
the audited image and NativeX64 Indirect schema are accepted: six patch bytes
displace ten instruction-aligned bytes. Validation checks the target, patch form,
pointer slot, hook entry and trampoline continuation before feature readiness. These
post-write checks cannot authorize rollback. Unknown
images or occupied entries fail closed. The helpers serve AI affordability, trade
execution and ally valuations; replacements must preserve consistent policy across
those callers. See [market query semantics](API_CATALOG.md#native-market-price-queries).

## Updating support

1. Identify the actual installed native image and Extender/backend versions.
2. Audit the complete affected feature flow, including validation, incoming edges,
   displaced instructions, register/flag use and fallback behavior.
3. Compare real managed/interop visibility, field types, offsets and native strides.
   Publicized assemblies are not proof of runtime access.
4. Check overlapping hooks, particularly Script Extender and
   [Fixes](https://gitlab.com/rawra-stronghold-crusader/shcde-fixes).
5. Update catalogs and behavior/byte/backend tests together; retain fail-closed
   behavior for unsupported contracts. Registered site spans follow the service
   constants; update the expected spans in `VerifyApiSharedSiteCatalog` with them.
6. Run local integration tests and game acceptance for the affected service.

`tools/Validation/Verify-Interop.ps1` verifies consumed installed layouts;
`tests/APISharedTests` contains native transactions, byte generation and backend
fixtures. UI/session doubles and synthetic memory are not gameplay acceptance.
Relevant gameplay checks include startup cleanup, map transitions, save/load,
host/client synchronization, gatehouse orientations and cooperating route-search publishers.

Audio owns coordinated native source-capture entries and managed playback
interception. See [bridge validation](AUDIO.md#native-bridge-validation) for the
specific backend contract and [UpdateToNewDLL](../UpdateToNewDLL.md) for hashes,
addresses and the feature update checklist.
