# Ally goods dialog extensions

`APIShared.Presentation.AllyGoodsPanelExtensions` provides independent sections below
Vanilla's **Send goods** dialog. It retains the existing goods selection, amount
buttons, commands, names, DataContexts and other mods' controls. This is UI only;
it does not send gifts or authorize gameplay actions.

## Registering a section

Register after Script Extender and APIShared readiness on the initialized Unity
thread. Use a stable mod GUID and an owner-local section ID:

    AllyGoodsPanelExtensions.TryRegisterSection(MyGuid, "automation",
        context => CreateFreshAutomationControl(context),
        out var registration, out string reason);

The factory returns a fresh unattached `Noesis.FrameworkElement`. Its DataContext
and resources remain yours. `context.Host` supplies inherited Vanilla resources;
do not change its layout or retain it across host recreation. Sections sort by
ascending order, ordinal mod GUID and section ID. Identical IDs from different
mods are independent; duplicates within one owner are rejected.

`SetVisible` removes a hidden section from layout. `SetEnabled` changes only that
section's interaction. `RequestContentRefresh` recreates its control and retries a
failed factory. These methods require Unity. Registrations last until process exit;
do not unregister or tear down shared hooks on plugin/component destruction.

## Selection and quantity

`TryGetContext(out context)` reads the current selection on Unity. The immutable
snapshot distinguishes the locally controlled player, viewed player and selected
recipient. `Good` and `Amount` are nullable: an unselected quantity differs from
zero, which is a valid threshold. Amounts above current inventory remain readable.
The selected quantity already includes any modifiers applied by another mod.

Read the context again inside a command before adopting the selected amount.
`PanelGeneration` invalidates retained UI identity after host replacement, but is
neither a mission ID nor network authority. Actual transport/source authority must
be checked separately, especially when viewing another player or spectating.

`TrySubscribeContext` replays the current state once and subsequently publishes
changes after existing GUI checks. Callbacks run synchronously on Unity, in
order/mod-GUID/ID order, with exceptions isolated. They also receive closure and
host-generation changes. No event reports that a gift has actually arrived.

## Layout and compatibility

The single APIShared XAML patch appends an initially collapsed host to the existing
`LayoutRoot`; it replaces no existing element. The runtime owns only its extension
surface and the additional root height while a sending section is visible. It
stacks independently registered sections and scrolls them within the remaining
viewport. It does not manually measure unattached views.

No factory runs under a registration lock. A faulty factory hides only its own
section until refresh or host recreation. Rebuilding a HUD creates fresh controls;
old view callbacks are detached. Persistent work uses the existing `GuiChecks`
publisher and Extender binding notifications, surviving SHCDE startup cleanup.

A replacement UI must preserve the named host in the expected root to use this
broker. Missing/moved anchors or a concurrent foreign root-height writer suspend
the extension with a diagnostic; foreign controls and dimensions are preserved.
Arbitrary complete UI replacements are not automatically supported. Consumers
must not install additional hooks on the goods panel's selection/update methods.

Tests cover independent owner registrations, selection boundaries, zero amounts,
factory error isolation and the actual private managed-member contract. They do
not verify Noesis rendering. In-game checks must cover multiple contributors,
viewport scaling, HUD recreation and coexistence with quantity/spectator mods.
