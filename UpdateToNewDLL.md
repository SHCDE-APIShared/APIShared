# Native binary update contracts

## Fixes wheat-sale startup correction

Owner: internal `Compatibility/FixesWheatStartup`; foreign hook ownership stays
with Fixes. See [startup safety, behavior and test limits](docs/FIXES_COMPATIBILITY.md).

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
game and backend rows remain pinned to the audited machine/private-backend
contracts. Unsupported hook bytes and startup evidence are always refused.

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
Native game/report ABI changes still require their own audit. CLI fixtures validate
rewriting and JIT preparation; actual Mono/game and multiplayer acceptance are separate.
