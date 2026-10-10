# APIShared

Shared services for Stronghold Crusader Definitive Edition mods: mission and lobby
events, cancellable command and HUD interception, recruitment helpers, native market
price queries, building services, selections, optional preset settings, and shared
game-mode permissions with settings presentation, and multiplayer packet helpers.
Mods share one BepInEx plugin instead of installing competing hooks.

**Plugin GUID:** `APIShared_Serp` · **Assembly:** `APIShared.dll` ·
**Runtime:** .NET Framework 4.8.1 · **License:** MIT

## Install

Install [Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender)
and one central APIShared copy under `BepInEx/plugins/APIShared_Serp`.
Download from [releases](https://github.com/SHCDE-APIShared/APIShared/releases).
Until the first release here, downloads remain in the
[previous repository](https://github.com/Serpens66/Stronghold-Crusader-DE-Mods/releases?q=APIShared).
If your mod pack already includes APIShared, use that copy; do not install a duplicate.
Consumer mods must not bundle the APIShared DLL. SerpsModsHost is not required.

## Use

Reference the installed DLL with `Private=false`. Declare the minimum APIShared and
Script Extender versions your mod needs in BepInEx and
[info.json](docs/DEPENDENCIES.md); newer versions are accepted.
Use your own stable plugin GUID:

```csharp
private static readonly APIShared.ModApiClient Api =
    APIShared.ApiShared.ForMod("Example.Author.MyMod");

// Call from Awake on the Unity thread; retain handlers in a persistent runtime.
if (Api.TryGetMissionLifecycle(out var missions, out var diagnostic))
    missions.TryRegisterObserver("main", OnStarted, OnEnded, null, out diagnostic);
```

Managed capabilities can be available before native initialization finishes.
`WhenReady` reports completion, including failure; always check individual `TryGet`
results. Late callbacks run synchronously on the registering thread. Registrations
and hooks persist until process exit because the game destroys startup plugin components.

APIShared 0.6.0 adds shared [command and HUD events](docs/API_CATALOG.md#managed-actions-and-hud-interception)
with command Pre/Accepted/Post and HUD Pre/Post phases, cancellable UI replacements
and recruitment policies,
plus [native market price queries](docs/API_CATALOG.md#native-market-price-queries).
Use these registration brokers when extending their shared sites; consumers keep
their own activation and multiplayer policies.

Mods can define their own activation profiles and feed mission notifications to
`GameplayModeGate` for cached runtime permissions. The same profiles can optionally
mark inactive settings with a subtle amber background; settings remain editable
under the existing host/client rules, and changes stay saved for later games.
Without a selected target the display stays neutral; with several possible modes,
rows are marked only when all of them exclude the setting. See
[game-mode permissions](docs/API_CATALOG.md#game-mode-permissions-and-settings-presentation)
and the [integration guide](docs/THIRD_PARTY_GUIDE.md#optional-game-mode-permissions)
for lifecycle wiring, feature exceptions and XAML bindings. These helpers also work
for third-party mods without SerpsModsHost.

## Multiplayer actions and messages

APIShared 0.7.0 adds `ApiShared.ForMod(yourGuid).Network` for typed multiplayer
channels, available to SerpsMods and third-party mods alike:

- `RegisterChore<T>` sends gameplay actions through the Script Extender's existing
  Chore pipeline and executes the handler synchronously on every peer, including
  the sender. Button and key handlers submit stable IDs and desired states.
- `RegisterMessage<T>` copies control messages and their Steam sender, then runs
  validation and receipt in FIFO order on the Unity thread. Send to the current
  game, one player, the active lobby, or a Steam peer.
- Options cover activation, validation, payload limits, isolated diagnostics and
  message snapshots. `TryPostPresentation` posts copied UI/log work from a Chore;
  `ChoreTransport` also supports existing Extender packet registrations.

Register compatible packet types with explicit MessagePack formatters once, in the
same order on all peers. Keep gameplay in the synchronized callback; never apply a
button effect immediately. The consumer retains ownership/session checks and
deduplication. A successful send is local submission, not a delivery acknowledgement.
There is no automatic retry, payload splitting or mod negotiation.

See the [networking guide](docs/THIRD_PARTY_GUIDE.md#multiplayer-actions-and-messages),
[API catalog](docs/API_CATALOG.md#multiplayer-packets), and
[compiled example](examples/ThirdPartyMod/NetworkingExample.cs).

## Script Extender fixes

Since 0.6.1, APIShared also provides targeted fixes for the Script Extender and its
bundled dependencies in `src/ScriptExtenderFixes`. The first fix protects UU-ImGUI
during Alt+F4 and normal menu shutdown. It activates automatically for the verified
implementation; unknown binaries are skipped with a diagnostic log message.
Consumer mods do not need to register the fix.

The fix is enabled by default. To disable it, set `EnableImGuiShutdownFix = false`
in the `[ScriptExtenderFixes]` section of `BepInEx/config/APIShared_Serp.cfg` and
restart the game. See the [mod author guide](docs/THIRD_PARTY_GUIDE.md#script-extender-bugfixes)
for supported implementations and the policy for official upstream fixes.

## Documentation

- [API catalog](docs/API_CATALOG.md): capabilities, events, contracts and limitations.
- [Mod author guide](docs/THIRD_PARTY_GUIDE.md): event integration, mode policies, settings and packaging.
- [Compilable examples](examples/ThirdPartyMod): missions, settings, custom profiles, HUD, interception and networking.
- [Contributing](CONTRIBUTING.md): development setup, tests and pull requests.
- [Architecture](ARCHITECTURE.md): runtime responsibilities and integration boundaries.
