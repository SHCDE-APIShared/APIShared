#!/usr/bin/env bash
# Native Linux protocol/retention fixture. Does not simulate flock or Wine.
set -euo pipefail
for tool in flock sha256sum stat date sync; do command -v "$tool" >/dev/null || { printf 'SKIP: Linux maintenance fixture requires %s\n' "$tool"; exit 77; }; done
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/apishared-maintenance.XXXXXXXX")
game="$fixture/game"; transactions="$game/_APIShared/Workshop/Transactions"; mkdir -p -- "$transactions"
hash_file() { sha256sum -- "$1" | cut -d ' ' -f1; }
make_transaction() {
    local number=$1 item=${2:-123} id dir before after receipt_hash
    printf -v id '%032x' "$number"; dir="$transactions/$id"; mkdir -p -- "$dir/New" "$dir/Backup"
    printf 'new %s' "$number" > "$dir/New/0"; printf 'old %s' "$number" > "$dir/Backup/0"; printf 'receipt %s' "$number" > "$dir/New/1"
    before=$(hash_file "$dir/Backup/0"); after=$(hash_file "$dir/New/0"); receipt_hash=$(hash_file "$dir/New/1")
    printf 'APISHARED-WORKSHOP-2\r\nwrite\tBepInEx/plugins/Example/Example.dll\t0\t%s\t%s\r\nreceipt-write\t_APIShared/Workshop/Receipts/%s.json\t1\tmissing\t%s\r\n' "$before" "$after" "$item" "$receipt_hash" > "$dir/operations.tsv"
    hash_file "$dir/operations.tsv" | tr -d '\n' > "$dir/operations.sha256"
    printf 'applying\r\ncommitted\r\n' > "$dir/journal.log"
    date -u -d "2026-01-01 UTC + $number minutes" '+%Y-%m-%dT%H:%M:%SZ' | tr -d '\n' > "$dir/completed.utc"
}
for ((i=1;i<=100;i++)); do make_transaction "$i"; done
mkdir -p -- "$game/_APIShared/Workshop/Receipts" "$game/_APIShared/Workshop/Publishing" "$game/_APIShared/Workshop/Uploads/active"
printf preserve > "$game/_APIShared/Workshop/Receipts/123.json"; printf preserve > "$game/_APIShared/Workshop/Publishing/Example.json"; printf preserve > "$game/_APIShared/Workshop/Uploads/active/content"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain | tee "$fixture/first.log"
[[ $(find "$transactions" -mindepth 1 -maxdepth 1 -type d | wc -l) == 2 ]]
[[ $(find "$transactions" -type d -name New | wc -l) == 0 ]]
[[ $(find "$transactions" -type f -path '*/Backup/0' | wc -l) == 2 ]]
for file in Receipts/123.json Publishing/Example.json Uploads/active/content; do [[ $(cat -- "$game/_APIShared/Workshop/$file") == preserve ]]; done
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/repeat.log"
[[ ! -f $game/_APIShared/Workshop/cleanup.pending.tsv ]]
printf 'PASS: Linux 100 updates, two backups, staging removal, repeatability and protected state.\n'

# A CRLF journal with an interrupted original, then a valid committed replacement.
make_transaction 101; make_transaction 102; make_transaction 103; make_transaction 104
printf -v old '%032x' 101; printf -v repair '%032x' 102
printf 'applying\r\nbegin\t0\r\n' > "$transactions/$old/journal.log"
printf '%s\t%s\t%s\r\n' "$old" "$(hash_file "$transactions/$old/operations.tsv")" "$(hash_file "$transactions/$old/journal.log")" > "$transactions/$repair/supersedes.tsv"
hash_file "$transactions/$repair/supersedes.tsv" | tr -d '\n' > "$transactions/$repair/supersedes.sha256"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/repair.log"
[[ ! -d $transactions/$old && ! -d $transactions/$repair ]]
printf 'PASS: Linux removes a resolved predecessor before its replacement proof.\n'

# Unknown files and symlink targets are never deletion candidates.
make_transaction 105; make_transaction 106; make_transaction 107
printf -v unknown '%032x' 105; printf keep > "$transactions/$unknown/user.txt"
mkdir -p -- "$fixture/outside"; printf outside > "$fixture/outside/0"
ln -s -- "$fixture/outside" "$transactions/$unknown/RecoveryBackup"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/linked.log"
[[ -d $transactions/$unknown && $(cat -- "$fixture/outside/0") == outside ]]

# The real flock, not a test replacement, prevents competing maintenance.
exec 8>"$game/_APIShared/Workshop/apply.lock"; flock -n 8
if bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/locked.log" 2>&1; then printf 'Lock was bypassed\n' >&2; exit 1; fi
flock -u 8; exec 8>&-
printf 'PASS: Linux unknown/link preservation and real updater lock.\nEvidence retained: %s\n' "$fixture"
