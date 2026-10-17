function Assert-SERuntimeModPreflight([object]$Mod, [string]$Workspace) {
    if (-not $Mod.Plugin) { return }

    $projectPath = Join-Path $Workspace $Mod.Project
    $projectRoot = Split-Path -Parent $projectPath
    $sources = [Collections.Generic.List[string]]::new()
    foreach ($file in Get-ChildItem -LiteralPath $projectRoot -Recurse -File -Filter '*.cs' | Where-Object {
        # Classify paths relative to this project. The checkout itself may live
        # under .work; generated fixtures inside it must not become runtime code.
        ('\' + $_.FullName.Substring($projectRoot.Length).TrimStart('\','/')) -notmatch '[\\/](BepInEx[\\/]plugins|bin|obj|tests|[^\\/]*\.Tests|\.inspect|\.local|\.work|\.release-output)[\\/]'
    }) {
        $sources.Add($file.FullName)
    }

    [xml]$project = [IO.File]::ReadAllText($projectPath)
    foreach ($compile in $project.SelectNodes('//*[local-name()="Compile"][@Include]')) {
        $include = [string]$compile.Include
        if (-not $include -or $include.IndexOfAny([char[]]'*?') -ge 0) { continue }
        $linkedPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $include))
        if ((Test-Path -LiteralPath $linkedPath -PathType Leaf) -and -not $sources.Contains($linkedPath)) {
            $sources.Add($linkedPath)
        }
    }

    $forbiddenJson = 'System\.Web\.Extensions|JavaScriptSerializer|System\.Text\.Json|Newtonsoft\.Json|DataContractJsonSerializer|JsonUtility'
    # Piped path strings are searched as text, not opened as files. Read the
    # selected production files explicitly so serializers cannot pass unnoticed.
    $jsonHits = @(if ($sources.Count) { Select-String -LiteralPath $sources.ToArray() -Pattern $forbiddenJson })
    $projectHits = @(Select-String -LiteralPath $projectPath -Pattern $forbiddenJson)
    if ($jsonHits -or $projectHits) {
        throw "$($Mod.Name): forbidden runtime JSON dependency or serializer found."
    }

    foreach ($sourcePath in $sources) {
        $text = [IO.File]::ReadAllText($sourcePath)
        # BepInEx plugin components are destroyed during SHCDE startup cleanup.
        # An ordinary service Update method is not a Unity callback; only plugin
        # source files declaring BaseUnityPlugin are subject to this callback gate.
        if ($text -match '\bclass\s+\w+\s*:\s*(?:BepInEx\.)?BaseUnityPlugin\b' -and
            ($text -match '(?m)^\s*(?:(?:public|private|protected|internal|override|virtual|sealed|new|async)\s+)*(?:void|IEnumerator)\s+(?:Update|LateUpdate|FixedUpdate|OnGUI|OnRenderObject|OnPostRender)\s*\(' -or
             $text -match '\b(?:StartCoroutine|InvokeRepeating)\s*\(')) {
            throw "$($Mod.Name): plugin source depends on a short-lived MonoBehaviour callback: $sourcePath"
        }
        if ($text -match '\b(?:StartCoroutine|InvokeRepeating)\s*\(') {
            throw "$($Mod.Name): MonoBehaviour scheduling requires a persistent publisher: $sourcePath"
        }
        # Real process shutdown may release ordinary resources. Published hooks
        # are checked independently; startup component destruction is not unload.
        foreach ($match in [regex]::Matches($text, '\b(OnDestroy|OnDisable|OnApplicationPause)\s*\([^)]*\)\s*\{')) {
            $open = $text.IndexOf('{', $match.Index)
            $depth = 0
            $end = -1
            for ($index = $open; $index -lt $text.Length; $index++) {
                if ($text[$index] -eq '{') { $depth++ }
                elseif ($text[$index] -eq '}') {
                    $depth--
                    if ($depth -eq 0) { $end = $index; break }
                }
            }
            if ($end -lt 0) { throw "$($Mod.Name): unterminated Unity lifecycle method in $sourcePath." }
            $body = $text.Substring($open, $end - $open + 1)
            if ($body -match '\b(?:Dispose|DisposeRuntime|Stop|Undo|Unpatch|UnpatchAll|UnHook)\s*\(' -or $body -match '-=') {
                throw "$($Mod.Name): startup lifecycle method reaches teardown/unsubscription in $sourcePath."
            }
        }
    }
}
