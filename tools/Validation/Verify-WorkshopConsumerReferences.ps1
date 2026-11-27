param([Parameter(Mandatory=$true)][string]$CandidateAssembly,[Parameter(Mandatory=$true)][string]$GameDir,[string]$BaselineAssembly='')
$ErrorActionPreference='Stop'
[void][Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $GameDir 'BepInEx/core/Mono.Cecil.dll')))
$resolver=[Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($CandidateAssembly)))
foreach($dir in @('BepInEx/core','Stronghold Crusader Definitive Edition_Data/Managed')){$resolver.AddSearchDirectory((Join-Path $GameDir $dir))}
$plugins=Join-Path $GameDir 'BepInEx/plugins'
foreach($dir in Get-ChildItem -LiteralPath $plugins -Recurse -Directory){$resolver.AddSearchDirectory($dir.FullName)}
$parameters=[Mono.Cecil.ReaderParameters]::new();$parameters.AssemblyResolver=$resolver
$candidate=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($CandidateAssembly,$parameters)
$failures=[Collections.Generic.List[string]]::new();$checked=0
function Public-Shape($Assembly){
    foreach($type in $Assembly.MainModule.GetTypes()){
        if(-not ($type.IsPublic -or $type.IsNestedPublic)){continue}
        'T '+$type.FullName
        foreach($member in @($type.Methods)+@($type.Fields)){if($member.IsPublic -or $member.IsFamily -or $member.IsFamilyOrAssembly){'M '+$member.FullName}}
        foreach($property in $type.Properties){if(($property.GetMethod -and $property.GetMethod.IsPublic) -or ($property.SetMethod -and $property.SetMethod.IsPublic)){'P '+$property.FullName}}
        foreach($event in $type.Events){if($event.AddMethod -and $event.AddMethod.IsPublic){'E '+$event.FullName}}
    }
}
try{
    if($BaselineAssembly){
        $baseline=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($BaselineAssembly)
        try{$expected=@(Public-Shape $baseline);$actual=@(Public-Shape $candidate);$difference=@(Compare-Object $expected $actual);if($difference.Count){throw ('Public API differs from the released baseline: '+($difference.InputObject -join '; '))}}finally{$baseline.Dispose()}
    }
    # Resolve APIShared references against the candidate, never the installed DLL.
    $resolved=$resolver.Resolve([Mono.Cecil.AssemblyNameReference]::new('APIShared',$candidate.Name.Version))
    if([IO.Path]::GetFullPath($resolved.MainModule.FileName) -cne [IO.Path]::GetFullPath($CandidateAssembly)){throw 'Consumer resolver did not select the candidate assembly.'}
    foreach($file in Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter '*.dll'){
        if($file.FullName -match '[\\/](000shcdese|APIShared_Serp)[\\/]'){continue}
        $assembly=$null
        try{$assembly=[Mono.Cecil.AssemblyDefinition]::ReadAssembly($file.FullName,$parameters)}catch [BadImageFormatException]{continue}
        try{
            # Reference resolution alone cannot detect two owners of the requester hook.
            # Use the published plugin identity and the same opt-in contract as the runtime
            # guard; filenames/folder names and package versions do not prove migration.
            foreach($pluginType in $assembly.MainModule.GetTypes()){
                $isExtendedData=@($pluginType.CustomAttributes | Where-Object {
                    $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' -and
                    $_.ConstructorArguments.Count -ge 1 -and
                    [string]$_.ConstructorArguments[0].Value -eq 'ExtendedData_Serp'
                }).Count -gt 0
                if(-not $isExtendedData){continue}
                $marker=@($pluginType.Fields | Where-Object {
                    $_.Name -ceq 'UsesApiSharedSaveLoadDialog' -and $_.IsPublic -and
                    $_.IsStatic -and $_.IsLiteral -and $_.HasConstant -and
                    $_.FieldType.FullName -eq 'System.Boolean' -and
                    $_.Constant -is [bool] -and $_.Constant -eq $true
                })
                if($marker.Count -ne 1){
                    $failures.Add($file.FullName+': Update ExtendedData together with APIShared: the installed ExtendedData still owns the load/save requester.')
                }
            }
            foreach($reference in @($assembly.MainModule.GetTypeReferences())+@($assembly.MainModule.GetMemberReferences())){
                $type=if($reference -is [Mono.Cecil.TypeReference]){$reference}else{$reference.DeclaringType}
                if(-not $type -or $type.GetElementType().Scope.Name -ne 'APIShared'){continue}
                $checked++
                try{if($null -eq $reference.Resolve()){$failures.Add($file.Name+': '+$reference.FullName)}}catch{$failures.Add($file.Name+': '+$reference.FullName+'; '+$_.Exception.Message)}
            }
        }finally{$assembly.Dispose()}
    }
    if($failures.Count){throw ("Installed consumers are incompatible with the candidate:`n"+($failures -join "`n"))}
    Write-Host ('PASS: '+$checked+' installed APIShared type/member references resolve against candidate '+$candidate.Name.Version+'.')
}finally{$candidate.Dispose();$resolver.Dispose()}
