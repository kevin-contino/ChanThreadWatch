# Publishes the command line (ctw) release zip: framework-dependent and without a runtime identifier, so the one zip
# runs on Windows, Linux and macOS with the .NET 10 runtime ("dotnet ctw/ctw.dll"). Writes <OutputDir>/ctw-<version>.zip,
# where the version is Major.Minor.Revision as in the release tag. Fails when ctw's version differs from the WinForms
# app's: src/ChanThreadWatch/Properties/AssemblyInfo.cs is the one version source of every app (G13), and the
# ctw build reads it (see src/ChanThreadWatch.Cli/ChanThreadWatch.Cli.csproj). Used by ci.yml and release.yml; also
# runs locally. Single-file builds per OS are not made here.
#
# Example: pwsh .github/scripts/publish-cli.ps1
param(
    [string] $OutputDir = 'publish'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $PSScriptRoot '../..'
$assemblyInfo = Join-Path $root 'src/ChanThreadWatch/Properties/AssemblyInfo.cs'
$match = Select-String -LiteralPath $assemblyInfo -Pattern '^\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]'
if (-not $match) { throw "AssemblyVersion not found in $assemblyInfo" }
$g = $match.Matches[0].Groups
$appVersion = "$($g[1].Value).$($g[2].Value).$($g[3].Value).$($g[4].Value)"
$version = "$($g[1].Value).$($g[2].Value).$($g[4].Value)"

$project = Join-Path $root 'src/ChanThreadWatch.Cli/ChanThreadWatch.Cli.csproj'
$publishDir = Join-Path $OutputDir 'ctw'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
dotnet publish $project -c Release -p:RestoreLockedMode=true -o $publishDir -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish of ctw failed with exit code $LASTEXITCODE" }

# The same version as the app: in the assembly, and as ctw --version prints it (which reads no settings)
$dll = (Resolve-Path -LiteralPath (Join-Path $publishDir 'ctw.dll')).Path
$dllVersion = [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
if ($dllVersion -ne $appVersion) { throw "ctw.dll has version $dllVersion, but the app has $appVersion" }
$printed = & dotnet $dll --version
if ($LASTEXITCODE -ne 0) { throw "ctw --version failed with exit code $LASTEXITCODE" }
if ("$printed".Trim() -ne $version) { throw "ctw --version printed '$printed', but the app's version is $version" }
Write-Host "ctw version $version matches the app ($appVersion)"

# Everything ctw needs, without symbols. A subfolder (e.g. runtimes/) would mean a dependency with files per OS,
# which this zip for every OS is not checked for
Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File | Remove-Item -Force
$subfolders = @(Get-ChildItem -LiteralPath $publishDir -Directory)
if ($subfolders) { throw "The ctw publish has subfolders: $($subfolders.Name -join ', ')" }

# The files go in a ctw/ folder of the zip, so unzipping it next to ChanThreadWatch.exe gives <app>/ctw/ctw.dll, which
# finds the app's portable settings.txt in its parent folder and never overwrites a file of the app.
# ZipFile writes "/" as the separator on every system.
$zip = Join-Path $OutputDir "ctw-$version.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zipPath = Join-Path (Resolve-Path -LiteralPath $OutputDir).Path "ctw-$version.zip"
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $publishDir).Path, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)

$archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
} finally {
    $archive.Dispose()
}
$outside = @($entries | Where-Object { -not $_.StartsWith('ctw/') -or $_.Contains('\') })
if ($outside) { throw "Zip entries outside ctw/: $($outside -join ', ')" }
if ($entries -notcontains 'ctw/ctw.dll') { throw 'The zip has no ctw/ctw.dll' }
Write-Host "$zip ($((Get-Item -LiteralPath $zip).Length) bytes): $($entries -join ', ')"
