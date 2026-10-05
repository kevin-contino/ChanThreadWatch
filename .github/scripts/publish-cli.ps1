# Publishes the command line (ctw) release files: one self-contained single file per runtime identifier, which needs
# no .NET install, copied to <OutputDir>/ctw-<version>-<rid> (with ".exe" for Windows), where the version is
# Major.Minor.Revision as in the release tag. Each publish folder is <OutputDir>/build-ctw/<rid>. The publish settings are in src/ChanThreadWatch.Cli/ChanThreadWatch.Cli.csproj.
# Used by ci.yml and release.yml; also runs locally.
#
# macOS refuses to run an arm64 executable without a code signature, and the SDK signs only on macOS, so the osx files
# are published on macOS only, and signed there ad hoc (codesign --sign -). They are not signed with a Developer ID, so
# Gatekeeper asks before the first run (see README.md). Files published on Linux or macOS get the executable bit; a
# release asset loses it, so users run chmod +x.
#
# ctw's version must be the WinForms app's: src/ChanThreadWatch/Properties/AssemblyInfo.cs is the one version source of
# every app (G13), and the ctw build reads it. The file for this machine's own runtime identifier is run with --version
# and --help. A Windows file published on Windows has the version in its file version too (the SDK writes it there on
# Windows only). The other files rely on the build reading the same AssemblyInfo.cs.
#
# Example: pwsh .github/scripts/publish-cli.ps1 -RuntimeIdentifiers win-x64
param(
    [string[]] $RuntimeIdentifiers = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64'),
    [string] $OutputDir = 'publish'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# "pwsh -File" passes "a,b" as one string
$RuntimeIdentifiers = @($RuntimeIdentifiers | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$supported = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
$root = Join-Path $PSScriptRoot '../..'
$assemblyInfo = Join-Path $root 'src/ChanThreadWatch/Properties/AssemblyInfo.cs'
$match = Select-String -LiteralPath $assemblyInfo -Pattern '^\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]'
if (-not $match) { throw "AssemblyVersion not found in $assemblyInfo" }
$g = $match.Matches[0].Groups
$appVersion = "$($g[1].Value).$($g[2].Value).$($g[3].Value).$($g[4].Value)"
$version = "$($g[1].Value).$($g[2].Value).$($g[4].Value)"

# This machine's runtime identifier, for the file that can run here
$os = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } elseif ($IsLinux) { 'linux' } else { throw 'Unknown OS' }
$arch = switch ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64' { 'x64' }
    'Arm64' { 'arm64' }
    default { 'other' }
}
$nativeRid = "$os-$arch"

function Test-Run([string] $path, [string[]] $arguments) {
    $printed = & $path @arguments
    if ($LASTEXITCODE -ne 0) { throw "ctw $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
    return "$printed"
}

$project = Join-Path $root 'src/ChanThreadWatch.Cli/ChanThreadWatch.Cli.csproj'
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
foreach ($rid in $RuntimeIdentifiers) {
    if ($supported -notcontains $rid) { throw "Unsupported runtime identifier $rid (supported: $($supported -join ', '))" }
    $isMac = $rid.StartsWith('osx-')
    if ($isMac -and -not $IsMacOS) { throw "$rid must be published on macOS, where the file is code-signed" }
    $exeName = if ($rid.StartsWith('win-')) { 'ctw.exe' } else { 'ctw' }

    # Kept apart from the release files, so "ctw-*" in OutputDir is only those
    $ridDir = Join-Path (Join-Path $OutputDir 'build-ctw') $rid
    if (Test-Path -LiteralPath $ridDir) { Remove-Item -LiteralPath $ridDir -Recurse -Force }
    dotnet publish $project -c Release -r $rid -p:RestoreLockedMode=true -o $ridDir -v:minimal -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish of ctw for $rid failed with exit code $LASTEXITCODE" }

    # Only the executable ships; anything else next to it would mean the publish is no longer a single file
    $extra = @(Get-ChildItem -LiteralPath $ridDir -File | Where-Object { $_.Name -ne $exeName -and $_.Extension -ne '.pdb' })
    if ($extra) { throw "The ctw $rid publish is not a single file; it also has: $($extra.Name -join ', ')" }
    $subfolders = @(Get-ChildItem -LiteralPath $ridDir -Directory)
    if ($subfolders) { throw "The ctw $rid publish has subfolders: $($subfolders.Name -join ', ')" }

    $assetName = "ctw-$version-$rid" + $(if ($rid.StartsWith('win-')) { '.exe' } else { '' })
    $asset = Join-Path $OutputDir $assetName
    Copy-Item -LiteralPath (Join-Path $ridDir $exeName) -Destination $asset -Force
    if (-not $IsWindows) {
        chmod +x $asset
        if ($LASTEXITCODE -ne 0) { throw "chmod +x $asset failed with exit code $LASTEXITCODE" }
    }
    if ($isMac) {
        codesign --force --sign - $asset
        if ($LASTEXITCODE -ne 0) { throw "codesign of $asset failed with exit code $LASTEXITCODE" }
        codesign --verify --strict $asset
        if ($LASTEXITCODE -ne 0) { throw "codesign --verify of $asset failed with exit code $LASTEXITCODE" }
    }

    if ($rid -eq $nativeRid) {
        # As ctw --version prints it (it reads no settings); --help only prints and shows the settings folder it would use
        $printed = (Test-Run (Resolve-Path -LiteralPath $asset).Path @('--version')).Trim()
        if ($printed -ne $version) { throw "ctw $rid --version printed '$printed', but the app's version is $version" }
        Test-Run (Resolve-Path -LiteralPath $asset).Path @('--help') | Out-Null
        Write-Host "ctw $rid ran: version $version matches the app ($appVersion)"
    } elseif ($IsWindows -and $rid.StartsWith('win-')) {
        $fileVersion = (Get-Item -LiteralPath $asset).VersionInfo.FileVersion
        if ($fileVersion -ne $appVersion) { throw "ctw $rid has file version '$fileVersion', but the app has $appVersion" }
        Write-Host "ctw $rid file version $fileVersion matches the app (not run on this $nativeRid machine)"
    } else {
        Write-Host "ctw $rid not run on this $nativeRid machine; its version comes from the same AssemblyInfo.cs as the build of the others"
    }
    Write-Host "$asset ($((Get-Item -LiteralPath $asset).Length) bytes)"
}
