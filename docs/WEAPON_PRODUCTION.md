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

## Produce a registered custom good

Register an Armory good through `ModApiClient.NativeStorage` before the first
mission, then bind that same owner's registered handle with `WithStoredOutput`:

```csharp
if (!api.NativeStorage.TryRegisterGood("alternate-armour", NativeStoragePool.Armory,
        out var armour, out string reason)) { Log(reason); return; }
var definition = new WeaponProductionOptionDefinition(
    eStructs.STRUCT_ARMOURERS_WORKSHOP, eGoods.STORED_METAL_ARMOUR,
    eGoods.STORED_METAL_ARMOUR, localizedName, baseAmount: 2, workTicks: 60)
    .WithIngredientAmount(1)
    .WithStoredOutput(armour);
if (!WeaponProduction.TryRegister(pluginGuid, "alternate-armour", definition,
        out var registration, out reason)) Log(reason);
```

See the [compiled custom-output example](../examples/ThirdPartyMod/CustomWorkshopExample.cs).
The stored-good owner must
match the recipe owner, the handle must be the exact registered instance, and its
pool must be Armory. Native workshop destination search does not select Granaries.
`WithIngredientAmount` and `WithStoredOutput` return immutable copies and preserve
each other's settings. Passing null to `WithStoredOutput` throws.

The template retains its native ingredients, manufacturing and transport. The
native `OutputGood` remains the recipe's native icon/carrier descriptor; a custom
delivery creates only the separate `StoredOutput` stock. Map restrictions follow
the work template for these recipes. Existing native-only recipes keep their
output-based restriction rule. The latched option follows the finished local
product and its carrier, so selecting another recipe does not reinterpret goods
already manufactured. Existing output modifiers still determine issued cargo.

Each qualified arrival attempts one item. Acceptance atomically publishes one
physical custom item and subtracts one signed native cargo unit. Refusal publishes
neither and retains the carried goods; a protected retry transition can re-enter
the original native Armory search. Native search still requests enough space for
the remaining cargo and retains its normal pathfinding/hazard behavior. It does
not guarantee eventual delivery. No ingredients are refunded after manufacture.

Custom acceptance bypasses native template stock, weapons-produced counters,
weapon achievements, the native stock cue and the template AddGood event chain.
Those would describe an item which was not delivered. Native recipes retain their
complete original delivery and accounting behavior. A custom Tanner requires the
original qualified route; if ImprovedHunters changes that route, only the new
stored-output Tanner capability is unavailable. Legacy Tanner recipes remain
available under their existing compatibility rules.

Stored-output workshops currently support confirmed offline and editor worlds.
Real multiplayer is rejected before launch/activation because native worker repair
and private storage repair have not been qualified as one state transfer. Matching
catalogue hashes alone do not establish that guarantee.

### Stored-output persistence

The existing building/worker wire rows retain their layout and catalogue option
indices. Native-only catalogues retain their exact established digest formats;
custom-output catalogues include the stable good owner, local ID and pool.

A stored-output save uses workshop reader requirement 2. Older framework readers
preserve that record as too new. Native-only captures retain their exact schema-1
payload and envelope. At the existing critical save boundary, the native carrier,
workshop state and both pooled-storage entries are frozen under the same engine
monitor. The save succeeds only after the matching archive records and native I/O
are verified; failed or stale captures preserve the previous destination file.
Reentrant custom mutations are refused while that checkpoint is frozen.

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
