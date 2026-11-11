function Get-PluginDependencyDeclarations([string]$SourceText) {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $defines = @(& dotnet msbuild (Join-Path $root 'APIShared.csproj') -nologo '-getProperty:DefineConstants' '-p:Configuration=Release') -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate dependency configuration symbols.' }
    # The parser evaluates constants in their declaration scope and ignores
    # comments/trivia. PowerShell streams the text; no temporary source is needed.
    $previousEncoding = $OutputEncoding
    try {
        $OutputEncoding = [Text.UTF8Encoding]::new($false)
        $output = @($SourceText | & dotnet run --project (Join-Path $root 'tools/SourceContracts/SourceContracts.csproj') --configuration Release --verbosity quiet -- $root dependencies $defines)
    } finally { $OutputEncoding = $previousEncoding }
    if ($LASTEXITCODE -ne 0) { throw 'Could not parse dependency declarations.' }
    foreach ($declaration in (($output -join "`n") | ConvertFrom-Json)) { $declaration }
}
function Assert-PluginDependencyMetadata([string]$SourceText, [object]$Manifest, [string]$Name) {
    $declared = @(Get-PluginDependencyDeclarations $SourceText)
    $metadata = @($Manifest.Dependencies | Where-Object { $null -ne $_ })
    $seen = @{}
    foreach ($dependency in $declared) {
        if ($seen.ContainsKey($dependency.GUID)) { throw "$Name declares duplicate BepInEx dependency $($dependency.GUID)." }
        $seen[$dependency.GUID] = $true
        if ($dependency.GUID -eq '000shcdese') {
            $minimum = [string]$Manifest.MinimumScriptExtenderVersion
        } else {
            $matches = @($metadata | Where-Object { [string]$_.GUID -ceq $dependency.GUID })
            if ($matches.Count -ne 1) { throw "$Name must declare dependency $($dependency.GUID) exactly once in info.json." }
            $minimum = [string]$matches[0].MinimumVersion
            $maximum = [string]$matches[0].MaximumVersion
            if ($maximum) {
                $parsedMaximum = $null
                $parsedMinimum = $null
                if (-not [version]::TryParse($maximum, [ref]$parsedMaximum) -or
                    ($minimum -and (-not [version]::TryParse($minimum, [ref]$parsedMinimum) -or $parsedMaximum -lt $parsedMinimum))) {
                    throw "$Name has an invalid dependency range for $($dependency.GUID)."
                }
            }
        }
        # These are minimum bounds, not equality checks against the installed version.
        if ($minimum -cne $dependency.MinimumVersion) {
            throw "$Name minimum for $($dependency.GUID) differs between info.json and BepInEx."
        }
        if ($minimum) {
            $parsed = $null
            if (-not [version]::TryParse($minimum, [ref]$parsed)) { throw "$Name has an invalid minimum: $minimum" }
        }
    }
    foreach ($dependency in $metadata) {
        if (-not $dependency.GUID -or -not $seen.ContainsKey([string]$dependency.GUID)) {
            throw "$Name info.json dependency lacks a corresponding hard BepInEx declaration: $($dependency.GUID)"
        }
    }
}
