[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExtenderDir)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$matched = 0
$coverage = @{}
foreach ($relative in @('Assets/GUI/XAML/MainHUD.xaml', 'Assets/GUI/XAMLResources/HUD_Buildings.xaml', 'Assets/GUI/XAMLResources/HUD_Troops.xaml')) {
    [xml]$patch = [IO.File]::ReadAllText((Join-Path $root ('Patches/' + $relative)))
    [xml]$original = [IO.File]::ReadAllText((Join-Path $ExtenderDir ('Override/' + $relative)))
    $names = New-Object Xml.XmlNamespaceManager($original.NameTable)
    $names.AddNamespace('n', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    $names.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    foreach ($operation in $patch.SelectNodes('/Patch/Operation[@AttributeName="apt:TooltipAnchor.Anchor"]')) {
        if ($operation.Type -ne 'SetAttribute') { throw 'Tooltip anchors must annotate the original controls.' }
        $targets = @($original.SelectNodes($operation.XPath, $names))
        if ($targets.Count -ne 1 -or $targets[0].LocalName -ne 'TextBlock') { throw "Tooltip anchor changed: $relative [$($operation.Value)], count=$($targets.Count)" }
        # Names and original bound contents must survive; annotations add no background or empty inline.
        if ($operation.SelectSingleNode('Content')) { throw 'An anchor must not copy or replace a Vanilla control.' }
        $coverage[[string]$operation.Value] = $true
        $matched++
    }
}
foreach ($kind in @('BuildingCompact', 'BuildingDetailed', 'BuildingDescription', 'Recruitment', 'SiegeConstruction')) {
    if (-not $coverage.ContainsKey($kind)) { throw "Missing semantic tooltip surface: $kind" }
}
Write-Host "PASS: $matched shared tooltip anchors resolve to original TextBlocks in the selected installed Extender XAML. No render claim."
