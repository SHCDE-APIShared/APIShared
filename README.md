# APIShared

Shared services for Stronghold Crusader Definitive Edition mods: events, command
and HUD interception, unit/building helpers, settings, networking and situational
audio. Mods share one BepInEx plugin instead of installing competing hooks.

**Plugin GUID:** `APIShared_Serp` · **Assembly:** `APIShared.dll` ·
**Runtime:** .NET Framework 4.8.1 · **License:** MIT

## Install

Install [Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender)
and one central APIShared copy under `BepInEx/plugins/APIShared_Serp`.
The recommended installation is through the
[Steam Workshop](https://steamcommunity.com/workshop/filedetails/?id=3818609220).
Downloads are also available from [releases](https://github.com/SHCDE-APIShared/APIShared/releases).
If your mod pack already includes APIShared, use that copy. SerpsModsHost is not required.

## Use

Reference APIShared with `Private=false` and declare the minimum APIShared and
Script Extender versions your mod needs. Consumer mods must not bundle the DLL.
Documentation describes this source checkout. Confirm that the installed release
contains each API you use; source-checkout features can precede a published package.
See the [mod author guide](docs/THIRD_PARTY_GUIDE.md) for registration, lifetime,
settings and packaging contracts.

## Documentation

- [API catalog](docs/API_CATALOG.md): available services, events and limitations.
- [Mod author guide](docs/THIRD_PARTY_GUIDE.md): integration and usage recipes, including sound policies.
- [Compilable examples](examples/ThirdPartyMod): practical consumer implementations.
- [Architecture](ARCHITECTURE.md): runtime responsibilities and shared hook ownership.
- [Contributing](CONTRIBUTING.md): development setup and tests.
- [Changelog](info.json): release history in `SerpChangelog`.
