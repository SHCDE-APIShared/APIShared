# GameData layers

Several mods write the same Script Extender data tables: trade base prices, unit gold and
good costs, building costs. Each writes the whole table at its own moment, so the last
writer wins, a factor of 1.0 still overwrites another mod's values, and multiplayer peers
can end with different tables. `APIShared.GameTables.GameDataLayers` composes every
registered layer from the game's own values into one write per phase.

This is a pilot: only the trade base price table is served. Audience: authors of mods that
change those tables. Contracts are on the declarations in
[GameDataContracts.cs](../src/GameTables/GameDataContracts.cs) and
[GameDataLayers.cs](../src/GameTables/GameDataLayers.cs); the
[compiled example](../examples/ThirdPartyMod/GameDataExample.cs) shows a registration.

## Usage

Register from `Awake` or the Extender's `LibraryLoaded`, on every multiplayer peer:

```csharp
GameDataLayers.TryRegister(MyPlugin.Guid, "market-buy", GameDataTable.TradeBasePrices,
    GameDataLayer.Multiplier, order: 0,
    (GameDataCell cell, out GameDataContribution contribution) =>
    {
        contribution = GameDataContribution.Percent(buyPercent);   // a synchronized setting
        return cell.Column == 0 && buyPercent != 100;              // false: no contribution
    },
    out GameDataRegistration registration, out string reason);
```

Rows of `TradeBasePrices` are `eGoods` values; column 0 is the buy price, column 1 the sell
price. Keep handlers deterministic: integer settings that are identical on every peer.

## Contract

**Base.** Every cell starts from the game's own value, read once per process before APIShared
first writes the table (first write wins). For trade prices that is the default table
(`GetDefaultTradeBasePrice`) that every map load copies into the current table.

**Layers and order.** After Base, in this order; within a layer by ascending `order`,
ordinal owner GUID and ordinal registration ID, independent of registration time:

| Layer | Contribution | Effect |
|---|---|---|
| `Additive` | `Add(amount)` | Adds a signed amount |
| `Multiplier` | `Scale(numerator, denominator)`, `Percent(percent)` | Multiplies, rounding half away from zero after each step |
| `Override` | `Set(value)` | Replaces the result so far; the last one wins |
| `Clamp` | `Range(minimum, maximum)` | Limits the result |

A contribution of the wrong kind, an invalid ratio or range, and a throwing handler are
counted in `CallbackFailures` and skipped; the other layers still apply.

**Phases.** A table is composed at every mission start (new game and loaded save, after the
native initialization, not in the editor) and when an owner calls `Reapply`. A row is written
only when one of its composed cells differs from the live value, so a layer that changes
nothing, such as a factor of 1.0, writes nothing. Disposing the last layer writes the game's
values back at the next phase.

**Writer rule.** Compositions serialize on one lock and run on the Unity thread: the mission
lifecycle publisher, or the caller of `Reapply`.

**Direct writers are reported, not fought.** Before composing a cell the service checks that
its live value is the Base (the game filled the table) or the value its last composition of
this mission produced. Any other value was written by another mod: the cell is reported once
per mission in APIShared's log (`GAMEDATA:` line with table, row, column and values),
counted in `ForeignCells`, and left exactly as that mod wrote it. A direct write of the
game's own value (for example a factor-1.0 rewrite) is the table as the game fills it, so the
next phase composes it normally.

**Reload: restore, then apply.** The composed values are kept in the versioned save record
`APIShared.GameData` (entry `trade-prices`). After a load the table holds either those values
or the game's defaults; both count as this service's own, and the new composition starts from
Base, so nothing compounds and changed layers take effect. Every load replaces the record's
values; a map or a save without the record starts empty.

**Multiplayer.** Per peer, from the same Base with the same layers at the same phase. Layer
inputs must be identical on every peer before the phase, for example host-synchronized before
the mission starts. A mid-mission `Reapply` must run at a point that is the same on every peer.

**Cost.** Nothing is subscribed and no table is read before the first registration. A
composition allocates nothing.

## Consumer notes

Notes for each maintainer; nothing here changes those mods. Keep the old path behind a switch
for two releases, and never run both for the same table.

**ExtraFeatures market price multipliers** (first Serpens consumer). Today it writes
`default x multiplier` into every trade base price at settings apply and at map load, also at
factor 1.0, which replaces CrusaderDETweaker's `[Trade Prices]` and can differ between peers.
As two `Multiplier` layers (buy column, sell column) with integer percentages, a factor of 1.0
writes nothing and other mods' layers compose with it.

**CrusaderDETweaker `[Trade Prices]`.** Today it writes configured absolute prices into the
current table. As an `Override` layer for the configured cells it survives every other mod's
multiplier and is restored after reloads.

**UnitCosts and BuildingCosts.** Not served yet: unit gold and good costs and building costs
are the next tables. UnitCosts writes its startup snapshot for every `-1` cost, which
replaces host-synchronized costs on clients; as layers, a `-1` would simply register nothing.

## Limits

- Pilot: trade base prices only. The other tables of FW-11 (unit gold and good costs, building
  costs, default health and speed, damage matrices) need their own Base and phase rules.
- The service sees only the values it reads at a phase. A mod that writes after the phase
  wins until the next phase, where it is reported.
- The trade price table is read and written through the Script Extender's
  `GamePlayerManagerAPI`; the members are checked once at the first registration.

## Implementation

[GameDataTableComposer](../src/GameTables/GameDataComposer.cs) keeps, per table, the Base
values, the last composed value of every cell with an ownership flag, and a once-per-mission
report flag, all in arrays sized at construction. A composition reads the immutable layer
snapshot once, reads each row's live values into a reused buffer, composes each owned cell in
one pass over the sorted layers and writes the row only if a cell changed. A foreign cell
keeps its live value and never becomes the service's own. The composing methods skip
`.locals init`. The save record stores the composed values with their flags per cell.

### Measurements

`tests/APISharedTests/GameDataBenchmarkTests.cs`: one phase over all 25 trade base prices
through the same array-backed store calls, starting from the game's defaults every time.
"Today" is the direct loop a mod uses now: read each default, multiply buy and sell by a
double, round, write every row. 20,000 phases per sample, 31 samples after two warm-up
rounds, alternating mode order, raised priority; the machine was at 97 to 100 percent CPU
from other work. 0 bytes allocated per phase in every mode.

| Mode (ns per phase, whole table, min / median) | Cost |
|---|---|
| Direct loop, x1.5 (today) | 1098 / 1212 |
| Composer, 1 layer x1.5 | 1083 / 1188 |
| Composer, 1 layer x1.0 (writes nothing) | 987 / 1109 |
| Composer, 4 layers (all four kinds) | 1985 / 2190 |

A phase runs once per mission start (or explicit re-apply), so these are microseconds per
mission. The Script Extender calls themselves (one read per row, plus one write per changed
row; the direct loop also reads the default table every time) are not included.

Limits: desktop CLR 4.8.1 on the test host, not the game's Unity Mono; no in-game timing.
