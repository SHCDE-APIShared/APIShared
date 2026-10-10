# APIShared

Shared services for Stronghold Crusader Definitive Edition mods: mission and lobby
events, cancellable command and HUD interception, recruitment helpers, native market
price queries, building services, selections, and optional preset settings.
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
- [Compilable examples](examples/ThirdPartyMod): missions, settings, custom profiles, HUD and interception.
- [Contributing](CONTRIBUTING.md): development setup, tests and pull requests.
- [Architecture](ARCHITECTURE.md): runtime responsibilities and integration boundaries.
