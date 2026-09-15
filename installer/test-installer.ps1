# Installs a Signal Quality setup exe silently, checks everything landed where it should,
# then uninstalls and checks everything was removed. CI runs this on a clean machine. It's
# safe to run locally, but it really installs and uninstalls for the current user, stops any
# running copy of the app, and deletes %APPDATA%\SignalQuality.
#   installer\test-installer.ps1 -Setup installer\out\SignalQuality-Setup-1.2.3.exe

param(
    [Parameter(Mandatory = $true)]
    [string] $Setup
)

$ErrorActionPreference = 'Stop'
$failures = 0

$appDir = Join-Path $env:LOCALAPPDATA 'Programs\Signal Quality'
$exe = Join-Path $appDir 'SignalQuality.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Signal Quality.lnk'
$settingsDir = Join-Path $env:APPDATA 'SignalQuality'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'

function Check([bool] $condition, [string] $name) {
    if ($condition) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name"; $script:failures++ }
}

function Get-StartupValue {
    (Get-ItemProperty $runKey -ErrorAction SilentlyContinue).SignalQuality
}

function Get-UninstallEntry {
    Get-ChildItem $uninstallKey -ErrorAction SilentlyContinue |
        Get-ItemProperty | Where-Object { $_.DisplayName -eq 'Signal Quality' }
}

$setupPath = (Resolve-Path $Setup).Path
Write-Host "Installing $setupPath"
Start-Process $setupPath -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/TASKS=startup' -Wait

Check (Test-Path $exe) 'app installed under %LOCALAPPDATA%\Programs'
Check (Test-Path $shortcut) 'Start menu shortcut created'
Check ((Get-StartupValue) -eq "`"$exe`"") 'start-with-Windows entry points at the installed exe'
Check ($null -ne (Get-UninstallEntry)) 'listed in Settings > Apps'

# Leave the app running with a settings file, as a real user would, then uninstall.
New-Item -ItemType Directory -Force $settingsDir | Out-Null
Set-Content (Join-Path $settingsDir 'settings.ini') 'IntervalSeconds=5'
$app = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
$wasRunning = -not $app.HasExited
Write-Host "      app running before uninstall: $wasRunning"

$uninstaller = Get-ChildItem $appDir -Filter 'unins*.exe' | Select-Object -First 1
Check ($null -ne $uninstaller) 'uninstaller present'
if ($uninstaller) {
    Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
    # The uninstaller relaunches itself from a temp copy and returns early, so wait for it.
    $deadline = (Get-Date).AddSeconds(60)
    while ((Test-Path $appDir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
}

Check (-not (Test-Path $appDir)) 'app folder removed'
Check (-not (Test-Path $shortcut)) 'Start menu shortcut removed'
Check ($null -eq (Get-StartupValue)) 'start-with-Windows entry removed'
Check ($null -eq (Get-UninstallEntry)) 'removed from Settings > Apps'
Check (-not (Test-Path $settingsDir)) 'settings folder removed'
if ($wasRunning) { Check ($null -eq (Get-Process -Id $app.Id -ErrorAction SilentlyContinue)) 'running app was stopped' }

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed."
    exit 1
}
Write-Host 'Install and uninstall checks passed.'
