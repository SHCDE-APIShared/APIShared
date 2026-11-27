# Ally goods dialog extensions

`APIShared.Presentation.AllyGoodsPanelExtensions` provides independent sections below
Vanilla's **Send goods** dialog, optional shared frame presentations, compact footer controls and noninteractive markers
on its goods icons. It retains the existing goods selection, amount
buttons, commands, names, DataContexts and other mods' controls. This is UI only;
it does not send gifts or authorize gameplay actions.

## Registering a section

Register after Script Extender and APIShared readiness on the initialized Unity
thread. Use a stable mod GUID and an owner-local section ID. For new controls that
should follow the visible goods dialog, choose the placement explicitly:

    AllyGoodsPanelExtensions.TryRegisterSection(MyGuid, "automation",
        context => CreateFreshAutomationControl(context),
        AllyGoodsSectionPlacement.GoodsDialogBottom,
        out var registration, out string reason);

The original overload remains supported and keeps its original placement below
the entire menu root (`PanelRootBottom`):

    AllyGoodsPanelExtensions.TryRegisterSection(MyGuid, "automation",
        context => CreateFreshAutomationControl(context),
        out var legacyRegistration, out string legacyReason);

The factory returns a fresh unattached `Noesis.FrameworkElement`. Its DataContext
and resources remain yours. `context.Host` supplies inherited Vanilla resources;
do not change its layout or retain it across host recreation. Within each placement,
sections sort by ascending order, ordinal mod GUID and section ID. Identical IDs from different
mods are independent; duplicates within one owner are rejected.

`SetVisible` removes a hidden section from layout. `SetEnabled` changes only that
section's interaction. `RequestContentRefresh` recreates its control and retries a
failed factory. These methods require Unity. Registrations last until process exit;
do not unregister or tear down shared hooks on plugin/component destruction.

### Layout availability

`registration.TryGetLayoutAvailability(out reason)` reports whether the selected
placement has valid geometry and any remaining viewport space. It is independent
of `SetVisible` and gameplay permission: a collapsed section can be available, and
an available section does not prove its factory succeeded or its commands are
authorized. The reason is a developer diagnostic, not localized player text.

Subscribe to `LayoutAvailabilityChanged`, then read the getter immediately;
subscriptions have no replay. Notifications run on Unity when availability or its
reason changes, including menu closure and HUD replacement. Subscriber failures
are isolated. Layout may still be unavailable before the first completed GUI
layout pass. A footer launcher can use this notification to disable itself and
present its own localized explanation without submitting an edit or game command.
The compiled example shows this pattern.

## Registering a frame presentation

An optional frame presentation supplies the shared decoration for the marked goods
frame and `GoodsDialogBottom` sections. It is a separate registration, not a style
or background attached privately to one owner's section:

    var options = new AllyGoodsFrameOptions(
        new Thickness(10, 8, 10, 8), // contentPadding
        new Thickness(3),          // visualOverflow
        200,                       // maximumExtensionHeight
        8,                         // viewportMargin
        8);                        // sectionSpacing
    if (!AllyGoodsPanelExtensions.TryRegisterFramePresentation(MyGuid, "details-frame",
        context => CreateFreshFrameDecoration(context), options,
        out var frame, out string reason))
        Log(reason);
    else
        frame.SetEnabled(detailsExpanded);

Register on the initialized Unity thread after readiness. The registration starts
disabled. `SetEnabled` selects whether this owner participates; it does not toggle
sections or grant gameplay permission. `RequestContentRefresh` recreates the
decoration and explicitly retries a failed factory. Registrations last until
process exit and have their own owner-local ID namespace.

Enabled profiles sort by ascending order, ordinal GUID and ID. The first enabled
profile supplies the common goods-anchored presentation for all contributors.
It is used only while that group has visible content.
There is no fallback to a later profile if its factory fails. In that case the
original frame and existing standard section geometry remain available; retry
explicitly or disable the selected profile. No active presentation preserves the
existing standard rendering and layout. This opt-in does not apply new styles or
content metrics to `PanelRootBottom`; its subsequent placement still clears the
goods-anchored presentation, including declared bottom overflow. Footer controls
and gameplay remain independent.

`AllyGoodsFrameContext` exposes the attached `Host`, marked `OriginalFrame`, and
factory-time `Selection`. Return a fresh unattached noninteractive decoration;
APIShared supplies its bounds and disables interaction. Do not move native
controls into it, retain the host across HUD recreation, or modify the original
frame. Successful and failed factory results are cached per HUD and profile until
explicit content refresh or host replacement. They are not recomputed for every
ware/recipient change. The compiled example checks a fresh context's
`PanelGeneration` against the factory snapshot before creating its decoration.
Returning null explicitly declines the current layout and is cached by the same
retry policy, keeping the standard presentation.

`AllyGoodsFrameOptions` is immutable and has no implicit layout defaults. Pass
content padding, visual overflow, maximum extension height, viewport margin and
section spacing explicitly. All thickness components and scalar values must be
finite and nonnegative; maximum extension height must be positive. Content
padding belongs inside the shared extension scroll surface; section spacing is
the vertical gap between its section containers. Visual overflow
declares decoration outside the assigned frame bounds on all four sides; its bottom allowance is
also reserved when computing viewport room and subsequent legacy placement.
The consumer supplies its own colors, templates, assets and metric values;
APIShared supplies no Vanilla style key or color palette for this path.

The original frame keeps its position and dimensions. APIShared can temporarily
own only its opacity while the joined decoration is active, and restores the
owned opacity when it stops. An existing opacity binding or incompatible frame
prevents joining: the original display and standard geometry remain unchanged.
Consumer factories must never change that opacity themselves or overwrite
foreign bindings. Rendering, clipping and popup coexistence still require game
acceptance; a successful factory or compile is not a rendering test.

## Registering footer controls

Use `TryRegisterFooterControl` for a compact launcher or action in the lower free
strip of the selected-goods box. Register on Unity after readiness, with a stable
owner GUID and an owner-local ID:

    AllyGoodsPanelExtensions.TryRegisterFooterControl(MyGuid, "automation-toggle",
        context => CreateFreshCompactLauncher(context),
        out var footer, out string reason);

The footer has its own registration namespace. Controls sort by ascending order,
ordinal owner GUID and ID. Return a fresh unattached element; its DataContext and
resources remain yours. The factory's attached `Host` supplies inherited resources.
Do not retain the host across HUD replacement, change its layout, or assume a
factory-time selection still applies when a command executes.

Footer controls remain available independently of section visibility. They have
18 logical units of height, with four units of bottom spacing and 16 units of
horizontal inset. Use compact controls with their own suitable tooltips. The
broker centers controls in one horizontal row. If their attached, normally
measured widths exceed the strip, a localized **More...** button opens a bounded,
scrollable popup containing the same controls. It adds no footer row and never
measures or arranges a detached control. Switching between the row and popup
reparents the existing controls and can therefore raise their normal Noesis
`Unloaded`/`Loaded` events; keep presentation state outside these events.

`SetVisible` hides a control while retaining it. `SetEnabled` changes only its
interaction availability. `RequestContentRefresh` recreates that control and
retries its factory after an error. Factories fail independently, with no implicit
retry until explicit content refresh or host replacement. All registration methods
require Unity, and registrations remain rooted until process exit.

The footer is active only in the open sending dialog and requires the shared
marked selected-goods anchor. It does not resize that box or move foreign controls.
The broker observes attached layout events and suspends its own row if existing
visible content occupies the intended lower strip or its geometry is unavailable.
Only the explicitly marked `FooterBackground` is excluded from occupancy detection;
an unmarked foreign button is still occupied content. Missing or
incompatible anchors preserve the existing dialog. Popup closure follows dialog
closure, host replacement and changes to the registered controls. The broker
supplies no gameplay authority or mod-specific open/closed state.

Tests cover independent registration ordering, cache lifetime, explicit retries,
failure isolation and strip/width boundaries without constructing native Noesis
views. Popup rendering, input and viewport scaling still require game acceptance.

## Registering goods markers

Use an independent adornment registration for compact status icons on each existing
goods button. This shares the broker and lifecycle without replacing button content,
commands, bindings or selection behavior:

    AllyGoodsPanelExtensions.TryRegisterGoodsAdornment(MyGuid, "status",
        (context, good) => CreateFreshStatusIcon(context, good),
        out var markers, out string reason);

The factory receives the real button's ware and a factory-time selection context.
Its `Host` is an attached APIShared container with inherited resources; do not mutate
that container. Return a fresh unattached element per ware and host. Successful
controls remain cached across recipient/ware/quantity selection changes and refresh
requests. Update their own `Visibility` or bound view model on Unity as your state
changes, then call `markers.RequestRefresh()`. This requests rearrangement and retries
failed factories; it does not invoke a consumer update callback or recreate successful
controls. Host recreation creates fresh controls; discard old view references using
the existing context generation/lifecycle information.

Adornments have their own owner-local ID namespace and sort by ascending order,
ordinal owner GUID and ID. Multiple providers receive separate ordered slots that
wrap inside the actual button bounds. Use small icons: oversized content or more
markers than fit are clipped to the button. Collapsed controls consume no space.
Pointer input and keyboard navigation are disabled for the overlay. Interactive
commands belong in a section. No gameplay or automation state belongs in this API.

Only unambiguous `Gxx` buttons with Grid parents are anchored. The scan runs once per
bound host; geometry follows the actual controls rather than screen coordinates.
Markers inherit the goods raster's visibility in send and request views. Their
consumer controls decide whether a marker applies to the current mode. Missing,
ambiguous or moved anchors leave the existing buttons intact. Marker layout is
independent of the additional section height and its foreign-writer checks.

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

The APIShared XAML patch adds an initially collapsed host to the existing
`LayoutRoot` and semantic attached roles to the goods frame and selected-goods
footer. It replaces no existing element, name, command, Tag or DataContext.

`GoodsDialogBottom` starts at the transformed bottom of the marked visible goods
frame, independently of a taller root used by another mod's main diplomacy view.
`PanelRootBottom` retains the original entire-root anchor. If both placements are
visible, goods-anchored sections come first; legacy sections start at the greater
of the original root height and the end of the goods-anchored group. For example,
with a 484-high menu root and an unchanged 320-high goods frame, a new anchored
section starts at 320 while a legacy section starts at least at 484.

Without an active frame presentation, each group scrolls within the remaining
viewport and its existing 260-unit maximum. An active profile supplies explicit
goods-anchored content metrics and an extension limit; legacy placement retains
its existing contract. The
runtime extends the original root only as far as needed and restores its original
height when extensions close, provided no foreign writer has changed its owned
value. It never reduces a taller original root or manually measures unattached
views. Footer controls live inside the marked footer and do not require a visible
section or additional root height.

### Anchors for replacement layouts

Preserve `APISharedAllyGoodsExtensionsHost` as a direct child of the expected
`LayoutRoot` for the broker. Replacement layouts may annotate corresponding
elements with `AllyGoodsPanelAnchors.Role` before the first anchor resolution:

- Exactly one `GoodsFrame` marks the visible goods backdrop, excluding extension
  space.
- Exactly one `Footer` marks its contained selected-goods/confirmation Grid.
- An optional `FooterBackground` marks at most one decorative element that is a
  direct child of `Footer`. Only this identity is exempt from footer occupancy
  checks; the broker does not classify backgrounds by type, style or commands.

Roles can be attached in XAML using the
`clr-namespace:APIShared.Presentation;assembly=APIShared` namespace, or through
`AllyGoodsPanelAnchors.SetRole` on Unity. They do not rename elements or register
controls. The goods frame must fit inside the original root, and the footer must
fit inside the frame after coordinate transforms. Duplicate, missing or
incompatible roles disable role-dependent placement and footer controls for that
bound host; legacy placement remains independent. Role resolution is cached until
host replacement. Do not add duplicate roles to the original controls already
marked by APIShared.

No factory runs under a registration lock. A faulty factory hides only its own
contribution until an explicit retry or host recreation. Unchanged section controls
retain their identity and focus when other providers refresh or hide. Rebuilding
a HUD creates fresh controls; old view callbacks are detached. Persistent work uses the existing `GuiChecks`
publisher and Extender binding notifications, surviving SHCDE startup cleanup.

A missing/moved shared host or a concurrent foreign root-height writer suspends
section layout with a diagnostic; foreign controls and dimensions are preserved.
Footer occupancy and marker anchors have their own checks. Arbitrary complete UI
replacements are not automatically supported. Consumers
must not install additional hooks on the goods panel's selection/update methods.

Tests cover independent owner registrations, immutable ordering snapshots, selection boundaries,
zero amounts, factory error isolation, marker/footer cache and refresh semantics,
mixed section placements, layout availability, explicit roles, footer occupancy,
exact goods-command parsing and the actual private managed-member contract. They do
not verify Noesis rendering. In-game checks must cover multiple contributors,
viewport scaling, HUD recreation and coexistence with quantity/spectator mods.


## Coordinating button actions

Use `AllyPanelButtonEvents.TryRegister` for `HUD_AlliesPanel.ButtonClicked`.
Register on Unity with an owner GUID and an owner-local ID. The process-owned
broker orders callbacks by order, GUID and ID. In Pre, check the current panel
context and call `TryClaim` only for a command your mod owns. Leave execution to
the claimed handler. The first claim wins, and any Pre veto suppresses both
the handler and Vanilla.

The handler runs synchronously after Pre, on the button's caller thread. It can
forward once to the original through `InvokeOriginal`. Forwarding may produce
separate Vanilla nested clicks. Clicks issued from Pre or Post on this broker
are suppressed. Claims and panel references are valid only during the callback.
Original or handler exceptions are reported in Post and propagate without a
retry. Post reports managed completion. Use actual transfer events to observe
native delivery. See the compiled `AllyGoodsExample` for an observation.
