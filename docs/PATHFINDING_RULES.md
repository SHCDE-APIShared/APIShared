# Pathfinding rules for mods

APIShared does not search routes itself. It offers passive contracts through which a mod states
movement rules, and route engines in other mods (route providers) apply them. Without such an
engine a rule has no effect, and Vanilla's own searches and move commands are never changed by a
rule. This guide shows which contract fits which rule; each contract's details stay at its source
and in the [API catalog](API_CATALOG.md).

## Which contract for which rule

| Rule a mod wants | Contract | Consumer side | Engines that apply it |
|---|---|---|---|
| Avoid tiles (fire, hazards), close areas, add entry costs | [`RouteTileRules`](../src/Pathfinding/Routes/RouteTileRules.cs) | `TryRegister`, then `Set`/`Clear` of `Forbidden` or `Penalty` (1..255 ground steps) per tile and player mask | Route providers that read the effects and the change journal (for example a moat-routing core's masks and edge costs). |
| React to rules that appear after a move was planned (fire on a route already walked) | [`RouteReplanPolicy`](../src/Pathfinding/Routes/RouteTileRules.cs) on a rule set | trigger distance, allowed detour in percent and minimum steps, maximum wait | Providers that watch remaining routes; the unit keeps its order and target. |
| Prefer or avoid moat crossings, change ground versus moat edge costs of a weighted search | [`RouteSearchEvents`](../src/Pathfinding/Routes/RouteSearchContracts.cs) | Pre callback adjusts `GroundEdgeCost`/`MoatEdgeCost` or skips the additional search | Publishers of additional weighted searches (moat-aware movement). |
| Allow or deny gates (own, allied, captured) per player | [`GateAccessRules`](../src/Pathfinding/GateRoutes/GateAccessRules.cs), [guide](GATE_ROUTING.md) | `Allow`/`Deny`/`Unchanged` per gate query, `Invalidate` after own data changes | Gate routing providers while publishing player masks. |
| Wall climbing and weighted Assassin routes | [`AssassinPathAPI`](../src/Pathfinding/Assassin/AssassinPathAPI.cs) | one registered weighted builder (sole owner), reconstruction and gatehouse-climb policy flags | The registered builder; APIShared owns the native sites. |

Combine contracts freely: a fire mod registers tile penalties with a replan policy; a siege mod can
additionally deny gates. Each contract documents its own combination rule (for tile rules: Forbidden
wins, otherwise the largest penalty).

## Rules for rule consumers

- Call every contract on the simulation thread and derive rule data only from deterministic game
  state. Rules shape gameplay routes, so all multiplayer peers need the same rule mods and settings.
- Rules only restrict or penalize: they never allow a transition the game forbids and never change
  unit speed. A unit standing on a forbidden tile may still leave it.
- Clear map-bound rules at a map change (for example `RouteTileRuleSet.ClearAll`).
- Keep rule updates incremental; engines follow change journals and versions instead of re-reading
  everything.

## Rules for route engines

- Read rules cheaply when none exist (`RouteTileRules.HasRules`, `HasReplanRules`) and follow
  changes through journals and versions.
- Keep Vanilla's search and move commands available as the fallback for everything the engine cannot
  answer; a rule must never make an otherwise valid Vanilla command fail silently.
- Apply rules to every route the engine publishes, including later live checks of a route.

## Limits

- Rules affect only routes of engines that support the contract; with several engines, each states
  what it supports.
- A replan policy is a request: engines may check units in intervals, share one decision among the
  members of one group order and skip units whose route they cannot read.
