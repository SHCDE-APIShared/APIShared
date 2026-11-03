# Player control

This guide covers controlling another player's objects. The available capability is
spectator input control: an original spectator plays one chosen player with Vanilla's
own input. Rights-gated control, several owners at once and multiplayer control are
work in progress (see [WIP](#work-in-progress)).

## Usage

```csharp
// Once, in the mod's CrusaderLibrary.LibraryLoaded handler (or later outside a mission),
// and only when the mod's feature is actually enabled. Nothing is installed without it.
if (!PlayerPerspectiveAPI.TryRegisterSpectatorControl(MyPlugin.Guid, out string reason))
    LogUnavailable(reason); // unsupported image, refused in a running mission, or failed install

// Unity main thread, after the spectator has selected the player in the consumer's UI.
if (!PlayerPerspectiveAPI.TrySetSpectatorControl(playerId))
    ShowUnavailable(); // unsupported mode/image or rejected view; details are logged

int controlled = PlayerPerspectiveAPI.GetInputControlledPlayerId(); // 0 when inactive
PlayerPerspectiveAPI.ClearSpectatorControl(); // keeps the selected view
```

Requirements: a ready offline multiplayer-type game (local skirmish) with Vanilla's
spectator flag, a nonpositive `EditorDirector.ActivePlayerID`, no participant view
lease and an occupied human/skirmish slot. Real multiplayer and the map editor return
false; in the editor Vanilla's own player switch already provides this.

While active:

- Map input (selection, unit orders, building clicks and panels) runs as the chosen
  player. Tax, rations and other GameActions use the native view, which is that player.
- `GamePlayerManagerAPI.GetLocalPlayerId` reports the controlled player, so local
  selection services and consumers recognize its selection.
  `GetControlledPlayerId` stays -1: the client is still a spectator.
- The player's AI keeps acting on its own objects.
- Managed building placement and demolition (`EngineInterface.PlaceMapperItem`,
  `DeleteBuilding`) pass `EditorDirector.ActivePlayerID`. The mod owning a hook on
  these methods substitutes `GetInputControlledPlayerId()`; APIShared does not
  install a competing managed hook.

Control ends with `ClearSpectatorControl`, any other `TrySetSpectatorView` or clear,
and every native world change (load, unload, new mission). Switching or ending
deselects the controlled player's units through Vanilla's own deselection while that
player's view is still current. A world change only resets the actor and sends no
command into the next world; it also clears the selected spectator view, as for every
original-spectator view. A failed view write rolls back; if the rollback cannot
be confirmed, spectator input stays blocked until control is cleared.

## Limits

- No rights categories: every action the chosen player could do is available.
- Hash-bound to the audited native image; another image disables only this feature.
- Static evidence plus executable fixtures; no live-game acceptance is claimed. Whether
  Vanilla's deselection releases every tribe reservation is unproven, but it is the
  same path a player uses.

## Implementation

Vanilla zeroes the native view for spectators at two input boundaries: in
`DLL_RunTick` and in the input dispatcher's nested re-dispatch. APIShared's startup only
validates the image (`ControlInputNative.Prepare`). The first `TryRegisterSpectatorControl`
installs one NativeX64 Indirect stub at each place: without thread suspension inside the
LibraryLoaded startup window, otherwise only outside a running mission and with suspension.
Without a registered consumer there is no hook, no callback and no per-tick cost; the
dormant stubs cost a few instructions per input pass and never enter managed code.
With actor zero, or when the spectator flag is clear, both stubs follow the complete
original blocks. For a spectator, a nonzero actor keeps the view for the input scope only
if it equals the current view in 1..8; otherwise the view stays zero. `LocalInputControl` is the only writer of the actor cell, always under
the engine monitor that also serializes `DLL_RunTick`. `PlayerPerspectiveAPI` couples it
to the view transaction: deselect as the old view, confirm the new view, then publish
the actor. Byte contracts and update checks are in [UpdateToNewDLL](../UpdateToNewDLL.md#spectator-input-control-boundaries);
the registry groups are in [native sites](NATIVE_SITES.md).

## Work in progress

The internal building blocks under `src/Players/Control` marked `WIP` are not wired
into the game. TODOs:

Rights-gated control (single player)
- `IControlCommandRules` and an object resolver for every native command opcode, each
  with its own Vanilla audit (payload layout, affected objects, required right).
- A producer at the local queue `0x23990` that binds queued commands to sidecars.
- An owner of the Chore handler table that installs `ControlHandlerBridge`.
- Real release of selection reservations for rejected commands.
- `LocalControlRuntime` must use `LocalInputControl` as the input-actor owner.
- A native queue reset/drain contract before retirement (managed sidecars only today).

Several owners at once (`AdditionalOwners`)
- A mixed selection backend that partitions selections and commands by owner.

Multiplayer
- Host epoch generator and grant protocol over the authenticated Chore channel.
- Fingerprint handshake, fragment transport and the commit barrier at the
  synchronized tick (`ControlProtocolSession`, `ControlBatchAssembler`).
- An in-flight limit across all peers (500 native queue slots).
- Reservation expiry semantics in multiplayer (tick + 400).
- Grants across save/load and roster remapping.
- Desync tests and genuine host/client acceptance.
- Control by eliminated participants in multiplayer.

Spectator input control
- Eliminated participants and the active singleplayer test mode, through the shared
  player-view identity lease before the view switch.
- A pattern fallback for unknown native images at both boundaries.
