#!/usr/bin/env bash
# Linux entry point: use SHCDE's existing Proton installation and prefix.
set -euo pipefail
publisher_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
text() {
    local key="$1" locale="${LC_ALL:-${LC_MESSAGES:-${LANG:-en_US}}}" line
    locale="${locale%%.*}"; locale="${locale%%@*}"; locale="${locale/_/-}"
    case "$locale" in de|de-*) locale=de-DE ;; en|en-*) locale=en-US ;; es|es-*) locale=es-ES ;; esac
    # These launcher entries are single-line values without escape sequences.
    for file in "$publisher_dir/Locales/$locale.txt" "$publisher_dir/Locales/en-US.txt"; do
        [[ -f "$file" ]] || continue
        while IFS= read -r line || [[ -n "$line" ]]; do
            line="${line%$'\r'}"
            if [[ "$line" == "$key="* ]]; then printf '%s' "${line#*=}"; return; fi
        done < "$file"
    done
    printf '%s' "$key"
}
fail() { printf 'APIShared Publisher: %s\n' "$(text "$1")" >&2; exit 1; }
[[ -f "$publisher_dir/WorkshopUploader.exe" ]] || fail 'Launcher.PackageMissing'
game_root=$(cd -- "$publisher_dir/../../.." && pwd -P)
[[ -f "$game_root/Stronghold Crusader Definitive Edition.exe" ]] || fail 'Launcher.GameFolder'
# Explicit overrides support non-default Steam libraries and custom Proton builds.
steam_root=${STEAM_COMPAT_CLIENT_INSTALL_PATH:-}
if [[ -z $steam_root ]]; then
    for candidate in "$HOME/.steam/steam" "$HOME/.local/share/Steam"; do
        if [[ -d $candidate ]]; then steam_root=$(cd -- "$candidate" && pwd -P); break; fi
    done
fi
[[ -n $steam_root && -d $steam_root ]] || fail 'Launcher.SteamRoot'
library_root=$(cd -- "$game_root/../.." && pwd -P)
prefix=${STEAM_COMPAT_DATA_PATH:-"$library_root/compatdata/3024040"}
[[ -d $prefix/pfx ]] || fail 'Launcher.Prefix'
proton=${APISHARED_PROTON:-}
if [[ -z $proton && -f $prefix/config_info ]]; then
    # Proton records its selected distribution in the prefix configuration.
    while IFS= read -r candidate; do
        if [[ -f $candidate/proton ]]; then proton=$candidate/proton; break; fi
    done < "$prefix/config_info"
fi
[[ -n $proton && -f $proton ]] || fail 'Launcher.Proton'
command -v python3 >/dev/null 2>&1 || fail 'Launcher.Python'
export STEAM_COMPAT_CLIENT_INSTALL_PATH="$steam_root" STEAM_COMPAT_DATA_PATH="$prefix"
export SteamAppId=3024040 SteamGameId=3024040
printf '%s\n' "$(text Launcher.Intro)"
cd -- "$publisher_dir"
exec python3 "$proton" run "$publisher_dir/WorkshopUploader.exe" --interactive "$@"
