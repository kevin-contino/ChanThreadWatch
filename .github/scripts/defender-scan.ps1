# Scans each release file with Microsoft Defender (custom scan of one file, no remediation) and fails on a
# detection. The signatures are updated first. A runner without a working Defender gets a warning instead, so
# the job log says the scan did not run. With -Strict (the tag release), anything short of a clean scan with
# updated signatures fails the job.
#
# Environment: RELEASE_FILES  space-separated paths of the files to scan
param([switch]$Strict)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Fails the job with -Strict, otherwise leaves a warning in the job log
function Write-ScanProblem([string]$message) {
    if ($Strict) { throw $message }
    Write-Host "::warning::$message"
}

if (-not $env:RELEASE_FILES) { throw 'RELEASE_FILES is empty' }
$mpCmdRun = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not (Test-Path -LiteralPath $mpCmdRun)) {
    Write-ScanProblem "$mpCmdRun not found; the release files were not scanned"
    return
}
# A network blip can fail one update, so it is tried again, the last time straight from Microsoft
# Malware Protection Center (-MMPC)
foreach ($updateArgs in @('-SignatureUpdate'), @('-SignatureUpdate'), @('-SignatureUpdate', '-MMPC')) {
    & $mpCmdRun @updateArgs
    if ($LASTEXITCODE -eq 0) { break }
}
if ($LASTEXITCODE -ne 0) {
    Write-ScanProblem "Defender could not update its signatures (MpCmdRun exit code $LASTEXITCODE)"
}
foreach ($file in @($env:RELEASE_FILES -split ' ' | Where-Object { $_ })) {
    $path = (Resolve-Path -LiteralPath $file).Path
    & $mpCmdRun -Scan -ScanType 3 -File $path -DisableRemediation
    # MpCmdRun exit codes: 0 no threat, 2 threat found, anything else the scan did not complete
    switch ($LASTEXITCODE) {
        0 { Write-Host "Defender: no threats in $file" }
        2 { throw "Defender found a threat in $file" }
        default { Write-ScanProblem "Defender could not scan $file (MpCmdRun exit code $LASTEXITCODE)" }
    }
}
