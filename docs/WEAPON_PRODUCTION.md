# Weapon workshop recipes

Mods can add recipes to the Fletcher, Poleturner, Blacksmith, Armourer and Tanner
through `APIShared.Economy.WeaponProduction`. APIShared provides the in-game
selector and synchronizes recipe choices. The original recipes keep their slots
in each workshop's four-option limit.

## Register a recipe

Call `WeaponProduction.TryRegister` on the initialized Unity thread during
startup on every simulation peer. Use your BepInEx GUID and an owner-local option
ID that stays stable across saves. Registrations live for the process. Supply a
localized `displayName` from your mod.

```csharp
using APIShared.Economy;
using SHCDESE.Interop;

var definition = new WeaponProductionOptionDefinition(
    eStructs.STRUCT_FLETCHERS_WORKSHOP,
    eGoods.STORED_CROSSBOWS,
    eGoods.STORED_CROSSBOWS,
    localizedName,
    baseAmount: 2,
    workTicks: 60,
    priority: 10)
    .WithIngredientAmount(4);

bool registered = WeaponProduction.TryRegister(
    pluginGuid, "fletcher-crossbow-batch", definition, out var registration, out var reason);
```

The template must be a built-in product of the target workshop. It supplies
the default ingredient cost and work sequence. The output can be any of the
eight existing weapon and armour goods. The example consumes four wood per
charge and supplies a base output of two crossbows to the existing worker
yield pipeline. Script Extender yield events and the shared base, fixed and
[fear-level production rules](WORKER_PRODUCTION_BONUS.md#fear-levels) still apply.
Fractional output credits stay with the worker.
See the [compiled example](../examples/ThirdPartyMod/WeaponProductionExample.cs).

`baseAmount` accepts 0–100. `workTicks` accepts 1–65535 active manufacturing
updates. Travel, ingredient pickup, pauses and delivery are separate from that
count. The nullable overrides use the template defaults when omitted.
`WithIngredientAmount` returns a new definition with the requested cost.

### Ingredient cost per charge

| Workshop | Native ingredient | Default cost per charge | Accepted override |
| --- | --- | --- | --- |
| Fletcher | Wood | Bows 2, crossbows 3 | 1–48 |
| Poleturner | Wood | Spears 1, pikes 2 | 1–48 |
| Blacksmith | Iron | Maces and swords 1 each | 1–48 |
| Armourer | Iron | Metal armour 1 | 1–48 |
| Tanner | Cowhides | Leather armour 1 | 1–3 |

Wood and iron still arrive one unit per trip. A Tanner's cow still provides
three hides. The quantity is the total ingredient cost for the charge, even
when it yields multiple items. The HUD hover text shows explicitly configured
costs. A new selection changes the next charge and preserves the latched recipe
for the current charge.

### Compatibility and admission

Successful registration means the definition passed validation. Check
`registration.Status` and `registration.Diagnostic` after mission preparation
for the admission result. After reserving the built-in recipes, APIShared fills
the remaining slots in descending priority order. Equal priorities sort by
ordinal owner GUID and then option ID. A recipe that requires an unavailable
ingredient adapter reports `AdapterUnavailable`, leaving the slot available
for another eligible recipe.

If ImprovedHunters changes the Tanner's cow yield, recipes costing two or three
hides report `AdapterUnavailable`. With ImprovedHunters, the selected weapon
applies to armoury deliveries. Meat deliveries continue to use ImprovedHunters'
yield and storage rules. A duration override applies to the shared hide-working
phase before either route is selected.

## Select recipes in a mission

`GetOptions` returns the active catalogue for a workshop type. The building HUD
shows the selector when an additional recipe is available. Map restrictions can
disable individual choices. Hold Shift when choosing a recipe to apply it to all
live workshops of the same type owned by the issuing player.

From custom Unity UI, call `RequestSelection` with the one-based building ID,
current Global ID and stable option ID. Set `allOfType: true` for the same group
action as Shift. `Submitted` means the command was queued locally. Call
`TryGetSelection` to read the current and next recipes after execution. Use the
same API to select a built-in recipe on an extended workshop, since a raw
template field write cannot distinguish recipes that share a template.

For AI workshops, call `TrySetAiNextOption` from the same deterministic simulation
callback on every peer. Supply the same AI-owned building identity and admitted
option. The consumer decides when the AI should change recipes.

## Multiplayer and saved worlds

Use matching gameplay definitions on all peers. Ingredient cost, output base
and manufacturing duration contribute to catalogue agreement. Display labels
can be localized independently. The mission freezes the admitted catalogue.
Saved recipe selections and active batches require the matching catalogue when
restored, so keep the original definitions available when loading those saves.

For native adapter and repair-transport update checks, see
[UpdateToNewDLL](../UpdateToNewDLL.md#weapon-workshop-ingredient-amounts).
