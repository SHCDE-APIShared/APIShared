# AI market sell policies

`APIShared.Economy.AiMarketSellPolicies` shares the final AI reserve decision and
the quantity preparation before native market sale booking. It does not change
lord JSON or stored AIC values. The service installs inactive native hooks at
LibraryLoaded; a registered consumer must explicitly enable its policy.

## Consumer contract

Register once after native initialization with your own GUID and stable local ID.
`TryRegister` returns an initially inactive, process-held registration or a reason
for refusal. Duplicate owner/ID pairs, unknown safety bits, unknown DLLs, occupied
sites and incompatible hook backends are refused. Failed startup installation is
never retried later during gameplay. There is no disposal or unpatch operation.

Use `SetEnabled` before a mission on **every peer**, using the same synchronized
setting. The two safety options are combined with OR across enabled owners:

- `NonNegativeReserve` floors the final effective reserve at zero. Vanilla then
  compares stock against it and continues searching if this good is unavailable.
- `LimitQuantityToStock` caps the prepared amount at nonnegative signed total
  stock. Both pricing and removal receive that amount. Zero bypasses pricing,
  all three credits, removal and sale presentation through the original epilogue.

Optional reserve/quantity adjusters run synchronously on the native caller thread
(normally the simulation thread), outside registration locks. Order is numeric,
then owner GUID and registration ID using ordinal comparison. Contexts expose
validated native player IDs 1..8, a native `eGoods` index excluding null/gold,
signed total stock and the original value. Only the proposed reserve or amount is
mutable. Contexts may be retained for reading, but are writable only during their
dispatch on that thread. They are never pooled.

A callback exception restores that callback's proposed value and increments its
registration's bounded error counter; subsequent callbacks continue. Recursion
bypasses notifications, but native safety remains active. After callbacks, active
safety applies last; the quantity limit re-reads current stock because a recursive
sale may already have consumed it. Nonpositive callback amounts cancel the sale
even without stock safety. Vanilla rejects negative original requests before this
site, so these are not notifications for every rejected AI decision.

Do not touch UI, send network messages, log per candidate, change synchronized
settings during dispatch, or make local-only simulation decisions. These contexts
describe preparation, **not a completed sale**. No success/Post event is provided.
MarketPriceEvents remain price queries, including ally valuations; returning a
zero price there does not cancel the containing sale.

See the independently identified [consumer example](../examples/ThirdPartyMod/AiMarketSellExample.cs).
For a custom reserve, optionally pass `reserveAdjuster: context => { ... }` and set
`context.Reserve`; to reject a prepared sale, set `context.Amount = 0` in a quantity
adjuster. A safety-only consumer supplies neither callback.

## Limits and native ownership

The guard uses Vanilla's signed aggregate stock; it is not a repair for already
corrupt physical storage or a guarantee against mods that replace later removal
or booking behavior. Unrelated overflow behavior for arbitrary prices/gold is not
redefined. All eight sale categories and 20 table goods are covered.

APIShared exclusively owns both spans as `Economy.AiMarketSell` in the native-site
registry. Consumers must not hook them independently. Fixes' wheat category site,
the stone-reserve fix, MarketPriceEvents and the sale-entry diagnostic are outside
these spans. Disable the standalone AISellThresholdFixTest installation before
using this shared owner; retain its sources and evidence for comparison.

## Implementation and verification

Native demand bits skip managed calls unless an enabled adjuster exists for that
site. Safety-only decisions and sales allocate nothing in managed code. Optional
bridges preserve actual flags, volatile integer registers and XMM0..5, dynamically
align the stack and reserve Windows x64 shadow space; they do not depend on
RedBird context flags. Nonvolatile state follows the Windows x64 ABI.

Both hooks prepare without executable writes, are rooted through
NativeHookPublication before the first Enable attempt and remain rooted even after
partial activation or post-write validation failure. Demand remains zero until the
complete installation validates. Only unpublished candidates may release state.
See [native update contracts](../UpdateToNewDLL.md#ai-market-sell-policies).

`tests/AiMarketSell.NativeTests` executes the production generator and broker using
the installed RedBird backend against private copies of the audited original
decision, pricing, sale, removal and recount routines. It reproduces the Vanilla
exploit before installation, tests all goods/categories, signed extremes, branches,
disabled parity, callback composition/rollback/recursion and register/flags/SIMD
sentinels. Invoke it with `dotnet run --project tests/AiMarketSell.NativeTests -c
Release -- <CrusaderDE.dll>` with the game/Extender paths configured as in
[CONTRIBUTING](../CONTRIBUTING.md). The regular build driver runs it as well.

The 2026-10-22 Windows .NET 10 fixture measured 10,000 empty safety-only native
sales at approximately 3.4 ms with zero managed bytes; no adjuster was called.
This is a private execution fixture, not a Unity/Mono benchmark or FPS claim.
In-game map/save/editor and real multiplayer acceptance must be recorded separately.
