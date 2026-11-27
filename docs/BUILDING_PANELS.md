# Building panel sections

`APIShared.Presentation.BuildingPanelSections` lets several mods add their own controls to
Vanilla's building panels (the HUD shown after clicking a building) without patching
`HUD_Buildings.xaml` themselves and without detecting the open building on their own. APIShared
owns the single XAML insertion point and the detection; each mod owns its control, its texts and
the gameplay commands behind it. It does not authorize or synchronize anything.

Contracts are documented at the declarations in
[BuildingPanelContracts.cs](../src/Presentation/BuildingPanel/BuildingPanelContracts.cs) and
[BuildingPanelSections.cs](../src/Presentation/BuildingPanel/BuildingPanelSections.cs).

## Usage

Register once after Script Extender readiness on the initialized Unity thread, for example in
`LibraryLoaded`, with your plugin GUID, an owner-local ID, the building types and a factory:

    var options = new BuildingPanelSectionOptions(MyGuid, "meat-slider",
        new[] { eStructs.STRUCT_TANNERS_WORKSHOP }, CreateControl)
    {
        ShowWhen = context => context.IsOwnedByLocalPlayer && featureEnabled,
        Opened = args => ShowValuesOf(args.Context.BuildingId, args.Context.GlobalId),
        Updated = args => FollowChanges(args.Context),
        Closed = args => StopWaitingForRequests()
    };
    if (!BuildingPanelSections.TryRegister(options, out var section, out string reason)) Log(reason);

- **Factory.** Runs once per building HUD instance when APIShared first sees it, before any
  section is shown, and returns a fresh unattached `FrameworkElement` (built in code or with
  `Noesis.GUI.ParseXaml`). `context.Host` resolves Vanilla resources, for example
  `context.Host.TryFindResource("UISprites I039") as ImageSource`; static resource references in
  parsed XAML are not resolved for you. APIShared adds the control to its host collapsed. A null
  result or an exception disables the section for that HUD instance and is logged once.
- **Layout.** The host is an 800×170 Grid with the alignment of Vanilla's building panels, so a
  control positions itself with `Margin` and alignment as if it were part of the panel. APIShared
  arranges nothing; keep clear of Vanilla's controls and of other mods' known sections.
- **Showing.** The section is visible while a panel of one of its building types is open and
  `ShowWhen` (evaluated on every GUI check, may be null) returns true. `Opened` starts a showing,
  `Updated` follows on each later GUI check while it lasts, also while paused, and `Closed` ends it
  with the same immutable arguments. Another building, owner or viewpoint, or a new HUD instance,
  ends the showing and starts a new one. Use the callbacks instead of polling.
- **Context.** `BuildingPanelContext` carries the one-based building ID, GlobalId, type, owner,
  the locally controlled and the viewed player, local ownership and the HUD generation. It is
  presentation data: send commands with the ID and GlobalId and validate ownership again where the
  command executes, for example in a Chore (see [Building state](BUILDING_STATE.md)).
- `TryGetContext` reads the open building of any type on Unity, without a registration.

## Presenters and building actions

Requires APIShared 0.73.0 or later. Some mods show Vanilla's building panel for another building
than the local player's own selection, for example another player's building in a spectator tool
or a foreign-control mod. Such a mod registers a presenter, and every section then works on that
building as well, without knowing the presenting mod:

    BuildingPanelSections.TryRegisterPresenter(new BuildingPanelPresenterOptions(MyGuid, "panel",
        (out int building, out int acting) => TryGetShownBuilding(out building, out acting),
        (BuildingActionRequest request, out string reason) => QueueAuthorizedExecution(request, out reason)), out string reason);

- **Query.** Runs on Unity at every GUI check while sections exist and reports the shown building
  and the player the presenter acts for (normally its owner). Report nothing while your panel is
  closed. The first presenter in order (`Order`, then ordinal owner GUID and ID) that reports a
  building owns the panel; its building replaces Vanilla's selection in every section's context.
- **Context.** A presented context has `IsPresented`, `PresenterOwnerGuid`/`PresenterRegistrationId`
  and `ActingPlayerId`; `CanActAsOwner` says whether that player owns the building.
  `IsOwnedByLocalPlayer` keeps meaning the locally controlled player. For Vanilla's own selection
  `ActingPlayerId` is the local player.
- **Actions.** A section that offers owner controls registers each command once as a
  `BuildingActions` action (owner GUID, action ID, building types, executor). On a presented panel
  its control calls `BuildingPanelSections.TrySubmitAction(context, guid, actionId, argument, ...)`;
  APIShared hands the request to the presenter, which runs it through its own authorized path (an
  authenticated Chore on every peer, or a single-player engine action) ending in
  `BuildingActions.TryExecute`. That call validates the building (alive, same GlobalId, one of the
  action's types, owned by the acting player) and runs the executor. Any other mod may also call
  `TryExecute` itself from deterministic simulation code. For Vanilla's own selection a section
  keeps its own path, for example its own Chore.
- **Compact transports.** A presenter whose Chore carries only numbers sends
  `BuildingActions.GetActionKey(owner, actionId)` (FNV-1a, the same in every process) and resolves
  it on each peer with `TryGetActionByKey`, which refuses unknown and ambiguous keys.

## Recruitment-area replacements

A mod whose building variant is built on a native type, for example a custom garrison on the stone
barracks, registers a replacement of Vanilla's recruitment area for exactly that variant:

    var options = new BuildingPanelRecruitmentAreaOptions(MyGuid, "imperial-garrison",
        new[] { eStructs.STRUCT_BARRACKS_STONE }, CreateRecruitmentControl)
    {
        Selects = context => garrisons.Contains(context.GlobalId),
        Opened = args => ShowRecruitsFor(args.Context.BuildingId, args.Context.GlobalId),
        Closed = args => StopWaitingForRequests()
    };
    if (!BuildingPanelSections.TryRegisterRecruitmentArea(options, out var area, out string reason)) Log(reason);

- **Showing.** The factory works like a section factory; its control sits in the same host and
  positions itself where Vanilla's recruitment area is. While it is shown, Vanilla's recruitment
  panels (barracks, mercenary post, Bedouin stockade, engineers' and tunnellers' guilds, cathedral)
  and the full-panel click mask are collapsed, and APIShared features attached to Vanilla's barracks
  panel hide with it. Every other building, including other stone barracks the selector rejects,
  keeps Vanilla's area. Never shown in the map editor. `Selects` must be cheap and free of side
  effects; it runs on every GUI check for a building of the variant's types.
- **Coexistence.** Any number of owners can register variants on one native type. Registration
  refuses only invalid options or a variant ID the owner already registered. Two replacements compete
  only when both select the same open building; then one is shown by a fixed precedence that never
  depends on registration order: a replacement with a selector before one for every building of its
  types, then ordinal owner GUID, then ordinal variant ID. A replacement whose factory or callback fails
  lets the next one, or Vanilla, show the area.
- **Settings.** `SetEnabled` on the registration is logical activation for the owner's settings; a
  disabled replacement matches nothing and hands a shown area back at the next GUI check.

### Explaining the effective owner in settings

APIShared shows no popup. Each competing pair is logged once, and
`BuildingPanelSections.TryGetRecruitmentAreaStatus` (or `area.TryGetStatus`) returns an immutable
explanation for the owner's own settings presentation, as described in issue #47: the variant's
selector and enabled state, whether it is shown in the open panel now, and every rival variant on a
shared native type in precedence order, with `Precedes` (the rival is shown instead where both
select a building) and `ObservedOverlap` (this process has seen both select one building). Which
variant applies depends on the building, so there is no single effective value: show the rule and
the rivals, for example in the tooltip of the setting that enables the variant ("On garrisons that
also match Fort mod's selector, Fort mod's recruitment area is shown"). Reading the status evaluates
no selector and changes nothing. It is per process and not synchronized; peers decide from their own
registrations.

## Limits

- Unity thread only. Callbacks, conditions and factories are isolated: a failure is logged once
  and hides that section until the panel shows another context; other sections are unaffected.
- The open building is read without the engine monitor, like Vanilla's own HUD code; a building
  created or destroyed in that frame can be seen one frame late.
- A panel counts as a building panel when the HUD shows a building, its sub mode is one of
  Vanilla's building panels (`InBuildingModes` 1 to 63) and `in_structure` is a living building
  with a nonzero GlobalId. Unit panels, Peace Time and report pages show no sections.
- After a mission ends, `Closed` arrives with the next in-game GUI check.
- A mod that replaces `HUD_Buildings.xaml` completely removes the host; APIShared then logs once
  and shows no sections, leaving the panels unchanged.

## Implementation

`APIShared/Patches/Assets/GUI/XAMLResources/HUD_Buildings.xaml` inserts
`APISharedBuildingPanelSectionsHost` directly after `FletchersPanel` as one root element. The
service registers its Post observer on `PresentationOperation.GuiChecks` at the first registration
and is active only while sections exist. Detection follows Vanilla's
`MainViewModel.setUpInbuilding`, which selects the building panel from `app_sub_mode` and the
building from `in_structure`; it reuses one context object while nothing changes, so a steady
panel allocates nothing per frame.

Recruitment-area replacements share that observer and host. `MainViewModel.setUpInbuilding` is the
only game code that writes the visibility of the recruitment panels and the click mask, and it
writes only Hidden and Visible (Cecil scan of the audited Assembly-CSharp). A shown replacement
therefore hides them with Collapsed, records what Vanilla had, re-records and re-hides any later
Vanilla write while it stays shown, and on deactivation restores only panels that still carry its
Collapsed, so a newer Vanilla write always wins. The members are validated at the first replacement
registration; sections are unaffected if they are missing. A steady replaced panel, including a
known competing pair, allocates nothing per GUI check; the first observation of a pair allocates
its record once. These rules are covered by `BuildingPanelRecruitmentAreaTests` with a fake view;
the live barracks panel, Noesis rendering and multiplayer presentation are not yet tested in game.
