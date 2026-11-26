#!/usr/bin/env bash
set -euo pipefail

mode=$1
run=$2
game=$3
windows_game=$4
batch=$5
[[ $batch =~ ^[a-f0-9]{32}$ && $game == /* && -f $game ]] || exit 1
[[ $run == "${game%/*}/_APIShared/I/restart/$batch" && -d $run ]] || exit 1

unlinked() {
    local part=$1
    while [[ -n $part ]]; do
        [[ ! -L $part ]] || return 1
        part=${part%/*}
    done
}
unlinked "$run"
unlinked "$game"

if [[ $mode == --handoff ]]; then
    # The Steam reaper waits for its descendants even after the Wine game exits.
    # A user service leaves that process tree before taking ownership of the wait.
    if ! command -v systemd-run >/dev/null; then
        printf '%s\n' 'Automatic Proton restart requires an available Linux user service manager.' >&2
        exit 1
    fi
    steam_root=${6:-${STEAM_COMPAT_CLIENT_INSTALL_PATH:-}}
    if [[ $steam_root != /* ]] || ! steam_log=$(realpath -e -- "$steam_root/logs/gameprocess_log.txt") ||
        [[ ! -f $steam_log || ! -r $steam_log ]]; then
        printf '%s\n' 'Cannot read the active Steam client game process log.' >&2
        exit 1
    fi
    script=$(realpath -e -- "${BASH_SOURCE[0]}")
    service_args=()
    for value in "$script" --wait "$run" "$game" "$windows_game" "$batch" "$steam_log"; do
        # A transient unit interprets percent specifiers and dollar escapes.
        value=${value//%/%%}
        service_args+=("${value//\$/\$\$}")
    done
    exec systemd-run --user --quiet --collect --no-ask-password --unit="apishared-restart-$batch" \
        --property=Type=exec --property=RuntimeMaxSec=330s /bin/bash "${service_args[@]}"
fi
[[ $mode == --wait ]] || exit 1
steam_log=$6

set_status() {
    local name=$1 value=$2 path="$run/$1" temp="$run/$1.tmp"
    unlinked "$path" && unlinked "$temp" || return 1
    [[ ! -e $path ]] || return 1
    (set -o noclobber; printf '%s\n' "$value" > "$temp")
    mv -T -- "$temp" "$path"
}

has_status() {
    local path="$run/$1"
    unlinked "$path" && [[ -f $path && $(<"$path") == "$batch" ]]
}

complete_restart() {
    set_status result "$1 [$(date '+%Y-%m-%d %H:%M:%S.%3N')]"
}

fail_restart() {
    complete_restart "failed: $1 Open the game manually in Steam."
    exit 1
}

start_time() {
    local entry
    local -a fields
    IFS= read -r entry 2>/dev/null < "/proc/$1/stat" || return 1
    read -r -a fields <<< "${entry##*) }"
    [[ ${fields[0]:-} != Z && ${fields[19]:-} =~ ^[0-9]+$ ]] || return 1
    printf '%s\n' "${fields[19]}"
}

windows_game=${windows_game//\\//}
windows_game=${windows_game,,}
host_game=${game,,}
exe_name=${host_game##*/}

game_processes() {
    local cmdline argument position pid cwd stamp
    for cmdline in /proc/[0-9]*/cmdline; do
        [[ -r $cmdline ]] || continue
        pid=${cmdline#/proc/}; pid=${pid%/cmdline}
        position=0
        # Wine puts the program at argv[0] or argv[1]. Steam wrappers mention it
        # later in their argv and are never counted as game processes.
        while (( position < 2 )) && IFS= read -r -d '' argument; do
            argument=${argument//\\//}; argument=${argument,,}
            if [[ $argument == "$exe_name" || $argument == "./$exe_name" ]]; then
                cwd=$(readlink -f -- "/proc/$pid/cwd" 2>/dev/null) || break
                argument=${cwd,,}/$exe_name
            fi
            if [[ $argument == "$windows_game" || $argument == "/??/$windows_game" || $argument == "$host_game" ]]; then
                if stamp=$(start_time "$pid"); then printf '%s %s\n' "$pid" "$stamp"; fi
                break
            fi
            position=$((position + 1))
        done 2>/dev/null < "$cmdline" || continue
    done
    return 0
}

game_running() {
    has_status claimed || [[ -n $(game_processes) ]]
}

# The app remains busy while Steam tracks any of its Proton wrapper processes.
# Read the Steam client's append-only log from a single open file descriptor.
# Rotation or truncation makes the observed session ambiguous, so do not launch.
steam_phase=unknown
steam_bytes=0
steam_partial=
steam_log_identity() {
    stat -Lc '%d:%i' -- "$1"
}

steam_record() {
    [[ $1 == \[*'] '* ]] || return 0
    local event=${1#*] }
    event=${event%$'\r'}
    if [[ $event =~ ^AppID\ 3024040\ adding\ PID\ [0-9]+\ as\ a\ tracked\ process($|[[:space:]]) ]]; then
        case $steam_phase in
            watching-release) steam_phase=restarted ;;
            unknown|active|released) steam_phase=active ;;
        esac
    elif [[ $event == 'Remove 3024040 from running list' ]]; then
        case $steam_phase in
            active) steam_phase=released ;;
            watching) steam_phase=watching-release ;;
        esac
    elif [[ $event == 'Client version: '* ]]; then
        case $steam_phase in
            watching|watching-release) steam_phase=invalid ;;
            unknown|active|released) steam_phase=unknown ;;
        esac
    fi
}

scan_steam_log() {
    local LC_ALL=C line identity bytes
    identity=$(steam_log_identity "$steam_log") || return 1
    bytes=$(stat -Lc '%s' -- "$steam_log") || return 1
    [[ $identity == "$steam_identity" && $bytes -ge $steam_bytes ]] || return 1
    while IFS= read -r line <&"$steam_fd"; do
        steam_bytes=$((steam_bytes + ${#line} + 1))
        steam_record "$steam_partial$line"
        steam_partial=
    done
    # A write can be visible before its newline. Keep the fragment until the
    # next read instead of mistaking it for a complete Steam release message.
    steam_bytes=$((steam_bytes + ${#line}))
    steam_partial+=$line
    identity=$(steam_log_identity "$steam_log") || return 1
    [[ $identity == "$steam_identity" && $steam_phase != invalid ]]
}

mapfile -t matches < <(game_processes)
[[ ${#matches[@]} == 1 ]] || fail_restart 'Could not identify exactly one calling Proton game process.'
if ! command -v timeout >/dev/null || { ! command -v xdg-open >/dev/null && ! command -v steam >/dev/null; }; then
    fail_restart 'No usable Steam launch command is available.'
fi
read -r pid stamp <<< "${matches[0]}"
original_running() { [[ $(start_time "$pid") == "$stamp" ]]; }
if [[ $steam_log != /* || ! -f $steam_log ]]; then
    fail_restart 'The Steam client log is unavailable.'
fi
{ exec {steam_fd}< "$steam_log"; } || fail_restart 'The Steam client log could not be opened.'
steam_identity=$(steam_log_identity "/proc/$$/fd/$steam_fd") || fail_restart 'The Steam client log could not be identified.'
if ! scan_steam_log || [[ $steam_phase != active ]]; then
    fail_restart 'The running game could not be confirmed in the Steam client log.'
fi
steam_phase=watching

set_status ready "$batch"
deadline=$((SECONDS + 30))
while ! has_status armed; do
    if (( SECONDS >= deadline )) || ! original_running; then
        if has_status armed; then break; fi
        complete_restart 'cancelled before shutdown was authorized'
        exit 0
    fi
    sleep 0.1
done

deadline=$((SECONDS + 120))
while original_running; do
    (( SECONDS < deadline )) || fail_restart 'Timed out waiting for the game to close.'
    sleep 0.5
done

deadline=$((SECONDS + 120))
while :; do
    if game_running; then complete_restart 'game already started'; exit 0; fi
    scan_steam_log || fail_restart 'The Steam client log changed before it released the game.'
    if [[ $steam_phase == restarted ]]; then complete_restart 'game already started'; exit 0; fi
    if [[ $steam_phase == watching-release ]]; then break; fi
    (( SECONDS < deadline )) || fail_restart 'Steam did not release the game.'
    sleep 0.5
done

if game_running; then complete_restart 'game already started'; exit 0; fi
if command -v xdg-open >/dev/null; then
    timeout 5s xdg-open 'steam://run/3024040' || fail_restart 'The Steam launch command failed.'
else
    timeout 5s steam -applaunch 3024040 || fail_restart 'The Steam launch command failed.'
fi
deadline=$((SECONDS + 30))
while (( SECONDS < deadline )); do
    if game_running; then complete_restart 'game started through Steam'; exit 0; fi
    sleep 0.5
done
fail_restart 'Steam did not start the game.'
