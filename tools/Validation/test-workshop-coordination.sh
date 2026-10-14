#!/usr/bin/env bash
# Native Linux fixture: actual flock, /proc and production process detection.
set -euo pipefail
for tool in flock sha256sum sync stat head sed grep tr realpath sleep; do
    command -v "$tool" >/dev/null || { printf 'SKIP: Linux coordination requires %s\n' "$tool"; exit 77; }
done
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/apishared-coordination.XXXXXXXX")
hash_file() { sha256sum -- "$1" | cut -d ' ' -f1; }
start_identity() {
    local line; local -a values
    IFS= read -r line < "/proc/$1/stat" || return 1
    read -r -a values <<< "${line##*) }"; printf '%s' "${values[19]}"
}
owner=''; owner_stamp=''; game=''
stop_owned_game() {
    local pid stamp
    if [[ -n $game && -f $game/fixture.pid ]]; then
        read -r pid stamp < "$game/fixture.pid"
        if [[ $(start_identity "$pid" 2>/dev/null) == "$stamp" ]]; then kill -TERM "$pid" 2>/dev/null || true; fi
    fi
}
finish() {
    stop_owned_game
    if [[ -n $owner && $(start_identity "$owner" 2>/dev/null) == "$owner_stamp" ]]; then kill -TERM "$owner" 2>/dev/null || true; wait "$owner" 2>/dev/null || true; fi
}
trap finish EXIT
for case_name in backup apply receipt restore; do
    printf 'Checking native Linux coordination: %s\n' "$case_name"
    game="$fixture/$case_name/game"; scripts="$fixture/$case_name/scripts"; state="$game/_APIShared/Workshop"
    id=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa; old="$state/Transactions/$id"; transaction=$old
    mkdir -p -- "$old/New" "$game/BepInEx/plugins/Example" "$scripts"
    target="$game/BepInEx/plugins/Example/Example.dll"; receipt="$state/Receipts/123.json"
    printf original > "$target"; printf updated > "$old/New/0"; printf receipt > "$old/New/1"
    printf 'APISHARED-WORKSHOP-2\nwrite\tBepInEx/plugins/Example/Example.dll\t0\t%s\t%s\nreceipt-write\t_APIShared/Workshop/Receipts/123.json\t1\tmissing\t%s\n' "$(hash_file "$target")" "$(hash_file "$old/New/0")" "$(hash_file "$old/New/1")" > "$old/operations.tsv"
    phase=$case_name; ordinal=0
    if [[ $case_name == receipt ]]; then phase=apply; ordinal=1; fi
    if [[ $case_name == restore ]]; then
        mkdir -p -- "$old/Backup"; cp -- "$target" "$old/Backup/0"; printf updated > "$target"
        printf 'applying\nbegin\t0\ndone\t0\n' > "$old/journal.log"
        transaction="$state/Transactions/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; mkdir -p -- "$transaction"
        printf 'APISHARED-WORKSHOP-RECOVERY-1\nstop\n%s\t%s\t%s\n' "$id" "$(hash_file "$old/operations.tsv")" "$(hash_file "$old/journal.log")" > "$transaction/operations.tsv"
    fi
    cp -- "$root/Workshop/workshop-updater.sh" "$root/Workshop/workshop-maintenance.sh" "$scripts/"
    cat >> "$scripts/workshop-maintenance.sh" <<'HOOK'

eval "$(declare -f wait_game_exit | sed '1s/wait_game_exit/original_wait_game_exit/')"
wait_game_exit() {
    if [[ ${fixture_injected:-0} == 0 && $activity_phase == "$FIXTURE_PHASE" && ( $activity_phase == restore || ${index:-0} == "$FIXTURE_ORDINAL" ) ]]; then
        fixture_injected=1
        bash -c 'exec -a "$1" sleep 60' fixture "$game/Stronghold Crusader Definitive Edition.exe" &
        fixture_pid=$!
        printf '%s %s\n' "$fixture_pid" "$(process_start "$fixture_pid")" > "$game/fixture.pid"
    fi
    original_wait_game_exit
}
HOOK
    FIXTURE_PHASE=$phase FIXTURE_ORDINAL=$ordinal bash "$scripts/workshop-updater.sh" "$game" "$transaction" "$(hash_file "$transaction/operations.tsv")" --fixture > "$fixture/$case_name/output.log" 2>&1 &
    owner=$!; owner_stamp=$(start_identity "$owner"); deadline=$((SECONDS+60))
    while ! grep -q $'\tpaused\t' "$state/apply.lock" 2>/dev/null; do
        kill -0 "$owner" 2>/dev/null || { cat "$fixture/$case_name/output.log"; exit 1; }
        (( SECONDS < deadline )) || { printf 'Pause checkpoint timeout\n'; exit 1; }
        sleep 0.05
    done
    status=$(bash "$scripts/workshop-updater.sh" "$game" --status)
    [[ $status == APISHARED-WORKSHOP-ACTIVE-1$'\t'linux$'\t'"$owner"$'\t'* ]] || { printf 'Live owner was not authenticated: %s\n' "$status"; exit 1; }
    if flock -n "$state/apply.lock" true; then printf 'Concurrent writer lock was allowed\n'; exit 1; fi
    expected=original; if [[ $case_name == receipt || $case_name == restore ]]; then expected=updated; fi
    sleep 0.1
    [[ $(cat -- "$target") == "$expected" && ! -f $receipt ]]
    stop_owned_game
    wait "$owner" || { cat "$fixture/$case_name/output.log"; exit 1; }; owner=''
    if [[ $case_name == restore ]]; then [[ $(cat -- "$target") == original && ! -f $receipt ]]; else [[ $(cat -- "$target") == updated && $(cat -- "$receipt") == receipt ]]; fi
    [[ $(bash "$scripts/workshop-updater.sh" "$game" --status) == idle ]]
done
printf 'PASS: native Linux backup/apply/receipt/restore pause, authenticated status, exclusive lock and resume. Evidence: %s\n' "$fixture"
