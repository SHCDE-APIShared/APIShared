#!/usr/bin/env bash
# Sourced by the updater while holding its real host apply.lock. Same inventory,
# two-per-item retention and resumable deletion protocol as the Windows backend.
maintenance_allowed() {
    [[ $1 =~ ^(operations\.tsv|operations\.sha256|diagnostics\.txt|journal\.log|ready\.flag|cancel\.flag|recovery-cancel\.flag|supersedes\.tsv|supersedes\.sha256|completed\.utc|New/(0|[1-9][0-9]*)|Backup/(0|[1-9][0-9]*)|RecoveryBackup/[a-f0-9]{32}/(0|[1-9][0-9]*))$ ]]
}
maintenance_files() {
    local dir=$1 prefix=${2:-} entry relative
    unlinked "$dir" || return 1
    for entry in "$dir"/* "$dir"/.[!.]* "$dir"/..?*; do
        [[ -e $entry || -L $entry ]] || continue
        unlinked "$entry" || return 1
        relative="$prefix$(basename -- "$entry")"
        if [[ -d $entry ]]; then
            [[ $relative =~ ^(New|Backup|RecoveryBackup)(/[a-f0-9]{32})?$ ]] || return 1
            maintenance_files "$entry" "$relative/" || return 1
        else
            maintenance_allowed "$relative" || return 1
            m_scan_files+=("$relative")
        fi
    done
}
maintenance_completed() {
    local file
    file=$(inside "$transaction" completed.utc) || return 1
    [[ -f $file ]] || { date -u '+%Y-%m-%dT%H:%M:%SZ' | tr -d '\n' > "$file"; sync -f "$file"; }
}
maintenance_digest() { printf '%s' "$1" | sha256sum | cut -d ' ' -f1; }
maintenance_node() {
    local id=$1 dir="$state/Transactions/$1" file protocol line r_id r_ops r_journal extra i ordinal kind expected timestamp
    m_scan_files=(); maintenance_files "$dir" || return 1
    [[ -f $dir/operations.tsv && -f $dir/journal.log ]] || return 1
    if [[ -f $dir/operations.sha256 ]]; then [[ $(cat -- "$dir/operations.sha256") == "$(hash "$dir/operations.tsv")" ]] || return 1; fi
    IFS= read -r protocol < "$dir/operations.tsv"; protocol=${protocol%$'\r'}
    m_terminal[$id]=0; finished "$dir" && m_terminal[$id]=1
    m_resolved[$id]=${m_terminal[$id]}; m_request[$id]=0; m_refs[$id]=''; m_items[$id]=''
    if [[ $protocol == APISHARED-WORKSHOP-RECOVERY-1 ]]; then
        local -a request_lines=(); mapfile -t request_lines < "$dir/operations.tsv"
        [[ ${#request_lines[@]} -ge 3 && ( ${request_lines[1]%$'\r'} == restart || ${request_lines[1]%$'\r'} == stop ) ]] || return 1
        m_request[$id]=1
        for ((i=2;i<${#request_lines[@]};i++)); do
            reference "${request_lines[$i]}" || return 1
            m_refs[$id]+="$ref_id"$'\t'"$ref_ops"$'\t'"$ref_journal"$'\n'
        done
        for file in "${m_scan_files[@]}"; do
            [[ $file != New/* && $file != Backup/* ]] || return 1
            if [[ $file == RecoveryBackup/* ]]; then
                r_id=${file#RecoveryBackup/}; r_id=${r_id%%/*}
                [[ ${m_refs[$id]} == *"$r_id"$'\t'* ]] || return 1
            fi
        done
    else
        read_old "$dir" || return 1
        local -a operation_lines=(); mapfile -t operation_lines < "$dir/operations.tsv"
        for ((i=0;i<${#old_targets[@]};i++)); do
            if [[ ${old_kinds[$i]} == receipt-* ]]; then file=${old_targets[$i]##*/}; m_items[$id]+="${file%.json}"$'\n'; fi
        done
        while IFS= read -r line; do
            line=${line%$'\r'}
            if [[ $line == begin$'\t'* ]]; then ordinal=${line#*$'\t'}; [[ $ordinal =~ ^(0|[1-9][0-9]*)$ && ${#ordinal} -lt 10 && $ordinal -lt ${#old_targets[@]} ]] || return 1; fi
        done < "$dir/journal.log"
        for file in "${m_scan_files[@]}"; do
            if [[ $file =~ ^(New|Backup)/([0-9]+)$ ]]; then
                kind=${BASH_REMATCH[1]}; ordinal=${BASH_REMATCH[2]}
                [[ ${#ordinal} -lt 10 && $ordinal -lt ${#old_targets[@]} ]] || return 1
                IFS=$'\t' read -r _ _ _ before after <<< "${operation_lines[$((ordinal+1))]%$'\r'}"
                if [[ $kind == New ]]; then expected=$after; else expected=$before; fi
                [[ $expected != missing && $(hash "$dir/$file") == "$expected" ]] || return 1
            elif [[ $file == RecoveryBackup/* ]]; then return 1; fi
        done
        if [[ -f $dir/supersedes.tsv ]]; then
            [[ $(cat -- "$(inside "$dir" supersedes.sha256)") == "$(hash "$dir/supersedes.tsv")" ]] || return 1
            while IFS= read -r line; do reference "$line" || return 1; m_refs[$id]+="$ref_id"$'\t'"$ref_ops"$'\t'"$ref_journal"$'\n'; done < "$dir/supersedes.tsv"
        else [[ ! -f $dir/supersedes.sha256 ]] || return 1; fi
    fi
    if [[ -f $dir/completed.utc ]]; then
        timestamp=$(cat -- "$dir/completed.utc")
        [[ $timestamp =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || return 1
        m_time[$id]=$(date -u -d "$timestamp" '+%s') || return 1
    else m_time[$id]=$(stat -c '%Y' -- "$dir/journal.log") || return 1; fi
    m_files[$id]=$(printf '%s\n' "${m_scan_files[@]}")$'\n'
    m_valid[$id]=1
}
maintenance_scan() {
    local dir id other line changed all item key count timestamp
    for dir in "$state"/Transactions/* "$state"/Transactions/.[!.]*; do
        [[ -d $dir || -L $dir ]] || continue
        id=$(basename -- "$dir"); m_ids+=("$id"); m_valid[$id]=0; m_keep[$id]=1; m_refs[$id]=''; m_resolved[$id]=0
        if [[ $id =~ ^[a-f0-9]{32}$ ]] && maintenance_node "$id"; then m_keep[$id]=0
        else printf 'Workshop maintenance retained: %s; incomplete, changed or unknown transaction\n' "$dir"; fi
    done
    for id in "${m_ids[@]}"; do
        [[ ${m_valid[$id]} == 1 && ${m_request[$id]} == 0 ]] || continue
        journal_has "$state/Transactions/$id" committed || continue
        while IFS= read -r line; do
            [[ -n $line ]] || continue; reference "$line" || return 1
            other=$ref_id; [[ -n ${m_valid[$other]:-} ]] || continue
            if [[ $(hash "$(inside "$state/Transactions/$other" operations.tsv)") == "$ref_ops" && $(hash "$(inside "$state/Transactions/$other" journal.log)") == "$ref_journal" ]]; then m_resolved[$other]=1; fi
        done <<< "${m_refs[$id]}"
    done
    for id in "${m_ids[@]}"; do
        [[ ${m_valid[$id]} == 1 ]] || continue
        if [[ ${m_request[$id]} == 1 && ${m_terminal[$id]} == 0 ]]; then
            all=1; [[ -n ${m_refs[$id]} ]] || all=0
            while IFS= read -r line; do [[ -n $line ]] || continue; reference "$line" || return 1; [[ ${m_valid[$ref_id]:-0} == 1 && ${m_resolved[$ref_id]:-0} == 1 ]] || all=0; done <<< "${m_refs[$id]}"
            m_resolved[$id]=$all
        fi
        [[ ${m_resolved[$id]} == 1 ]] || m_keep[$id]=1
    done
    # One sorted list per item, keeping complete batches whenever any item needs it.
    local -A items=()
    for id in "${m_ids[@]}"; do
        [[ ${m_valid[$id]} == 1 && ${m_request[$id]} == 0 && ${m_terminal[$id]} == 1 ]] || continue
        while IFS= read -r item; do [[ -n $item ]] || continue; items[$item]+="$(printf '%020d' "${m_time[$id]}") $id"$'\n'; done <<< "${m_items[$id]}"
    done
    for item in "${!items[@]}"; do
        count=0
        while read -r timestamp id; do [[ -n $id ]] || continue; m_keep[$id]=1; count=$((count+1)); [[ $count -lt 2 ]] || break; done < <(printf '%s' "${items[$item]}" | LC_ALL=C sort -r)
    done
    changed=1
    while [[ $changed == 1 ]]; do
        changed=0
        for id in "${m_ids[@]}"; do
            while IFS= read -r line; do
                [[ -n $line ]] || continue; reference "$line" || return 1; other=$ref_id
                [[ -n ${m_keep[$other]:-} ]] || continue
                if [[ ${m_keep[$other]} == 1 && ${m_keep[$id]} == 0 ]]; then m_keep[$id]=1; changed=1; fi
                if [[ ${m_request[$id]:-0} == 1 && ${m_keep[$id]} == 1 && ${m_keep[$other]} == 0 ]]; then m_keep[$other]=1; changed=1; fi
            done <<< "${m_refs[$id]}"
        done
    done
}
maintenance_visit() {
    local id=$1 other line file mode
    [[ ${m_visited[$id]:-0} == 0 ]] || return 0
    [[ ${m_visiting[$id]:-0} == 0 ]] || return 1
    m_visiting[$id]=1
    while IFS= read -r line; do
        [[ -n $line ]] || continue; reference "$line" || return 1; other=$ref_id
        if [[ ${m_keep[$other]:-1} == 0 ]]; then maintenance_visit "$other" || return 1; fi
    done <<< "${m_refs[$id]}"
    m_visited[$id]=1; m_visiting[$id]=0
    if [[ ${m_valid[$id]} != 1 || ${m_resolved[$id]} != 1 ]]; then m_deferred=$((m_deferred+1)); return 0; fi
    if [[ ${m_keep[$id]} == 1 ]]; then mode=new; else mode=all; fi
    local rows=''
    while IFS= read -r file; do
        [[ -n $file ]] || continue
        [[ $mode == all || $file == New/* ]] || continue
        rows+="F"$'\t'"$id"$'\t'"$file"$'\t'"$(hash "$(inside "$state/Transactions/$id" "$file")")"$'\n'
    done <<< "${m_files[$id]}"
    if [[ -n $rows || $mode == all ]]; then
        m_payload+="D"$'\t'"$id"$'\t'"$mode"$'\n'
        if [[ $mode == all ]]; then while IFS= read -r line; do [[ -n $line ]] || continue; reference "$line" || return 1; m_payload+="R"$'\t'"$id"$'\t'"$ref_id"$'\n'; done <<< "${m_refs[$id]}"; fi
        m_payload+="$rows"
    fi
}
maintenance_execute() {
    local id=$1 mode=$2 file key path bytes reference_file line child nested
    local dir="$state/Transactions/$id"
    [[ -d $dir ]] || return 0
    if [[ $mode == all ]]; then
        while IFS= read -r line; do [[ -n $line ]] || continue; [[ ! -d $state/Transactions/$line ]] || return 1; done <<< "${m_dependencies[$id]:-}"
    fi
    m_scan_files=(); maintenance_files "$dir" || return 1
    for file in "${m_scan_files[@]}"; do
        [[ $mode == all || $file == New/* ]] || continue
        key="$id/$file"
        [[ -n ${m_pins[$key]:-} && $(hash "$(inside "$dir" "$file")") == "${m_pins[$key]}" ]] || return 1
    done
    if [[ $mode == all && -f $dir/supersedes.tsv ]]; then
        while IFS= read -r line; do reference "$line" || return 1; [[ ! -d $state/Transactions/$ref_id ]] || return 1; done < "$dir/supersedes.tsv"
    fi
    # Journal first for approved whole-directory retirement. Otherwise deleting
    # operations before an applying journal could invalidate its replacement pin.
    if [[ $mode == all && -n ${m_pins[$id/journal.log]:-} && -f $dir/journal.log ]]; then
        path=$(inside "$dir" journal.log) || return 1
        [[ $(hash "$path") == "${m_pins[$id/journal.log]}" ]] || return 1
        bytes=$(stat -c '%s' -- "$path") || return 1; rm -- "$path" || return 1; m_bytes=$((m_bytes+bytes))
    fi
    for file in "${m_scan_files[@]}"; do
        key="$id/$file"; [[ -n ${m_pins[$key]:-} ]] || continue
        [[ $mode != all || $file != journal.log ]] || continue
        path=$(inside "$dir" "$file") || return 1
        [[ $(hash "$path") == "${m_pins[$key]}" ]] || return 1
        bytes=$(stat -c '%s' -- "$path") || return 1; rm -- "$path" || return 1; m_bytes=$((m_bytes+bytes))
    done
    for child in "$dir"/*; do
        [[ -d $child ]] || continue; unlinked "$child" || return 1
        for nested in "$child"/*; do [[ -d $nested ]] || continue; unlinked "$nested" || return 1; rmdir -- "$nested" 2>/dev/null || true; done
        rmdir -- "$child" 2>/dev/null || true
    done
    if [[ $mode == all ]]; then rmdir -- "$dir" || return 1; m_removed=$((m_removed+1)); fi
}
maintenance_resume() {
    local pending="$state/cleanup.pending.tsv" header digest payload line tag id mode file pin extra current='' remaining=0
    [[ -f $pending ]] || return 0
    unlinked "$pending" || return 1
    IFS= read -r header < "$pending"; digest=$(sed -n '2p' -- "$pending")
    [[ $header == APISHARED-WORKSHOP-CLEANUP-1 && $digest =~ ^[a-f0-9]{64}$ && $(tail -n +3 -- "$pending" | sha256sum | cut -d ' ' -f1) == "$digest" ]] || return 1
    local -a actions=(); local -A modes=() m_pins=() m_dependencies=() references_seen=()
    while IFS=$'\t' read -r tag id file pin extra; do
        [[ -n $tag ]] || continue
        if [[ $tag == D && $id =~ ^[a-f0-9]{32}$ && ( $file == all || $file == new ) && -z ${pin:-} && -z ${extra:-} && -z ${modes[$id]:-} ]]; then
            current=$id; actions+=("$id"); modes[$id]=$file
        elif [[ $tag == R && $id == "$current" && ${modes[$id]} == all && $file =~ ^[a-f0-9]{32}$ && -z ${pin:-} && -z ${extra:-} && -z ${references_seen[$id/$file]:-} ]]; then
            m_dependencies[$id]+="$file"$'\n'; references_seen[$id/$file]=1
        elif [[ $tag == F && $id == "$current" && $pin =~ ^[a-f0-9]{64}$ && -z ${extra:-} && -z ${m_pins[$id/$file]:-} ]]; then
            maintenance_allowed "$file" || return 1
            [[ ${modes[$id]} == all || $file == New/* ]] || return 1
            m_pins[$id/$file]=$pin
        else return 1; fi
    done < <(tail -n +3 -- "$pending")
    for id in "${actions[@]}"; do
        if ! maintenance_execute "$id" "${modes[$id]}"; then remaining=1; m_deferred=$((m_deferred+1)); printf 'Workshop maintenance deferred: %s; changed/locked content or retained predecessor\n' "$state/Transactions/$id"; fi
    done
    if [[ $remaining == 0 ]]; then rm -- "$pending" || return 1; fi
}
maintenance_run() {
    local pending="$state/cleanup.pending.tsv" id
    if [[ -f $pending ]]; then maintenance_resume || return 1; fi
    [[ ! -f $pending ]] || return 0
    local -a m_ids=() m_scan_files=()
    local -A m_valid=() m_terminal=() m_resolved=() m_keep=() m_request=() m_time=() m_refs=() m_items=() m_files=() m_visited=() m_visiting=()
    maintenance_scan || return 1
    local m_payload=''
    for id in "${m_ids[@]}"; do maintenance_visit "$id" || return 1; done
    [[ -n $m_payload ]] || return 0
    local temporary
    temporary=$(inside "$state" cleanup.pending.tmp) || return 1
    printf 'APISHARED-WORKSHOP-CLEANUP-1\n%s\n%s' "$(maintenance_digest "$m_payload")" "$m_payload" > "$temporary" || return 1
    sync -f "$temporary" || return 1; mv -T -- "$temporary" "$pending" || return 1; sync -f "$state" || return 1
    maintenance_resume
}
maintenance() {
    local m_removed=0 m_bytes=0 m_deferred=0
    if ! maintenance_run; then m_deferred=$((m_deferred+1)); printf 'Workshop maintenance warning: invalid/incomplete history; retry on next startup\n'; fi
    printf 'Workshop maintenance: removed=%s; freedBytes=%s; deferred=%s\n' "$m_removed" "$m_bytes" "$m_deferred"
    return 0
}
completed_maintenance() {
    maintenance_completed || printf 'Workshop maintenance warning: completion timestamp unavailable\n'
    maintenance
}
