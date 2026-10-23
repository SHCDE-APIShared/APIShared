param([string]$GameDir, [string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../WorkshopPackage.ps1')
if (-not $GameDir) { $GameDir = $env:SHCDE_GAME_DIR }
if (-not $ExtenderDir) { $ExtenderDir = Join-Path $GameDir 'BepInEx\plugins\000shcdese' }
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx\core\Mono.Cecil.dll')))
function Assert-WorkshopMethod($Assembly, [string]$Type, [string]$Name, [string]$Result, [string[]]$Parameters, [bool]$Public, [bool]$Static) {
    $definition = $Assembly.MainModule.GetType($Type)
    if (-not $definition) { throw "Missing installed type: $Type" }
    $methods = @($definition.Methods | Where-Object {
        $_.Name -ceq $Name -and $_.ReturnType.FullName -ceq $Result -and $_.IsPublic -eq $Public -and $_.IsStatic -eq $Static -and
        (($_.Parameters | ForEach-Object { $_.ParameterType.FullName }) -join '|') -ceq ($Parameters -join '|')
    })
    if ($methods.Count -ne 1) { throw "Changed installed Workshop contract: $Type.$Name" }
    Write-Host "Verified installed contract: $Type.$Name ($Result); public=$Public, static=$Static"
}
$game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data\Managed\Assembly-CSharp.dll'))
$extender = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'SHCDESE.dll'))
$zip = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ExtenderDir 'ICSharpCode.SharpZipLib.dll'))
$steam = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir 'Stronghold Crusader Definitive Edition_Data/Managed/com.rlabrecque.steamworks.net.dll'))
try {
    # Installation depends on actual contracts, never a complete SE DLL hash or release list.
    # WorkshopHookTests also exercise the runtime's dry transformation and compilation.
    Assert-WorkshopMethod $game 'Platform_Workshop' 'get_Instance' 'Platform_Workshop' @() $true $true
    Assert-WorkshopMethod $game 'Platform_Workshop' 'GetListOfSubscribedItemsPaths' 'System.Collections.Generic.List`1<System.String>' @() $true $false
    $map = 'SHCDESE.API.Components.Archive.MapArchive'
    Assert-WorkshopMethod $extender 'SHCDESE.API.Components.Archive.MapModManager' 'TryStageModUpdate' 'System.Boolean' @('System.String',$map) $false $false
    $staging = $extender.MainModule.GetType('SHCDESE.API.Components.Archive.MapModManager').Methods | Where-Object Name -CEQ 'TryStageModUpdate'
    if (-not $staging.HasBody -or -not @($staging.Body.Instructions | Where-Object { $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ret }).Count) {
        throw 'Workshop staging method requires a managed body with return sites.'
    }
    Assert-WorkshopMethod $extender $map '.ctor' 'System.Void' @('System.String') $true $false
    Assert-WorkshopMethod $extender $map 'TryReadTextFile' 'System.String' @('System.String','System.Boolean') $true $false
    Assert-WorkshopMethod $extender $map 'get_Info' 'SHCDESE.API.Components.ModManager.ModInfo' @() $true $false
    Assert-WorkshopMethod $extender $map 'get_Archive' 'ICSharpCode.SharpZipLib.Zip.ZipFile' @() $true $false
    foreach ($property in @('GUID','Version')) {
        Assert-WorkshopMethod $extender 'SHCDESE.API.Components.ModManager.ModInfo' ('get_' + $property) 'System.String' @() $true $false
    }
    foreach ($contract in @(@('get_Name','System.String'), @('get_IsFile','System.Boolean'))) {
        Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipEntry' $contract[0] $contract[1] @() $true $false
    }
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' 'GetEnumerator' 'System.Collections.IEnumerator' @() $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' '.ctor' 'System.Void' @('System.IO.Stream') $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' 'set_IsStreamOwner' 'System.Void' @('System.Boolean') $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' 'GetEntry' 'ICSharpCode.SharpZipLib.Zip.ZipEntry' @('System.String') $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' 'GetInputStream' 'System.IO.Stream' @('ICSharpCode.SharpZipLib.Zip.ZipEntry') $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipFile' 'Close' 'System.Void' @() $true $false
    foreach ($contract in @(@('Init','System.Boolean'),@('RunCallbacks','System.Void'),@('Shutdown','System.Void'))) {
        Assert-WorkshopMethod $steam 'Steamworks.SteamAPI' $contract[0] $contract[1] @() $true $true
    }
    Assert-WorkshopMethod $steam 'Steamworks.SteamUser' 'BLoggedOn' 'System.Boolean' @() $true $true
    Assert-WorkshopMethod $steam 'Steamworks.SteamFriends' 'GetPersonaName' 'System.String' @() $true $true
    $valid = $extender.MainModule.GetType($map).Fields | Where-Object Name -eq 'IsValid'
    if (-not $valid.IsPublic -or $valid.FieldType.FullName -cne 'System.Boolean') { throw 'MapArchive.IsValid contract changed.' }
    Assert-WorkshopMethod $extender 'SHCDESE.API.LowLevel.CrusaderLibrary' 'add_LibraryLoaded' 'System.Void' @('SHCDESE.API.LowLevel.CrusaderLibrary/OnLibraryLoadedDelegate') $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.API.GameXAMLManagerAPI' 'RegisterLobbyModSettings' 'System.Void' @('BepInEx.BaseUnityPlugin','System.String','System.Object','System.String') $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.API.GameXAMLManagerAPI' 'RegisterLobbyModSettings' 'System.Void' @('BepInEx.BaseUnityPlugin','System.String','System.Object','System.String','System.Boolean') $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.API.GameAssetManagerAPI' 'get_Instance' 'SHCDESE.API.GameAssetManagerAPI' @() $true $true
    Assert-WorkshopMethod $extender 'SHCDESE.API.GameAssetManagerAPI' 'GetModifiedFilePath' 'System.Boolean' @('System.String','System.String&') $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipEntry' 'get_IsDirectory' 'System.Boolean' @() $true $false
    Assert-WorkshopMethod $zip 'ICSharpCode.SharpZipLib.Zip.ZipEntry' 'get_ExternalFileAttributes' 'System.Int32' @() $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.API.Components.ModManager.GameAssetModManager' 'GetRegisteredAssetDirectories' 'System.Collections.Generic.IEnumerable`1<System.Collections.Generic.KeyValuePair`2<SHCDESE.API.Components.ModManager.ModInfo,System.String>>' @() $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.API.Components.ModManager.GameAssetModManager' 'TryGetRegisteredDirectory' 'System.Boolean' @('System.String','System.String&') $true $false
    Assert-WorkshopMethod $extender 'SHCDESE.UI.ToolTipResolutionScale' 'SetEnabled' 'System.Void' @('Noesis.DependencyObject','System.Boolean') $true $true
    Assert-WorkshopMethod $steam 'Steamworks.CSteamID' 'GetAccountID' 'Steamworks.AccountID_t' @() $true $false
    foreach ($contract in @(
        @('SteamUGCDetails_t','m_eResult','m_nConsumerAppID','m_ulSteamIDOwner','m_nPublishedFileId','m_unNumChildren'),
        @('SteamUGCQueryCompleted_t','m_eResult','m_unNumResultsReturned','m_unTotalMatchingResults'),
        @('CreateItemResult_t','m_eResult','m_nPublishedFileId','m_bUserNeedsToAcceptWorkshopLegalAgreement'),
        @('SubmitItemUpdateResult_t','m_eResult','m_bUserNeedsToAcceptWorkshopLegalAgreement'),
        @('AddUGCDependencyResult_t','m_eResult'),@('RemoveUGCDependencyResult_t','m_eResult'),
        @('SteamAPICall_t','m_SteamAPICall'),@('PublishedFileId_t','m_PublishedFileId'),@('AppId_t','m_AppId'),@('UGCQueryHandle_t','m_UGCQueryHandle'),@('UGCUpdateHandle_t','m_UGCUpdateHandle')
    )) {
        $type=$steam.MainModule.GetType('Steamworks.'+$contract[0])
        foreach ($name in $contract[1..($contract.Length-1)]) { $field=@($type.Fields | Where-Object { $_.Name -ceq $name -and $_.IsPublic }); if ($field.Count -ne 1) { throw ('Changed Steam field: '+$contract[0]+'.'+$name) }; Write-Host ('Verified Steam field: '+$field[0].FullName) }
    }
    Assert-WorkshopMethod $steam 'Steamworks.SteamUGCDetails_t' 'get_m_rgchTitle' 'System.String' @() $true $false
    $manager = $game.MainModule.GetType('SteamManager')
    foreach ($field in @(@('s_instance','SteamManager',$true),@('m_bInitialized','System.Boolean',$false))) {
        $member=@($manager.Fields | Where-Object { $_.Name -ceq $field[0] -and $_.FieldType.FullName -ceq $field[1] -and $_.IsStatic -eq $field[2] })
        if ($member.Count -ne 1) { throw 'SteamManager reflective contract changed.' }
        Write-Host ('Verified encapsulated SteamManager reflection: '+$field[0])
    }
    # Statically bound Steam calls/constants are checked by compiling the selected
    # plugin and publisher against these real assemblies. A source-word scan would
    # duplicate that check and wrongly reject harmless overloads or comments.
    # Keep the explicit reflective/interop signatures above: compilation alone
    # cannot validate those runtime member contracts.
} finally { $steam.Dispose(); $zip.Dispose(); $extender.Dispose(); $game.Dispose() }
