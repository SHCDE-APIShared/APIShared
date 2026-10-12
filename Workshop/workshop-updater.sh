#!/usr/bin/env bash
# Host-side Proton updater. LF intentionally; requires bash, flock and GNU coreutils.
set -euo pipefail
game=${1:?game root required}; transaction=${2:?transaction required}; expected=${3:?operation hash required}; recover=${4:-}
[[ -z ${SCDEModManagerLaunchId:-} ]] || { echo 'Externally managed installation.' >&2; exit 1; }
unlinked() {
    local part=$1
    while [[ $part != / && -n $part ]]; do [[ ! -L $part ]] || { echo "Linked path: $part" >&2; return 1; }; part=$(dirname -- "$part"); done
}
inside() {
    local root=$1 relative=$2 part
    [[ $relative != /* && $relative != */ && $relative != *\\* && $relative != *:* && ! $relative =~ [[:cntrl:]] ]] || return 1
    IFS=/ read -r -a parts <<< "$relative"
    for part in "${parts[@]}"; do [[ -n $part && $part != . && $part != .. && $part != *. && $part != *' ' ]] || return 1; done
    unlinked "$root/$relative" || return 1
    printf '%s' "$root/$relative"
}
hash() { if [[ -f $1 ]]; then sha256sum -- "$1" | cut -d ' ' -f1; else printf missing; fi; }
unlinked "$game"; unlinked "$transaction"
game=$(realpath -e -- "$game"); transaction=$(realpath -e -- "$transaction")
state=$(inside "$game" _APIShared/Workshop)
[[ $(dirname -- "$transaction") == "$state/Transactions" && $(basename -- "$transaction") =~ ^[a-f0-9]{32}$ ]] || exit 1
exec 9>"$state/apply.lock"; flock -n 9 || { echo 'Another updater is active.' >&2; exit 1; }
journal="$transaction/journal.log"; unlinked "$journal"
record() { printf '%s\n' "$1" >> "$journal"; sync -f "$journal"; }
operations=$(inside "$transaction" operations.tsv)
[[ $expected =~ ^[a-f0-9]{64}$ && $(hash "$operations") == "$expected" ]] || exit 1
mapfile -t lines < "$operations"
protocol=${lines[0]%$'\r'}
[[ ( $protocol == APISHARED-WORKSHOP-1 || $protocol == APISHARED-WORKSHOP-2 ) && ${#lines[@]} -gt 1 ]] || exit 1
receipt_phase=0
declare -a kinds=() targets=() ordinals=() befores=() afters=(); declare -A seen=() begun=()
for ((index=1; index<${#lines[@]}; index++)); do
    line=${lines[$index]%$'\r'}; IFS=$'\t' read -r kind target ordinal before after extra <<< "$line"
    [[ -z ${extra:-} && $ordinal == $((index-1)) && $before =~ ^(missing|[a-f0-9]{64})$ && $after =~ ^(missing|[a-f0-9]{64})$ ]] || exit 1
    if [[ $kind == receipt-write || $kind == receipt-delete ]]; then
        [[ ( $protocol == APISHARED-WORKSHOP-2 || $index == $((${#lines[@]}-1)) ) && $target =~ ^_APIShared/Workshop/Receipts/[1-9][0-9]*\.json$ ]] || exit 1
        receipt_phase=1
    else
        [[ $receipt_phase == 0 && $index != $((${#lines[@]}-1)) && ( $kind == write || $kind == delete ) && $target =~ ^BepInEx/plugins/[^/]+/.+ ]] || exit 1
        lower=${target,,}; [[ ! $lower =~ ^bepinex/plugins/(apishared_serp|000shcdese)/ && ! $lower =~ /(config|lobbymodsettings)(/|$) ]] || exit 1
    fi
    [[ -z ${seen[${target,,}]:-} ]] || exit 1; seen[${target,,}]=1
    actual=$(inside "$game" "$target")
    if [[ $kind == *write ]]; then [[ $after != missing ]] || exit 1; if [[ $recover != --recover ]]; then [[ $(hash "$(inside "$transaction" "New/$ordinal")") == "$after" ]] || exit 1; fi; else [[ $after == missing ]] || exit 1; fi
    kinds+=("$kind"); targets+=("$target"); ordinals+=("$ordinal"); befores+=("$before"); afters+=("$after")
done
[[ $receipt_phase == 1 ]] || exit 1
running() {
    local command
    for command in /proc/[0-9]*/cmdline; do
        [[ -r $command ]] || continue
        # Wine's native PID differs from its Win32 PID. Match argv rather than a Win32 identifier.
        if tr '\0' '\n' < "$command" 2>/dev/null | grep -qiE '(^|[/\\])Stronghold Crusader Definitive Edition\.exe$'; then return 0; fi
    done
    return 1
}
if [[ -f $journal ]]; then
    grep -qxE 'committed|restored' "$journal" && exit 1
    while IFS=$'\t' read -r tag ordinal; do [[ $tag != begin ]] || begun[$ordinal]=1; done < "$journal"
fi
for other in "$state"/Transactions/*/journal.log; do
    [[ -f $other && $other != "$journal" ]] || continue; unlinked "$other"
    if grep -qx applying "$other" && ! grep -qxE 'committed|restored' "$other"; then record "failure Recover unfinished transaction: $other"; exit 1; fi
done
if [[ ${#begun[@]} -gt 0 && $recover != --recover ]]; then record 'failure Interrupted transaction; use --recover'; exit 1; fi
printf '%s' "$expected" > "$(inside "$transaction" ready.flag)"
cancel=$(inside "$transaction" cancel.flag)
while running; do [[ ! -f $cancel ]] || { record 'failure Handoff cancelled'; exit 1; }; sleep 1; done
[[ ! -f $cancel ]] || { record 'failure Handoff cancelled'; exit 1; }
[[ ! -d "$(inside "$game" _SE/.staging)" ]] || { record 'failure Script Extender staging takes precedence'; exit 1; }
rollback() {
    local index target backup
    for ((index=${#targets[@]}-1; index>=0; index--)); do
        [[ -n ${begun[$index]:-} ]] || continue
        ! running || return 1
        target=$(inside "$game" "${targets[$index]}") || return 1; backup=$(inside "$transaction" "Backup/$index") || return 1
        if [[ ${befores[$index]} == missing ]]; then [[ ! -f $target ]] || rm -- "$target"
        else [[ $(hash "$backup") == "${befores[$index]}" ]] || return 1; mkdir -p -- "$(dirname -- "$target")"; cp -- "$backup" "$target"; fi
        [[ $(hash "$target") == "${befores[$index]}" ]] || return 1
        sync -f "$(dirname -- "$target")"
    done
    record restored
}
if [[ $recover == --recover ]]; then rollback; exit 2; fi
for ((index=0; index<${#targets[@]}; index++)); do
    target=$(inside "$game" "${targets[$index]}"); [[ ! -d $target && $(hash "$target") == "${befores[$index]}" ]] || { record "failure Target changed: ${targets[$index]}"; exit 1; }
    if [[ ${befores[$index]} != missing ]]; then backup=$(inside "$transaction" "Backup/$index"); mkdir -p -- "$(dirname -- "$backup")"; cp -- "$target" "$backup"; [[ $(hash "$backup") == "${befores[$index]}" ]] || exit 1; sync -f "$backup"; fi
done
record applying
trap 'record "failure Application interrupted"; rollback || record "failure Recovery required"; exit 1' ERR
for ((index=0; index<${#targets[@]}; index++)); do
    ! running
    target=$(inside "$game" "${targets[$index]}"); [[ $(hash "$target") == "${befores[$index]}" ]]
    record $'begin\t'"$index"; begun[$index]=1
    if [[ ${kinds[$index]} == *write ]]; then
        new=$(inside "$transaction" "New/$index"); [[ $(hash "$new") == "${afters[$index]}" ]]; mkdir -p -- "$(dirname -- "$target")"; target=$(inside "$game" "${targets[$index]}"); cp -- "$new" "$target"
    else [[ ! -f $target ]] || rm -- "$target"; fi
    [[ $(hash "$target") == "${afters[$index]}" ]]; sync -f "$(dirname -- "$target")"; record $'done\t'"$index"
done
record committed; trap - ERR
for ((index=0; index<${#targets[@]}; index++)); do
    [[ ${kinds[$index]} == delete ]] || continue
    directory=$(dirname -- "$(inside "$game" "${targets[$index]}")")
    while [[ $directory == "$game/BepInEx/plugins/"* && -d $directory ]]; do
        unlinked "$directory"; rmdir -- "$directory" 2>/dev/null || break; directory=$(dirname -- "$directory")
    done
done
if [[ $recover == --restart ]]; then
    if command -v xdg-open >/dev/null; then xdg-open 'steam://rungameid/3024040' >/dev/null 2>&1 9>&- &
    else record 'restart-warning xdg-open unavailable; start through Steam manually'; fi
fi
