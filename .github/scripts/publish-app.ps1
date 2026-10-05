# Publishes the release exes: one self-contained single-file ChanThreadWatch per Windows architecture (G14),
# copied to <OutputDir>/ChanThreadWatch-<rid>.exe. Used by ci.yml and release.yml; also runs locally.
# The publish settings (self-contained, single file, compression) are in src/ChanThreadWatch/ChanThreadWatch.csproj.
#
# Example: pwsh .github/scripts/publish-app.ps1
param(
    [string[]] $RuntimeIdentifiers = @('win-x64', 'win-arm64'),
    [string] $OutputDir = 'publish'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$project = Join-Path $PSScriptRoot '../../src/ChanThreadWatch/ChanThreadWatch.csproj'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($rid in $RuntimeIdentifiers) {
    $ridDir = Join-Path $OutputDir $rid
    dotnet publish $project -c Release -r $rid -p:RestoreLockedMode=true -o $ridDir -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish for $rid failed with exit code $LASTEXITCODE" }

    # Only the exe ships; a dll or config next to it would mean the publish is no longer a single file
    $extra = @(Get-ChildItem -LiteralPath $ridDir -File | Where-Object { $_.Name -ne 'ChanThreadWatch.exe' -and $_.Extension -ne '.pdb' })
    if ($extra) { throw "The $rid publish is not a single file; it also has: $($extra.Name -join ', ')" }

    $asset = Join-Path $OutputDir "ChanThreadWatch-$rid.exe"
    Copy-Item -LiteralPath (Join-Path $ridDir 'ChanThreadWatch.exe') -Destination $asset -Force
    Write-Host "$asset ($((Get-Item -LiteralPath $asset).Length) bytes)"
}
