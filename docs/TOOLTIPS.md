# In-game tooltip extensions

APIShared 0.13.0 provides `APIShared.Presentation.TooltipPresentation`. It adds literal text and shareable images
to existing Vanilla tooltips. It does not calculate, merge, charge or authorize
costs. Resource names, amounts, stocks, localization, feature activation and
gameplay rules belong to the contributing mod.

## Registering content

Register on Unity's main thread after APIShared's library initialization, using
your plugin GUID and a stable owner-local contribution ID. Check the returned
boolean and diagnostic. `TryRegisterText` accepts a provider returning one string;
`TryRegisterContent` accepts immutable `TooltipLine` sequences containing
`TooltipText` and `TooltipSymbol` segments. Text is literal, never XAML or markup.
An optional text colour is ARGB. Symbols use a shareable Noesis `ImageSource`,
never an attached `Image` control, and may specify literal fallback text.

The [standalone example](../examples/ThirdPartyMod/TooltipExample.cs) compiles
against the public assembly without workspace helpers or known consumer GUIDs.

| Target | Existing surface |
| --- | --- |
| `BuildingCompact` | Building hover/placement main line |
| `BuildingDetailed` | Expanded building tooltip |
| `Recruitment` | Active barracks, mercenary, Bedouin, engineer, tunneler or monk cost text |
| `SiegeConstruction` | Siege construction rollover in the troops panel |
| `Repair` | Building repair rollover, independently of placement |

Targets are flags; a registration may combine compatible targets. Each callback
receives one concrete target, an `IsActive` flag, an `eStructs`-domain building type,
an optional `eChimps`-domain unit type, the resolved local player (zero if unknown),
quantity and building symbol-hover state. Quantity reflects the current Vanilla
recruitment amount; other targets use one. No HUD object is exposed.

| Placement | Meaning |
| --- | --- |
| `BeforeMainLine` | Extra lines above the original title/cost line |
| `AfterMainLine` | Extra lines below that line |
| `InlineAfterCosts` | Suffix after the existing cost inlines; multiple contributed lines retain their line breaks |
| `BeforeDescription` | Lines before the expanded building description |
| `AfterDescription` | Lines after the expanded building description |

Description placements require `BuildingDetailed` alone. `Supports` reports valid
target/placement combinations. Invalid masks or placements are rejected, with no
implicit relocation. There is no arbitrary XPath or screen-coordinate API.

## Ownership, ordering and failure

Registrations live until process exit. Return null, empty lines or only whitespace
for logical deactivation. Inactive contexts are still delivered so providers can
retire state, but their returned content cannot render. A provider registered for
multiple targets must treat each context independently: an inactive siege context
must not clear a different active recruitment context.

Providers run synchronously outside registry locks after completed managed
presentation operations on the confirmed Unity thread. They must not mutate HUD
controls, recursively update presentation or retain live UI objects. Registering
from a provider affects the next collection. Providers may reuse immutable lines;
APIShared compares content and reuses unchanged snapshots and visuals.

At each placement contributions sort by ascending `order` (default zero), then
ordinal owner GUID and contribution ID. The owner/ID pair is unique across the
general service and the legacy building-text adapter, even across target masks.
All content is supplementary; arbitrary strings are not summed or deduplicated.

Exceptions or invalid null lines discard the complete current result of that
provider, preserve other providers and report a bounded diagnostic. Future valid
results can recover. Invalid or missing layout anchors disable only their own
presentation. This service does not arbitrate arbitrary third-party replacements
of Vanilla controls.

## Layout and lifetime

APIShared's patches annotate the original named TextBlocks. Names, existing
bindings and Vanilla content survive. A nonempty suffix temporarily appends only
APIShared-owned inlines; clearing it physically removes them. No permanently empty
inline container, background, padding or line break remains.

Additional building rows participate in the existing vertical layout and its
background. Recruitment and siege rows use attached overlays positioned from the
actual original text bounds instead of per-mod fixed offsets. Text and symbols
inherit the target's presentation, retain text direction and wrap within the
available screen width. Backgrounds exist only on nonempty supplementary rows.

The existing building-rollover, recruitment enter/leave, troop-panel enter/leave
and GUI-check publishers refresh content, including while paused. No additional
native hook or consumer frame loop is required. Missions publish a retirement
epoch; UI cleanup is marshalled to Unity's thread. Unloading an anchor retires old
hover ownership and releases the service's references to that HUD. New controls
attach independently.

## Compatibility and migration

`BuildingTooltipPresentation.TryRegister` retains its public signature, context,
types, ordering and position above the main line, including the existing 16-unit
font and 440-unit maximum width for legacy text rows. Its provider runs once per
presentation pass for the selected variant, including zero-building retirement;
unselected target collection does not invoke it a second time. It forwards to the same general
service. Existing callers do not need to rebuild merely to retain their text.
The public repair tooltip model and legacy binding remain available for older
consumers; the current APIShared patch renders repair extras through the common
service.

Consumers migrating from their own tooltip XAML should remove their copied
Vanilla controls and cost/limit hosts. Publish only the missing supplementary
content. In particular, Vanilla already displays its first two building resources;
do not publish those a second time. Keep independent notifications independent.
Require the APIShared release containing this service in both plugin dependency
metadata and the manifest.

## Validation

Core tests cover the production registry's target and placement rules, ordering,
empty results, immutable snapshots, retirement and provider failure isolation.
Production adapter tests additionally cover legacy callback count and zero
allocation after warm-up when active and inactive targets alternate.
Installed-assembly tests validate the HUD selection/repair readers. The standalone
validator resolves every supplied anchor against the selected installed Extender
XAML. These checks do not initialize or render Noesis.

Game acceptance must separately exercise empty hover, multiple providers, all
recruitment panels and quantities, siege construction, repair, pause, RTL, narrow
screens, editor, save/map transitions and HUD recreation. An ordinary successful
build is not evidence of correct in-game layout.
