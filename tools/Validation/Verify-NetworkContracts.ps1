param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $GameDir) { throw 'Set SHCDE_GAME_DIR for networking interop verification.' }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx\plugins\000shcdese' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
$gameAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
$extenderAssembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
function Find-NetworkType($module, [string]$name) {
    $type = $module.GetType($name)
    if (-not $type) { throw "Missing type: $name" }
    return $type
}
function Assert-NetworkField($type, [string]$name, [string]$fieldType) {
    $field = $type.Fields | Where-Object Name -eq $name
    if (-not $field -or -not $field.IsPublic -or $field.FieldType.FullName -cne $fieldType) {
        throw "Changed/private networking field: $($type.FullName).$name"
    }
    Write-Output "PASS public field: $($type.FullName).$name : $fieldType"
}
function Assert-NetworkMethod($type, [string]$name, [string]$result, [string[]]$parameters) {
    $matches = @($type.Methods | Where-Object {
        $_.Name -eq $name -and $_.IsPublic -and $_.ReturnType.FullName -ceq $result -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq ($parameters -join '|')
    })
    if ($matches.Count -ne 1) { throw "Changed/private networking method: $($type.FullName).$name" }
    Write-Output "PASS public method: $($matches[0].FullName)"
}
try {
    $mp = Find-NetworkType $gameAssembly.MainModule 'Platform_Multiplayer'
    $envelope = Find-NetworkType $gameAssembly.MainModule 'Platform_Multiplayer/MPData'
    $lobby = Find-NetworkType $gameAssembly.MainModule 'Platform_Multiplayer/MPLobby'
    $member = Find-NetworkType $gameAssembly.MainModule 'Platform_Multiplayer/MPLobbyMember'
    Assert-NetworkMethod $mp 'get_Instance' 'Platform_Multiplayer' @()
    Assert-NetworkField $mp 'activeLobby' 'Platform_Multiplayer/MPLobby'
    Assert-NetworkField $lobby 'members' 'System.Collections.Generic.List`1<Platform_Multiplayer/MPLobbyMember>'
    foreach ($name in @('packetType','dataLength','dataOffset','data')) {
        $expectedType = @{packetType='System.Int16';dataLength='System.Int32';dataOffset='System.Int32';data='System.Byte[]'}[$name]
        Assert-NetworkField $envelope $name $expectedType
    }
    Assert-NetworkMethod $envelope 'ToBytes' 'System.Byte[]' @()
    Assert-NetworkMethod $envelope '.ctor' 'System.Void' @()
    Assert-NetworkField $member 'id' 'Steamworks.CSteamID'
    Assert-NetworkField $member 'SkirmishMember' 'System.Boolean'
    Assert-NetworkField $member 'dummyToBeKicked' 'System.Boolean'
    Assert-NetworkMethod $member 'IsSelf' 'System.Boolean' @()
    $network = Find-NetworkType $extenderAssembly.MainModule 'SHCDESE.API.GameNetworkAPI'
    Assert-NetworkMethod $network 'SendPacketToAllEx2' 'System.Void' @('T','System.Int16','System.Boolean','System.Boolean')
    Assert-NetworkMethod $network 'SendPacketToAllEx' 'System.Void' @('Platform_Multiplayer/MPData','System.Boolean')
    Assert-NetworkMethod $network 'SendPacketToPlayerIdEx' 'System.Void' @('System.Int32','Platform_Multiplayer/MPData')
    Assert-NetworkMethod $network 'Serialize' 'System.Byte[]' @('T')
    Assert-NetworkMethod $network 'Deserialize' 'T' @('System.Byte[]')
    Assert-NetworkMethod $network 'GetPacketEventFor' 'SHCDESE.EventAPI.R3PacketEventHook`1<T>' @()
    Assert-NetworkMethod $network 'IsNetworkedEnvironment' 'System.Boolean' @()
    $globals = Find-NetworkType $extenderAssembly.MainModule 'SHCDESE.GameGlobals.GameGlobalsManager'
    Assert-NetworkField $globals 'ChoreManagerVA' 'System.UInt64'
    $flags = Find-NetworkType $extenderAssembly.MainModule 'SHCDESE.Interop.Enums.SteamNetworkingSend'
    foreach ($entry in @(@('Reliable', 8), @('AutoRestartBrokenSession', 32))) {
        $field = $flags.Fields | Where-Object Name -eq $entry[0]
        if (-not $field -or -not $field.HasConstant -or [int]$field.Constant -ne $entry[1]) { throw ('Steam send flag changed: ' + $entry[0]) }
    }
} finally {
    $gameAssembly.Dispose()
    $extenderAssembly.Dispose()
}
Write-Output 'PASS: networking accesses checked against real installed game/Extender visibility and signatures.'
