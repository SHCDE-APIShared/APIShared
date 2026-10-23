function Assert-SERuntimeModPreflight([object]$Mod, [string]$Workspace) {
    if (-not $Mod.Plugin) { return }

    $projectPath = Join-Path $Workspace $Mod.Project
    # Query evaluated items: neighboring tools/tests and excluded files are not
    # runtime sources. MSBuild expands Includes, Excludes, imports and conditions.
    $evaluation = @(& dotnet msbuild $projectPath -nologo '-getItem:Compile,Reference' '-p:Configuration=Release')
    if ($LASTEXITCODE -ne 0) { throw 'Cannot evaluate selected runtime source/reference inputs.' }
    $items = ($evaluation -join "`n" | ConvertFrom-Json).Items
    $sources = @($items.Compile | ForEach-Object { [string]$_.FullPath })
    if (-not $sources.Count) { throw 'Selected runtime project has no Compile inputs.' }
    foreach ($source in $sources) {
        if ([regex]::IsMatch([IO.File]::ReadAllText($source), '(?<!\r)\n')) { throw "Runtime source requires CRLF: $source" }
    }
    $forbiddenJson = 'System\.Web\.Extensions|JavaScriptSerializer|System\.Text\.Json|Newtonsoft\.Json|DataContractJsonSerializer|JsonUtility'
    $projectHits = @($items.Reference | Where-Object { $_.Identity -match $forbiddenJson })
    if ($projectHits) {
        throw "$($Mod.Name): forbidden runtime JSON dependency or serializer found."
    }

    # Parse real method declarations across partial files. Comments, log strings,
    # neighboring service classes and braces in strings are not lifecycle contracts.
    . (Join-Path $PSScriptRoot 'SourceContracts.Common.ps1')
    Invoke-ApiSourceContracts -Root $Workspace -Group lifecycle
}
