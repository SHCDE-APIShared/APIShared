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

A settings screen asks how a cell composes for its own registration:

```csharp
if (GameDataLayers.TryGetCellStatus(registration, (int)eGoods.Wood, 0, out GameDataCellStatus status, out _) &&
    status.IsOverridden && !status.HasDirectWrite)
    note = "Your buy price multiplier has no effect on wood: " + status.OverrideOwnerGuid + " sets an absolute price.";
```

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

So an absolute value (Override) replaces every Additive and Multiplier contribution to that
cell, whichever mod registered them, and the final Clamps still limit it. Where several
Overrides apply to one cell, the last by ascending `order`, then owner GUID, then registration
ID wins.

A contribution of the wrong kind, an invalid ratio or range, and a throwing handler are
counted in `CallbackFailures` and skipped; the other layers still apply.

**Phases.** A table is composed at every mission start (new game and loaded save, after the
native initialization, not in the editor, where trading is unavailable) and when an owner calls
`Reapply`. A row is written only when one of its composed cells differs from the live value, so
a layer that changes nothing, such as a factor of 1.0, writes nothing. Disposing the last layer
writes the game's values back at the next phase.

**Settings are frozen during a match.** Layer inputs change between matches, and the next
mission start applies them. `Reapply` exists for an individually designed exception: a setting
its mod explicitly allows to change in a running match, applied at a point that is the same on
every peer. APIShared has no general facility for such settings yet.

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
the mission starts.

**Status query.** `TryGetCellStatus(registration, row, column, ...)` composes one cell with the
current layer inputs and writes nothing. It reports whether the registration contributes
(`Contributes`), the composed value (`ComposedValue`) and the value the game uses
(`EffectiveValue`), the Override the value comes from (`OverrideOwnerGuid`,
`OverrideRegistrationId`), the last Clamp that changed it (`LimitOwnerGuid`,
`LimitRegistrationId`), and `IsOverridden`: the registration contributes, but a later Override
or a direct write by another mod replaces its result. The direct-write check is the
composition's own, on the cell's live value; APIShared cannot tell which mod made the write.
Unity thread; allocates nothing; the table composition itself is unchanged by the query.

**Cost.** Nothing is subscribed and no table is read before the first registration. A
composition allocates nothing.

## Explaining the result to players

Players see one price, made from several mods' settings. Each consumer's settings text should
say how its values combine with others:

- A multiplier or an added amount applies on top of the game's price together with every other
  mod's multipliers. An absolute price set by any mod replaces all of them for that good, and
  a range limit (Clamp) still applies last.
- An absolute price says that it replaces multipliers. Where two mods set an absolute price
  for the same good, say which one wins: the higher `order`, then the owner GUID and ID.
- Where the result depends on another mod, show it: `TryGetCellStatus` names the Override that
  decides a cell and the Clamp that limits it, and says when another mod wrote the cell
  directly, so the screen can say "no effect on wood: an absolute price from X applies".

## Migration guides

Step by step for each maintainer; nothing here changes those mods. Each step list replaces
the mod's own table writes. A transition switch that keeps the old path for a release or two
is an optional strategy, useful when many independent mods depend on the old behavior; it is
not required. Never run the old path and a layer for the same table at the same time.

### ExtraFeatures market price multipliers

Today `ApplyMarketPriceMultipliers` writes `default x (general x per-good multiplier)` into every
trade base price at every settings apply and map load, also at factor 1.0 (it then writes the
default price), and `RestoreTradeBasePrices` writes the defaults when the mod is disabled. Both
replace CrusaderDETweaker's `[Trade Prices]`, and a settings apply can differ between peers.

1. Require the APIShared release that contains `GameDataLayers` in the plugin's dependency
   attribute.
2. At startup (`Awake` or the Extender's `LibraryLoaded`), on every peer, register two
   `Multiplier` layers for `GameDataTable.TradeBasePrices`: `market-buy` (column 0) and
   `market-sell` (column 1), both at `order: 0`. Keep the two `GameDataRegistration` handles.
3. In each handler, return false when the mod is disabled (`GameplayModActivationGate`), when
   the cell is not its column, or when the combined multiplier is 1.0. Otherwise return
   `GameDataContribution.Scale(numerator, 10000)` with
   `numerator = (int)Math.Round(general * perGood * 10000, MidpointRounding.AwayFromZero)`.
   The layer rounds half away from zero like `MultiplyPrice`, but in integers, so a price can
   differ by 1 only where today's double product lands next to a .5 boundary.
4. Remove the `ApplyMarketPriceMultipliers` calls from `ApplySettings` and
   `ApplyMapLoadedSettings`, and the `RestoreTradeBasePrices` call from `RestoreDefaultSettings`.
   The service composes at every mission start from the settings of that moment, and writes the
   game's prices back when no layer contributes.
5. Do not call `Reapply` when a setting changes: settings are frozen during a match, and the
   next mission start applies them. The editor needs nothing (no trading there).
6. `AIMarketVanillaPriceHook` stays as it is: it reads the default table, which the service
   never writes.
7. In the settings help text, say that an absolute price from another mod replaces the
   multiplier for that good (see Explaining the result to players). Optionally show
   `TryGetCellStatus(...).IsOverridden` per good in the per-good price editor.
8. Check: with factor 1.0 the log shows no `GAMEDATA:` line and `GameDataLayers.RowWrites`
   stays 0; with x1.5 a new game and a reload of its save show the same prices.

### CrusaderDETweaker `[Trade Prices]`

Today `LoadTradePrices` writes the configured absolute buy and sell prices into the current
table when the configuration applies.

1. Register one `Override` layer (`trade-prices`, `order: 0`) for `TradeBasePrices` at startup.
2. Its handler returns `GameDataContribution.Set(price)` for each configured good and column
   (`row = (int)good`) and false for every other cell.
3. Remove the `SetTradeBasePrice` write; keep the read-back log as a check of the composed
   value at mission start.
4. In the `[Trade Prices]` help text, say that a configured price replaces every multiplier
   from other mods for that good.

### UnitCosts and BuildingCosts

Not served yet: unit gold and good costs and building costs are the next tables, each with its
own Base and phase rules. UnitCosts writes its startup snapshot for every `-1` cost, which
replaces host-synchronized costs on clients; as layers, a `-1` would simply register nothing.
Their guides follow with those tables.

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
rounds, alternating mode order, raised priority. The quietest of five runs; other work kept
the machine at about 97 percent CPU. 0 bytes allocated per phase in every mode.

| Mode (ns per phase, whole table, min / median) | Cost |
|---|---|
| Direct loop, x1.5 (today) | 578 / 607 |
| Composer, 1 layer x1.5 | 648 / 704 |
| Composer, 1 layer x1.0 (writes nothing) | 610 / 658 |
| Composer, 4 layers (all four kinds) | 1215 / 1357 |

One layer costs about 70 ns more per phase than the direct loop, which checks no cell's
ownership and calls no handler. A phase runs once per mission start (or explicit
re-apply), so this is well under a microsecond per mission. The Script Extender calls
themselves (one read per row, plus one write per changed row; the direct loop also reads the
default table every time) are not included. The status query has its own composition pass,
so the table composition carries no tracing.

Limits: desktop CLR 4.8.1 on the test host, not the game's Unity Mono; no in-game timing.
