# Audio binary update contract

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
and after publication. The rooted intermediary reserves 0x38 aligned Win64 stack
bytes and forwards the original return PC as argument five. Execute it on copied
full wrappers after every backend change. No published hook may be reverted.

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
