#!/usr/bin/env bash
# Native Linux protocol/retention fixture. Does not simulate flock or Wine.
set -euo pipefail
for tool in flock sha256sum stat date sync; do command -v "$tool" >/dev/null || { printf 'SKIP: Linux maintenance fixture requires %s\n' "$tool"; exit 77; }; done
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/apishared-maintenance.XXXXXXXX")
game="$fixture/game"; state="$game/_APIShared/Workshop"
transactions="$state/Transactions"; short="$game/_APIShared/T"; mkdir -p -- "$transactions" "$short"
hash_file() { sha256sum -- "$1" | cut -d ' ' -f1; }
make_transaction() {
    local number=$1 item=${2:-123} root=${3:-$transactions} id dir before after receipt_hash
    printf -v id '%032x' "$number"; dir="$root/$id"; mkdir -p -- "$dir/New" "$dir/Backup"
    printf 'new %s' "$number" > "$dir/New/0"; printf 'old %s' "$number" > "$dir/Backup/0"; printf 'receipt %s' "$number" > "$dir/New/1"
    before=$(hash_file "$dir/Backup/0"); after=$(hash_file "$dir/New/0"); receipt_hash=$(hash_file "$dir/New/1")
    printf 'APISHARED-WORKSHOP-2\r\nwrite\tBepInEx/plugins/Example/Example.dll\t0\t%s\t%s\r\nreceipt-write\t_APIShared/Workshop/Receipts/%s.json\t1\tmissing\t%s\r\n' "$before" "$after" "$item" "$receipt_hash" > "$dir/operations.tsv"
    hash_file "$dir/operations.tsv" | tr -d '\n' > "$dir/operations.sha256"
    printf 'applying\r\ncommitted\r\n' > "$dir/journal.log"
    date -u -d "2026-01-01 UTC + $number minutes" '+%Y-%m-%dT%H:%M:%SZ' | tr -d '\n' > "$dir/completed.utc"
}
for ((i=1;i<=100;i++)); do
    if ((i % 2 == 0)); then make_transaction "$i" 123 "$short"; else make_transaction "$i"; fi
done
mkdir -p -- "$game/_APIShared/Workshop/Receipts" "$game/_APIShared/Workshop/Publishing" "$game/_APIShared/Workshop/Uploads/active"
printf preserve > "$game/_APIShared/Workshop/Receipts/123.json"; printf preserve > "$game/_APIShared/Workshop/Publishing/Example.json"; printf preserve > "$game/_APIShared/Workshop/Uploads/active/content"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain | tee "$fixture/first.log"
[[ $(find "$transactions" "$short" -mindepth 1 -maxdepth 1 -type d | wc -l) == 2 ]]
[[ $(find "$transactions" "$short" -type d -name New | wc -l) == 0 ]]
[[ -f $transactions/$(printf '%032x' 99)/Backup/0 && -f $short/$(printf '%032x' 100)/Backup/0 ]]
for file in Receipts/123.json Publishing/Example.json Uploads/active/content; do [[ $(cat -- "$game/_APIShared/Workshop/$file") == preserve ]]; done
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/repeat.log"
[[ ! -f $game/_APIShared/Workshop/cleanup.pending.tsv ]]
printf 'PASS: Linux 100 updates across both roots, two backups, staging removal, repeatability and protected state.\n'

# A CRLF journal with an interrupted original, then a valid committed replacement.
make_transaction 101; make_transaction 102 123 "$short"; make_transaction 103; make_transaction 104
printf -v old '%032x' 101; printf -v repair '%032x' 102
printf 'applying\r\nbegin\t0\r\n' > "$transactions/$old/journal.log"
printf '%s\t%s\t%s\r\n' "$old" "$(hash_file "$transactions/$old/operations.tsv")" "$(hash_file "$transactions/$old/journal.log")" > "$short/$repair/supersedes.tsv"
hash_file "$short/$repair/supersedes.tsv" | tr -d '\n' > "$short/$repair/supersedes.sha256"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/repair.log"
[[ ! -d $transactions/$old && ! -d $short/$repair ]]
printf 'PASS: Linux removes a legacy predecessor before its short-root replacement proof.\n'

write_pending() {
    local version=$1 payload=$2
    printf 'APISHARED-WORKSHOP-CLEANUP-%s\n%s\n%s' "$version" "$(printf '%s' "$payload" | sha256sum | cut -d ' ' -f1)" "$payload" > "$state/cleanup.pending.tsv"
}
# A v1 action addresses legacy storage only. Identical bytes at the short path
# cannot authorize its deletion. A v2 action resumes against its pinned root.
printf -v pending_id '%032x' 400; mkdir -p -- "$short/$pending_id"
printf partial > "$short/$pending_id/operations.tsv"
printf -v pending_file 'F\t%s\toperations.tsv\t%s\n' "$pending_id" "$(hash_file "$short/$pending_id/operations.tsv")"
printf -v action 'D\t%s\tall\n' "$pending_id"; write_pending 1 "$action$pending_file"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/legacy-resume.log"
[[ -f $short/$pending_id/operations.tsv && ! -f $state/cleanup.pending.tsv ]]
printf -v action 'D\t%s\tall\tshort\n' "$pending_id"; write_pending 2 "$action$pending_file"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/short-resume.log"
[[ ! -d $short/$pending_id && ! -f $state/cleanup.pending.tsv ]]

make_transaction 401; make_transaction 401 123 "$short"
printf -v duplicate '%032x' 401
if bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/duplicate.log" 2>&1; then
    printf 'Duplicate ID did not block maintenance\n' >&2; exit 1
fi
[[ -f $transactions/$duplicate/journal.log && -f $short/$duplicate/journal.log ]]
grep -q 'Duplicate transaction ID' "$fixture/duplicate.log"
mv -- "$short/$duplicate" "$fixture/duplicate-retained"
printf 'PASS: Linux root-pinned pending cleanup and duplicate-ID preservation.\n'

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
printf 'PASS: Linux unknown/link preservation and real updater lock.\n'

# A crash can leave a pending list after its last transaction was removed.
game="$fixture/empty/game"; state="$game/_APIShared/Workshop"
mkdir -p -- "$state"
printf -v action 'D\t%s\tall\tshort\n' "$pending_id"; write_pending 2 "$action$pending_file"
bash "$root/Workshop/workshop-updater.sh" "$game" --maintain > "$fixture/empty-resume.log"
[[ ! -e $state/cleanup.pending.tsv && ! -e $state/Transactions && ! -e $game/_APIShared/T ]]
printf 'PASS: Linux clears completed pending cleanup with neither root present.\nEvidence retained: %s\n' "$fixture"
