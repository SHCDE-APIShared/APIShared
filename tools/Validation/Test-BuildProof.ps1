$ErrorActionPreference='Stop'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('ApiSharedProof-'+[Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp) | Out-Null
function Check([bool]$value,[string]$message) { if(-not$value){throw $message} }
function Write-Fixture([string]$Path,[string]$Text) { [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null; [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false)) }
function Commit-Fixture([string]$Directory,[string]$Message) {
    & git -C $Directory add -A; if($LASTEXITCODE){throw 'Fixture staging failed'}
    & git -C $Directory -c user.name=Fixture -c user.email=fixture@example.invalid commit -m $Message | Out-Null; if($LASTEXITCODE){throw 'Fixture commit failed'}
}
try {
    $proofRoot=Join-Path $temp 'proof'
    foreach($dir in @('src','Properties','Patches','tools')){[IO.Directory]::CreateDirectory((Join-Path $proofRoot $dir)) | Out-Null}
    Write-Fixture (Join-Path $proofRoot 'src/example.cs') 'actual input'
    Write-Fixture (Join-Path $proofRoot 'APIShared.csproj') '<Project />'
    Write-Fixture (Join-Path $proofRoot 'info.json') '{}'
    Write-Fixture (Join-Path $proofRoot '.gitignore') ".local/`n"
    Write-Fixture (Join-Path $proofRoot 'tools/Build.ps1') 'build fixture'
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../BuildProof.ps1') -Destination (Join-Path $proofRoot 'tools/BuildProof.ps1')
    $compiler=Join-Path $temp 'compiler'; Write-Fixture $compiler 'external compiler boundary'
    $dll=Join-Path $proofRoot 'BepInEx/plugins/APIShared_Serp/APIShared.dll'; Write-Fixture $dll 'external binary boundary'
    & git init --initial-branch=main $proofRoot | Out-Null; Commit-Fixture $proofRoot inputs
    . (Join-Path $proofRoot 'tools/BuildProof.ps1')
    $failed=$false; try{$null=Assert-ApiBuildProof $proofRoot}catch{$failed=$true}; Check $failed 'Missing evidence accepted'
    $state=Get-ApiInputState $proofRoot '' '' $compiler
    Write-ApiBuildProof $proofRoot '' '' $compiler $state
    $null=Assert-ApiBuildProof $proofRoot
    $null=Assert-ApiBuildProof $proofRoot -Release
    Write-Fixture (Join-Path $proofRoot 'src/example.cs') 'changed input'
    $failed=$false; try{$null=Assert-ApiBuildProof $proofRoot}catch{$failed=$true}; Check $failed 'Stale package accepted'
    $state=Get-ApiInputState $proofRoot '' '' $compiler; Write-ApiBuildProof $proofRoot '' '' $compiler $state
    $null=Assert-ApiBuildProof $proofRoot
    $failed=$false; try{$null=Assert-ApiBuildProof $proofRoot -Release}catch{$failed=$true}; Check $failed 'Uncommitted source accepted for release'
    $crossScript=Join-Path $temp 'cross-shell.ps1'
    Write-Fixture $crossScript 'param($Root,$Compiler) . (Join-Path $Root "tools/BuildProof.ps1"); (Get-ApiInputState $Root "" "" $Compiler).Fingerprint'
    $cross=@(& powershell.exe -NoProfile -File $crossScript $proofRoot $compiler)
    Check ($LASTEXITCODE -eq 0 -and ($cross -join '').Trim() -eq $state.Fingerprint) 'Fingerprint differs across PowerShell versions'
    Write-Fixture $dll 'tampered binary'
    $failed=$false; try{$null=Assert-ApiBuildProof $proofRoot}catch{$failed=$true}; Check $failed 'Tampered package accepted'
    Write-Host 'PASS: missing/stale build evidence, package tampering, dirty release rejection and cross-shell fingerprint.'
} finally { Write-Host ("Build proof fixtures: " + $temp) }
