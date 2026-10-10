param([string]$GameDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $GameDir) { throw 'GameDir is required for managed interception contract verification.' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
try {
    $contracts = @(
        @{ Type = 'EngineInterface'; Name = 'GameAction'; Static = $true; Result = 'System.Int32'; Parameters = @('Enums/GameActionCommand','System.Int32','System.Int32','System.Int32') },
        @{ Type = 'FatControler'; Name = 'NoesisGUIUpdateChecksInGame'; Static = $false; Result = 'System.Void'; Parameters = @() },
        @{ Type = 'FatControler'; Name = 'ExitApp'; Static = $false; Public = $true; Result = 'System.Void'; Parameters = @() },
        @{ Type = 'CrusaderDE.HUD_Main'; Name = 'UpdateRollover'; Static = $false; Result = 'System.Void'; Parameters = @() }
    ) + @(foreach ($name in @('ButtonEnterCreateTroop','ButtonLeaveCreateTroop','ButtonTroopPanelMouseEnter','ButtonTroopPanelMouseLeave','ButtonUnitRechargeRock')) {
        @{ Type = 'CrusaderDE.MainViewModel'; Name = $name; Static = $false; Result = 'System.Void'; Parameters = @('System.Object') }
    })
    foreach ($contract in $contracts) {
        $type = $assembly.MainModule.GetType($contract.Type)
        if ($null -eq $type) { throw "Missing type: $($contract.Type)" }
        $methods = @($type.Methods | Where-Object {
            $_.Name -ceq $contract.Name -and $_.IsStatic -eq $contract.Static -and
            $_.ReturnType.FullName -ceq $contract.Result -and
            (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq ($contract.Parameters -join '|')
        })
        if ($methods.Count -ne 1 -or -not $methods[0].HasBody) { throw "Changed interception signature/body: $($contract.Type).$($contract.Name)" }
        if ($contract.Public -and -not $methods[0].IsPublic) { throw "New direct public game contract is not public: $($contract.Type).$($contract.Name)" }
        # Private UI callbacks are invoked only through the validated detour trampoline,
        # never by a direct call compiled against a publicized assembly.
        Write-Output ("Installed interception: {0}; public={1}" -f $methods[0].FullName, $methods[0].IsPublic)
    }
    Write-Output ("PASS: all {0} managed interception signatures match the real installed game assembly." -f $contracts.Count)
} finally { $assembly.Dispose() }
