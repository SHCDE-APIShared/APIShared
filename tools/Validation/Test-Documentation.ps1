[CmdletBinding()]
param([string]$XmlDocumentation)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$documents = @(Get-ChildItem -LiteralPath $root -File -Filter '*.md') +
    @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Recurse -File -Filter '*.md')
$links = 0
foreach ($document in $documents) {
    $text = [regex]::Replace([IO.File]::ReadAllText($document.FullName), '(?ms)^```.*?^```[^\r\n]*', '')
    foreach ($match in [regex]::Matches($text, '\[[^\]\r\n]+\]\(([^)\r\n]+)\)')) {
        $reference = $match.Groups[1].Value.Trim().Trim('<', '>')
        if ($reference -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }
        $parts = $reference.Split('#', 2)
        $path = [Uri]::UnescapeDataString($parts[0])
        $target = if ($path.Length -eq 0) { $document.FullName } else {
            [IO.Path]::GetFullPath((Join-Path $document.Directory.FullName $path))
        }
        if (-not $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Documentation references an external workspace path: $reference ($($document.Name))"
        }
        if (-not (Test-Path -LiteralPath $target)) { throw "Broken documentation link: $reference ($($document.Name))" }
        if ($parts.Length -eq 2 -and [IO.Path]::GetExtension($target) -eq '.md') {
            $anchors = @(foreach ($heading in [regex]::Matches([IO.File]::ReadAllText($target), '(?m)^#{1,6}\s+([^\r\n]+)')) {
                $title = $heading.Groups[1].Value.Trim().ToLowerInvariant()
                [regex]::Replace($title, '[^\p{L}\p{Nd}_ -]', '').Replace(' ', '-')
            })
            if ([Uri]::UnescapeDataString($parts[1]) -notin $anchors) { throw "Broken heading link: $reference ($($document.Name))" }
        }
        $links++
    }
}
if ($XmlDocumentation) {
    [xml]$xml = [IO.File]::ReadAllText([IO.Path]::GetFullPath($XmlDocumentation))
    if ($xml.doc.assembly.name -ne 'APIShared') { throw 'XML documentation belongs to another assembly.' }
    $names = @($xml.doc.members.member | ForEach-Object { $_.name })
    if (($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Duplicate XML documentation member IDs.' }
    Write-Host "PASS: generated APIShared XML documentation ($($names.Count) members)."
}
Write-Host "PASS: documentation links ($links local targets/anchors); no external workspace references."
