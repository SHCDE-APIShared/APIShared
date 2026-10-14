param()
# Bash never applies set -e or the ERR trap to a negated command, so a bare
# '! cmd' statement asserts nothing. Shell scripts must use an explicit
# if/exit, '|| fail' or a negation inside an if/while/until condition.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$negated = '(^|;|\bthen\b|\bdo\b|\belse\b|\{|\(|&&|\|\|)\s*!\s+(?:[^;|&]|\|(?!\|))*(;|$)'
function Test-BareNegation([string]$Line) {
    $code = ($Line -replace '\[\[.*?\]\]', '[[ ]]').Trim()
    if ($code.StartsWith('#')) { return $false }
    # Conditions of if/elif/while/until legitimately negate their test.
    if ($code -match '^(if|elif|while|until)\s') { $code = $code -replace '^(if|elif|while|until)\s.*?;\s*(then|do)\b', '' }
    return $code -match $negated
}
$bad = @('! running', 'journal_has "$dir" applying; ! finished "$dir"', 'if x; then ! y; fi', 'a && ! b', 'a || ! b | c')
$good = @('! running || return 1', 'if ! a; then b; fi', 'while ! a; do b; done', 'if a && ! b && [[ -z $c ]]; then d; fi', '[[ ! -f $x ]] || exit 1', 'if running; then false; fi', '# ! comment')
foreach ($line in $bad) { if (-not (Test-BareNegation $line)) { throw "Shell lint missed a bare negation: $line" } }
foreach ($line in $good) { if (Test-BareNegation $line) { throw "Shell lint rejected a valid form: $line" } }
$findings = @()
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.sh' | Where-Object FullName -NotMatch '[\\/](bin|obj|BepInEx|\.git|\.local)[\\/]') {
    $number = 0
    foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
        $number++
        if (Test-BareNegation $line) { $findings += $file.FullName.Substring($root.Length + 1) + ':' + $number + ': ' + $line.Trim() }
    }
}
if ($findings.Count) { throw ("Bare negated shell commands are ignored by set -e and ERR traps:`n" + ($findings -join "`n")) }
Write-Host 'PASS: shell scripts contain no bare negated assertions.'
