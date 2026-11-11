#!/usr/bin/env bash
# Independent post-exit installer for the Script Extender's prepared map packages.
# LF intentionally. The parent passes only this request and its SHA-256, never a command.
set -euo pipefail
[[ $(uname -s) == Linux ]] || { printf 'Linux host required.\n' >&2; exit 1; }
[[ -z ${SCDEModManagerLaunchId:-} ]] || { printf 'Externally managed installation.\n' >&2; exit 1; }
for tool in flock sha256sum realpath cp mv mkdir rm sleep date sync mktemp cut dirname find; do
    command -v "$tool" >/dev/null || { printf 'Required host tool missing: %s\n' "$tool" >&2; exit 1; }
done
[[ -r /proc/self/cmdline && -r /proc/self/stat ]] || { printf 'Readable Linux process information is required.\n' >&2; exit 1; }
[[ $# == 2 || ( $# == 3 && $3 == --probe ) ]] || { printf 'Expected request, SHA-256, and optional --probe.\n' >&2; exit 1; }
request=$1; expected=$2; probe=${3:-}
[[ $request == /* && $expected =~ ^[a-f0-9]{64}$ ]] || exit 1
unlinked() {
    local part=$1
    while [[ $part != / && -n $part ]]; do
        [[ ! -L $part ]] || { printf 'Linked path refused: %s\n' "$part" >&2; return 1; }
        part=${part%/*}; [[ -n $part ]] || part=/
    done
}
hash() { sha256sum -- "$1" | cut -d ' ' -f1; }
unlinked "$request"
[[ -f $request && $(hash "$request") == "$expected" ]] || { printf 'Request identity mismatch.\n' >&2; exit 1; }
mapfile -t rows < "$request"
[[ ${#rows[@]} -ge 4 && ${rows[0]%$'\r'} == APISHARED-SE-PROTON-1 ]] || exit 1
metadata() {
    local row=${1%$'\r'} tag=$2 value extra
    IFS=$'\t' read -r key value extra <<< "$row"
    [[ $key == "$tag" && -n $value && -z ${extra:-} && ! $value =~ [[:cntrl:]] ]] || return 1
    printf '%s' "$value"
}
game=$(metadata "${rows[1]}" game)
staging=$(metadata "${rows[2]}" staging)
executable=$(metadata "${rows[3]}" exe)
[[ $game == /* && $staging == /* && $executable == *.exe && $executable != */* && $executable != *\\* ]] || exit 1
unlinked "$game"; unlinked "$staging"
game=$(realpath -e -- "$game"); staging=$(realpath -e -- "$staging")
[[ -d $game && -d $staging && $staging == "$game/_SE/.staging" ]] || { printf 'Invalid game or staging directory.\n' >&2; exit 1; }
run=$(dirname -- "$request")
unlinked "$run"
run=$(realpath -e -- "$run")
[[ $run == "$game/BepInEx/cache/APIShared.ProtonWorkshopFix/"* ]] || { printf 'Unexpected updater working directory.\n' >&2; exit 1; }
if [[ -z $probe ]]; then
    unlinked "$run/journal.log"
    exec >> "$run/journal.log" 2>&1
fi
relative() {
    local value=$1 part lower
    [[ -n $value && $value != /* && $value != */ && $value != *\\* && $value != *:* && ! $value =~ [[:cntrl:]] ]] || return 1
    local -a parts
    IFS=/ read -r -a parts <<< "$value"
    for part in "${parts[@]}"; do [[ -n $part && $part != . && $part != .. ]] || return 1; done
    lower=${value,,}
    case "$lower" in
        bepinex/cache|bepinex/cache/*|bepinex/config|bepinex/config/apishared.protonpaths.cfg|bepinex/config/apishared.protonworkshopfix|bepinex/config/apishared.protonworkshopfix/*|bepinex/patchers|bepinex/patchers/apishared.protonworkshopfix.dll|_se/.staging|_se/.staging/*) return 1 ;;
    esac
    case "$value" in BepInEx/*) ;; _SE/*.json) [[ $value != _SE/*/* ]] || return 1 ;; *) return 1 ;; esac
}
inside() {
    local root=$1 value=$2
    # Command substitution clears errexit on ordinary Bash; propagate both
    # refusals explicitly rather than relying on the caller's set -e.
    relative "$value" || return 1
    unlinked "$root/$value" || return 1
    [[ $(realpath -m -- "$root/$value") == "$root/"* ]] || return 1
    printf '%s' "$root/$value"
}
declare -a writes=() hashes=() deletes=()
declare -A destinations=() expected_sources=()
manifest_seen=0
for ((i=4;i<${#rows[@]};i++)); do
    row=${rows[i]%$'\r'}; IFS=$'\t' read -r action rel digest extra <<< "$row"
    [[ -n ${rel:-} && -z ${extra:-} ]] || exit 1
    if [[ $action == manifest ]]; then
        [[ $rel == delete_list.txt && ${digest:-} =~ ^[a-f0-9]{64}$ && $manifest_seen == 0 ]] || exit 1
        manifest_seen=1; expected_sources[$rel]=$digest
        continue
    fi
    target=$(inside "$game" "$rel")
    key=${rel,,}
    [[ -z ${destinations[$key]:-} ]] || { printf 'Duplicate destination: %s\n' "$rel" >&2; exit 1; }
    destinations[$key]=1
    case "$action" in
        write)
            [[ ${digest:-} =~ ^[a-f0-9]{64}$ ]] || exit 1
            source=$(inside "$staging" "$rel")
            [[ -f $source && $(hash "$source") == "$digest" ]] || { printf 'Staged source hash mismatch: %s\n' "$rel" >&2; exit 1; }
            writes+=("$rel"); hashes+=("$digest"); expected_sources[$rel]=$digest
            ;;
        delete)
            [[ -z ${digest:-} ]] || exit 1
            deletes+=("$rel")
            ;;
        *) exit 1 ;;
    esac
done
[[ ${#writes[@]} -gt 0 || ${#deletes[@]} -gt 0 ]] || { printf 'Empty deployment request.\n' >&2; exit 1; }
validate_inventory() {
    local listing="$run/inventory.tmp" entry rel
    local -A actual=()
    unlinked "$listing"
    # Capture find's exit status directly. A failed enumeration must never look
    # like an empty successful inventory through unchecked process substitution.
    if ! find "$staging" -mindepth 1 -print0 > "$listing"; then
        printf 'Source inventory enumeration failed; staging retained.\n' >&2
        return 1
    fi
    while IFS= read -r -d '' entry; do
        if [[ -L $entry ]]; then printf 'Linked staged entry; staging retained: %s\n' "$entry" >&2; return 1; fi
        if [[ -d $entry ]]; then continue; fi
        if [[ ! -f $entry ]]; then printf 'Nonregular staged entry; staging retained: %s\n' "$entry" >&2; return 1; fi
        [[ $entry == "$staging/"* ]] || return 1
        rel=${entry#"$staging/"}
        if [[ -z ${expected_sources[$rel]:-} ]]; then printf 'Unknown staged file; staging retained: %s\n' "$rel" >&2; return 1; fi
        unlinked "$entry"
        if [[ $(hash "$entry") != "${expected_sources[$rel]}" ]]; then printf 'Changed staged file; staging retained: %s\n' "$rel" >&2; return 1; fi
        actual[$rel]=1
    done < "$listing"
    [[ ${#actual[@]} == ${#expected_sources[@]} ]] || { printf 'Missing staged source; staging retained.\n' >&2; return 1; }
    rm -f -- "$listing"
}
validate_inventory
running() {
    local command argument position name=${executable,,}
    for command in /proc/[0-9]*/cmdline; do
        [[ -r $command ]] || continue
        # Only argv[0] or argv[1] identifies the game: Wine keeps the program there
        # (after its loader). Steam's reaper, steam-launch-wrapper, pressure-vessel and
        # Proton's script name the game only as a later argument while they wait for
        # every descendant, including this helper; counting them never ends the wait.
        position=0
        while (( position < 2 )) && IFS= read -r -d '' argument; do
            argument=${argument//\\//}; argument=${argument,,}
            if [[ $argument == "$name" || $argument == */"$name" ]]; then return 0; fi
            position=$((position + 1))
        done 2>/dev/null < "$command"
    done
    return 1
}
# Before readiness the calling game must still be alive. Prove visibility now;
# otherwise an inaccessible/mismatched process namespace could look like game exit.
if ! running; then printf 'Calling game is not visible to the Linux process detector.\n' >&2; exit 1; fi
if [[ $probe == --probe ]]; then printf 'Linux deployment preflight passed.\n'; exit 0; fi

lock="$game/_SE/.proton-workshop.lock"
unlinked "$lock"
exec 9>"$lock"
flock -n 9 || { printf 'Another Extender deployment is active.\n' >&2; exit 1; }
journal="$run/journal.log"; ready="$run/ready.flag"; cancel="$run/cancel.flag"
unlinked "$journal"; unlinked "$ready"; unlinked "$cancel"
[[ ! -e $ready && ! -e $cancel ]] || exit 1
record() { printf '[%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S.%3N')" "$*" >> "$journal"; }
step=preflight; temporary=
cleanup() { if [[ -n $temporary && -f $temporary ]]; then rm -f -- "$temporary"; fi; }
trap cleanup EXIT
trap 'code=$?; record "failure step=$step exit=$code; staging retained"; exit "$code"' ERR
request_unchanged() { [[ -f $request && $(hash "$request") == "$expected" ]]; }
assert_stopped() {
    [[ ! -e $cancel ]] || { record 'cancelled before next mutation'; return 1; }
    request_unchanged
    if running; then record 'failure game restarted; stopping before next mutation'; return 1; fi
}
request_unchanged
printf '%s' "$expected" > "$run/ready.tmp"
mv -T -- "$run/ready.tmp" "$ready"
record 'ready; waiting for game exit'
step=waiting; wait_budget=120
for ((i=0; ;i++)); do
    if [[ -e $cancel ]]; then record cancelled; exit 2; fi
    if ! running; then break; fi
    [[ $i -lt $wait_budget ]] || { record 'failure timeout waiting for game exit; no deployment performed'; exit 1; }
    sleep 1
done
assert_stopped
step=inventory-before-deployment
validate_inventory
step=deletions
for rel in "${deletes[@]}"; do
    assert_stopped
    target=$(inside "$game" "$rel")
    if [[ -e $target ]]; then rm -rf -- "$target"; record "deleted $rel"; fi
done
# Keep metadata last: a failed payload copy must not mark a package current.
for phase in payload metadata; do
    if [[ $phase == metadata ]]; then step=inventory-before-metadata; validate_inventory; fi
    step=$phase
    for ((i=0;i<${#writes[@]};i++)); do
        rel=${writes[i]}
        if [[ $phase == payload && $rel == _SE/* || $phase == metadata && $rel != _SE/* ]]; then continue; fi
        assert_stopped
        source=$(inside "$staging" "$rel"); target=$(inside "$game" "$rel")
        [[ -f $source && $(hash "$source") == "${hashes[i]}" ]]
        folder=$(dirname -- "$target"); unlinked "$folder"; mkdir -p -- "$folder"; unlinked "$folder"
        temporary=$(mktemp --tmpdir="$folder" '.proton-copy.XXXXXXXX')
        cp -- "$source" "$temporary"
        [[ $(hash "$temporary") == "${hashes[i]}" ]]
        assert_stopped
        mv -fT -- "$temporary" "$target"; temporary=
        record "updated $rel"
    done
done
step=cleanup
assert_stopped
validate_inventory
unlinked "$staging"
[[ $(realpath -e -- "$staging") == "$game/_SE/.staging" ]]
rm -rf -- "$staging"
record committed
# Restart failure never invalidates a completed installation. Do not inherit our lock.
(
    trap - ERR EXIT
    if command -v xdg-open >/dev/null; then
        if xdg-open 'steam://rungameid/3024040'; then exit 0; fi
        record 'restart-warning xdg-open failed; trying Steam'
    fi
    if command -v steam >/dev/null; then
        if steam -applaunch 3024040; then exit 0; fi
    fi
    record 'restart-warning start the game manually through Steam'
) 9>&- >> "$journal" 2>&1 &
exit 0
