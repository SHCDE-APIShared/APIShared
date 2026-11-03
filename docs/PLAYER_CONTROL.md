# Player views and control

This guide is the single reference for whose view a client shows and whose objects its
input controls. All entry points are on `PlayerPerspectiveAPI` unless named otherwise.

**Contents**

- [Terms](#terms)
- [Original-spectator views](#original-spectator-views)
- [Eliminated-participant views](#eliminated-participant-views)
- [Observer command exceptions](#observer-command-exceptions)
- [Spectator input control](#spectator-input-control)
- [Limits](#limits)
- [Implementation](#implementation)
- [Not part of this build](#not-part-of-this-build)

## Terms

- **Original spectator:** a client that joined as spectator: nonpositive
  `EditorDirector.ActivePlayerID` and Vanilla's local spectator flag.
- **Eliminated participant:** a positive player whose loss is confirmed by the current
  player-defeat snapshot (official loss or observed lord death).
- **Observer:** in `ObserverCommandExceptions` only, an eliminated participant that
  shows another player's view.
- **Native view:** Vanilla's viewpoint cell. Reports read it, and Vanilla's local command
  queue takes its network source from it.

## Original-spectator views

`TrySetSpectatorView(playerId)` changes Vanilla's view for an original spectator and
publishes it; `GetViewedPlayerId()` reads it and `ClearSpectatorView()` ends it.
`GetControlledPlayerId()` stays -1 for the spectator. Committed changes arrive through
`SubscribeChanges` (no replay; dispatch presentation work to Unity yourself). The call
returns false while another role owns the participant view or a Save/send scope is
active. A selection lasts for the current native world: every load, unload or new
mission clears it with a `Cleared` notification, so consumers select again per mission.

## Eliminated-participant views

Requires APIShared 0.24.0 or later.

Use `TrySetEliminatedPlayerView(playerId)` on Unity's main thread after enabling
Vanilla's local spectator flag for an eliminated participant, in a ready mission with
multiplayer-type native state (also used by local skirmishes). Keep a player-defeat
observer registered so the demand-driven defeat service observes the state; an absent
lord alone never grants access. Host permission and mod-specific activation belong to
the consumer.

The participant keeps its positive controlled and local player ID; `GetViewedPlayerId()`
reports the selected occupied player. Human registration, team, command ownership and
multiplayer permissions do not change. An `Uncertain` view must close or disable stale
reports; it never grants gameplay permission. `ClearEliminatedPlayerView()` restores the
own native view before releasing the identity protection (a failure keeps the
protection) and does not affect an original spectator's input control.

Foreign views need the identity protection described under [Implementation](#implementation);
the own view stays available without it. Saves serialize the original participant
identity, so Vanilla's load and remapping resolve the participant again. After loading,
re-enable an eliminated participant from the current loss snapshot, starting with their
own view; no spectator Save marker is needed.

## Observer command exceptions

Requires APIShared 0.25.0 or later.

Vanilla's local command queue takes its network source from the native view. A mod that
shows an eliminated participant another player's view must therefore block that
participant's local commands that act as the viewed player; APIShared's view guards do
not do this. An enforcing mod blocks only local input (Unity's main thread), never
synchronized simulation or Chore calls, which must run identically on every peer.

Other mods allow single commands with
`ObserverCommandExceptions.TryRegister(ownerGuid, registrationId, allow, out reason, order)`.
The callback receives an `ObserverCommandRequest` (observer's own player ID, viewed player
ID, `GameActionCommand` and its three payloads) and returns true to allow. The enforcing
mod calls `ObserverCommandExceptions.IsAllowed(request)` before each veto, synchronously
on its caller thread. Callbacks run in order, owner GUID and ID order; the first true wins
and a throwing callback counts as no opinion (`CallbackFailures`). An allowed command acts
as the viewed player in multiplayer: allow only what your mod intends to grant,
deterministically from shared state where it matters. Registrations live for the
process; without registrations the check returns false without allocation.

## Spectator input control

Requires APIShared 0.24.0 or later.

An original spectator plays one chosen player with Vanilla's own input.

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

Control ends with `ClearSpectatorControl`, `TrySetSpectatorView`, `ClearSpectatorView`
and every native world change (load, unload, new mission); `ClearEliminatedPlayerView`
does not affect it. Switching or ending deselects the controlled player's units through
Vanilla's own deselection while that player's view is still current. A world change only
resets the actor and sends no command into the next world. A failed view write rolls
back; if the rollback cannot be confirmed, spectator input stays blocked until control is
cleared and `GetLocalPlayerId` reports the spectator identity again.

## Limits

- No rights categories: every action the chosen player could do is available.
- One controlled player at a time, offline only.
- Foreign eliminated-participant views and spectator input control are bound to the
  audited native image; another image disables only these features.
- Static evidence plus executable fixtures; no live-game acceptance is claimed. Whether
  Vanilla's deselection releases every tribe reservation is unproven, but it is the
  same path a player uses.

## Implementation

**Identity protection.** `PlayerViewIdentityRuntime` (src/Players/ViewIdentity) owns one
process-wide `PlayerViewIdentity` core. The common Save writer (`PlayerViewSaveScope`), the
raw Chore submission and the local-ID correction use that same core. While a foreign
participant view is active, a save or raw Chore submission temporarily runs with the
original identity under the engine monitor, including nested saves and synchronous packet
callbacks; the published view does not change during those scopes, and view changes are
rejected while one is active. Save and raw submission take the same engine monitor even
before a lease exists, so concurrent or reentrant acquisition cannot bypass the protection.
A rejected protected raw send throws instead of allowing an unsynchronized Steam fallback.
Protection failures are reported on Unity and make the dependent view `Uncertain`.

Leases are immutable and bind owner, native-world epoch, original participant and token.
A role restores or releases only its own exact lease; a live lease of another role rejects
acquisition. Ordinary release refuses an ended world; the lifecycle's world invalidation
retires it without native writes. Failure delivery is bound to the originating lease, so a
delayed observation cannot invalidate a newer owner's view. Hooks stay installed for the
process; the dormant paths add no allocations.

**Spectator input boundaries.** Vanilla zeroes the native view for spectators at two input
boundaries: in `DLL_RunTick` and in the input dispatcher's nested re-dispatch. Startup only
validates the image (`ControlInputNative.Prepare`). The first `TryRegisterSpectatorControl`
installs one NativeX64 Indirect stub at each place: without thread suspension inside the
LibraryLoaded startup window, otherwise only outside a running mission and with suspension.
Without a registered consumer there is no hook, no callback and no per-tick cost; the
dormant stubs cost a few instructions per input pass and never enter managed code. With
actor zero, or when the spectator flag is clear, both stubs follow the complete original
blocks. For a spectator, a nonzero actor keeps the view for the input scope only if it
equals the current view in 1..8; otherwise the view stays zero.

`LocalInputControl` is the only writer of the actor cell, always under the engine monitor
that also serializes `DLL_RunTick`. `PlayerPerspectiveAPI` couples it to the view
transaction: deselect as the old view, confirm the new view, then publish the actor. Byte
contracts and update checks are in [UpdateToNewDLL](../UpdateToNewDLL.md#spectator-input-control-boundaries);
the registry groups are in [native sites](NATIVE_SITES.md).

## Not part of this build

Rights-gated control of individual command types, several owners at once, multiplayer
control and spectator input control for eliminated participants or the active
singleplayer test mode are not available. Their unfinished backend is kept on the
APIShared branch `wip/player-control`; it is not compiled into this build.
