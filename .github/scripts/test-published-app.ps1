# Starts a published ChanThreadWatch exe (self-contained single file) from a new temp folder, waits for
# its main window, closes it, and checks that it exited cleanly and saved its settings in the expected folder.
#
#   -Mode Portable  settings.txt is put next to the exe, so the app keeps its settings in the temp folder.
#                   Safe on any machine: nothing outside the temp folder is written.
#   -Mode AppData   no settings.txt next to the exe, so the app uses "%APPDATA%\Chan Thread Watch". The app
#                   resolves that folder through the Windows known-folder API, which ignores the APPDATA
#                   variable, so this mode uses the real profile folder. It runs only on a GitHub Actions
#                   runner (an ephemeral VM) and refuses to run when the folder already exists.
#
# Example: pwsh .github/scripts/test-published-app.ps1 -ExePath publish/ChanThreadWatch-win-x64.exe -Mode Portable
param(
    [Parameter(Mandatory)] [string] $ExePath,
    [Parameter(Mandatory)] [ValidateSet('Portable', 'AppData')] [string] $Mode,
    [int] $TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$exe = (Resolve-Path -LiteralPath $ExePath).Path
$runDir = Join-Path ([IO.Path]::GetTempPath()) ('ctw-published-' + [guid]::NewGuid().ToString('N'))
$downloadDir = Join-Path $runDir 'downloads'
New-Item -ItemType Directory -Path $downloadDir | Out-Null
$runExe = Join-Path $runDir (Split-Path $exe -Leaf)
Copy-Item -LiteralPath $exe -Destination $runExe

# The download folder is set so the app never falls back to My Documents, and the update check is off
$settings = @("DownloadFolder=$downloadDir", 'DownloadFolderIsRelative=0', 'CheckForUpdates=0')
if ($Mode -eq 'Portable') {
    $settingsDir = $runDir
} else {
    if ($env:GITHUB_ACTIONS -ne 'true') {
        throw 'AppData mode writes to the real "%APPDATA%\Chan Thread Watch" folder, so it only runs on a GitHub Actions runner. Use -Mode Portable locally.'
    }
    $settingsDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Chan Thread Watch'
    if (Test-Path -LiteralPath $settingsDir) { throw "$settingsDir already exists; refusing to touch existing settings" }
    New-Item -ItemType Directory -Path $settingsDir | Out-Null
}
$settingsPath = Join-Path $settingsDir 'settings.txt'
Set-Content -LiteralPath $settingsPath -Value $settings

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class CtwWindows {
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
    // True if a child window of parent with exactly this text is enabled
    public static bool IsChildEnabled(IntPtr parent, string text) {
        bool enabled = false;
        EnumChildWindows(parent, (hwnd, lParam) => {
            var buffer = new StringBuilder(256);
            GetWindowText(hwnd, buffer, buffer.Capacity);
            if (buffer.ToString() == text && IsWindowEnabled(hwnd)) enabled = true;
            return true;
        }, IntPtr.Zero);
        return enabled;
    }
}
"@

function Wait-Until([scriptblock] $Condition, [string] $Description) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (-not (& $Condition)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw "Timed out after $TimeoutSeconds s waiting for $Description" }
        Start-Sleep -Milliseconds 200
    }
}

$startInfo = [Diagnostics.ProcessStartInfo]::new($runExe)
$startInfo.WorkingDirectory = $runDir
$startInfo.UseShellExecute = $false
# Anything the app would open in Explorer or a browser is written here instead (see Classes/Shell.cs)
$startInfo.Environment['CTW_TEST_SHELL_LOG'] = Join-Path $runDir 'shell-log.txt'
$process = [Diagnostics.Process]::Start($startInfo)
try {
    Wait-Until { $process.Refresh(); $process.HasExited -or $process.MainWindowTitle -eq 'Chan Thread Watch' } 'the main window'
    if ($process.HasExited) { throw "The app exited with code $($process.ExitCode) before its main window appeared" }
    Write-Host "$Mode run: main window up (process $($process.Id))"
    # The app creates its log at startup in the settings folder it chose
    Wait-Until { Test-Path -LiteralPath (Join-Path $settingsDir 'log.txt') } "log.txt in $settingsDir"
    # The app disables Add Thread when its window first shows and enables it once the thread list has loaded.
    # Closing before that hits a known startup race (the loader thread calls Invoke on the closed window), so
    # the button must stay enabled for 2 s, which also covers the moment before the app first disabled it.
    $enabledSince = $null
    Wait-Until {
        if (-not [CtwWindows]::IsChildEnabled($process.MainWindowHandle, 'Add Thread')) { $script:enabledSince = $null; return $false }
        if ($null -eq $script:enabledSince) { $script:enabledSince = [DateTime]::UtcNow }
        ([DateTime]::UtcNow - $script:enabledSince).TotalSeconds -ge 2
    } 'the thread list to load (Add Thread enabled for 2 s)'

    # WM_CLOSE, as when the user closes the window; the app saves its settings on exit
    if (-not $process.CloseMainWindow()) { throw 'Could not send WM_CLOSE to the main window' }
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { throw "The app did not exit within $TimeoutSeconds s after its window was closed" }
    if ($process.ExitCode -ne 0) { throw "The app exited with code $($process.ExitCode)" }

    $saved = @(Get-Content -LiteralPath $settingsPath)
    if (-not ($saved | Where-Object { $_.StartsWith('ColumnWidths=', [StringComparison]::Ordinal) })) {
        throw "settings.txt in $settingsDir was not saved on exit"
    }
    if ($Mode -eq 'AppData' -and (Test-Path -LiteralPath (Join-Path $runDir 'settings.txt'))) {
        throw 'AppData mode wrote settings.txt next to the exe'
    }
    if (Test-Path -LiteralPath (Join-Path $runDir 'shell-log.txt')) {
        throw "The app tried to open: $(Get-Content -LiteralPath (Join-Path $runDir 'shell-log.txt') -Raw)"
    }
    Write-Host "$Mode run: exited with code 0 and saved settings in $settingsDir"
} finally {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $process.Dispose()
    Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue
    if ($Mode -eq 'AppData') { Remove-Item -LiteralPath $settingsDir -Recurse -Force -ErrorAction SilentlyContinue }
}
