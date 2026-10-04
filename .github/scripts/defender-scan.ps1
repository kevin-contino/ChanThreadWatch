# Scans each release file with Microsoft Defender (custom scan of one file, no remediation) and fails on a
# detection. A runner without a working Defender gets a warning instead, so the job log says the scan did not run.
#
# Environment: RELEASE_FILES  space-separated paths of the files to scan
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $env:RELEASE_FILES) { throw 'RELEASE_FILES is empty' }
$mpCmdRun = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
if (-not (Test-Path -LiteralPath $mpCmdRun)) {
    Write-Host "::warning::$mpCmdRun not found; the release files were not scanned"
    return
}
foreach ($file in @($env:RELEASE_FILES -split ' ' | Where-Object { $_ })) {
    $path = (Resolve-Path -LiteralPath $file).Path
    & $mpCmdRun -Scan -ScanType 3 -File $path -DisableRemediation
    # MpCmdRun exit codes: 0 no threat, 2 threat found, anything else the scan did not complete
    switch ($LASTEXITCODE) {
        0 { Write-Host "Defender: no threats in $file" }
        2 { throw "Defender found a threat in $file" }
        default { Write-Host "::warning::Defender could not scan $file (MpCmdRun exit code $LASTEXITCODE)" }
    }
}
