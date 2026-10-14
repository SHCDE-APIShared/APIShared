#!/usr/bin/env bash
# Native Linux control-flow fixture for workshop-updater.sh under set -e. Both
# refusals below were silently skipped while they were written as bare '! cmd'.
set -euo pipefail
for tool in flock sha256sum sync realpath; do command -v "$tool" >/dev/null || { printf 'SKIP: Linux updater fixture requires %s\n' "$tool"; exit 77; }; done
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
updater=${APISHARED_UPDATER_SH:-$root/Workshop/workshop-updater.sh}
fixture=$(mktemp -d "${TMPDIR:-/tmp}/apishared-updater.XXXXXXXX")
hash_file() { sha256sum -- "$1" | cut -d ' ' -f1; }
fail() { printf 'FAIL: %s\nEvidence retained: %s\n' "$1" "$fixture" >&2; exit 1; }
new_game() {
    game="$fixture/$1/game"; transactions="$game/_APIShared/Workshop/Transactions"
    mkdir -p -- "$transactions" "$game/BepInEx/plugins/Example" "$game/_APIShared/Workshop/Receipts"
}
run() { local code=0; bash "$updater" "$@" > "$fixture/last.log" 2>&1 || code=$?; printf '%s' "$code"; }

# 1. A recovery request that pins an already committed transaction is refused
#    before any file changes (the journal pin alone cannot express "unfinished").
new_game finished
printf -v id '%032x' 1; dir="$transactions/$id"; mkdir -p -- "$dir/Backup" "$dir/New"
printf old > "$dir/Backup/0"; printf new > "$dir/New/0"; printf receipt > "$dir/New/1"
printf new > "$game/BepInEx/plugins/Example/Example.dll"; printf receipt > "$game/_APIShared/Workshop/Receipts/123.json"
printf 'APISHARED-WORKSHOP-2\nwrite\tBepInEx/plugins/Example/Example.dll\t0\t%s\t%s\nreceipt-write\t_APIShared/Workshop/Receipts/123.json\t1\tmissing\t%s\n' \
    "$(hash_file "$dir/Backup/0")" "$(hash_file "$dir/New/0")" "$(hash_file "$dir/New/1")" > "$dir/operations.tsv"
printf 'applying\nbegin\t0\ndone\t0\nbegin\t1\ndone\t1\ncommitted\n' > "$dir/journal.log"
printf -v request_id '%032x' 2; request="$transactions/$request_id"; mkdir -p -- "$request"
printf 'APISHARED-WORKSHOP-RECOVERY-1\nstop\n%s\t%s\t%s\n' "$id" "$(hash_file "$dir/operations.tsv")" "$(hash_file "$dir/journal.log")" > "$request/operations.tsv"
code=$(run "$game" "$request" "$(hash_file "$request/operations.tsv")" --restart "")
[[ $code == 1 ]] || fail "request for a committed transaction exited $code"
[[ $(cat -- "$game/BepInEx/plugins/Example/Example.dll") == new && -f $game/_APIShared/Workshop/Receipts/123.json ]] || fail 'committed installation was rolled back'
grep -q 'no longer interrupted' "$request/journal.log" || fail 'refusal was not journaled'
printf 'PASS: Linux recovery refuses a committed original without changing files.\n'

# 2. A game start during application aborts before the next row; the control run
#    without a game commits, so the abort comes from the restart check itself.
stage() {
    new_game "$1"
    printf -v id '%032x' 3; dir="$transactions/$id"; mkdir -p -- "$dir/New"
    head -c 67108864 /dev/zero > "$dir/New/0"; printf small > "$dir/New/1"; printf receipt > "$dir/New/2"
    printf 'APISHARED-WORKSHOP-2\nwrite\tBepInEx/plugins/Example/Large.dll\t0\tmissing\t%s\nwrite\tBepInEx/plugins/Example/Small.dll\t1\tmissing\t%s\nreceipt-write\t_APIShared/Workshop/Receipts/123.json\t2\tmissing\t%s\n' \
        "$(hash_file "$dir/New/0")" "$(hash_file "$dir/New/1")" "$(hash_file "$dir/New/2")" > "$dir/operations.tsv"
}
stage control
code=$(run "$game" "$dir" "$(hash_file "$dir/operations.tsv")" --fixture "")
[[ $code == 0 ]] && grep -qx committed "$dir/journal.log" || fail "control installation exited $code"
stage restarted
bash "$updater" "$game" "$dir" "$(hash_file "$dir/operations.tsv")" --fixture "" > "$fixture/restarted.log" 2>&1 & updater_pid=$!
until grep -q $'^begin\t0$' "$dir/journal.log" 2>/dev/null; do kill -0 "$updater_pid" 2>/dev/null || break; sleep 0.01; done
bash -c 'exec -a "/fixture/Stronghold Crusader Definitive Edition.exe" sleep 120' & game_pid=$!
code=0; wait "$updater_pid" || code=$?
kill "$game_pid" 2>/dev/null || true; wait "$game_pid" 2>/dev/null || true
[[ $code == 1 ]] || fail "installation during a game restart exited $code"
if grep -qx committed "$dir/journal.log"; then fail 'installation committed while the game was running'; fi
grep -q 'Game restarted during application' "$dir/journal.log" || fail 'restart abort was not journaled'
[[ ! -f $game/BepInEx/plugins/Example/Small.dll ]] || fail 'a row after the restart was written'
printf 'PASS: Linux installation stops at the next row after a game start and journals it.\nEvidence retained: %s\n' "$fixture"
