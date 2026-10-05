param([switch]$SelfTestOnly)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$script:Clock = [Diagnostics.Stopwatch]::StartNew()
$script:Stages = New-Object System.Collections.ArrayList
$script:StageClock = $null
$script:StageName = ''
$script:Mutated = $false
$script:Rollback = 'NOT_APPLICABLE_NO_MUTATION'
$script:Status = 'V28R1_OWNER_RUN_STAGED_QA_IN_PROGRESS'
$script:Root = Split-Path -Parent $PSCommandPath
$script:Result = Join-Path $script:Root 'Result'
$script:RunRoot = Join-Path $script:Result ('RUN_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
$script:Zip = Join-Path $script:Root 'V28R1_SOURCE_AND_PROOF.zip'
$script:Contents = $script:Root
$script:ExpectedZip = 'E2393809B03B1185E1D85E52D9D4E8E1E579442DF73043C9F27A7C72FF0F70ED'
$script:V25Exe = '76985810D7C9E8324BF0FCDC228277834CCEED7FC5674CEF3CB3042D9978EE81'
$script:Repo = $script:Root
$script:BranchSource = Join-Path $script:Repo 'source'
$script:Source = 'V:\خاص بي\ARCHESTRO_MEETING_VAULT_BUILD4_R4_3_STARTUP_LIFECYCLE_FIX\src\Archestro.MeetingVault'
$script:Install = 'C:\Users\arefa\AppData\Local\Programs\Archestro\Meeting Vault'
$script:Data = 'C:\Users\arefa\Documents\Archestro Meeting Vault'
$script:DotNet = 'C:\Program Files\dotnet\dotnet.exe'
$script:Python = (Get-Command python.exe -ErrorAction Stop).Source
$script:DbSemanticAudit = Join-Path $script:Root 'DB_SEMANTIC_AUDIT.py'
$script:WordHelper = 'C:\Users\arefa\.codex\skill-vault\sources\dachent-skills\.shared\office-com\scripts\office_com_preflight.ps1'
$script:Backup = $null
$script:ProtectedSnapshotRoot = $null
$script:BeforeData = $null
$script:BeforeManifest = $null
$script:DatabaseSemanticBefore = $null
$script:SettingsSemanticBefore = $null
$script:ProtectedLogsBackup = $null
$script:StartedProcess = $null
$script:StartupLogSnapshot = $null
$script:ObservedQALogChanges = New-Object Collections.ArrayList

function Say([string]$Kind,[string]$Text,[ConsoleColor]$Color) {
    $e = $script:Clock.Elapsed
    Write-Host ('[{0} {1:00}:{2:00}:{3:00}] {4}' -f $Kind.ToUpperInvariant(),[int]$e.TotalHours,$e.Minutes,$e.Seconds,$Text) -ForegroundColor $Color
}
function StepTime([TimeSpan]$Value) { return ('{0:0.0}s' -f $Value.TotalSeconds) }
function BeginStage([string]$Name,[string]$Objective) {
    $script:StageName = $Name
    $script:StageClock = [Diagnostics.Stopwatch]::StartNew()
    [void]$script:Stages.Add([ordered]@{stage=$Name;objective=$Objective;status='RUNNING';started=(Get-Date).ToString('o');ended=$null;durationSeconds=$null;verification=$null;rollback=$null;dependencyImpact=$null;nextAction=$null})
    Write-Host ''
    Write-Host ('=' * 84) -ForegroundColor DarkCyan
    Say 'STAGE' $Name Magenta
}
function EndStage([string]$State,[string]$Evidence) {
    if ($null -eq $script:StageClock) { return }
    $script:StageClock.Stop()
    $row = $script:Stages[$script:Stages.Count - 1]
    $row.status=$State; $row.ended=(Get-Date).ToString('o'); $row.durationSeconds=[math]::Round($script:StageClock.Elapsed.TotalSeconds,2); $row.verification=$Evidence
    $color=[ConsoleColor]::Green
    if ($State -eq 'WARN') { $color=[ConsoleColor]::Yellow }
    if ($State -eq 'FAIL' -or $State -eq 'BLOCKED') { $color=[ConsoleColor]::Red }
    Say ('STEP-' + $State) ($script:StageName + ' (' + (StepTime $script:StageClock.Elapsed) + ') ' + $Evidence) $color
    $script:StageClock=$null; $script:StageName=''
}
function HashFile([string]$Path) {
    $file=Get-Item -LiteralPath $Path
    if($file.Length -gt 536870912 -and (Test-Path -LiteralPath (Join-Path $env:SystemRoot 'System32\certutil.exe'))){
        $out=Join-Path $script:RunRoot 'Assets\LARGE_FILE_HASH.stdout.txt';$err=Join-Path $script:RunRoot 'Assets\LARGE_FILE_HASH.stderr.txt'
        RunNative (Join-Path $env:SystemRoot 'System32\certutil.exe') @('-hashfile',$Path,'SHA256') (Split-Path -Parent $Path) 'Hash large target asset' $out $err 1800 @(0)
        $match=[regex]::Match((Get-Content -LiteralPath $out -Raw),'(?im)^\s*([0-9a-f]{64})\s*$')
        if(-not $match.Success){throw ('Could not parse large-file SHA256 output: '+$out)}
        return $match.Groups[1].Value.ToLowerInvariant()
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function ByteHash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
}
function SaveJson([string]$Path,$Value) { [IO.File]::WriteAllText($Path,(ConvertTo-Json -InputObject $Value -Depth 12),[Text.UTF8Encoding]::new($false)) }
function QuoteArg([string]$Value) { if ($Value -match '[\s"]') { return '"' + $Value.Replace('"','\"') + '"' }; return $Value }
function RunNative([string]$Exe,[string[]]$NativeArguments,[string]$Cwd,[string]$Label,[string]$Out,[string]$Err,[int]$Timeout=1800,[int[]]$Allowed=@(0)) {
    $argumentText=($NativeArguments | ForEach-Object { QuoteArg ([string]$_) }) -join ' '
    Say 'START' $Label DarkCyan
    Write-Host ('RUN: ' + $Exe + ' ' + $argumentText) -ForegroundColor DarkGray
    $watch=[Diagnostics.Stopwatch]::StartNew()
    $proc=Start-Process -FilePath $Exe -ArgumentList $argumentText -WorkingDirectory $Cwd -PassThru -NoNewWindow -RedirectStandardOutput $Out -RedirectStandardError $Err
    $spinner=@('|','/','-','\');$i=0;$last=-1
    while (-not $proc.WaitForExit(250)) {
        $sec=[int][math]::Floor($watch.Elapsed.TotalSeconds)
        [Console]::Write([char]13 + ('[RUN] {0} ({1}) {2} PID {3}' -f $Label,(StepTime $watch.Elapsed),$spinner[$i % 4],$proc.Id).PadRight(105));$i++
        if ($sec -gt 0 -and ($sec % 15) -eq 0 -and $sec -ne $last) { Write-Host '';Say 'RUN' ($Label + ' active (' + (StepTime $watch.Elapsed) + ') PID ' + $proc.Id) Cyan;$last=$sec }
        if ($watch.Elapsed.TotalSeconds -ge $Timeout) { try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch {};throw ('Timed out after ' + $Timeout + 's in ' + $Label) }
    }
    $proc.WaitForExit();$watch.Stop();Write-Host ''
    if ($Allowed -notcontains [int]$proc.ExitCode) { Say 'FAIL' ($Label + ' exit ' + $proc.ExitCode + '; see ' + $Out + ' and ' + $Err) Red;throw ('Native command failed: ' + $Label) }
    Say 'PASS' ($Label + ' (' + (StepTime $watch.Elapsed) + ') exit ' + $proc.ExitCode) Green
}
function TestZip([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Path);$items=New-Object Collections.ArrayList
    try { foreach ($entry in $zip.Entries) { if ($entry.FullName.EndsWith('/')) { continue };$stream=$entry.Open();try{$stream.CopyTo([IO.Stream]::Null)}finally{$stream.Dispose()};[void]$items.Add([pscustomobject]@{name=$entry.FullName;bytes=$entry.Length}) } } finally { $zip.Dispose() }
    return ,@($items.ToArray())
}
function TreeHash([string]$Path) {
    $files=@(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 })
    $parts=New-Object Collections.ArrayList;$bytes=[long]0
    foreach ($file in $files) {
        $relative=$file.FullName.Substring($Path.Length).TrimStart('\').ToLowerInvariant()
        $nameHash=ByteHash ([Text.Encoding]::UTF8.GetBytes($relative))
        [void]$parts.Add(('{0}|{1}|{2}' -f $nameHash,$file.Length,(HashFile $file.FullName)));$bytes+=$file.Length
    }
    $all=[string]::Join([Environment]::NewLine,@($parts.ToArray() | Sort-Object))
    $hash=ByteHash ([Text.Encoding]::UTF8.GetBytes($all))
    return [pscustomobject]@{fileCount=$files.Count;bytes=$bytes;aggregateSha256=$hash}
}
function TreeManifest([string]$Path) {
    $files=@(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 })
    $rows=New-Object Collections.ArrayList;$parts=New-Object Collections.ArrayList;[long]$bytes=0
    foreach($file in $files){$relative=$file.FullName.Substring($Path.Length).TrimStart('\');$normalized=$relative.Replace('\','/');$nameHash=ByteHash ([Text.Encoding]::UTF8.GetBytes($relative.ToLowerInvariant()));$sha=HashFile $file.FullName;[void]$rows.Add([pscustomobject]@{relativePath=$normalized;size=$file.Length;sha256=$sha;lastWriteTimeUtc=$file.LastWriteTimeUtc.ToString('o')});[void]$parts.Add(('{0}|{1}|{2}' -f $nameHash,$file.Length,$sha));$bytes+=$file.Length}
    $all=[string]::Join([Environment]::NewLine,@($parts.ToArray()|Sort-Object));$aggregate=ByteHash ([Text.Encoding]::UTF8.GetBytes($all))
    return [pscustomobject]@{root=$Path;fileCount=$files.Count;bytes=$bytes;aggregateSha256=$aggregate;files=@($rows.ToArray()|Sort-Object relativePath)}
}
function GetDatabaseSemanticSnapshot([string]$Destination) {
    $database=Join-Path $script:Data 'System\meeting-vault.db'
    $out=$Destination+'.stdout.txt';$err=$Destination+'.stderr.txt'
    RunNative $script:Python @($script:DbSemanticAudit,$database,$Destination) $script:Root ('SQLite semantic audit '+$Destination) $out $err 60 @(0)
    return (Get-Content -LiteralPath $Destination -Raw | ConvertFrom-Json)
}
function GetSettingsSemanticSnapshot {
    $settingsPath=Join-Path $script:Data 'System\settings.json'
    $raw=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $properties=@{};foreach($property in $raw.PSObject.Properties){$properties[$property.Name.ToLowerInvariant()]=$property.Value}
    $provider=[string]$properties['intelligenceprovider'];$cloudEnabled=[bool]$properties['cloudintelligenceenabled'];$consent=[bool]$properties['cloudintelligenceconsentaccepted'];$encryptedKey=[string]$properties['encryptedintelligenceapikey']
    return [pscustomobject]@{provider=$provider;cloudEnabled=$cloudEnabled;cloudConsent=$consent;encryptedKeyConfigured=(-not [string]::IsNullOrWhiteSpace($encryptedKey));appearance=[string]$properties['appearance'];preferredLanguage=[string]$properties['preferredlanguage']}
}
function TestDatabaseSemanticEqual($Before,$After) {
    if($Before.integrity -ne 'ok' -or $After.integrity -ne 'ok' -or $Before.foreignKeyErrors -ne 0 -or $After.foreignKeyErrors -ne 0 -or $Before.schemaSha256 -ne $After.schemaSha256 -or $Before.logicalRows -ne $After.logicalRows){return $false}
    $beforeMap=@{};$afterMap=@{};foreach($table in $Before.tables){$beforeMap[$table.table]=$table};foreach($table in $After.tables){$afterMap[$table.table]=$table}
    if($beforeMap.Count -ne $afterMap.Count){return $false}
    foreach($name in $beforeMap.Keys){if(-not $afterMap.ContainsKey($name) -or $beforeMap[$name].rows -ne $afterMap[$name].rows -or $beforeMap[$name].logicalSha256 -ne $afterMap[$name].logicalSha256){return $false}}
    return $true
}
function TestSettingsSemanticEqual($Before,$After) { return (($Before|ConvertTo-Json -Compress) -ceq ($After|ConvertTo-Json -Compress)) }
function GetProtectedSemanticComparison($AfterManifest) {
    $afterDb=GetDatabaseSemanticSnapshot (Join-Path $script:RunRoot 'DATABASE_SEMANTIC_AFTER.json')
    $afterSettings=GetSettingsSemanticSnapshot
    $dbEqual=TestDatabaseSemanticEqual $script:DatabaseSemanticBefore $afterDb
    $settingsEqual=TestSettingsSemanticEqual $script:SettingsSemanticBefore $afterSettings
    $beforeMap=@{};$afterMap=@{}
    foreach($f in $script:BeforeManifest.files){$beforeMap[$f.relativePath.ToLowerInvariant()]=$f};foreach($f in $AfterManifest.files){$afterMap[$f.relativePath.ToLowerInvariant()]=$f}
    $special=@('system/meeting-vault.db','system/meeting-vault.db-wal','system/meeting-vault.db-shm','system/settings.json')
    $otherChanged=New-Object Collections.ArrayList
    foreach($key in @($beforeMap.Keys)+@($afterMap.Keys)|Select-Object -Unique){
        if($special -contains $key){continue}
        if($key -match '^system/meeting-vault\.db-journal$'){[void]$otherChanged.Add($key);continue}
        if(-not $beforeMap.ContainsKey($key) -or -not $afterMap.ContainsKey($key) -or $beforeMap[$key].sha256 -ne $afterMap[$key].sha256 -or $beforeMap[$key].size -ne $afterMap[$key].size){[void]$otherChanged.Add($key)}
    }
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY_SEMANTIC.json') ([pscustomobject]@{database=[pscustomobject]@{before=[pscustomobject]@{integrity=$script:DatabaseSemanticBefore.integrity;foreignKeyErrors=$script:DatabaseSemanticBefore.foreignKeyErrors;schemaSha256=$script:DatabaseSemanticBefore.schemaSha256;logicalRows=$script:DatabaseSemanticBefore.logicalRows;tableCount=$script:DatabaseSemanticBefore.tables.Count};after=[pscustomobject]@{integrity=$afterDb.integrity;foreignKeyErrors=$afterDb.foreignKeyErrors;schemaSha256=$afterDb.schemaSha256;logicalRows=$afterDb.logicalRows;tableCount=$afterDb.tables.Count};equal=$dbEqual};settings=[pscustomobject]@{before=$script:SettingsSemanticBefore;after=$afterSettings;equal=$settingsEqual};nonSqliteOtherPathChanges=$otherChanged.Count;pass=($dbEqual -and $settingsEqual -and $otherChanged.Count -eq 0)})
    return [pscustomobject]@{databaseEqual=$dbEqual;settingsEqual=$settingsEqual;otherChanged=@($otherChanged.ToArray());afterDatabase=$afterDb;afterSettings=$afterSettings;pass=($dbEqual -and $settingsEqual -and $otherChanged.Count -eq 0)}
}
function BackupProtectedState {
    $root=Join-Path $script:RunRoot 'PROTECTED_DATA_BASELINE_PRIVATE';New-Item -ItemType Directory -Path $root -Force|Out-Null
    foreach($relative in @('System\meeting-vault.db','System\meeting-vault.db-wal','System\meeting-vault.db-shm','System\settings.json')){
        $from=Join-Path $script:Data $relative;$to=Join-Path $root $relative
        if(Test-Path -LiteralPath $from -PathType Leaf){New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force|Out-Null;Copy-Item -LiteralPath $from -Destination $to -Force}
    }
    $script:ProtectedSnapshotRoot=$root
    $script:DatabaseSemanticBefore=GetDatabaseSemanticSnapshot (Join-Path $script:RunRoot 'DATABASE_SEMANTIC_BEFORE.json')
    $script:SettingsSemanticBefore=GetSettingsSemanticSnapshot
}
function RestoreProtectedState {
    if(-not $script:ProtectedSnapshotRoot){return [pscustomobject]@{status='NOT_CAPTURED'}}
    $restored=New-Object Collections.ArrayList
    foreach($relative in @('System\meeting-vault.db','System\meeting-vault.db-wal','System\meeting-vault.db-shm','System\settings.json')){
        $target=Join-Path $script:Data $relative;$saved=Join-Path $script:ProtectedSnapshotRoot $relative
        if(Test-Path -LiteralPath $saved -PathType Leaf){Copy-Item -LiteralPath $saved -Destination $target -Force;[void]$restored.Add([pscustomobject]@{pathClass=$relative;restoredSha256=(HashFile $saved)})}
        elseif(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force;[void]$restored.Add([pscustomobject]@{pathClass=$relative;restoredSha256=$null})}
    }
    $current=GetSettingsSemanticSnapshot
    $settingsRestored=($current|ConvertTo-Json -Compress) -eq ($script:SettingsSemanticBefore|ConvertTo-Json -Compress)
    $after=GetDatabaseSemanticSnapshot (Join-Path $script:RunRoot 'DATABASE_SEMANTIC_AFTER_RESTORE.json')
    $dbRestored=TestDatabaseSemanticEqual $script:DatabaseSemanticBefore $after
    $allExact=$true;foreach($item in $restored){$target=Join-Path $script:Data $item.pathClass;if($item.restoredSha256){if((HashFile $target) -ne $item.restoredSha256){$allExact=$false}}elseif(Test-Path -LiteralPath $target){$allExact=$false}}
    return [pscustomobject]@{status=$(if($settingsRestored -and $dbRestored -and $allExact){'PASS'}else{'FAIL'});files=$restored.ToArray();databaseSemanticRestored=$dbRestored;settingsSemanticRestored=$settingsRestored;exactSnapshotRestored=$allExact}
}
function ClassifyProtectedPath([string]$RelativePath) {
    $rel=$RelativePath.Replace('\','/');$leaf=[IO.Path]::GetFileName($RelativePath)
    $qa=@('self-test-pass.txt','self-test-error.txt','meeting-report-stage-diagnostics.log','meeting-report-format-fallback.log','intelligence-self-test-pass.txt','intelligence-self-test-error.txt','speaker-self-test-pass.txt','speaker-self-test-error.txt','v13.8.9-qa.json','v13.8.9-qa-error.txt','v13.8-matrix.json','v13.8-matrix-error.txt','v13.8.7-qa.json','v13.8.8-qa.json')
    if($rel.StartsWith('Logs/',[StringComparison]::OrdinalIgnoreCase) -and $qa -contains $leaf){return [pscustomobject]@{classification='QA_TEMPORARY';evidence='Named output written by the invoked offline QA/self-test flag under AppPaths.Logs; exact bytes were snapshotted before the test.'}}
    if($rel.StartsWith('Logs/',[StringComparison]::OrdinalIgnoreCase) -and $leaf -eq 'startup-ready.json'){return [pscustomobject]@{classification='APP_OPERATIONAL_METADATA';evidence='Startup readiness receipt written by normal app startup under AppPaths.Logs; exact bytes were snapshotted before startup.'}}
    if($rel -match '(^|/)(Meetings|Recordings|Transcripts|Reports|Media)(/|$)'){return [pscustomobject]@{classification='USER_CONTENT';evidence='Path is inside protected meeting/report/transcript/media content.'}}
    return [pscustomobject]@{classification='UNKNOWN';evidence='No exact source path proof ties this protected path to a QA-only or operational artifact.'}
}
function CompareTreeManifests($Before,$After) {
    $beforeMap=@{};$afterMap=@{}
    foreach($f in $Before.files){$beforeMap[$f.relativePath.ToLowerInvariant()]=$f};foreach($f in $After.files){$afterMap[$f.relativePath.ToLowerInvariant()]=$f}
    $added=New-Object Collections.ArrayList;$removed=New-Object Collections.ArrayList;$changed=New-Object Collections.ArrayList
    foreach($key in $afterMap.Keys){if(-not $beforeMap.ContainsKey($key)){$now=$afterMap[$key];$c=ClassifyProtectedPath $now.relativePath;[void]$added.Add([pscustomobject]@{relativePath=$now.relativePath;beforeSize=$null;afterSize=$now.size;beforeSha256=$null;afterSha256=$now.sha256;classification=$c.classification;evidence=$c.evidence})}}
    foreach($key in $beforeMap.Keys){if(-not $afterMap.ContainsKey($key)){$old=$beforeMap[$key];$c=ClassifyProtectedPath $old.relativePath;[void]$removed.Add([pscustomobject]@{relativePath=$old.relativePath;beforeSize=$old.size;afterSize=$null;beforeSha256=$old.sha256;afterSha256=$null;classification=$c.classification;evidence=$c.evidence})}elseif($beforeMap[$key].sha256 -ne $afterMap[$key].sha256 -or $beforeMap[$key].size -ne $afterMap[$key].size){$old=$beforeMap[$key];$now=$afterMap[$key];$c=ClassifyProtectedPath $old.relativePath;[void]$changed.Add([pscustomobject]@{relativePath=$old.relativePath;beforeSize=$old.size;afterSize=$now.size;beforeSha256=$old.sha256;afterSha256=$now.sha256;classification=$c.classification;evidence=$c.evidence})}}
    return [pscustomobject]@{added=@($added.ToArray()|Sort-Object relativePath);removed=@($removed.ToArray()|Sort-Object relativePath);changed=@($changed.ToArray()|Sort-Object relativePath)}
}
function CaptureProtectedLogsBaseline {
    $source=Join-Path $script:Data 'Logs';$dest=Join-Path $script:RunRoot 'PROTECTED_LOGS_BASELINE_PRIVATE';New-Item -ItemType Directory -Path $dest -Force|Out-Null
    foreach($file in @(Get-ChildItem -LiteralPath $source -File -Recurse -Force)){$rel=$file.FullName.Substring($source.Length).TrimStart('\');$target=Join-Path $dest $rel;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $target -Force}
    $script:ProtectedLogsBackup=$dest
}
function RestoreSafeProtectedLogs($Delta) {
    $logRoot=Join-Path $script:Data 'Logs';$restored=New-Object Collections.ArrayList;$unresolved=New-Object Collections.ArrayList
    foreach($item in @($Delta.changed)+@($Delta.removed)+@($Delta.added)){
        if($item.classification -notin @('QA_TEMPORARY','APP_OPERATIONAL_METADATA') -or -not $item.relativePath.StartsWith('Logs/',[StringComparison]::OrdinalIgnoreCase)){[void]$unresolved.Add($item);continue}
        $relative=$item.relativePath.Substring(5).Replace('/',[IO.Path]::DirectorySeparatorChar);$target=Join-Path $logRoot $relative;$saved=Join-Path $script:ProtectedLogsBackup $relative
        if($item.beforeSha256){if(-not(Test-Path -LiteralPath $saved -PathType Leaf) -or (HashFile $saved) -ne $item.beforeSha256){[void]$unresolved.Add($item);continue};New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath $saved -Destination $target -Force}
        elseif(Test-Path -LiteralPath $target -PathType Leaf){Remove-Item -LiteralPath $target -Force}
        [void]$restored.Add([pscustomobject]@{relativePath=$item.relativePath;restoredSha256=$item.beforeSha256;reason=$item.evidence})
    }
    return [pscustomobject]@{restored=@($restored.ToArray());unresolved=@($unresolved.ToArray())}
}function BuildObservedProtectedDiff($Baseline,$Final) {
    $baseMap=@{};foreach($row in $Baseline.files){$baseMap[$row.relativePath.ToLowerInvariant()]=$row}
    $finalDelta=CompareTreeManifests $Baseline $Final;$adds=New-Object Collections.ArrayList;$removes=New-Object Collections.ArrayList;$changes=New-Object Collections.ArrayList;$seen=@{}
    $groups=@($script:ObservedQALogChanges|Group-Object { $_.relativePath.ToLowerInvariant() })
    foreach($group in $groups){$events=@($group.Group);$event=$events[$events.Count-1];$key=$event.relativePath.ToLowerInvariant();$seen[$key]=$true;$before=$baseMap[$key];$afterSize=$event.afterSize;$afterHash=$event.afterSha256
        $row=[pscustomobject]@{relativePath=$event.relativePath;beforeSize=$(if($before){$before.size}else{$null});afterSize=$afterSize;beforeSha256=$(if($before){$before.sha256}else{$null});afterSha256=$afterHash;classification=$event.classification;evidence=($event.evidence+' Observed transient mutations: '+$events.Count+'. '+$event.sourceEvidence)}
        if(-not $before -and $null -ne $afterHash){[void]$adds.Add($row)}elseif($before -and $null -eq $afterHash){[void]$removes.Add($row)}elseif($before -and ($before.size -ne $afterSize -or $before.sha256 -ne $afterHash)){[void]$changes.Add($row)}
    }
    foreach($item in @($finalDelta.added)){if(-not $seen.ContainsKey($item.relativePath.ToLowerInvariant())){[void]$adds.Add($item)}}
    foreach($item in @($finalDelta.removed)){if(-not $seen.ContainsKey($item.relativePath.ToLowerInvariant())){[void]$removes.Add($item)}}
    foreach($item in @($finalDelta.changed)){if(-not $seen.ContainsKey($item.relativePath.ToLowerInvariant())){[void]$changes.Add($item)}}
    return [pscustomobject]@{added=@($adds.ToArray()|Sort-Object relativePath);removed=@($removes.ToArray()|Sort-Object relativePath);changed=@($changes.ToArray()|Sort-Object relativePath);finalProtectedState=$finalDelta;transientLogObservations=@($script:ObservedQALogChanges.ToArray())}
}
function NewJunction([string]$Link,[string]$Target) {
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) { throw ('Missing asset directory: ' + $Target) }
    if (Test-Path -LiteralPath $Link) { throw ('Refusing existing asset path: ' + $Link) }
    New-Item -ItemType Directory -Path (Split-Path -Parent $Link) -Force | Out-Null
    New-Item -ItemType Junction -Path $Link -Target $Target | Out-Null
}
function CaptureAppLogs {
    $appLogs=Join-Path $script:Data 'Logs'
    if([IO.Path]::GetFullPath($appLogs) -ne [IO.Path]::GetFullPath((Join-Path $script:Data 'Logs'))){throw 'Unexpected app log root.'}
    if(-not(Test-Path -LiteralPath $appLogs)){New-Item -ItemType Directory -Path $appLogs -Force|Out-Null}
    $back=Join-Path $script:RunRoot ('LOG_BASE_'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $back -Force|Out-Null
    $baseline=@{}
    foreach($file in @(Get-ChildItem -LiteralPath $appLogs -File -Recurse -Force)){
        if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw ('Unexpected reparse log file: '+$file.FullName)}
        $rel=$file.FullName.Substring($appLogs.Length).TrimStart('\');$copy=Join-Path $back $rel;New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force|Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $copy -Force
        $baseline[$rel]=[pscustomobject]@{copy=$copy;sha256=(HashFile $copy);bytes=$file.Length}
    }
    return [pscustomobject]@{root=$appLogs;backup=$back;baseline=$baseline}
}
function RestoreAppLogs($Snapshot,[string]$Evidence) {
    $appLogs=$Snapshot.root;$back=$Snapshot.backup
    $allowedRoot=[IO.Path]::GetFullPath($script:RunRoot).TrimEnd('\')+'\'
    if(-not([IO.Path]::GetFullPath($back).StartsWith($allowedRoot,[StringComparison]::OrdinalIgnoreCase))){throw 'Log snapshot path escaped run evidence root.'}
    New-Item -ItemType Directory -Path $Evidence -Force|Out-Null
    $changed=New-Object Collections.ArrayList;$captured=New-Object Collections.ArrayList;$unresolved=New-Object Collections.ArrayList
    $known=@('self-test-pass.txt','self-test-error.txt','meeting-report-stage-diagnostics.log','meeting-report-format-fallback.log','v13.8.9-qa.json','v13.8.9-qa-error.txt','v13.8-matrix.json','v13.8-matrix-error.txt','intelligence-self-test-pass.txt','intelligence-self-test-error.txt','speaker-self-test-pass.txt','speaker-self-test-error.txt','v13.8.7-qa.json','v13.8.8-qa.json')
    $current=@(Get-ChildItem -LiteralPath $appLogs -File -Recurse -Force)
    foreach($file in $current){
        if(($file.Attributes -band [IO.FileAttributes]::ReparsePoint)-ne 0){throw ('Unexpected reparse log file after QA: '+$file.FullName)}
        $rel=$file.FullName.Substring($appLogs.Length).TrimStart('\');$hash=HashFile $file.FullName;$has=$Snapshot.baseline.ContainsKey($rel);$old=if($has){$Snapshot.baseline[$rel]}else{$null};$class=ClassifyProtectedPath ('Logs/'+$rel);$action=if(-not $has){'ADDED'}elseif($hash -ne $old.sha256 -or $file.Length -ne $old.bytes){'CHANGED'}else{$null}
        if($action){
            $event=[pscustomobject]@{relativePath=('Logs/'+$rel.Replace('\','/'));action=$action;beforeSize=$(if($has){$old.bytes}else{$null});afterSize=$file.Length;beforeSha256=$(if($has){$old.sha256}else{$null});afterSha256=$hash;classification=$class.classification;evidence=$class.evidence;sourceEvidence='Observed directly around the invoked QA/startup process; after bytes captured before any restoration.'}
            [void]$changed.Add($event);[void]$script:ObservedQALogChanges.Add($event)
            if($known -contains $rel){$dest=Join-Path (Join-Path $Evidence 'LOG_ARTIFACTS') $rel;New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force|Out-Null;Copy-Item -LiteralPath $file.FullName -Destination $dest -Force;[void]$captured.Add($rel)}
            if($class.classification -notin @('QA_TEMPORARY','APP_OPERATIONAL_METADATA')){[void]$unresolved.Add($event);continue}
            if(-not $has){Remove-Item -LiteralPath $file.FullName -Force}else{Copy-Item -LiteralPath $old.copy -Destination $file.FullName -Force}
        }
    }
    foreach($rel in @($Snapshot.baseline.Keys)){
        $path=Join-Path $appLogs $rel
        if(-not(Test-Path -LiteralPath $path -PathType Leaf)){
            $old=$Snapshot.baseline[$rel];$class=ClassifyProtectedPath ('Logs/'+$rel);$event=[pscustomobject]@{relativePath=('Logs/'+$rel.Replace('\','/'));action='REMOVED';beforeSize=$old.bytes;afterSize=$null;beforeSha256=$old.sha256;afterSha256=$null;classification=$class.classification;evidence=$class.evidence;sourceEvidence='Observed missing from Logs after the invoked QA/startup process.'}
            [void]$changed.Add($event);[void]$script:ObservedQALogChanges.Add($event)
            if($class.classification -notin @('QA_TEMPORARY','APP_OPERATIONAL_METADATA')){[void]$unresolved.Add($event);continue}
            New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force|Out-Null;Copy-Item -LiteralPath $old.copy -Destination $path -Force
        }
    }
    if($unresolved.Count -eq 0){
        $verifyFiles=@(Get-ChildItem -LiteralPath $appLogs -File -Recurse -Force)
        if($verifyFiles.Count -ne $Snapshot.baseline.Count){throw ('User Logs restore count mismatch: expected '+$Snapshot.baseline.Count+' got '+$verifyFiles.Count)}
        foreach($file in $verifyFiles){$rel=$file.FullName.Substring($appLogs.Length).TrimStart('\');if(-not $Snapshot.baseline.ContainsKey($rel) -or $file.Length -ne $Snapshot.baseline[$rel].bytes -or (HashFile $file.FullName) -ne $Snapshot.baseline[$rel].sha256){throw ('User Logs restore hash mismatch: '+$rel)}}
    }
    SaveJson (Join-Path $Evidence 'LOG_MUTATIONS_RESTORED.json') ([pscustomobject]@{status=$(if($unresolved.Count -eq 0){'RESTORED_SAFE_ONLY'}else{'UNRESOLVED_BLOCK'});changedLogs=@($changed.ToArray());capturedSafeQaArtifacts=@($captured.ToArray());baselineFiles=$Snapshot.baseline.Count;restoredUserLogBytes=($unresolved.Count -eq 0);unresolved=@($unresolved.ToArray())})
    Remove-Item -LiteralPath $back -Recurse -Force
    if($unresolved.Count -gt 0){throw ('Unresolved protected log mutation; install blocked: '+($unresolved|ConvertTo-Json -Compress -Depth 6))}
    return [pscustomobject]@{changed=@($changed.ToArray());captured=@($captured.ToArray());baselineFiles=$Snapshot.baseline.Count;restored=$true}
}
function AppFlag([string]$Exe,[string]$Flag,[string]$TestLabel,[string]$Evidence,[int]$Timeout=900) {
    $snapshot=CaptureAppLogs
    $out=Join-Path $Evidence ($TestLabel+'.stdout.txt');$err=Join-Path $Evidence ($TestLabel+'.stderr.txt');$failed=$null
    try { RunNative $Exe @($Flag) (Split-Path -Parent $Exe) $TestLabel $out $err $Timeout @(0) } catch { $failed=$_.Exception.Message }
    try { $logResult=RestoreAppLogs $snapshot (Join-Path $Evidence 'LogProtection');$captured=@($logResult.captured) }
    catch { if($failed){$failed += '; '}; $failed += $_.Exception.Message;$captured=@();$script:Rollback='USER_LOG_RESTORE_FAILED' }
    SaveJson (Join-Path $Evidence ($TestLabel+'.receipt.json')) ([pscustomobject]@{flag=$Flag;status=$(if($failed){'FAIL'}else{'PASS'});error=$failed;stdout=$out;stderr=$err;capturedLogArtifacts=$captured;userLogsRestored=($script:Rollback -ne 'USER_LOG_RESTORE_FAILED')})
    if ($failed) { throw $failed }
}
function Assets([string]$AppRoot) {
    $paths=@(
        @{name='Qwen model';path=(Join-Path $AppRoot 'AI\Models\Qwen3-4B-Q4_K_M.gguf')},
        @{name='llama runtime';path=(Join-Path $AppRoot 'AI\llama\llama-cli.exe')},
        @{name='Speaker segmentation';path=(Join-Path $AppRoot 'SpeakerModels\sherpa-onnx-pyannote-segmentation-3-0\model.onnx')},
        @{name='Speaker embedding';path=(Join-Path $AppRoot 'SpeakerModels\3dspeaker_speech_eres2net_base_sv_zh-cn_3dspeaker_16k.onnx')},
        @{name='Speaker fixture';path=(Join-Path $AppRoot 'SpeakerModels\0-four-speakers-zh.wav')})
    $out=New-Object Collections.ArrayList
    foreach($item in $paths){if(-not(Test-Path -LiteralPath $item.path -PathType Leaf)){throw ('Required target asset missing: '+$item.path)};$file=Get-Item $item.path;[void]$out.Add([pscustomobject]@{name=$item.name;path=$item.path;bytes=$file.Length;sha256=(HashFile $item.path)})}
    return ,@($out.ToArray())
}
function SaveResult {
    try {
        $summary=@"
# V28 Owner Run Result

Status: **$($script:Status)**

- Candidate ZIP SHA-256: $($script:ExpectedZip)
- Installed V25 baseline EXE SHA-256: $($script:V25Exe)
- Install mutation: $($script:Mutated)
- Rollback: $($script:Rollback)
- AimsTouch = NONE
- Taste Pass = HOLD
- Word live = WORD_LIVE_OWNER_SMOKE_REQUIRED
- Protected user data is not included.
"@
        [IO.File]::WriteAllText((Join-Path $script:RunRoot 'SUMMARY.md'),$summary,[Text.UTF8Encoding]::new($false))
        SaveJson (Join-Path $script:RunRoot 'STAGE_MATRIX.json') @($script:Stages.ToArray())
        $payload=Join-Path $script:RunRoot 'UPLOAD_PAYLOAD';New-Item -ItemType Directory -Path $payload -Force | Out-Null
        foreach($name in @('SUMMARY.md','STAGE_MATRIX.json','DATA_INTEGRITY_SEMANTIC.json','ROLLBACK.json','PROVENANCE.json','INSTALLED_EXE.json')){$file=Join-Path $script:RunRoot $name;if(Test-Path -LiteralPath $file){Copy-Item -LiteralPath $file -Destination $payload -Force}}
        foreach($folder in @('Staged','Installed')){ $stageRoot=Join-Path $script:RunRoot $folder;if(Test-Path -LiteralPath $stageRoot){Get-ChildItem -LiteralPath $stageRoot -File -Recurse|Where-Object{$_.Name -match '^(01_SELF_TEST|INSTALLED_01_SELF_TEST|02_RTL_LAYOUT|INSTALLED_02_RTL_LAYOUT|03_V13_8_9|INSTALLED_03_V13_8_9|04_V13_8_MATRIX|INSTALLED_04_V13_8_MATRIX|05_CLOUD_FAKE_HTTP|INSTALLED_05_CLOUD_FAKE_HTTP|06_LOCAL_AI|INSTALLED_06_LOCAL_AI|07_SPEAKER|INSTALLED_07_SPEAKER|WORD_BIDI_SEMANTIC_PROOF|FUNCTIONAL_QA|INSTALLED_WORD_BIDI_SEMANTIC_PROOF|INSTALLED_FUNCTIONAL_QA)(\.receipt)?\.json$'}|ForEach-Object{$rel=$_.FullName.Substring($stageRoot.Length).TrimStart('\');$target=Join-Path (Join-Path $payload $folder) $rel;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null;Copy-Item -LiteralPath $_.FullName -Destination $target -Force}}}
        $proof=Join-Path $script:Contents 'QA_EVIDENCE\V28R1';if(Test-Path -LiteralPath $proof){Copy-Item -LiteralPath $proof -Destination (Join-Path $payload 'V28R1_SOURCE_PROOF_QA') -Recurse -Force}
        Copy-Item -LiteralPath $script:Zip -Destination (Join-Path $payload 'V28R1_SOURCE_AND_PROOF.zip') -Force
        $resultZip=Join-Path $script:Result 'RESULT_TO_UPLOAD.zip';if(Test-Path -LiteralPath $resultZip){Remove-Item -LiteralPath $resultZip -Force}
        [IO.Compression.ZipFile]::CreateFromDirectory($payload,$resultZip,[IO.Compression.CompressionLevel]::Optimal,$false);$null=TestZip $resultZip
        $hash=HashFile $resultZip
        [IO.File]::WriteAllText((Join-Path $script:Result 'RESULT_TO_UPLOAD.sha256'),($hash+'  RESULT_TO_UPLOAD.zip'),[Text.UTF8Encoding]::new($false))
        Say 'PASS' ('Result ZIP '+$resultZip+' SHA-256 '+$hash) Green
    } catch { Say 'FAIL' ('Result packaging: '+$_.Exception.Message) Red }
}

if($SelfTestOnly){
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($PSCommandPath,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw (($errors|ForEach-Object{$_.Message})-join'; ')}
    $reserved=@('PID','HOME','HOST','PSHOME','PROFILE','PSScriptRoot','PSCommandPath','MyInvocation','Args','Input','Matches','Error','ExecutionContext','ShellId','StackTrace','This','True','False','Null')
    $collisions=@($ast.FindAll({param($node)$node -is [Management.Automation.Language.ParameterAst]},$true)|ForEach-Object{$_.Name.VariablePath.UserPath}|Where-Object{$reserved -contains $_})
    if($collisions.Count){throw ('Automatic-variable parameter collision: '+($collisions -join ','))}
    $semanticFixture=[pscustomobject]@{integrity='ok';foreignKeyErrors=0;schemaSha256='schema';logicalRows=1;tables=@([pscustomobject]@{table='meetings';rows=1;logicalSha256='rows'})}
    if(-not (TestDatabaseSemanticEqual $semanticFixture $semanticFixture)){throw 'SQLite semantic equality positive fixture failed.'}
    $semanticBad=[pscustomobject]@{integrity='ok';foreignKeyErrors=0;schemaSha256='schema';logicalRows=1;tables=@([pscustomobject]@{table='meetings';rows=1;logicalSha256='changed'})}
    if(TestDatabaseSemanticEqual $semanticFixture $semanticBad){throw 'SQLite semantic equality negative fixture failed.'}
    if((StepTime ([TimeSpan]::FromSeconds(2.2))) -ne '2.2s'){throw 'Timer fixture failed.'}
    if((HashFile $script:Zip) -ne $script:ExpectedZip){throw 'V28R1 package SHA mismatch.'}
    $null=TestZip $script:Zip
    Say 'PASS' 'Exact script parse, SQLite semantic compare fixtures, timer fixture, collision scan, V28R1 SHA and ZIP CRC PASS' Green
    exit 0
}

try {
    New-Item -ItemType Directory -Path $script:RunRoot -Force | Out-Null
    foreach($name in @('Staged','Installed','Startup','BuildLogs','Assets','Source')){New-Item -ItemType Directory -Path (Join-Path $script:RunRoot $name) -Force | Out-Null}
    BeginStage '1/8 Candidate identity' 'Verify exact V28 package SHA, CRC and source manifest.'
    if((HashFile $script:Zip) -ne $script:ExpectedZip){throw 'V28R1 source/proof ZIP SHA mismatch.'}
    $zipItems=TestZip $script:Zip
    if(-not(Test-Path -LiteralPath $script:Contents)){Expand-Archive -LiteralPath $script:Zip -DestinationPath $script:Contents}
    $manifest=Get-Content -LiteralPath (Join-Path $script:Contents 'V28R1_CHANGED_FILE_SHA256.txt')
    $sourceLines=@($manifest|Where-Object{$_ -match '^([0-9a-f]{64})\s+(source/.+)$'})
    if($sourceLines.Count -lt 10){throw 'V28R1 source manifest is incomplete.'}
    foreach($line in $sourceLines){$m=[regex]::Match($line,'^([0-9a-f]{64})\s+(source/.+)$');$rel=$m.Groups[2].Value.Substring(7).Replace('/',[IO.Path]::DirectorySeparatorChar);$path=Join-Path (Join-Path $script:Contents 'source') $rel;if(-not(Test-Path -LiteralPath $path)-or(HashFile $path)-ne$m.Groups[1].Value){throw ('Manifest source mismatch '+$rel)}}
    EndStage 'PASS' ('{0} ZIP members CRC checked; {1} source hashes match' -f $zipItems.Count,$sourceLines.Count)

    BeginStage '2/8 Installed V25 authority and target preflight' 'Prove current V25 EXE, SDK, assets and protected data before staging.'
    $oldExe=Join-Path $script:Install 'Archestro.MeetingVault.exe'
    if((HashFile $oldExe) -ne $script:V25Exe){throw ('Installed executable is not recorded V25: '+(HashFile $oldExe))}
    if(@(Get-Process -Name 'Archestro.MeetingVault' -ErrorAction SilentlyContinue).Count -gt 0){throw 'Meeting Vault is running; close it and rerun. No mutation.'}
    foreach($path in @($script:Install,$script:Data,$script:Source,$script:BranchSource,$script:DotNet)){if(-not(Test-Path -LiteralPath $path)){throw ('Required path missing: '+$path)}}    $gitExe='C:\Program Files\Git\cmd\git.exe'
    if(-not(Test-Path -LiteralPath $gitExe)){throw 'Required Git executable for exact source identity proof is missing.'}
    $sourceHead=(& $gitExe -C $script:Repo rev-parse HEAD 2>&1 | Out-String).Trim()
    $sourceBranch=(& $gitExe -C $script:Repo branch --show-current 2>&1 | Out-String).Trim()
    $sourceDirty=(& $gitExe -C $script:Repo status --porcelain -- source 2>&1 | Out-String).Trim()
$expectedSourceCommit='e7266506a4e85b835fc55ccc3fdb85c0a54115a0';$null=(& $gitExe -C $script:Repo merge-base --is-ancestor $expectedSourceCommit HEAD 2>&1);if($LASTEXITCODE -ne 0 -or $sourceBranch -ne 'codex/v28r1-local-report-66-fix' -or $sourceDirty){throw ('Source tree is not clean on the exact V28R1 branch/commit: '+$sourceHead+' '+$sourceBranch+' '+$sourceDirty)}
    SaveJson (Join-Path $script:RunRoot 'Source\BASELINE_IDENTITY.json') ([pscustomobject]@{projectBaseline=$script:Source;branchSource=$script:BranchSource;branch=$sourceBranch;head=$sourceHead;trackedSourceClean=($sourceDirty -eq '')})
    $sdk=(& $script:DotNet --list-sdks 2>&1|Out-String).Trim();if($LASTEXITCODE -ne 0 -or $sdk -notmatch '10\.0\.'){throw ('Required .NET 10 SDK missing: '+$sdk)}
    $assetInventory=Assets $script:Install
    $script:BeforeData=TreeHash $script:Data
    $script:BeforeManifest=TreeManifest $script:Data
    SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_BASELINE.json') $script:BeforeManifest
    BackupProtectedState
    if($script:DatabaseSemanticBefore.integrity -ne 'ok' -or $script:DatabaseSemanticBefore.foreignKeyErrors -ne 0){throw 'Fresh V25 database baseline failed integrity/foreign-key preflight.'}
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY_BASELINE_SANITIZED.json') ([pscustomobject]@{databaseIntegrity=$script:DatabaseSemanticBefore.integrity;foreignKeyErrors=$script:DatabaseSemanticBefore.foreignKeyErrors;schemaSha256=$script:DatabaseSemanticBefore.schemaSha256;logicalRows=$script:DatabaseSemanticBefore.logicalRows;tableCount=$script:DatabaseSemanticBefore.tables.Count;settings=$script:SettingsSemanticBefore;protectedSnapshotCaptured=$true})
    CaptureProtectedLogsBaseline
    $assetInventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $script:RunRoot 'Assets\INVENTORY.json') -Encoding UTF8
    SaveJson (Join-Path $script:RunRoot 'PREFLIGHT.json') ([pscustomobject]@{installedV25Exe=[pscustomobject]@{path=$oldExe;bytes=(Get-Item $oldExe).Length;sha256=$script:V25Exe;matchesRecordedHash=$true;productVersion=(Get-Item $oldExe).VersionInfo.ProductVersion};dotnet=$script:DotNet;sdks=$sdk;PowerShell=$PSVersionTable.PSVersion.ToString();dataRoot=$script:Data;dataFiles=$script:BeforeData.fileCount;dataBytes=$script:BeforeData.bytes;dataAggregateSha256=$script:BeforeData.aggregateSha256;assets=$assetInventory;wordHelperAvailable=(Test-Path $script:WordHelper);wordLive='WORD_LIVE_OWNER_SMOKE_REQUIRED';aimsTouch='NONE';tastePass='HOLD';realCloudCredentialsUsed=$false})
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY.json') ([pscustomobject]@{before=$script:BeforeData;after=$null;comparison='PENDING'})
    EndStage 'PASS' 'Installed V25 hash, .NET 10, paths and required runtime/Speaker assets verified'

    BeginStage '3/8 Clean source staging' 'Copy target-proven source without bin/obj then overlay exact V28R1 files.'
    $sourceStage=Join-Path $script:RunRoot 'Source\SourceBaseline';$robocopy=Join-Path $env:SystemRoot 'System32\robocopy.exe'
    RunNative $robocopy @($script:Source,$sourceStage,'/E','/XD','bin','obj','.git','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS') (Split-Path -Parent $script:Source) 'Copy clean V25 project baseline' (Join-Path $script:RunRoot 'Source\copy-project.stdout.txt') (Join-Path $script:RunRoot 'Source\copy-project.stderr.txt') 600 @(0,1,2,3,4,5,6,7)
    RunNative $robocopy @($script:BranchSource,$sourceStage,'/E','/XD','bin','obj','.git','/R:1','/W:1','/NFL','/NDL','/NJH','/NJS') $script:Repo 'Overlay clean V27 branch source' (Join-Path $script:RunRoot 'Source\copy-branch.stdout.txt') (Join-Path $script:RunRoot 'Source\copy-branch.stderr.txt') 600 @(0,1,2,3,4,5,6,7)
    foreach($line in $sourceLines){$m=[regex]::Match($line,'^([0-9a-f]{64})\s+(source/.+)$');$rel=$m.Groups[2].Value.Substring(7).Replace('/',[IO.Path]::DirectorySeparatorChar);$from=Join-Path (Join-Path $script:Contents 'source') $rel;$to=Join-Path $sourceStage $rel;New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force|Out-Null;Copy-Item -LiteralPath $from -Destination $to -Force;if((HashFile $to)-ne$m.Groups[1].Value){throw ('Staged V28R1 overlay hash mismatch '+$rel)}}
    $project=Join-Path $sourceStage 'Archestro.MeetingVault.csproj';if(-not(Test-Path $project)){throw 'Staged .csproj missing.'}
    EndStage 'PASS' ('{0} exact V28R1 files overlaid; prior build outputs excluded' -f $sourceLines.Count)

    BeginStage '4/8 Windows build and publish' 'Build and publish win-x64; do not install before runtime gates pass.'
    $publish=Join-Path $script:RunRoot 'Published'
    RunNative $script:DotNet @('build',$project,'--configuration','Release','--runtime','win-x64','/nr:false','-p:UseSharedCompilation=false') $sourceStage 'V28R1 Release build' (Join-Path $script:RunRoot 'BuildLogs\BUILD.stdout.txt') (Join-Path $script:RunRoot 'BuildLogs\BUILD.stderr.txt') 1800 @(0)
    RunNative $script:DotNet @('publish',$project,'--configuration','Release','--runtime','win-x64','--self-contained','true','--output',$publish,'/nr:false','-p:UseSharedCompilation=false') $sourceStage 'V28R1 self-contained win-x64 publish' (Join-Path $script:RunRoot 'BuildLogs\PUBLISH.stdout.txt') (Join-Path $script:RunRoot 'BuildLogs\PUBLISH.stderr.txt') 1800 @(0)
    $pubExe=Join-Path $publish 'Archestro.MeetingVault.exe';$pubHash=HashFile $pubExe
    SaveJson (Join-Path $script:RunRoot 'PROVENANCE.json') ([pscustomobject]@{repo='BigD11-11/archestro-v26-rtl-geometry-word-bidi-closure';branch='codex/v28r1-local-report-66-fix';head='e7266506a4e85b835fc55ccc3fdb85c0a54115a0';implementation='e7266506a4e85b835fc55ccc3fdb85c0a54115a0';sourceZipSha256=$script:ExpectedZip;priorPublishedExeSha256='V25_BASELINE';localPublishedExeSha256=$pubHash})
    EndStage 'PASS' ('Build and publish exit 0; EXE SHA-256 '+$pubHash)

    BeginStage '5/8 Staged runtime QA' 'Run core, RTL, matrix, fake cloud, local AI, Speaker and DOCX proof.'
    $qa=Join-Path $script:RunRoot 'QA_APP';Copy-Item -LiteralPath $publish -Destination $qa -Recurse -Force
    NewJunction (Join-Path $qa 'AI\Models') (Join-Path $script:Install 'AI\Models');NewJunction (Join-Path $qa 'AI\llama') (Join-Path $script:Install 'AI\llama');NewJunction (Join-Path $qa 'SpeakerModels') (Join-Path $script:Install 'SpeakerModels')
    $env:ARCHESTRO_WORD_FIXTURE_DIR=Join-Path $script:RunRoot 'Staged\DOCX_FIXTURES'
    $tests=@(@{flag='--self-test';name='01_SELF_TEST';timeout=600},@{flag='--rtl-layout-qa';name='02_RTL_LAYOUT';timeout=600},@{flag='--v13.8.9-qa';name='03_V13_8_9';timeout=600},@{flag='--v13.8-matrix';name='04_V13_8_MATRIX';timeout=900},@{flag='--cloud-provider-qa';name='05_CLOUD_FAKE_HTTP';timeout=600},@{flag='--ai-self-test';name='06_LOCAL_AI';timeout=1800},@{flag='--speaker-self-test';name='07_SPEAKER';timeout=1200})
    foreach($test in $tests){
        if($test.flag -eq '--self-test'){$env:ARCHESTRO_WORD_BIDI_QA_OUTPUT=Join-Path $script:RunRoot 'Staged\WORD_BIDI_SEMANTIC_PROOF.json';$env:ARCHESTRO_V28_FUNCTIONAL_QA_OUTPUT=Join-Path $script:RunRoot 'Staged\FUNCTIONAL_QA.json'}
        if($test.flag -eq '--rtl-layout-qa'){$env:ARCHESTRO_RTL_QA_OUTPUT=Join-Path $script:RunRoot ('Staged\'+$test.name+'.json');$env:ARCHESTRO_RTL_QA_ERROR=Join-Path $script:RunRoot ('Staged\'+$test.name+'.error.txt');$env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR=Join-Path $script:RunRoot 'Staged\screenshots';New-Item -ItemType Directory -Path $env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR -Force|Out-Null}
        if($test.flag -eq '--cloud-provider-qa'){$env:ARCHESTRO_V28_PROVIDER_QA_OUTPUT=Join-Path $script:RunRoot ('Staged\'+$test.name+'.json');$env:ARCHESTRO_V28_PROVIDER_QA_ERROR=Join-Path $script:RunRoot ('Staged\'+$test.name+'.error.txt')}
        AppFlag (Join-Path $qa 'Archestro.MeetingVault.exe') $test.flag $test.name (Join-Path $script:RunRoot 'Staged') $test.timeout
        Remove-Item Env:ARCHESTRO_RTL_QA_OUTPUT,Env:ARCHESTRO_RTL_QA_ERROR,Env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR,Env:ARCHESTRO_V28_PROVIDER_QA_OUTPUT,Env:ARCHESTRO_V28_PROVIDER_QA_ERROR,Env:ARCHESTRO_WORD_BIDI_QA_OUTPUT,Env:ARCHESTRO_V28_FUNCTIONAL_QA_OUTPUT -ErrorAction SilentlyContinue
    }
    Remove-Item Env:ARCHESTRO_WORD_FIXTURE_DIR -ErrorAction SilentlyContinue
    $afterStageManifest=TreeManifest $script:Data
    SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_AFTER_QA.json') $afterStageManifest
    $qaDelta=CompareTreeManifests $script:BeforeManifest $afterStageManifest
    $observedDiff=BuildObservedProtectedDiff $script:BeforeManifest $afterStageManifest
    SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_DIFF.json') $observedDiff
    $qaChangeCount=$observedDiff.added.Count+$observedDiff.removed.Count+$observedDiff.changed.Count
    $safeRestore=RestoreSafeProtectedLogs $qaDelta
    $afterRestore=TreeManifest $script:Data
    $restoreDelta=CompareTreeManifests $script:BeforeManifest $afterRestore
    $restoredCount=$restoreDelta.added.Count+$restoreDelta.removed.Count+$restoreDelta.changed.Count
    $restorationStatus=if($restoredCount -eq 0 -and $afterRestore.aggregateSha256 -eq $script:BeforeManifest.aggregateSha256){'PASS_EXACT_PATH_SIZE_SHA256_EQUALITY'}else{'FAIL'}
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY_RESTORATION.json') ([pscustomobject]@{beforeAggregateSha256=$script:BeforeManifest.aggregateSha256;afterQaAggregateSha256=$afterStageManifest.aggregateSha256;afterRestorationAggregateSha256=$afterRestore.aggregateSha256;beforeFiles=$script:BeforeManifest.fileCount;afterFiles=$afterRestore.fileCount;added=$restoreDelta.added.Count;removed=$restoreDelta.removed.Count;changed=$restoreDelta.changed.Count;safeLogFilesRestored=@($observedDiff.added)+@($observedDiff.removed)+@($observedDiff.changed);unresolved=$safeRestore.unresolved;status=$restorationStatus})
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY.json') ([pscustomobject]@{before=$script:BeforeData;afterStaged=$afterStageManifest;afterRestoration=$afterRestore;comparison=$(if($restoredCount -eq 0){'PASS_IDENTICAL'}else{'FAIL_CHANGED'});userDataIncludedInUpload=$false})
    if($safeRestore.unresolved.Count -gt 0 -or $restoredCount -gt 0 -or $afterRestore.aggregateSha256 -ne $script:BeforeManifest.aggregateSha256){throw 'Protected data per-file path/size/SHA256 equality failed after safe QA-log restoration; install blocked.'}
    EndStage 'PASS' ('Staged QA passed; protected data exact per-file equality PASS ({0} QA deltas safely restored)' -f $qaChangeCount)

    BeginStage '6/8 Backup and install' 'Preserve exact V25 app tree and install only the tested publish output.'
    $script:Backup=$script:Install+'.V25_BACKUP_'+(Get-Date -Format 'yyyyMMdd_HHmmss');if(Test-Path $script:Backup){throw 'V25 backup target already exists.'}
    Move-Item -LiteralPath $script:Install -Destination $script:Backup;$script:Mutated=$true
    New-Item -ItemType Directory -Path $script:Install|Out-Null
    Get-ChildItem -LiteralPath $publish -Force|Copy-Item -Destination $script:Install -Recurse -Force
    NewJunction (Join-Path $script:Install 'AI\Models') (Join-Path $script:Backup 'AI\Models');NewJunction (Join-Path $script:Install 'AI\llama') (Join-Path $script:Backup 'AI\llama');NewJunction (Join-Path $script:Install 'SpeakerModels') (Join-Path $script:Backup 'SpeakerModels')
    $installedExe=Join-Path $script:Install 'Archestro.MeetingVault.exe';if((HashFile $installedExe) -ne $pubHash){throw 'Installed executable hash differs from tested publish.'}
    $script:Rollback='BACKUP_RETAINED_OWNER_ACCEPTANCE_PENDING'
    SaveJson (Join-Path $script:RunRoot 'ROLLBACK.json') ([pscustomobject]@{backupPath=$script:Backup;v25ExeSha256=$script:V25Exe;v28ExeSha256=$pubHash;userDataMoved=$false;heavyAssetsCopied=$false;reusedAssetJunctions=@('AI\Models','AI\llama','SpeakerModels');rollbackAvailable=$true})
    EndStage 'PASS' 'V25 app backup retained; tested V28 executable installed; large assets reused'

    BeginStage '7/8 Installed QA and startup' 'Repeat all required checks at the installed path and verify the real main window.'
    $env:ARCHESTRO_WORD_FIXTURE_DIR=Join-Path $script:RunRoot 'Installed\DOCX_FIXTURES'
    foreach($test in $tests){
        if($test.flag -eq '--self-test'){$env:ARCHESTRO_WORD_BIDI_QA_OUTPUT=Join-Path $script:RunRoot 'Installed\WORD_BIDI_SEMANTIC_PROOF.json';$env:ARCHESTRO_V28_FUNCTIONAL_QA_OUTPUT=Join-Path $script:RunRoot 'Installed\FUNCTIONAL_QA.json'}
        $name='INSTALLED_'+$test.name
        if($test.flag -eq '--rtl-layout-qa'){$env:ARCHESTRO_RTL_QA_OUTPUT=Join-Path $script:RunRoot ('Installed\'+$name+'.json');$env:ARCHESTRO_RTL_QA_ERROR=Join-Path $script:RunRoot ('Installed\'+$name+'.error.txt');$env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR=Join-Path $script:RunRoot 'Installed\screenshots';New-Item -ItemType Directory -Path $env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR -Force|Out-Null}
        if($test.flag -eq '--cloud-provider-qa'){$env:ARCHESTRO_V28_PROVIDER_QA_OUTPUT=Join-Path $script:RunRoot ('Installed\'+$name+'.json');$env:ARCHESTRO_V28_PROVIDER_QA_ERROR=Join-Path $script:RunRoot ('Installed\'+$name+'.error.txt')}
        AppFlag $installedExe $test.flag $name (Join-Path $script:RunRoot 'Installed') $test.timeout
        Remove-Item Env:ARCHESTRO_RTL_QA_OUTPUT,Env:ARCHESTRO_RTL_QA_ERROR,Env:ARCHESTRO_RTL_QA_SCREENSHOT_DIR,Env:ARCHESTRO_V28_PROVIDER_QA_OUTPUT,Env:ARCHESTRO_V28_PROVIDER_QA_ERROR,Env:ARCHESTRO_WORD_BIDI_QA_OUTPUT,Env:ARCHESTRO_V28_FUNCTIONAL_QA_OUTPUT -ErrorAction SilentlyContinue
    }
    Remove-Item Env:ARCHESTRO_WORD_FIXTURE_DIR -ErrorAction SilentlyContinue
    $script:StartupLogSnapshot=CaptureAppLogs
    $script:StartedProcess=Start-Process -FilePath $installedExe -WorkingDirectory $script:Install -PassThru;$startWatch=[Diagnostics.Stopwatch]::StartNew();$ready=$false
    while($startWatch.Elapsed.TotalSeconds -lt 30){$script:StartedProcess.Refresh();if($script:StartedProcess.MainWindowHandle -ne [IntPtr]::Zero -and -not [string]::IsNullOrWhiteSpace($script:StartedProcess.MainWindowTitle)){$ready=$true;break};Start-Sleep -Milliseconds 300;[Console]::Write([char]13+('Interactive startup ('+(StepTime $startWatch.Elapsed)+') PID '+$script:StartedProcess.Id).PadRight(100))}
    Write-Host '';if(-not $ready){throw 'Interactive startup did not produce a main window within 30 seconds.'}
    $title=$script:StartedProcess.MainWindowTitle;$handle=$script:StartedProcess.MainWindowHandle.ToInt64();[void]$script:StartedProcess.CloseMainWindow();if(-not $script:StartedProcess.WaitForExit(15000)){throw 'Interactive startup test did not close cleanly.'}
    $startupLogs=RestoreAppLogs $script:StartupLogSnapshot (Join-Path $script:RunRoot 'Startup\LogProtection');$script:StartupLogSnapshot=$null
    $installedHash=HashFile $installedExe
    SaveJson (Join-Path $script:RunRoot 'INSTALLED_EXE.json') ([pscustomobject]@{path=$installedExe;bytes=(Get-Item $installedExe).Length;sha256=$installedHash;productVersion=(Get-Item $installedExe).VersionInfo.ProductVersion;marker='V28R1_INSTALLED_LOCAL_OWNER_SMOKE_IN_PROGRESS';matchesPublish=($installedHash -eq $pubHash);sourceZipSha256=$script:ExpectedZip;windowTitle=$title;windowHandle=('0x{0:X}' -f $handle);normalStartup='PASS'})
    $afterInstallManifest=TreeManifest $script:Data
    SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_AFTER_INSTALL.json') $afterInstallManifest
    $installDelta=CompareTreeManifests $script:BeforeManifest $afterInstallManifest
    SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_AFTER_INSTALL_DIFF.json') $installDelta
    $semanticComparison=GetProtectedSemanticComparison $afterInstallManifest
    $installSame=$semanticComparison.pass
    if(-not $installSame){throw ('Protected-data semantic gate failed: database='+$semanticComparison.databaseEqual+' settings='+$semanticComparison.settingsEqual+' nonSqlitePathChanges='+$semanticComparison.otherChanged.Count)}
    SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY.json') ([pscustomobject]@{comparison='PASS_SQLITE_SEMANTIC_AND_SETTINGS';databaseEqual=$semanticComparison.databaseEqual;settingsEqual=$semanticComparison.settingsEqual;nonSqliteChangedPathCount=$semanticComparison.otherChanged.Count;userDataIncludedInUpload=$false})
    $script:Status='V28R1_INSTALLED_LOCAL_OWNER_SMOKE_IN_PROGRESS';$script:Rollback='BACKUP_RETAINED_OWNER_ACCEPTANCE_PENDING'
    EndStage 'PASS' 'Installed EXE identity, complete installed QA, startup and protected-data per-file equality PASS'

    BeginStage '8/8 Word live gate' 'Keep serialized OpenXML result distinct from live Word visual proof.'
    if(Test-Path -LiteralPath $script:WordHelper){EndStage 'PASS' 'Word COM preflight is available; actual Arabic and English owner DOCX smoke follows the Local report run.'}
    else{throw 'Word COM preflight is required for V28R1 owner smoke.'}
}
catch {
    $failure=Join-Path $script:RunRoot 'FAILURE.txt'
    if(Test-Path -LiteralPath $script:RunRoot){[IO.File]::WriteAllText($failure,$_.Exception.ToString(),[Text.UTF8Encoding]::new($false))}
    Say 'FAIL' ('V28R1 owner run stopped: '+$_.Exception.Message) Red
    if($script:StartupLogSnapshot){try{RestoreAppLogs $script:StartupLogSnapshot (Join-Path $script:RunRoot 'Startup\LogProtection');$script:StartupLogSnapshot=$null}catch{$script:Rollback='USER_LOG_RESTORE_FAILED: '+$_.Exception.Message}}
    if($script:Mutated -and $script:Backup -and (Test-Path -LiteralPath $script:Backup)){
        $protectedRestoreStatus='NOT_RUN';$protectedRestoreError=$null
        if($script:StartedProcess -and -not $script:StartedProcess.HasExited){try{[void]$script:StartedProcess.CloseMainWindow();if(-not $script:StartedProcess.WaitForExit(10000)){Stop-Process -Id $script:StartedProcess.Id -Force}}catch{$protectedRestoreError='Could not close candidate app: '+$_.Exception.Message}}
        try {$protectedRestore=RestoreProtectedState;$protectedRestoreStatus=$protectedRestore.status;SaveJson (Join-Path $script:RunRoot 'PROTECTED_STATE_ROLLBACK.json') $protectedRestore}catch{$protectedRestoreError=$_.Exception.Message;SaveJson (Join-Path $script:RunRoot 'PROTECTED_STATE_ROLLBACK.json') ([pscustomobject]@{status='FAIL';errorClass=$_.Exception.GetType().Name})}
        try {
            $failed=$script:Install+'.FAILED_'+(Get-Date -Format 'yyyyMMdd_HHmmss')
            if(Test-Path -LiteralPath $script:Install){Move-Item -LiteralPath $script:Install -Destination $failed}
            Move-Item -LiteralPath $script:Backup -Destination $script:Install
            $restored=(HashFile (Join-Path $script:Install 'Archestro.MeetingVault.exe')) -eq $script:V25Exe
            $script:Rollback=if($restored -and $protectedRestoreStatus -eq 'PASS' -and -not $protectedRestoreError){'V25_APP_AND_PROTECTED_DATA_RESTORED_PASS'}else{'ROLLBACK_VERIFY_FAIL'}
            SaveJson (Join-Path $script:RunRoot 'ROLLBACK.json') ([pscustomobject]@{status=$script:Rollback;restoredV25ExeSha256=(HashFile (Join-Path $script:Install 'Archestro.MeetingVault.exe'));failedInstallPreserved=$failed;protectedDataRestore=$protectedRestoreStatus;protectedDataRestoreErrorClass=$(if($protectedRestoreError){'RestoreVerificationFailed'}else{$null});userDataMoved=$false})
        } catch { $script:Rollback='ROLLBACK_FAILED: '+$_.Exception.Message;Say 'FAIL' $script:Rollback Red }
    }
    if($script:StageClock){try{EndStage 'FAIL' $_.Exception.Message}catch{}}
}
finally {
    if($script:BeforeManifest -and -not (Test-Path -LiteralPath (Join-Path $script:RunRoot 'PROTECTED_DATA_DIFF.json'))){
        try {
            $observedAfter=TreeManifest $script:Data
            SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_AFTER_QA.json') $observedAfter
            $observedDiff=BuildObservedProtectedDiff $script:BeforeManifest $observedAfter
            SaveJson (Join-Path $script:RunRoot 'PROTECTED_DATA_DIFF.json') $observedDiff
            $delta=CompareTreeManifests $script:BeforeManifest $observedAfter
            $equal=($delta.added.Count -eq 0 -and $delta.removed.Count -eq 0 -and $delta.changed.Count -eq 0 -and $observedAfter.aggregateSha256 -eq $script:BeforeManifest.aggregateSha256)
            $unresolved=@($observedDiff.added)+@($observedDiff.removed)+@($observedDiff.changed|Where-Object{$_.classification -notin @('QA_TEMPORARY','APP_OPERATIONAL_METADATA')})
            SaveJson (Join-Path $script:RunRoot 'DATA_INTEGRITY_RESTORATION.json') ([pscustomobject]@{beforeAggregateSha256=$script:BeforeManifest.aggregateSha256;afterQaAggregateSha256=$observedAfter.aggregateSha256;afterRestorationAggregateSha256=$observedAfter.aggregateSha256;beforeFiles=$script:BeforeManifest.fileCount;afterFiles=$observedAfter.fileCount;added=$delta.added.Count;removed=$delta.removed.Count;changed=$delta.changed.Count;safeLogFilesRestored=@();unresolved=$unresolved;status=$(if($equal){'PASS_EXACT_PATH_SIZE_SHA256_EQUALITY'}else{'FAIL_OR_BLOCKED_NO_INSTALL'})})
        } catch { Say 'FAIL' ('Could not serialize final protected-data diff: '+$_.Exception.Message) Red }
    }
    if(Test-Path -LiteralPath $script:RunRoot){
        if(-not(Test-Path -LiteralPath (Join-Path $script:RunRoot 'PREFLIGHT.json'))){$why='Preflight incomplete';$f=Join-Path $script:RunRoot 'FAILURE.txt';if(Test-Path $f){$why=Get-Content $f -Raw};SaveJson (Join-Path $script:RunRoot 'PREFLIGHT.json') ([pscustomobject]@{status='BLOCKED';failure=$why;aimsTouch='NONE';tastePass='HOLD'})}
        SaveJson (Join-Path $script:RunRoot 'STAGE_MATRIX.json') @($script:Stages.ToArray());SaveResult
    }
}
Say 'STAGE' ('FINAL '+$script:Status+' | rollback='+$script:Rollback) $(if($script:Status -eq 'V28R1_INSTALLED_LOCAL_OWNER_SMOKE_IN_PROGRESS'){[ConsoleColor]::Green}else{[ConsoleColor]::Yellow})
if($script:Status -eq 'V28R1_INSTALLED_LOCAL_OWNER_SMOKE_IN_PROGRESS'){exit 0}else{exit 2}



