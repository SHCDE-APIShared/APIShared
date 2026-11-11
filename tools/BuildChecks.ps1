# Build-only helpers; no runtime or neighboring-workspace dependency.
function Get-ApiMaintainedFiles([Parameter(Mandatory)][string]$Root) {
    # These are generated-output boundaries, not a prescribed feature layout.
    # Prune before descending: old snapshots/fixtures can dwarf maintained sources.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count) {
        $directory = $pending.Pop()
        Get-ChildItem -LiteralPath $directory -File
        foreach ($child in Get-ChildItem -LiteralPath $directory -Directory) {
            if ($child.Name -notin @('bin','obj','BepInEx','.git','.local','.work','.release-output')) { $pending.Push($child.FullName) }
        }
    }
}
function Get-ApiBuildTestMode {
    param([string]$Driver = 'build.bat', [string[]]$DriverArguments = @())
    $choices = @($DriverArguments | Where-Object { $_ -match '^/tests(?::|$)' })
    if ($choices.Count -and $Driver.Replace('\','/') -ine 'build.bat') { throw '/tests:full|quick|none is supported by APIShared/build.bat only.' }
    $modes = @($choices | ForEach-Object {
        if ($_ -notmatch '^/tests:(full|quick|none)$') { throw 'Use /tests:full, /tests:quick or /tests:none.' }
        $Matches[1].ToLowerInvariant()
    } | Sort-Object -Unique)
    if ($modes.Count -gt 1) { throw 'Conflicting /tests arguments.' }
    if ($modes.Count) { return $modes[0] }
    return 'full'
}
function New-ApiBuildContext([ValidateSet('full','quick','none')][string]$Mode = 'full') {
    return [pscustomobject]@{ Mode=$Mode; StartedUtc=[DateTime]::UtcNow.ToString('o'); Steps=[Collections.Generic.List[object]]::new(); Cache=$null }
}
function Initialize-ApiStepCache($Context,[string]$Root,[scriptblock]$Fingerprint) {
    $Context.Cache=[pscustomobject]@{Path=(Join-Path $Root '.local/check-cache.json'); Fingerprint=$Fingerprint}
}
function Get-ApiStepCacheKey($Context,[string]$Name,[string]$Category,[string]$When,[scriptblock]$Action) {
    if(-not $Context.Cache -or $env:APISHARED_UPDATE_API_BASELINE -or $env:APISHARED_CONSUMER_SCAN){return $null}
    $state=(& $Context.Cache.Fingerprint)-join ''
    if($state -cnotmatch '^[0-9a-f]{64}$'){throw 'Invalid check input fingerprint.'}
    $environment=@(Get-ChildItem Env: | Where-Object {$_.Name -match '^(APISHARED_|SHCDE|SCDE|DOTNET_|NUGET_|MSBUILD|COMPlus_|PATH$|TEMP$|TMP$|LANG$|LC_|USERPROFILE$)'} | Sort-Object Name | ForEach-Object {$_.Name+'='+$_.Value})
    $parts=@('api-step-cache-v1',$state,$Name,$Category,$When,$Context.Mode,$Action.ToString(),$PSVersionTable.PSVersion.ToString(),[Environment]::Version.ToString(),[Environment]::OSVersion.ToString(),[Environment]::Is64BitProcess,[Globalization.CultureInfo]::CurrentCulture.Name,[Globalization.CultureInfo]::CurrentUICulture.Name,[Console]::OutputEncoding.CodePage,(Get-Location).Path)+$environment
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($parts -join "`n")))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
}
function Read-ApiStepCache([string]$Path) {
    if(-not [IO.File]::Exists($Path)){return @()}
    if((Get-Item -LiteralPath $Path).Length -gt 8MB){return @()}
    try{$data=[IO.File]::ReadAllText($Path)|ConvertFrom-Json;if($data.SchemaVersion-ne1){return @()};return @($data.Records|Where-Object {$_.Status-ceq'Passed'-and$_.Key-cmatch'^[0-9a-f]{64}$'-and$_.Name.Length-le256})}catch{return @()}
}
function Save-ApiStepCache($Context,[string]$Name,[string]$Key,[long]$DurationMs) {
    $path=$Context.Cache.Path
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$identity=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($path))))).Replace('-','')}finally{$sha.Dispose()}
    $gate=[Threading.Mutex]::new($false,('Local\APIShared-StepCache-'+$identity))
    try{
        try{[void]$gate.WaitOne()}catch [Threading.AbandonedMutexException]{}
        $rows=@(Read-ApiStepCache $path|Where-Object {$_.Name-cne$Name-or$_.Key-cne$Key})
        $rows+=@([pscustomobject]@{Name=$Name;Key=$Key;Status='Passed';PassedUtc=[DateTime]::UtcNow.ToString('o');DurationMs=$DurationMs})
        $rows=@($rows|Sort-Object PassedUtc -Descending|Group-Object Name|ForEach-Object {$_.Group|Select-Object -First 2}|Sort-Object PassedUtc -Descending|Select-Object -First 512)
        do{$text=(@{SchemaVersion=1;Records=$rows}|ConvertTo-Json -Depth 5);if([Text.Encoding]::UTF8.GetByteCount($text)-le8MB){break};$rows=@($rows|Select-Object -First ([Math]::Max(0,$rows.Count-1)))}while($rows.Count)
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
        # One metadata file and one fixed atomic-write temporary; no binary/log copies.
        $temporary=$path+'.new';[IO.File]::WriteAllText($temporary,($text.Replace("`r`n","`n").Replace("`n","`r`n")+"`r`n"),[Text.UTF8Encoding]::new($false))
        if([IO.File]::Exists($path)){[IO.File]::Replace($temporary,$path,[System.Management.Automation.Language.NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
    }finally{try{$gate.ReleaseMutex()}catch{};$gate.Dispose()}
}
function Invoke-ApiBuildStep {
    param([Parameter(Mandatory)]$Context, [Parameter(Mandatory)][string]$Name,
        [string]$Category = 'tests', [ValidateSet('always','checked','full')][string]$When = 'full',
        [Parameter(Mandatory)][scriptblock]$Action,[switch]$Cacheable)
    $started = [DateTime]::UtcNow.ToString('o')
    $skip = ($When -eq 'full' -and $Context.Mode -ne 'full') -or ($When -eq 'checked' -and $Context.Mode -eq 'none')
    if ($skip) {
        Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] SKIP {2} (tests:{3})' -f [DateTime]::Now,$Category,$Name,$Context.Mode)
        $Context.Steps.Add([pscustomobject]@{ Name=$Name; Category=$Category; Selection=$When; Status='Skipped'; StartedUtc=$started; DurationMs=0 })
        return
    }
    $cacheKey=$null
    if($Cacheable -and $Category-eq'tests'){
        try{$cacheKey=Get-ApiStepCacheKey $Context $Name $Category $When $Action}catch{Write-Host ('Check cache unavailable: '+$_.Exception.Message)}
        if($cacheKey -and @(Read-ApiStepCache $Context.Cache.Path|Where-Object {$_.Name-ceq$Name-and$_.Key-ceq$cacheKey}).Count){
            $Context.Steps.Add([pscustomobject]@{Name=$Name;Category=$Category;Selection=$When;Status='Reused';StartedUtc=$started;DurationMs=0;CacheKey=$cacheKey})
            Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] REUSED {2} (matching passed check)' -f [DateTime]::Now,$Category,$Name);return
        }
    }
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] START {2}' -f [DateTime]::Now,$Category,$Name)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $status = 'Failed'
    try {
        & $Action | Out-Host; $status = 'Passed'
        if($cacheKey){try{if((Get-ApiStepCacheKey $Context $Name $Category $When $Action)-ceq$cacheKey){Save-ApiStepCache $Context $Name $cacheKey $watch.ElapsedMilliseconds}}catch{Write-Host ('Check result was not cached: '+$_.Exception.Message)}}
    }
    finally {
        $watch.Stop()
        $Context.Steps.Add([pscustomobject]@{ Name=$Name; Category=$Category; Selection=$When; Status=$status; StartedUtc=$started; DurationMs=$watch.ElapsedMilliseconds })
        Write-Host ('[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2} {3} ({4} ms)' -f [DateTime]::Now,$Category,$status.ToUpperInvariant(),$Name,$watch.ElapsedMilliseconds)
    }
}
function Save-ApiBuildSteps([string]$Root, $Context, [bool]$Succeeded) {
    $directory = Join-Path $Root '.local'
    [void][IO.Directory]::CreateDirectory($directory)
    $record = [ordered]@{ SchemaVersion=1; TestMode=$Context.Mode; StartedUtc=$Context.StartedUtc; FinishedUtc=[DateTime]::UtcNow.ToString('o'); Succeeded=$Succeeded; Steps=$Context.Steps.ToArray() }
    $text = ($record | ConvertTo-Json -Depth 6).Replace("`r`n","`n").Replace("`n","`r`n") + "`r`n"
    [IO.File]::WriteAllText((Join-Path $directory 'build-steps.json'),$text,[Text.UTF8Encoding]::new($false))
}
