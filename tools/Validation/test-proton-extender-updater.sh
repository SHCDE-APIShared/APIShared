#!/usr/bin/env bash
# Runs the actual independent Extender updater against owned Linux files/processes.
set -euo pipefail
[[ $(uname -s) == Linux ]] || { printf 'SKIP: native Linux required.\n'; exit 77; }
for tool in flock sha256sum realpath bash sed grep cp mv find mkfifo; do command -v "$tool" >/dev/null || { printf 'SKIP: missing %s\n' "$tool"; exit 77; }; done
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
updater=${APISHARED_EXTENDER_UPDATER_SH:-$root/proton-fix/Assets/extender-updater.sh}
fixture=$(mktemp -d "${TMPDIR:-/tmp}/api-proton-se.XXXXXXXX")
fakebin="$fixture/bin"; mkdir -p -- "$fakebin"
real_mv=$(command -v mv)
real_find=$(command -v find)
printf '#!/usr/bin/env bash\nprintf restart >> "$PROTON_FIX_RESTART_LOG"\n' > "$fakebin/xdg-open"
cat > "$fakebin/mv" <<'WRAPPER'
#!/usr/bin/env bash
if [[ ${PROTON_FIX_FAIL_COPY:-} == yes && ${*: -1} == */Sample.dll ]]; then printf 'owned fixture copy failure\n' >&2; exit 9; fi
"$PROTON_FIX_REAL_MV" "$@" || exit $?
if [[ ${PROTON_FIX_INJECT_SOURCE:-} == yes && ${*: -1} == */Sample.dll ]]; then printf unexpected > "$PROTON_FIX_STAGING/Unlisted.txt"; fi
WRAPPER
cat > "$fakebin/find" <<'WRAPPER'
#!/usr/bin/env bash
if [[ ${PROTON_FIX_FAIL_ENUMERATION:-} == yes ]]; then printf 'owned enumeration failure\n' >&2; exit 42; fi
exec "$PROTON_FIX_REAL_FIND" "$@"
WRAPPER
chmod +x "$fakebin/xdg-open" "$fakebin/mv" "$fakebin/find"
export PATH="$fakebin:$PATH" PROTON_FIX_REAL_MV="$real_mv" PROTON_FIX_REAL_FIND="$real_find" PROTON_FIX_RESTART_LOG="$fixture/restart.log"
unset SCDEModManagerLaunchId
game_pid= updater_pid= wrapper_pid=
stop_owned() {
    if [[ -n $updater_pid ]] && kill -0 "$updater_pid" 2>/dev/null; then kill "$updater_pid"; wait "$updater_pid" 2>/dev/null || true; fi
    if [[ -n $game_pid ]] && kill -0 "$game_pid" 2>/dev/null; then kill "$game_pid"; wait "$game_pid" 2>/dev/null || true; fi
    if [[ -n $wrapper_pid ]] && kill -0 "$wrapper_pid" 2>/dev/null; then kill "$wrapper_pid"; wait "$wrapper_pid" 2>/dev/null || true; fi
    updater_pid=; game_pid=; wrapper_pid=
}
trap stop_owned EXIT
fail() { printf 'FAIL: %s\nEvidence retained: %s\n' "$1" "$fixture" >&2; exit 1; }
hash_file() { sha256sum -- "$1" | cut -d ' ' -f1; }
new_case() {
    stop_owned
    case_dir="$fixture/$1"; game="$case_dir/game"; staging="$game/_SE/.staging"
    run="$game/BepInEx/cache/APIShared.ProtonWorkshopFix/r-owned"; request="$run/request.tsv"
    mkdir -p -- "$staging/BepInEx/plugins/Sample" "$staging/_SE" "$game/BepInEx/plugins/Sample" "$run"
    printf old > "$game/BepInEx/plugins/Sample/Sample.dll"
    printf obsolete > "$game/BepInEx/plugins/Sample/Obsolete.dll"
    printf old-metadata > "$game/_SE/Sample.json"
    printf new > "$staging/BepInEx/plugins/Sample/Sample.dll"
    printf new-metadata > "$staging/_SE/Sample.json"
    printf '%s\r\n' 'S:\owned\BepInEx\plugins\Sample\Obsolete.dll' > "$staging/delete_list.txt"
    exe="OwnedProtonFixture-$$.exe"; printf owned > "$game/$exe"
    write_request
    start_game
}
write_request() {
    {
        printf 'APISHARED-SE-PROTON-1\ngame\t%s\nstaging\t%s\nexe\t%s\n' "$game" "$staging" "$exe"
        if [[ -f $staging/delete_list.txt ]]; then printf 'manifest\tdelete_list.txt\t%s\n' "$(hash_file "$staging/delete_list.txt")"; fi
        printf 'write\tBepInEx/plugins/Sample/Sample.dll\t%s\nwrite\t_SE/Sample.json\t%s\n' \
            "$(hash_file "$staging/BepInEx/plugins/Sample/Sample.dll")" "$(hash_file "$staging/_SE/Sample.json")"
        if [[ -s $staging/delete_list.txt ]]; then printf 'delete\tBepInEx/plugins/Sample/Obsolete.dll\n'; fi
    } > "$request"
}
run_sync() { code=0; bash "$updater" "$request" "$(hash_file "$request")" "$@" > "$case_dir/output.log" 2>&1 || code=$?; }
unchanged() {
    [[ $(cat -- "$game/BepInEx/plugins/Sample/Sample.dll") == old && $(cat -- "$game/_SE/Sample.json") == old-metadata && -d $staging ]] || fail 'refused/cancelled updater changed installed files or discarded staging'
}
start_game() { bash -c 'exec -a "$1" sleep 30' fixture "$exe" & game_pid=$!; }
# Steam's reaper/launch wrapper shape: the game path is a later argument and the process
# outlives the game while it waits for descendants. '; :' keeps Bash from exec'ing sleep.
start_wrapper() { bash -c 'sleep 60; :' reaper SteamLaunch AppId=3024040 -- "$game/$exe" >/dev/null 2>&1 & wrapper_pid=$!; }
# Wine's shape: loader as argv[0], the Windows program path as argv[1].
start_wine_game() {
    local program="Z:\\owned\\$exe"
    printf 'sleep 30\n' > "$case_dir/$program"
    (cd -- "$case_dir" && exec -a "$case_dir/wine64" bash "$program") >/dev/null 2>&1 & game_pid=$!
}
wait_ready() {
    for ((n=0;n<100;n++)); do
        [[ ! -f $run/ready.flag ]] || return 0
        kill -0 "$updater_pid" 2>/dev/null || fail 'updater exited before publishing readiness'
        sleep 0.05
    done
    fail 'readiness timeout'
}
run_after_exit() {
    bash "$updater" "$request" "$(hash_file "$request")" > "$case_dir/output.log" 2>&1 & updater_pid=$!
    wait_ready
    kill "$game_pid"; wait "$game_pid" 2>/dev/null || true; game_pid=
    code=0; wait "$updater_pid" || code=$?; updater_pid=
}

new_case probe
run_sync --probe
[[ $code == 0 && ! -e $run/ready.flag && ! -e $run/journal.log ]] || fail 'probe did not remain read-only'
unchanged
printf 'PASS: complete Linux preflight does not deploy or publish readiness.\n'

new_case absent-manifest
rm -- "$staging/delete_list.txt"; write_request
run_sync --probe
[[ $code == 0 ]] || fail 'absent manifest was not represented correctly'
: > "$staging/delete_list.txt"
run_sync --probe
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'an unlisted empty manifest was silently accepted'
unchanged
printf 'PASS: missing and newly added empty manifests remain distinct.\n'

new_case empty-manifest
: > "$staging/delete_list.txt"; write_request
run_sync --probe
[[ $code == 0 ]] || fail 'an explicitly hashed empty manifest was rejected'
unchanged
printf 'PASS: an explicitly hashed empty manifest is valid.\n'

new_case success
run_after_exit
[[ $code == 0 && $(cat -- "$game/BepInEx/plugins/Sample/Sample.dll") == new && $(cat -- "$game/_SE/Sample.json") == new-metadata ]] || fail 'normal installation did not commit'
[[ ! -e $game/BepInEx/plugins/Sample/Obsolete.dll && ! -d $staging ]] || fail 'obsolete file/staging retained after success'
payload_line=$(grep -n 'updated BepInEx/plugins/Sample/Sample.dll' "$run/journal.log" | cut -d: -f1)
metadata_line=$(grep -n 'updated _SE/Sample.json' "$run/journal.log" | cut -d: -f1)
[[ $payload_line -lt $metadata_line ]] || fail 'metadata preceded payload'
grep -q ' committed$' "$run/journal.log" || fail 'commit not journaled'
printf 'PASS: payload, deletion, metadata-last and completion; updater does not match itself as a game.\n'

# Regression: Steam's wrappers name the game as a later argument and wait for every
# descendant, including this helper. Counting them as the game deadlocked until the
# 120-second budget. Only Wine's program position may count.
new_case steam-wrappers
kill "$game_pid"; wait "$game_pid" 2>/dev/null || true; game_pid=
start_wrapper
run_sync --probe
[[ $code != 0 ]] && grep -q 'not visible' "$case_dir/output.log" || fail 'a Steam wrapper alone was treated as the running game'
start_wine_game
run_sync --probe
[[ $code == 0 ]] || fail 'the Wine loader layout (program as argv[1]) was not recognized'
run_after_exit
[[ $code == 0 && $(cat -- "$game/BepInEx/plugins/Sample/Sample.dll") == new ]] || fail 'a live Steam wrapper delayed or prevented installation after game exit'
kill -0 "$wrapper_pid" 2>/dev/null || fail 'the wrapper fixture ended early, so the case proves nothing'
printf 'PASS: Steam wrappers are not the game; the Wine program position is.\n'

new_case changed-source
printf altered > "$staging/BepInEx/plugins/Sample/Sample.dll"
run_sync
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'changed source authorized handoff'
unchanged
printf 'PASS: changed staged bytes are refused before readiness.\n'

new_case traversal
printf 'delete\t../outside\n' >> "$request"
run_sync
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'escaping deletion accepted'
unchanged
printf 'PASS: escaping deletion refused without deployment.\n'

# These two cases stay inside the root after realpath. They specifically catch
# ignored helper failures inside command substitutions (Bash clears errexit there).
new_case protected-cache-target
printf keep > "$game/BepInEx/cache/keep.txt"
printf 'delete\tBepInEx/cache/keep.txt\n' >> "$request"
run_sync --probe
[[ $code != 0 && ! -f $run/ready.flag && $(cat -- "$game/BepInEx/cache/keep.txt") == keep ]] || fail 'protected cache target passed preflight'
unchanged
printf 'PASS: protected target refusal propagates out of command substitution.\n'

new_case linked-in-root-target
mv -- "$game/BepInEx/plugins/Sample" "$game/BepInEx/plugins/Actual"
ln -s "$game/BepInEx/plugins/Actual" "$game/BepInEx/plugins/Sample"
run_sync --probe
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'in-root linked target passed preflight'
unchanged
printf 'PASS: in-root symlink refusal propagates out of command substitution.\n'

new_case linked-target
mkdir -p -- "$case_dir/outside"
mv -- "$game/BepInEx/plugins/Sample" "$case_dir/original"
ln -s "$case_dir/outside" "$game/BepInEx/plugins/Sample"
run_sync
[[ $code != 0 && ! -f $run/ready.flag && -z $(ls -A -- "$case_dir/outside") ]] || fail 'linked target was followed'
printf 'PASS: linked target refused before readiness.\n'

new_case nonregular-source
mkfifo "$staging/Unknown.fifo"
run_sync
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'a staged FIFO was accepted or published readiness'
unchanged
printf 'PASS: unexpected staged FIFO is refused without opening it.\n'

new_case failed-enumeration
export PROTON_FIX_FAIL_ENUMERATION=yes
run_sync
unset PROTON_FIX_FAIL_ENUMERATION
[[ $code != 0 && ! -f $run/ready.flag ]] || fail 'failed enumeration looked like a complete inventory'
unchanged
grep -q 'Source inventory enumeration failed' "$run/journal.log" || fail 'enumeration failure was not diagnosed'
printf 'PASS: failed find enumeration cannot authorize deployment.\n'

new_case cancel
bash "$updater" "$request" "$(hash_file "$request")" > "$case_dir/output.log" 2>&1 & updater_pid=$!
wait_ready
printf cancelled > "$run/cancel.flag"
code=0; wait "$updater_pid" || code=$?; updater_pid=
[[ $code == 2 ]] || fail 'cancellation did not stop the waiting updater'
unchanged
printf 'PASS: confirmed helper cancels while the owned game remains running.\n'

# Only the private fixture copy shortens the unchanged production wait boundary.
new_case wait-timeout
short_updater="$case_dir/short-updater.sh"
sed 's/step=waiting; wait_budget=120/step=waiting; wait_budget=0/' "$updater" > "$short_updater"
code=0; bash "$short_updater" "$request" "$(hash_file "$request")" > "$case_dir/output.log" 2>&1 || code=$?
[[ $code != 0 ]] || fail 'live game timeout was ignored'
unchanged
grep -q 'timeout waiting for game exit' "$run/journal.log" || fail 'wait timeout not journaled'
printf 'PASS: wait-budget exhaustion leaves payload, metadata and staging untouched.\n'

new_case copy-failure
export PROTON_FIX_FAIL_COPY=yes
run_after_exit
unset PROTON_FIX_FAIL_COPY
[[ $code != 0 && $(cat -- "$game/_SE/Sample.json") == old-metadata && -d $staging ]] || fail 'payload failure committed metadata or discarded staging'
grep -q 'failure step=payload' "$run/journal.log" || fail 'payload failure was not journaled'
printf 'PASS: payload copy failure preserves old metadata and staged evidence.\n'

new_case added-source
export PROTON_FIX_INJECT_SOURCE=yes PROTON_FIX_STAGING="$staging"
run_after_exit
unset PROTON_FIX_INJECT_SOURCE PROTON_FIX_STAGING
[[ $code != 0 && -f $staging/Unlisted.txt && $(cat -- "$game/_SE/Sample.json") == old-metadata ]] || fail 'unlisted staged file was erased or metadata committed despite drift'
grep -q 'Unknown staged file; staging retained: Unlisted.txt' "$run/journal.log" || fail 'source drift was not diagnosed'
grep -q 'failure step=inventory-before-metadata' "$run/journal.log" || fail 'source drift was not caught before metadata'
printf 'PASS: newly added staged file is retained and prevents metadata commit.\n'
printf 'Linux Extender updater evidence retained: %s\n' "$fixture"
