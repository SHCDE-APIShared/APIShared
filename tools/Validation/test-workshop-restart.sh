#!/usr/bin/env bash
# Exercises the production restart function with owned fake desktop launchers.
# It never starts Steam or a game and needs no Wine, flock or system launcher.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
fixture=$(mktemp -d "${TMPDIR:-/tmp}/apishared-restart.XXXXXXXX")
definition=$(sed -n '/^restart_game() {$/,/^}$/p' "$root/Workshop/workshop-updater.sh")
[[ -n $definition ]] || { printf 'Missing production restart function.\n' >&2; exit 1; }
eval "$definition"
record() { printf '%s\n' "$1" >> "$journal"; }
fail() { printf 'FAIL: %s\nEvidence: %s\n' "$1" "$fixture" >&2; exit 1; }
make_tool() {
    cat > "$1" <<'SH'
#!/bin/bash
if { : >&9; } 2>/dev/null; then printf 'inherited\n' > "$APISHARED_RESTART_FIXTURE/fd-leak"; fi
printf '%s' "${0##*/}" >> "$APISHARED_RESTART_FIXTURE/calls"
printf '\t%s' "$@" >> "$APISHARED_RESTART_FIXTURE/calls"
printf '\n' >> "$APISHARED_RESTART_FIXTURE/calls"
case "${0##*/}" in xdg-open) exit "$FIXTURE_XDG_EXIT";; steam) exit "$FIXTURE_STEAM_EXIT";; esac
SH
    chmod +x "$1"
}
for case_name in desktop steam-only desktop-failed unavailable both-failed steam-failed; do
    case_dir="$fixture/$case_name"; bin="$case_dir/bin"; mkdir -p "$bin"
    journal="$case_dir/journal.log"; printf 'committed\n' > "$journal"
    export APISHARED_RESTART_FIXTURE="$case_dir" FIXTURE_XDG_EXIT=0 FIXTURE_STEAM_EXIT=0
    if [[ $case_name == desktop || $case_name == desktop-failed || $case_name == both-failed ]]; then make_tool "$bin/xdg-open"; fi
    if [[ $case_name != unavailable ]]; then make_tool "$bin/steam"; fi
    if [[ $case_name == desktop-failed || $case_name == both-failed ]]; then FIXTURE_XDG_EXIT=4; fi
    if [[ $case_name == both-failed || $case_name == steam-failed ]]; then FIXTURE_STEAM_EXIT=5; fi
    (
        # A child must neither retain the updater's file descriptor nor run its
        # inherited EXIT cleanup. Assertions run while this owner is still live.
        trap 'printf child-trap > "$case_dir/trap-leak"' EXIT
        exec 9>"$case_dir/owner.lock"
        PATH="$bin"
        restart_game
        child=$!; wait "$child"
        [[ ! -f $case_dir/fd-leak && ! -f $case_dir/trap-leak ]] || fail 'restart retained updater ownership or ran its cleanup trap'
        trap - EXIT
    )
    calls=''; [[ ! -f $case_dir/calls ]] || calls=$(<"$case_dir/calls")
    case "$case_name" in
        desktop) [[ $calls == $'xdg-open\tsteam://rungameid/3024040' ]] || fail 'desktop launch was not preferred';;
        desktop-failed|both-failed) [[ $calls == $'xdg-open\tsteam://rungameid/3024040\nsteam\t-applaunch\t3024040' ]] || fail 'failed desktop launch did not fall back to Steam';;
        steam-only|steam-failed) [[ $calls == $'steam\t-applaunch\t3024040' ]] || fail 'missing desktop launcher did not use Steam';;
        unavailable) [[ -z $calls ]] || fail 'a real/unexpected launcher was invoked';;
    esac
    text=$(<"$journal")
    [[ $text == committed* && $text != *failure* ]] || fail 'restart failure changed the committed outcome'
    if [[ $case_name == unavailable || $case_name == both-failed || $case_name == steam-failed ]]; then
        [[ $text == *'start through Steam manually'* ]] || fail 'manual restart diagnostic missing'
    fi
    printf 'PASS: production restart %s; no inherited updater ownership or rollback.\n' "$case_name"
done
printf 'Restart fixture evidence retained: %s\n' "$fixture"
