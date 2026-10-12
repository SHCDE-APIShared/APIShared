#!/usr/bin/env bash
# Linux entry point: use SHCDE's existing Proton installation and prefix.
set -euo pipefail
publisher_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
fail() { printf 'APIShared Publisher: %s\n' "$1" >&2; exit 1; }
[[ -f "$publisher_dir/WorkshopUploader.exe" ]] || fail 'Incomplete publisher package.'
game_root=$(cd -- "$publisher_dir/../../.." && pwd -P)
[[ -f "$game_root/Stronghold Crusader Definitive Edition.exe" ]] || fail 'Keep this launcher in the game BepInEx/tools/APISharedWorkshopPublisher folder.'
# Explicit overrides support non-default Steam libraries and custom Proton builds.
steam_root=${STEAM_COMPAT_CLIENT_INSTALL_PATH:-}
if [[ -z $steam_root ]]; then
    for candidate in "$HOME/.steam/steam" "$HOME/.local/share/Steam"; do
        if [[ -d $candidate ]]; then steam_root=$(cd -- "$candidate" && pwd -P); break; fi
    done
fi
[[ -n $steam_root && -d $steam_root ]] || fail 'Set STEAM_COMPAT_CLIENT_INSTALL_PATH to your Steam client directory.'
library_root=$(cd -- "$game_root/../.." && pwd -P)
prefix=${STEAM_COMPAT_DATA_PATH:-"$library_root/compatdata/3024040"}
[[ -d $prefix/pfx ]] || fail 'Start SHCDE through Steam once, or set STEAM_COMPAT_DATA_PATH to its existing Proton prefix.'
proton=${APISHARED_PROTON:-}
if [[ -z $proton && -f $prefix/config_info ]]; then
    # Proton records its selected distribution in the prefix configuration.
    while IFS= read -r candidate; do
        if [[ -f $candidate/proton ]]; then proton=$candidate/proton; break; fi
    done < "$prefix/config_info"
fi
[[ -n $proton && -f $proton ]] || fail 'Set APISHARED_PROTON to the proton script selected for SHCDE.'
command -v python3 >/dev/null 2>&1 || fail 'Python 3 is required by Proton.'
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam_root" STEAM_COMPAT_DATA_PATH="$prefix"
export SteamAppId=3024040 SteamGameId=3024040
printf '%s\n' 'APIShared Workshop Publisher (Linux/Proton). Steam must be running and signed in.'
cd -- "$publisher_dir"
exec python3 "$proton" run "$publisher_dir/WorkshopUploader.exe" --interactive "$@"
