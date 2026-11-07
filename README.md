# APIShared

APIShared is a shared requirement for Stronghold Crusader Definitive Edition mods.
It provides common mod settings, compatible interfaces and support for multiplayer,
saved games and Workshop updates. Install it once for all mods that require it.

## Install

Install the [Script Extender](https://gitlab.com/rawra-stronghold-crusader/shcde-script-extender)
first. The recommended APIShared installation is its separate
[Steam Workshop subscription](https://steamcommunity.com/workshop/filedetails/?id=3818609220).
APIShared works independently of the Serps Mods pack.

For manual installation, download a package from the
[official releases](https://github.com/SHCDE-APIShared/APIShared/releases), close the
game and extract the complete package into the game directory. Update its files in
both `BepInEx/plugins` and `BepInEx/patchers` together. Use one central APIShared
installation; do not add extra copies for individual mods.

On Linux/Proton, see [Linux paths and first-install repair](docs/WORKSHOP.md#linux-paths-and-first-install-repair)
for path settings and the separate manual patcher when it is needed.

## Mod settings and saved games

Supported mods share presets and a searchable settings interface. Gameplay options
follow the host's permissions; personal options remain yours.

APIShared adds **Use current mod settings** to the Load Game dialog independently
of ExtendedData. The choice is remembered between games and is initially off.
Saved mission restrictions and fixed creator rules remain in force. See
[loading saved games](docs/MOD_SETTINGS.md#loadsave-requester-integration) for details.

## Help and documentation

- [Workshop installation and updates](docs/WORKSHOP.md).
- [Mod settings](docs/MOD_SETTINGS.md).
- [Release history](info.json).
- [Documentation for mod authors](docs/THIRD_PARTY_GUIDE.md), [API catalog](docs/API_CATALOG.md)
  and [contributing](CONTRIBUTING.md).
- [Shared worker-production rules](docs/WORKER_PRODUCTION_BONUS.md) for mod authors:
  combine output multipliers and fixed production bonuses through one common service.

License: MIT.
