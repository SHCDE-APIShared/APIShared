#!/usr/bin/env bash
# Host-side Proton updater. LF intentionally; requires bash, flock and GNU coreutils.
set -euo pipefail
game=${1:?game root required}; transaction=${2:?transaction or --maintain required}; expected=${3:-}; recover=${4:-}; supersedes_hash=${5:-}
[[ $game == /* && ( $transaction == /* || $transaction == --maintain || $transaction == --status ) ]] || { echo 'Host absolute paths are required.' >&2; exit 1; }
[[ -z ${SCDEModManagerLaunchId:-} ]] || { echo 'Externally managed installation.' >&2; exit 1; }
unlinked() {
    local part=$1
    while [[ $part != / && -n $part ]]; do
        [[ ! -L $part ]] || { echo "Linked path: $part" >&2; return 1; }
        part=${part%/*}; [[ -n $part ]] || part=/
    done
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
process_start() {
    local stat_line; local -a values
    IFS= read -r stat_line < "/proc/$1/stat" || return 1
    read -r -a values <<< "${stat_line##*) }"
    printf '%s' "${values[19]}"
}
unlinked "$game"
game=$(realpath -e -- "$game")
if [[ $transaction != --maintain && $transaction != --status ]]; then unlinked "$transaction"; transaction=$(realpath -e -- "$transaction"); fi
state=$(inside "$game" _APIShared/Workshop)
if [[ $transaction == --status ]]; then
    if [[ ! -f $state/apply.lock ]]; then printf 'idle\n'; exit 0; fi
    unlinked "$state/apply.lock"; exec 9<"$state/apply.lock"
    if flock -n 9; then printf 'idle\n'; exit 0; fi
    record_text=$(head -c 512 -- "$state/apply.lock"); record_text=${record_text%$'\r'}
    IFS=$'\t' read -r -a activity <<< "$record_text"
    if [[ ${#activity[@]} == 8 && ${activity[0]} == APISHARED-WORKSHOP-ACTIVE-1 && ${activity[1]} == linux && ${activity[2]} =~ ^[1-9][0-9]*$ && ${activity[3]} =~ ^[0-9]+$ && ( ${activity[4]} == install || ${activity[4]} == recover ) && ${activity[5]} =~ ^(waiting|backup|apply|restore|paused)$ && ${activity[6]} =~ ^[a-f0-9]{32}$ && ${activity[7]} =~ ^[a-f0-9]{64}$ ]]; then
        if [[ $(process_start "${activity[2]}" 2>/dev/null) == "${activity[3]}" ]]; then
            active_dir=$(inside "$state/Transactions" "${activity[6]}")
            ready_path=$(inside "$active_dir" ready.flag); operation_path=$(inside "$active_dir" operations.tsv); history_path=$(inside "$active_dir" journal.log)
            terminal=0
            if [[ -f $history_path ]]; then
                while IFS= read -r history_line || [[ -n $history_line ]]; do
                    history_line=${history_line%$'\r'}
                    if [[ $history_line == committed || $history_line == restored ]]; then terminal=1; fi
                done < "$history_path"
            fi
            if [[ $terminal == 0 && -f $ready_path && $(cat -- "$ready_path") == "${activity[7]}" && $(hash "$operation_path") == "${activity[7]}" ]]; then
                if flock -n 9; then printf 'idle\n'; exit 0; fi
                if [[ $(head -c 512 -- "$state/apply.lock") != "$record_text" ]]; then printf 'busy\n'; exit 0; fi
                printf '%s\n' "$record_text"; exit 0
            fi
        fi
    fi
    printf 'busy\n'; exit 0
fi
if [[ $transaction == --maintain && ! -d $state/Transactions ]]; then printf 'Workshop maintenance: removed=0; freedBytes=0; deferred=0\n'; exit 0; fi
[[ $transaction == --maintain || ( $(dirname -- "$transaction") == "$state/Transactions" && $(basename -- "$transaction") =~ ^[a-f0-9]{32}$ ) ]] || exit 1
exec 9<>"$state/apply.lock"; flock -n 9 || { echo 'Another updater is active.' >&2; exit 1; }
activity_mode=maintenance; activity_phase=waiting; owner_start=$(process_start $$); pause_spent=0; keep_interrupted=0
publish_activity() {
    activity_phase=$1
    unlinked "$state/apply.lock"
    printf 'APISHARED-WORKSHOP-ACTIVE-1\tlinux\t%s\t%s\t%s\t%s\t%s\t%s' "$$" "$owner_start" "$activity_mode" "$activity_phase" "$(basename -- "$transaction")" "$expected" > "$state/apply.lock"
}
trap 'unlinked "$state/apply.lock" && : > "$state/apply.lock"' EXIT
publish_activity waiting
if [[ $transaction != --maintain ]]; then journal="$transaction/journal.log"; unlinked "$journal"; fi
record() {
    local text=$1
    if [[ $text == 'failure '* ]]; then text="failure $(date -u '+%Y-%m-%dT%H:%M:%SZ'); step=${step:-validation}; ${text#failure }"; fi
    printf '%s\n' "$text" >> "$journal"; sync -f "$journal"
}
wait_game_exit() {
    if ! running; then return 0; fi
    local phase=$activity_phase pause_start=$SECONDS j checkpoint expected_current observed backup
    publish_activity paused
    record "pause $(date -u '+%Y-%m-%dT%H:%M:%SZ'); step=${step:-validation}; waiting for game exit"
    while running; do
        if (( pause_spent + SECONDS - pause_start >= 120 )); then keep_interrupted=1; record 'failure Game exit wait exceeded 120 seconds; confirm recovery on the next start'; return 1; fi
        if [[ -n ${cancel:-} && -f $cancel ]]; then keep_interrupted=1; record 'failure Handoff cancelled while waiting for game exit'; return 1; fi
        sleep 0.2
    done
    pause_spent=$((pause_spent + SECONDS - pause_start))
    keep_interrupted=1
    [[ -z ${cancel:-} || ! -f $cancel ]] || return 1
    [[ ! -d "$(inside "$game" _SE/.staging)" ]] || { record 'failure Script Extender staging takes precedence after pause'; return 1; }
    if [[ $(hash "$operations") != "$expected" || ( -n $supersedes_hash && $(hash "$transaction/supersedes.tsv") != "$supersedes_hash" ) ]]; then keep_interrupted=1; record 'failure References changed during game-exit pause'; return 1; fi
    if [[ $protocol == APISHARED-WORKSHOP-RECOVERY-1 ]]; then
        for ((j=2;j<${#lines[@]};j++)); do reference "${lines[$j]}" || return 1; assert_reference || { keep_interrupted=1; return 1; }; done
    fi
    if [[ $activity_mode == install && $phase != restore ]]; then
        for ((checkpoint=0;checkpoint<${#targets[@]};checkpoint++)); do
            expected_current=${befores[$checkpoint]}
            if [[ $phase == apply && -n ${begun[$checkpoint]:-} ]]; then expected_current=${afters[$checkpoint]}; fi
            [[ $(hash "$(inside "$game" "${targets[$checkpoint]}")") == "$expected_current" ]] || return 1
            if [[ ${kinds[$checkpoint]} == *write ]]; then [[ $(hash "$(inside "$transaction" "New/$checkpoint")") == "${afters[$checkpoint]}" ]] || return 1; fi
            backup=$(inside "$transaction" "Backup/$checkpoint")
            if [[ ${befores[$checkpoint]} != missing && ( $phase == apply || -f $backup ) ]]; then [[ $(hash "$backup") == "${befores[$checkpoint]}" ]] || return 1; fi
        done
    elif [[ $phase == restore ]]; then
        for ((checkpoint=0;checkpoint<${#restore_targets[@]};checkpoint++)); do
            observed=$(hash "$(inside "$game" "${restore_targets[$checkpoint]}")")
            [[ $observed == "${restore_before[$checkpoint]}" || $observed == "${restore_current[$checkpoint]}" ]] || return 1
            if [[ $observed != "${restore_before[$checkpoint]}" && ${restore_before[$checkpoint]} != missing ]]; then
                [[ $(hash "$(inside "${restore_dirs[$checkpoint]}" "Backup/${restore_ordinals[$checkpoint]}")") == "${restore_before[$checkpoint]}" ]] || return 1
            fi
        done
    fi
    keep_interrupted=0
    record "resumed $(date -u '+%Y-%m-%dT%H:%M:%SZ'); step=${step:-validation}"
    publish_activity "$phase"
}
journal_has() { [[ -f $1/journal.log ]] && tr -d '\r' < "$1/journal.log" | grep -qx -- "$2"; }
finished() { journal_has "$1" committed || journal_has "$1" restored; }
reference() {
    local extra
    IFS=$'\t' read -r ref_id ref_ops ref_journal extra <<< "${1%$'\r'}"
    [[ -z ${extra:-} && $ref_id =~ ^[a-f0-9]{32}$ && $ref_ops =~ ^[a-f0-9]{64}$ && $ref_journal =~ ^[a-f0-9]{64}$ ]] || return 1
}
assert_reference() {
    local dir
    dir=$(inside "$state/Transactions" "$ref_id") || return 1
    [[ $(hash "$(inside "$dir" operations.tsv)") == "$ref_ops" && $(hash "$(inside "$dir" journal.log)") == "$ref_journal" ]]
}
superseded() {
    local original=$1 candidate line
    for candidate in "$state"/Transactions/*; do
        [[ -d $candidate ]] || continue
        journal_has "$candidate" committed || continue
        [[ -f $candidate/supersedes.tsv ]] || continue
        unlinked "$candidate/supersedes.tsv" || return 1
        [[ $(cat -- "$(inside "$candidate" supersedes.sha256)") == "$(hash "$candidate/supersedes.tsv")" ]] || return 1
        while IFS= read -r line; do
            reference "$line" || return 1
            if [[ $ref_id == "$(basename -- "$original")" && $(hash "$original/operations.tsv") == "$ref_ops" && $(hash "$original/journal.log") == "$ref_journal" ]]; then return 0; fi
        done < "$candidate/supersedes.tsv"
    done
    return 1
}
# Validate an old operation list independently of staged downloads. The same
# validation is used for manual rollback, dialog rollback and replacement scope.
read_old() {
    local dir=$1 line kind target ordinal before after extra phase=0 i=0 protocol lower
    old_targets=(); old_befores=(); old_ordinals=(); old_kinds=()
    local -A unique=()
    unlinked "$dir/operations.tsv" || return 1
    IFS= read -r protocol < "$dir/operations.tsv"; protocol=${protocol%$'\r'}
    [[ $protocol == APISHARED-WORKSHOP-1 || $protocol == APISHARED-WORKSHOP-2 ]] || return 1
    while IFS= read -r line; do
        IFS=$'\t' read -r kind target ordinal before after extra <<< "${line%$'\r'}"
        [[ -z ${extra:-} && $ordinal == "$i" && $before =~ ^(missing|[a-f0-9]{64})$ && $after =~ ^(missing|[a-f0-9]{64})$ && -z ${unique[${target,,}]:-} ]] || return 1
        unique[${target,,}]=1
        if [[ $kind == receipt-write || $kind == receipt-delete ]]; then
            [[ $target =~ ^_APIShared/Workshop/Receipts/[1-9][0-9]*\.json$ && ( $protocol == APISHARED-WORKSHOP-2 || $phase == 0 ) ]] || return 1; phase=1
        else
            [[ $phase == 0 && ( $kind == write || $kind == delete ) && $target =~ ^BepInEx/plugins/[^/]+/.+ ]] || return 1
            lower=${target,,}; [[ ! $lower =~ ^bepinex/plugins/(apishared_serp|000shcdese)/ && ! $lower =~ /(config|lobbymodsettings)(/|$) ]] || return 1
        fi
        if [[ $kind == *write ]]; then [[ $after != missing ]] || return 1; else [[ $after == missing ]] || return 1; fi
        inside "$game" "$target" >/dev/null || return 1
        old_targets+=("$target"); old_befores+=("$before"); old_ordinals+=("$ordinal"); old_kinds+=("$kind"); i=$((i+1))
    done < <(tail -n +2 -- "$dir/operations.tsv")
    [[ $phase == 1 && $i -gt 0 ]]
}
restore_preflight() {
    local dir=$1 tag ordinal extra current target backup i
    read_old "$dir" || return 1
    local -A begun_old=()
    while IFS=$'\t' read -r tag ordinal extra; do
        tag=${tag%$'\r'}; ordinal=${ordinal%$'\r'}
        [[ $tag == begin ]] || continue
        [[ -z ${extra:-} && $ordinal =~ ^(0|[1-9][0-9]*)$ && ${#ordinal} -lt 10 && $ordinal -lt ${#old_targets[@]} ]] || return 1
        begun_old[$ordinal]=1
    done < "$dir/journal.log"
    for ((i=0;i<${#old_targets[@]};i++)); do
        [[ -n ${begun_old[$i]:-} ]] || continue
        target=$(inside "$game" "${old_targets[$i]}") || return 1
        [[ ! -d $target ]] || return 1
        current=$(hash "$target"); backup=$(inside "$dir" "Backup/$i") || return 1
        if [[ $current != "${old_befores[$i]}" && ${old_befores[$i]} != missing ]]; then [[ $(hash "$backup") == "${old_befores[$i]}" ]] || return 1; fi
        restore_dirs+=("$dir"); restore_targets+=("${old_targets[$i]}"); restore_before+=("${old_befores[$i]}"); restore_current+=("$current"); restore_ordinals+=("$i")
    done
}
restore_rows() {
    local i target backup
    for ((i=${#restore_targets[@]}-1;i>=0;i--)); do
        wait_game_exit || return 1
        target=$(inside "$game" "${restore_targets[$i]}") || return 1
        [[ $(hash "$target") != "${restore_before[$i]}" ]] || continue
        [[ $(hash "$target") == "${restore_current[$i]}" ]] || return 1
        if [[ ${restore_before[$i]} == missing ]]; then [[ ! -f $target ]] || rm -- "$target" || return 1
        else
            backup=$(inside "${restore_dirs[$i]}" "Backup/${restore_ordinals[$i]}") || return 1
            [[ $(hash "$backup") == "${restore_before[$i]}" ]] || return 1
            mkdir -p -- "$(dirname -- "$target")" || return 1; cp -- "$backup" "$target" || return 1
        fi
        [[ $(hash "$target") == "${restore_before[$i]}" ]] || return 1
        sync -f "$(dirname -- "$target")" || return 1
    done
}
running() {
    local command argument executable='stronghold crusader definitive edition.exe'
    for command in /proc/[0-9]*/cmdline; do
        [[ -r $command ]] || continue
        # Bash builtins avoid spawning tr/grep for every host process before
        # every file operation. Preserve case-insensitive Unix/Wine path matching.
        while IFS= read -r -d '' argument; do
            argument=${argument//\\//}; argument=${argument,,}
            if [[ $argument == "$executable" || $argument == */"$executable" ]]; then return 0; fi
        done 2>/dev/null < "$command"
    done
    return 1
}
restart_game() {
    # A desktop launcher can fail or wait for its client. Do not hold apply.lock
    # or run inherited recovery/cleanup traps in this detached restart attempt.
    (
        trap - ERR EXIT
        if command -v xdg-open >/dev/null; then
            if xdg-open 'steam://rungameid/3024040'; then exit 0; fi
            record 'restart-warning xdg-open failed; trying Steam directly' || true
        fi
        if command -v steam >/dev/null; then
            if steam -applaunch 3024040; then exit 0; fi
            record 'restart-warning Steam launch failed; start through Steam manually' || true
        else
            record 'restart-warning no usable Steam launcher; start through Steam manually' || true
        fi
    ) >/dev/null 2>&1 9>&- &
}
source "$(dirname -- "${BASH_SOURCE[0]}")/workshop-maintenance.sh"
if [[ $transaction == --maintain ]]; then maintenance; exit 0; fi
operations=$(inside "$transaction" operations.tsv)
[[ $expected =~ ^[a-f0-9]{64}$ && $(hash "$operations") == "$expected" ]] || exit 1
mapfile -t lines < "$operations"
protocol=${lines[0]%$'\r'}
if [[ $protocol == APISHARED-WORKSHOP-RECOVERY-1 ]]; then
    activity_mode=recover
    step='recovery preflight'
    [[ ${#lines[@]} -ge 3 && ( ${lines[1]%$'\r'} == restart || ${lines[1]%$'\r'} == stop ) ]] || exit 1
    trap 'record "failure Recovery request failed; keep transaction backups"; exit 1' ERR
    declare -a restore_dirs=() restore_targets=() restore_before=() restore_current=() restore_ordinals=() recovery_dirs=()
    declare -A recovery_targets=()
    for ((j=2;j<${#lines[@]};j++)); do
        reference "${lines[$j]}"; assert_reference
        dir=$(inside "$state/Transactions" "$ref_id"); journal_has "$dir" applying
        if finished "$dir"; then record 'failure Recovery target is no longer interrupted'; exit 1; fi
        read_old "$dir"
        for target in "${old_targets[@]}"; do [[ -z ${recovery_targets[${target,,}]:-} ]]; recovery_targets[${target,,}]=1; done
        restore_preflight "$dir"; recovery_dirs+=("$dir")
    done
    printf '%s' "$expected" > "$(inside "$transaction" ready.flag)"
    publish_activity waiting
    step='recovery waiting for game exit'
    cancel=$(inside "$transaction" cancel.flag)
    wait_game_exit
    [[ ! -f $cancel && ! -d "$(inside "$game" _SE/.staging)" ]]
    restore_dirs=(); restore_targets=(); restore_before=(); restore_current=(); restore_ordinals=()
    step='recovery final validation and interrupted-file backup'
    for ((j=2;j<${#lines[@]};j++)); do reference "${lines[$j]}"; assert_reference; restore_preflight "$(inside "$state/Transactions" "$ref_id")"; done
    for ((i=0;i<${#restore_targets[@]};i++)); do
        wait_game_exit
        [[ ${restore_current[$i]} != missing && ${restore_current[$i]} != "${restore_before[$i]}" ]] || continue
        saved=$(inside "$transaction" "RecoveryBackup/$(basename -- "${restore_dirs[$i]}")/${restore_ordinals[$i]}")
        mkdir -p -- "$(dirname -- "$saved")"; cp -- "$(inside "$game" "${restore_targets[$i]}")" "$saved"; [[ $(hash "$saved") == "${restore_current[$i]}" ]]; sync -f "$saved"
    done
    record recovering; publish_activity restore; step='restore original files and receipts'; restore_rows
    publish_activity committing
    for dir in "${recovery_dirs[@]}"; do printf 'restored\n' >> "$dir/journal.log"; sync -f "$dir/journal.log"; done
    record committed; publish_activity completed; trap - ERR
    completed_maintenance
    [[ ${lines[1]%$'\r'} != restart ]] || restart_game
    exit 0
fi
[[ ( $protocol == APISHARED-WORKSHOP-1 || $protocol == APISHARED-WORKSHOP-2 ) && ${#lines[@]} -gt 1 ]] || exit 1
activity_mode=install; [[ $recover != --recover ]] || activity_mode=recover
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
if [[ -f $journal ]]; then
    finished "$transaction" && exit 1
    while IFS=$'\t' read -r tag ordinal; do [[ $tag != begin ]] || begun[${ordinal%$'\r'}]=1; done < "$journal"
fi
declare -A replacing=()
if [[ -f $transaction/supersedes.tsv ]]; then
    [[ $supersedes_hash =~ ^[a-f0-9]{64}$ && $(hash "$(inside "$transaction" supersedes.tsv)") == "$supersedes_hash" ]] || exit 1
    [[ $(cat -- "$(inside "$transaction" supersedes.sha256)") == "$supersedes_hash" ]] || exit 1
    while IFS= read -r line; do
        reference "$line"; assert_reference || exit 1
        dir=$(inside "$state/Transactions" "$ref_id"); read_old "$dir" || exit 1
        for ((i=0;i<${#old_targets[@]};i++)); do
            covered=0
            for target in "${targets[@]}"; do
                if [[ ${old_kinds[$i]} == receipt-* ]]; then [[ $target != "${old_targets[$i]}" ]] || covered=1
                else prefix=${old_targets[$i]#BepInEx/plugins/}; prefix="BepInEx/plugins/${prefix%%/*}/"; [[ ${target,,} != "${prefix,,}"* ]] || covered=1; fi
            done
            [[ $covered == 1 ]] || exit 1
        done
        replacing[$dir]=1
    done < "$transaction/supersedes.tsv"
else [[ -z $supersedes_hash ]] || exit 1; fi
for other in "$state"/Transactions/*/journal.log; do
    [[ -f $other && $other != "$journal" ]] || continue; unlinked "$other"
    dir=$(dirname -- "$other")
    if journal_has "$dir" applying && ! finished "$dir" && [[ -z ${replacing[$dir]:-} ]] && ! superseded "$dir"; then
        [[ $recover == --recover ]] || { record "failure Recover unfinished transaction: $other"; exit 1; }
        read_old "$dir" || exit 1
        for old_target in "${old_targets[@]}"; do [[ -z ${seen[${old_target,,}]:-} ]] || { record 'failure Overlapping transactions require complete repair'; exit 1; }; done
    fi
done
if [[ ${#begun[@]} -gt 0 && $recover != --recover ]]; then record 'failure Interrupted transaction; use --recover'; exit 1; fi
printf '%s' "$expected" > "$(inside "$transaction" ready.flag)"
publish_activity waiting
if [[ $recover == --recover ]]; then cancel=$(inside "$transaction" recovery-cancel.flag); else cancel=$(inside "$transaction" cancel.flag); fi
wait_game_exit
[[ ! -f $cancel ]] || { record 'failure Handoff cancelled'; exit 1; }
[[ ! -d "$(inside "$game" _SE/.staging)" ]] || { record 'failure Script Extender staging takes precedence'; exit 1; }
rollback() {
    publish_activity restore
    step='rollback preflight and restore'
    restore_dirs=(); restore_targets=(); restore_before=(); restore_current=(); restore_ordinals=()
    restore_preflight "$transaction" || return 1
    restore_rows || return 1
    publish_activity committing
    record restored
    completed_maintenance
}
if [[ $recover == --recover ]]; then rollback; exit 2; fi
if [[ -n $supersedes_hash ]]; then
    [[ $(hash "$transaction/supersedes.tsv") == "$supersedes_hash" ]] || exit 1
    while IFS= read -r line; do reference "$line"; assert_reference || exit 1; done < "$transaction/supersedes.tsv"
fi
publish_activity backup
for ((index=0; index<${#targets[@]}; index++)); do
    wait_game_exit
    target=$(inside "$game" "${targets[$index]}"); [[ ! -d $target && $(hash "$target") == "${befores[$index]}" ]] || { record "failure Target changed: ${targets[$index]}"; exit 1; }
    if [[ ${befores[$index]} != missing ]]; then backup=$(inside "$transaction" "Backup/$index"); mkdir -p -- "$(dirname -- "$backup")"; cp -- "$target" "$backup"; [[ $(hash "$backup") == "${befores[$index]}" ]] || exit 1; sync -f "$backup"; fi
done
record applying
publish_activity apply
step='installation writes and receipt commit'
trap 'record "failure Application interrupted"; if [[ $keep_interrupted == 1 ]] || running; then exit 1; fi; rollback || record "failure Recovery required"; exit 1' ERR
for ((index=0; index<${#targets[@]}; index++)); do
    wait_game_exit
    target=$(inside "$game" "${targets[$index]}"); [[ $(hash "$target") == "${befores[$index]}" ]]
    record $'begin\t'"$index"; begun[$index]=1
    if [[ ${kinds[$index]} == *write ]]; then
        new=$(inside "$transaction" "New/$index"); [[ $(hash "$new") == "${afters[$index]}" ]]; mkdir -p -- "$(dirname -- "$target")"; target=$(inside "$game" "${targets[$index]}"); cp -- "$new" "$target"
    else [[ ! -f $target ]] || rm -- "$target"; fi
    # A delete of an already absent target may name a folder that does not exist (clean repair).
    [[ $(hash "$target") == "${afters[$index]}" ]]; [[ ! -d $(dirname -- "$target") ]] || sync -f "$(dirname -- "$target")"; record $'done\t'"$index"
done
if [[ -n $supersedes_hash ]]; then [[ $(hash "$transaction/supersedes.tsv") == "$supersedes_hash" ]]; fi
publish_activity committing; record committed; publish_activity completed; trap - ERR
completed_maintenance
for ((index=0; index<${#targets[@]}; index++)); do
    [[ ${kinds[$index]} == delete ]] || continue
    directory=$(dirname -- "$(inside "$game" "${targets[$index]}")")
    while [[ $directory == "$game/BepInEx/plugins/"* && -d $directory ]]; do
        unlinked "$directory"; rmdir -- "$directory" 2>/dev/null || break; directory=$(dirname -- "$directory")
    done
done
if [[ $recover == --restart ]]; then
    restart_game
fi
