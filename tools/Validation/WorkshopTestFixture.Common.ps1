# Standalone test infrastructure; never imported by a runtime plugin or installed updater.
function New-WorkshopTestFixture([string]$Root,[ValidateSet('recovery','readiness','maintenance','coordination')][string]$Suite) {
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $name=[guid]::NewGuid().ToString('N')
    if($Suite -eq 'coordination'){$name=$name.Substring(0,8)}
    $path=Join-Path $rootPath ('.local/'+$Suite+'-tests/'+$name)
    for($part=[IO.Path]::GetDirectoryName($path);$part;$part=[IO.Path]::GetDirectoryName($part)){
        if(([IO.Directory]::Exists($part) -or [IO.File]::Exists($part)) -and ([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint)){throw 'Linked fixture allocation root.'}
    }
    if([IO.Directory]::Exists($path)){throw 'A fresh test fixture is required.'}
    [void][IO.Directory]::CreateDirectory($path)
    $record=[ordered]@{SchemaVersion=1;Kind='WorkshopTestFixture';Suite=$Suite;Root=$path;OwnerPid=$PID;CreatedUtc=[DateTime]::UtcNow.ToString('o')}
    [IO.File]::WriteAllText((Join-Path $path '.fixture-owner.json'),(($record|ConvertTo-Json)+"`r`n"),[Text.UTF8Encoding]::new($false))
    return $path
}
function Assert-WorkshopTestFixture([string]$Root,[string]$Suite,[string]$Path) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $base=[IO.Path]::GetFullPath((Join-Path $Root ('.local/'+$Suite+'-tests'))).TrimEnd('\','/')
    if([IO.Path]::GetDirectoryName($full) -ine $base -or [IO.Path]::GetFileName($full) -cnotmatch '^(?:[0-9a-f]{8}|[0-9a-f]{32})$'){throw 'Fixture escapes its allocated test directory.'}
    for($part=$full;$part;$part=[IO.Path]::GetDirectoryName($part)){
        if([IO.File]::GetAttributes($part) -band [IO.FileAttributes]::ReparsePoint){throw 'Linked fixture root.'}
    }
    $record=[IO.File]::ReadAllText((Join-Path $full '.fixture-owner.json'))|ConvertFrom-Json
    if($record.SchemaVersion -ne 1 -or $record.Kind -cne 'WorkshopTestFixture' -or $record.Suite -cne $Suite -or $record.Root -ine $full){throw 'Fixture ownership mismatch.'}
    return $record
}
function Complete-WorkshopTestFixture([string]$Root,[string]$Suite,[string]$Path,[bool]$Passed,[string]$FailureMessage='') {
    $record=Assert-WorkshopTestFixture $Root $Suite $Path
    if($record.OwnerPid -ne $PID){throw 'Only the allocating test process can remove this fixture.'}
    $failure=$FailureMessage
    if($failure.Length -gt 4096){$failure=$failure.Substring(0,4096)}
    $cleanupError=''
    $failureEvidence=@()
    if(-not $Passed){
        $pending=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
        $pending.Push([IO.DirectoryInfo]::new($Path))
        $logs=[Collections.Generic.List[IO.FileInfo]]::new()
        while($pending.Count){
            foreach($entry in $pending.Pop().GetFileSystemInfos()){
                if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
                if($entry -is [IO.DirectoryInfo]){$pending.Push($entry)}
                elseif($entry.Name -ceq 'journal.log' -or $entry.Name -match '^result-[0-9a-f]+\.txt$'){$logs.Add($entry)}
            }
        }
        foreach($log in @($logs|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 8)){
            $stream=$null
            try{
                $stream=[IO.File]::Open($log.FullName,'Open','Read','ReadWrite')
                if($stream.Length -gt 8192){[void]$stream.Seek(-8192,[IO.SeekOrigin]::End)}
                $reader=[IO.StreamReader]::new($stream)
                try{$tail=$reader.ReadToEnd()}finally{$reader.Dispose()}
            }catch{$tail='Diagnostic unavailable: '+$_.Exception.Message}
            finally{if($stream){$stream.Dispose()}}
            $failureEvidence+=@{Path=$log.FullName.Substring($Path.Length+1).Replace('\','/');Tail=$tail;Truncated=($log.Length -gt 8192)}
        }
    }
    function Remove-OwnedFixture([IO.DirectoryInfo]$Directory) {
        foreach($entry in $Directory.GetFileSystemInfos()){
            if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){
                if($entry -is [IO.DirectoryInfo]){[IO.Directory]::Delete($entry.FullName,$false)}else{[IO.File]::Delete($entry.FullName)}
            }elseif($entry.Name -ceq '.git'){throw 'Unexpected Git repository in disposable Workshop fixture.'}
            elseif($entry -is [IO.DirectoryInfo]){Remove-OwnedFixture $entry}
            else{$entry.IsReadOnly=$false;$entry.Delete()}
        }
        $Directory.Delete()
    }
    try{Remove-OwnedFixture ([IO.DirectoryInfo]::new($Path))}catch{$cleanupError=$_.Exception.Message}
    $result=[ordered]@{SchemaVersion=1;Suite=$Suite;StartedUtc=$record.CreatedUtc;CompletedUtc=[DateTime]::UtcNow.ToString('o');Passed=$Passed;Failure=$failure;FailureEvidence=$failureEvidence;FixtureRemoved=(-not [IO.Directory]::Exists($Path));CleanupError=$cleanupError}
    $output=Join-Path $Root ('.local/fixture-results/'+$Suite+'.json')
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
    [IO.File]::WriteAllText($output,(($result|ConvertTo-Json)+"`r`n"),[Text.UTF8Encoding]::new($false))
    if($cleanupError){
        if($Passed){throw "Workshop fixture cleanup failed: $cleanupError"}
        [Console]::Error.WriteLine('Additional Workshop fixture cleanup failure: '+$cleanupError)
    }
}
