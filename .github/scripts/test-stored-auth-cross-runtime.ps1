# Checks that saved logins (DPAPI, current user) move between the .NET Framework 4.8 app and the .NET 10 app,
# both ways, for settings.txt PageAuth/ImageAuth and per-thread logins in threads.txt. Run it as one user:
# DPAPI keys are per user. Builds tools/stored-auth-check for net48 and net10.0 into a temp folder, then
#   net48 write -> net10.0 read, and net10.0 write -> net48 read.
# Everything is written under a new temp folder, which is removed at the end. The logins are sterile.
#
# Example: pwsh .github/scripts/test-stored-auth-cross-runtime.ps1
param(
    [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $PSScriptRoot '../../tools/stored-auth-check/StoredAuthCheck.csproj'
$work = Join-Path ([IO.Path]::GetTempPath()) ('ctw-storedauth-' + [guid]::NewGuid().ToString('N'))
try {
    $bin = Join-Path $work 'bin'
    dotnet build $project -c $Configuration -p:RestoreLockedMode=true "-p:BaseOutputPath=$bin\" -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
    $tools = @{
        'net48' = Join-Path $bin "$Configuration/net48/StoredAuthCheck.exe"
        'net10.0' = Join-Path $bin "$Configuration/net10.0/StoredAuthCheck.exe"
    }
    foreach ($tool in $tools.Values) {
        if (-not (Test-Path -LiteralPath $tool)) { throw "Built tool not found: $tool" }
    }

    foreach ($pair in @(@('net48', 'net10.0'), @('net10.0', 'net48'))) {
        $writer, $reader = $pair
        $folder = Join-Path $work "$writer-to-$reader"
        New-Item -ItemType Directory -Path $folder | Out-Null
        & $tools[$writer] write $folder
        if ($LASTEXITCODE -ne 0) { throw "$writer could not write the logins (exit code $LASTEXITCODE)" }
        & $tools[$reader] read $folder
        if ($LASTEXITCODE -ne 0) { throw "$reader could not read the logins $writer wrote (exit code $LASTEXITCODE)" }
        Write-Host "OK: logins written by $writer decrypt in $reader"
    }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
