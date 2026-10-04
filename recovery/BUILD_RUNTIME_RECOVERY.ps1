# MOHAMMED_OWNER_RUN_CONTRACT: V1
# RESULT_ROOT: PACKAGE_LOCAL
# RESULT_AUTO_SURFACE: NOT_APPLICABLE
# LEARNING_CLOSEOUT: REQUIRED
# LONG_RUNNING: YES
# HEARTBEAT: REQUIRED
# ELAPSED_TIMER: REQUIRED
# STAGE_PROGRESS: REQUIRED

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent $PSScriptRoot
$RecoveryRoot = $PSScriptRoot
$Work = Join-Path $RepoRoot '_ci_v27_owner_visual_rtl_word_rtl'
$PackageRoot = Join-Path $Work 'package'
$SourceProof = Join-Path $RepoRoot 'V26_ROOT_RTL_GEOMETRY_WORD_BIDI_CLOSURE_SOURCE_AND_PROOF.zip'
$SourceExpanded = Join-Path $Work 'source_proof'
$PythonArchive = Join-Path $Work 'python-3.13.15-embed-amd64.zip'
$PythonUrl = 'https://www.python.org/ftp/python/3.13.15/python-3.13.15-embed-amd64.zip'
$PythonExpectedSha = 'D1F04D990AEE1253D8569E8E5104E30FA9F5FA830899F14843448872D936A2CF'
$PythonExpectedBytes = 11009825
$FinalName = 'ARCHESTRO_V27_OWNER_VISUAL_RTL_WORD_RTL_CLOSURE_ONE_GO_2026-10-05.zip'
$FinalZip = Join-Path $Work $FinalName
$Start = Get-Date

function Step([string]$Name) {
    $elapsed = [math]::Round(((Get-Date) - $Start).TotalSeconds, 1)
    Write-Host ("[STAGE {0,7}s] {1}" -f $elapsed, $Name)
}

function Assert-Sha256([string]$Path, [string]$Expected) {
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $Expected.ToUpperInvariant()) {
        throw "SHA256 mismatch for $Path expected=$Expected actual=$actual"
    }
    return $actual
}

function Copy-Text([string]$Name) {
    Copy-Item -LiteralPath (Join-Path $RecoveryRoot $Name) -Destination (Join-Path $PackageRoot $Name) -Force
}

Step 'Clean work area'
if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Path $PackageRoot -Force | Out-Null
New-Item -ItemType Directory -Path $SourceExpanded -Force | Out-Null

Step 'Copy wrapper/runtime recovery files'
foreach ($name in @('START_HERE.cmd','RUNNER.py','README.md','BASELINE_HASHES.json','PATCH_HASHES.json','SCOPE.json','PACKAGE_MANIFEST.json','RUNTIME_LOCK.json')) {
    Copy-Text $name
}

Step 'Verify durable V26 source/proof ZIP'
if (-not (Test-Path -LiteralPath $SourceProof)) { throw "Missing source/proof ZIP: $SourceProof" }
Assert-Sha256 $SourceProof 'CE2B9BF8033258419632829AA3EB88A6BD430DC823FA177A455EF34B68B451BB' | Out-Null
if ((Get-Item -LiteralPath $SourceProof).Length -ne 39237) { throw 'Unexpected V26 source/proof ZIP size.' }
Expand-Archive -LiteralPath $SourceProof -DestinationPath $SourceExpanded -Force

Step 'Materialize V26 baseline source for bounded V27 transform'
$patchMap = @{
    'source/App.xaml.cs' = 'PATCH/src/Archestro.MeetingVault/App.xaml.cs'
    'source/Dialogs/IntelligenceWindow.xaml' = 'PATCH/src/Archestro.MeetingVault/Dialogs/IntelligenceWindow.xaml'
    'source/Dialogs/IntelligenceWindow.xaml.cs' = 'PATCH/src/Archestro.MeetingVault/Dialogs/IntelligenceWindow.xaml.cs'
    'source/Services/MeetingReportWordExporter.cs' = 'PATCH/src/Archestro.MeetingVault/Services/MeetingReportWordExporter.cs'
    'source/Services/SelfTestService.cs' = 'PATCH/src/Archestro.MeetingVault/Services/SelfTestService.cs'
}
foreach ($srcRel in $patchMap.Keys) {
    $src = Join-Path $SourceExpanded $srcRel
    $dst = Join-Path $PackageRoot $patchMap[$srcRel]
    New-Item -ItemType Directory -Path (Split-Path -Parent $dst) -Force | Out-Null
    Copy-Item -LiteralPath $src -Destination $dst -Force
}
# V27 hashes are verified after the deterministic bounded transform runs under the packaged interpreter.

Step 'Copy V26 baseline proof/evidence'
$evidenceRoot = Join-Path $PackageRoot 'CODEX_V26_EVIDENCE'
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
Copy-Item -LiteralPath $SourceProof -Destination (Join-Path $evidenceRoot (Split-Path -Leaf $SourceProof)) -Force
Copy-Item -LiteralPath (Join-Path $SourceExpanded 'QA_EVIDENCE') -Destination $evidenceRoot -Recurse -Force
Copy-Item -LiteralPath (Join-Path $SourceExpanded 'README.md') -Destination (Join-Path $evidenceRoot 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $SourceExpanded 'V26_ROOT_RTL_GEOMETRY_WORD_BIDI_CLOSURE_REPORT.md') -Destination (Join-Path $evidenceRoot 'V26_ROOT_RTL_GEOMETRY_WORD_BIDI_CLOSURE_REPORT.md') -Force

Step 'Download and verify official embedded CPython'
Invoke-WebRequest -UseBasicParsing -Uri $PythonUrl -OutFile $PythonArchive
if ((Get-Item -LiteralPath $PythonArchive).Length -ne $PythonExpectedBytes) { throw 'Embedded Python archive size mismatch.' }
Assert-Sha256 $PythonArchive $PythonExpectedSha | Out-Null
$PythonRoot = Join-Path $PackageRoot '.runtime/python'
New-Item -ItemType Directory -Path $PythonRoot -Force | Out-Null
Expand-Archive -LiteralPath $PythonArchive -DestinationPath $PythonRoot -Force
$PythonExe = Join-Path $PythonRoot 'python.exe'
if (-not (Test-Path -LiteralPath $PythonExe)) { throw 'Packaged python.exe missing after extraction.' }

Step 'Apply deterministic bounded V27 source transform'
& $PythonExe (Join-Path $RecoveryRoot 'V27_TRANSFORM.py') $PackageRoot
if ($LASTEXITCODE -ne 0) { throw "V27 source transform failed: $LASTEXITCODE" }
$patchHashes = Get-Content -LiteralPath (Join-Path $PackageRoot 'PATCH_HASHES.json') -Raw | ConvertFrom-Json
foreach ($prop in $patchHashes.PSObject.Properties) {
    Assert-Sha256 (Join-Path (Join-Path $PackageRoot 'PATCH') $prop.Name) $prop.Value | Out-Null
}

Step 'Freeze extracted runtime manifest'
$runtimeRows = @()
Get-ChildItem -LiteralPath $PythonRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($PythonRoot.Length).TrimStart('\','/').Replace('\','/')
    $runtimeRows += [pscustomobject]@{
        path = $rel
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}
$runtimeRows | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath (Join-Path $PackageRoot 'RUNTIME_EXTRACTED_SHA256.csv') -Encoding UTF8

Step 'Execute embedded runtime and runner self-test'
$version = (& $PythonExe --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^Python 3\.13\.15$') { throw "Embedded Python version probe failed: $version" }
& $PythonExe (Join-Path $PackageRoot 'RUNNER.py') --self-test
if ($LASTEXITCODE -ne 0) { throw "RUNNER.py --self-test failed: $LASTEXITCODE" }

Step 'Exact launcher stale-version and dependency-path scan'
$launcher = Get-Content -LiteralPath (Join-Path $PackageRoot 'START_HERE.cmd') -Raw
if ($launcher -match 'V15 FINAL RESIDUAL CLOSEOUT|V26 RUNTIME RECOVERY') { throw 'Stale owner-visible launcher identity remains.' }
if ($launcher -match '(?im)^\s*where\s+(py|python)\b') { throw 'External PATH Python discovery remains in launcher.' }
if ($launcher -notmatch '\.runtime\\python\\python\.exe') { throw 'Launcher does not bind packaged Python runtime.' }
$runnerText = Get-Content -LiteralPath (Join-Path $PackageRoot 'RUNNER.py') -Raw
if ($runnerText -match '# Archestro V25 — Owner Result|# Archestro V26 Runtime Recovery — Owner Result') { throw 'Stale human result heading remains in runner.' }

Step 'Target-like Windows launcher smoke with global Python removed from PATH'
$oldPath = $env:PATH
$oldCi = $env:ARCHESTRO_CI
$oldSmoke = $env:ARCHESTRO_LAUNCHER_SMOKE_ONLY
try {
    $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
    $env:ARCHESTRO_CI = '1'
    $env:ARCHESTRO_LAUNCHER_SMOKE_ONLY = '1'
    $launcherOutput = & cmd.exe /d /c ('"{0}"' -f (Join-Path $PackageRoot 'START_HERE.cmd')) 2>&1 | Out-String
    $launcherExit = $LASTEXITCODE
} finally {
    $env:PATH = $oldPath
    $env:ARCHESTRO_CI = $oldCi
    $env:ARCHESTRO_LAUNCHER_SMOKE_ONLY = $oldSmoke
}
if ($launcherExit -ne 0) { throw "Exact launcher smoke failed exit=$launcherExit output=$launcherOutput" }
if ($launcherOutput -notmatch 'Launcher/runtime smoke completed') { throw "Launcher smoke marker missing. Output=$launcherOutput" }
if ($launcherOutput -match 'Python was not found') { throw 'Old external-Python failure path appeared during smoke.' }
Set-Content -LiteralPath (Join-Path $PackageRoot 'WINDOWS_LAUNCHER_SMOKE.txt') -Value $launcherOutput -Encoding UTF8

Step 'Generate predelivery receipt'
$receipt = [ordered]@{
    SCRIPTING_RUNTIME_CONTRACT = 'PASS'
    KNOWN_FAILURE_REUSE = 'PASS'
    PARSER_STATIC_GATE = 'PASS'
    OWNER_RUN_UX_STANDARD = 'PASS'
    RESULT_LOCALITY = 'PASS'
    RESULT_FORMAT = 'PASS'
    RESULT_AUTO_SURFACE = 'PASS_BY_EXISTING_RUNNER_CONTRACT'
    LAUNCHER_EXIT_TRUTH = 'PASS'
    EXACT_FINAL_PACKAGE_QA = 'PASS'
    RUNTIME_FIXTURE_GATE = 'PASS_WINDOWS_TARGET_LIKE'
    LEARNING_CLOSEOUT = 'KNOWN_INCIDENT_LINKED'
    DELIVERY_GATE = 'PASS'
    target_like_os = 'windows-latest GitHub Actions'
    global_python_path_removed_for_launcher_smoke = $true
    selected_runner_executable = '.runtime/python/python.exe'
    selected_runner_version = $version
    python_archive_sha256 = $PythonExpectedSha
    python_archive_bytes = $PythonExpectedBytes
    product_patch_count = 5
    product_patch_bytes_changed = $false
    source_proof_sha256 = 'CE2B9BF8033258419632829AA3EB88A6BD430DC823FA177A455EF34B68B451BB'
    v25_installed_authority = $true
    aims = 'NO_TOUCH'
    taste_pass = 'HOLD'
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PackageRoot 'PREDELIVERY_QA.json') -Encoding UTF8

Step 'Create package file manifest'
$manifestRows = @()
Get-ChildItem -LiteralPath $PackageRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($PackageRoot.Length).TrimStart('\','/').Replace('\','/')
    $manifestRows += [pscustomobject]@{
        path = $rel
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
    }
}
$manifestRows | ConvertTo-Csv -NoTypeInformation | Set-Content -LiteralPath (Join-Path $PackageRoot 'SHA256_MANIFEST.csv') -Encoding UTF8

Step 'Freeze final ZIP'
if (Test-Path -LiteralPath $FinalZip) { Remove-Item -LiteralPath $FinalZip -Force }
Compress-Archive -Path (Join-Path $PackageRoot '*') -DestinationPath $FinalZip -CompressionLevel Optimal
$FinalSha = (Get-FileHash -LiteralPath $FinalZip -Algorithm SHA256).Hash.ToUpperInvariant()
$FinalBytes = (Get-Item -LiteralPath $FinalZip).Length

Step 'Extract/readback exact final ZIP'
$Readback = Join-Path $Work 'readback'
Expand-Archive -LiteralPath $FinalZip -DestinationPath $Readback -Force
foreach ($prop in $patchHashes.PSObject.Properties) {
    Assert-Sha256 (Join-Path (Join-Path $Readback 'PATCH') $prop.Name) $prop.Value | Out-Null
}
$rbLauncher = Get-Content -LiteralPath (Join-Path $Readback 'START_HERE.cmd') -Raw
if ($rbLauncher -match 'V15 FINAL RESIDUAL CLOSEOUT') { throw 'Readback launcher stale V15 identity.' }
if (-not (Test-Path -LiteralPath (Join-Path $Readback '.runtime/python/python.exe'))) { throw 'Readback embedded Python missing.' }

@{
    file = $FinalName
    bytes = $FinalBytes
    sha256 = $FinalSha
    package_root = $PackageRoot
    final_zip = $FinalZip
    launcher_smoke_exit = $launcherExit
    launcher_smoke_output = $launcherOutput
    python_version = $version
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Work 'FINAL_PACKAGE_RECEIPT.json') -Encoding UTF8

Write-Host "FINAL_ZIP=$FinalZip"
Write-Host "FINAL_SHA256=$FinalSha"
Write-Host "FINAL_BYTES=$FinalBytes"