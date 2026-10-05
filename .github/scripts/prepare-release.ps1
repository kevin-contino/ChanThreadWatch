# Prepares and checks the release assets for .github/workflows/release.yml. It does not publish.
# Writes RELEASE_NOTES.md (the CHANGELOG.md section for the tag) and SHA256SUMS.txt (LF line endings),
# then recomputes every hash and compares it with SHA256SUMS.txt.
#
# Environment:
#   RELEASE_FILES  space-separated paths of the files the release uploads (besides SHA256SUMS.txt): exactly the two
#                  app exes (publish-app.ps1) and the six command line files ctw-<version>-<rid> (publish-cli.ps1)
#   DRY_RUN        'true' or 'false'; a dry run warns instead of failing when the CHANGELOG section is missing
#   TAG            the release tag; when empty, the tag is derived from AssemblyVersion
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dryRun = switch ($env:DRY_RUN) {
    'true' { $true }
    'false' { $false }
    default { throw "DRY_RUN must be 'true' or 'false', not '$env:DRY_RUN'" }
}

# The in-app update check compares Major.Minor.Revision of AssemblyVersion with the release tag
$match = Select-String -Path src/ChanThreadWatch/Properties/AssemblyInfo.cs -Pattern '^\[assembly: AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)\]'
if (-not $match) { throw 'AssemblyVersion not found in src/ChanThreadWatch/Properties/AssemblyInfo.cs' }
$g = $match.Matches[0].Groups
$expected = "v$($g[1].Value).$($g[2].Value).$($g[4].Value)"
if ($env:TAG) {
    if ($env:TAG -ne $expected) { throw "Tag $env:TAG does not match AssemblyVersion (expected $expected)" }
    $tag = $env:TAG
} else {
    $tag = $expected
    Write-Host "No tag given. A release of this build needs tag $tag (from AssemblyVersion $($match.Matches[0].Value))"
}

# Release notes are the user-facing section of CHANGELOG.md for this tag
$section = [regex]::Match((Get-Content CHANGELOG.md -Raw), "(?ms)^## $([regex]::Escape($tag))(?=[ \r\n])[^\r\n]*\r?\n(.*?)(?=^## |\z)")
if ($section.Success) {
    $section.Groups[1].Value.Trim() | Set-Content RELEASE_NOTES.md
    Write-Host "CHANGELOG.md has a '## $tag' section ($(@(Get-Content RELEASE_NOTES.md).Count) lines), written to RELEASE_NOTES.md"
} elseif ($dryRun) {
    Write-Host "::warning::CHANGELOG.md has no '## $tag' section; a tag release would fail here"
} else {
    throw "CHANGELOG.md has no '## $tag' section"
}

if (-not $env:RELEASE_FILES) { throw 'RELEASE_FILES is empty' }
$files = @($env:RELEASE_FILES -split ' ' | Where-Object { $_ })
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release file not found: $file" }
}
# Every app is released with the one shared version (G13), so the command line files are named with the tag's version:
# a self-contained single file per system (.exe on Windows)
$version = $tag.Substring(1)
$expectedNames = @('ChanThreadWatch-win-x64.exe', 'ChanThreadWatch-win-arm64.exe') +
    @('win-x64', 'win-arm64' | ForEach-Object { "ctw-$version-$_.exe" }) +
    @('linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64' | ForEach-Object { "ctw-$version-$_" })
$names = @($files | ForEach-Object { Split-Path $_ -Leaf })
$missing = @($expectedNames | Where-Object { $names -cnotcontains $_ })
if ($missing) { throw "RELEASE_FILES has no $($missing -join ', ') (see publish-app.ps1 and publish-cli.ps1)" }
$unexpected = @($names | Where-Object { $expectedNames -cnotcontains $_ })
if ($unexpected) { throw "RELEASE_FILES has files the release does not expect: $($unexpected -join ', ')" }
# Assets are uploaded by file name, so two files with one name (e.g. the exe of two architectures) would collide
$duplicates = @($files | ForEach-Object { Split-Path $_ -Leaf } | Group-Object | Where-Object Count -gt 1)
if ($duplicates) { throw "Release file names must be unique: $($duplicates.Name -join ', ')" }

# LF line endings, so sha256sum -c also works on Linux
$sums = Get-FileHash -LiteralPath $files -Algorithm SHA256 | ForEach-Object { "$($_.Hash.ToLower())  $(Split-Path $_.Path -Leaf)" }
# Full path: .NET resolves relative paths against the process directory, not the PowerShell location
$sumsPath = Join-Path $PWD 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sumsPath, ($sums -join "`n") + "`n")

# Check the written file: no CR bytes, one line per release file, and every hash matches a fresh computation
$bytes = [IO.File]::ReadAllBytes($sumsPath)
if ($bytes -contains 13) { throw 'SHA256SUMS.txt contains CR bytes' }
$lines = ([Text.Encoding]::ASCII.GetString($bytes)).TrimEnd("`n") -split "`n"
if ($lines.Count -ne $files.Count) { throw "SHA256SUMS.txt has $($lines.Count) lines, expected $($files.Count)" }
for ($i = 0; $i -lt $files.Count; $i++) {
    $file = $files[$i]
    $name = Split-Path $file -Leaf
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLower()
    if ($lines[$i] -notmatch '^([0-9a-f]{64})  (\S+)$' -or $Matches[2] -cne $name -or $Matches[1] -cne $actual) {
        throw "SHA256SUMS.txt line $($i + 1) '$($lines[$i])' does not match $file ($actual)"
    }
}
Write-Host "SHA256SUMS.txt verified: $($files.Count) hashes match, no CR bytes"

$assets = @($files) + 'SHA256SUMS.txt'
$report = @(
    "### Release assets for $tag$(if ($dryRun) { ' (dry run, nothing published)' })"
    ''
    '| File | Bytes |'
    '| --- | ---: |'
    ($assets | ForEach-Object { "| ``$_`` | $((Get-Item -LiteralPath $_).Length) |" })
    ''
    "CHANGELOG.md section '## $tag': $(if ($section.Success) { 'found' } else { '**missing**' })"
    ''
    'SHA256SUMS.txt:'
    ''
    '```'
    $sums
    '```'
)
$report | Write-Host
if ($env:GITHUB_STEP_SUMMARY) { $report | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY }
