# Native binary update contracts

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

## Audio

The audio implementation lives under src/Audio; this is a native-update checklist,
not a consumer setting. Native provenance and fixed catalog/field offsets remain
hash-bound; a matching prologue alone cannot authorize a new game binary.

Native SHA-256: FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2.
Managed SHA-256: BC8B6A395F01D48557DB413600C8DD8D1FDFD3ABDF97BFBBB68A3C56B04FD789.
NativeX64 SHA-256: 0843DD4C381A3E77DD6D8B51D5CCF95465B3FB49BFDF2980F39A212D774AADB0.
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

## Workshop staging cleanup

Managed contract, not native: Script Extender
`MapModManager.TryStageModUpdate(string, MapArchive)` returning bool, every return
intercepted. Consumer behavior: [Workshop package cleanup](docs/WORKSHOP.md#workshop-package-cleanup).
The only allow-list is `AuditedExtenderHashes` in
`src/Workshop/WorkshopCleanupRuntime.cs`; `tools/Validation/Verify-WorkshopContracts.ps1`
reads it from there. Hash is lowercase SHA-256 of `BepInEx/plugins/000shcdese/SHCDESE.dll`.

| SHCDESE.dll SHA-256 | Binary | Source |
|---|---|---|
| b170f163e69f7e8f64bda5874d282f4eb5ae5d9d1852e985ed6bef194c80d780 | Official 2.14.1 | GitLab package `shcdese` 2.14.1, SHCDESE.zip 9c3edd3b9dc101b080b4467ed4bb44a373ed0639f6a5717a3593b418c39895c6; built from db7415d5, parent of tag v2.14.1 |
| ef943914b24cf1eec313ff427e9f34f7cbe8711d1437bc106ab853e4638dea9b | Official 2.14.0 | GitLab package `shcdese` 2.14.0, SHCDESE.zip 9566e10b865f7a23d8fa8dfb572be7f1f729cb18b10fd2d820714c5ba420b34a; built from 6ff6b7f4, parent of tag v2.14.0 |
| e5d78fdf2ea9701336c398f7d29410f206d141fc01aa8099f30c824e6ab8a0b3 | Local build | Tag v2.14.1, commit 5908e1f |

The ProductVersion suffix names the source commit. 5908e1f differs from db7415d5
only in CHANGELOG.md, and from 6ff6b7f4 only in CHANGELOG.md and
`Detours/BulkUnitDetours.cs`, so `MapModManager` and the staging code are the same
source in all three.

Version numbers never authorize a binary. To add one:

1. Take SHCDESE.zip from the official GitLab package registry, check its SHA-256
   against the registry `file_sha256`, extract `000shcdese` into an empty folder and
   hash SHCDESE.dll. Record the ProductVersion commit. A local build is acceptable
   only with its tag and commit recorded.
2. Diff that commit against an audited source. If `MapModManager`, `MapArchive`,
   `ModInfo` or the staging/updater code changed, re-audit the
   [cleanup flow](docs/WORKSHOP.md#workshop-package-cleanup): the bool result,
   `_SE/.staging/BepInEx/plugins/<GUID>` and `_SE/.staging/delete_list.txt`
   applied after exit and before staged files are copied.
3. Add the hash to `AuditedExtenderHashes` and to the table above with its source.
4. Run `Verify-WorkshopContracts.ps1 -GameDir <game> -ExtenderDir <extracted 000shcdese>`
   and APISharedTests `WorkshopHookTests` built with `-p:ExtenderDir=<extracted 000shcdese>`.
   `InstalledExtenderPassesTheCleanupInstallerGate` runs the same hash, signature,
   transformation and compilation checks as `WorkshopCleanupRuntime.Initialize`.
5. Run the full `build.bat /nopause /noinstall` on an install with the new binary.
   Remove a hash only when that release is no longer supported.
