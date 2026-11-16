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
