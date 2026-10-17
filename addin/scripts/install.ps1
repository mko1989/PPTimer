<#
.SYNOPSIS
  Installs (or updates) PPTimer for the current user, including network access.

.DESCRIPTION
  1. Quits a running PPTimer, copies PPTimer.exe to %LOCALAPPDATA%\PPTimer\bin, adds a Start menu shortcut,
     starts PPTimer with Windows (unless -NoAutostart) and starts it now. No admin rights needed for this part.
     Removes the PowerPoint add-in of PPTimer 1.0.x if it is still registered: the app replaces it.
  2. If they are missing, adds an HTTP URL reservation for http://+:PORT/ and an inbound firewall
     rule for that TCP port, so other machines (Companion) can connect. That part needs admin, so
     Windows asks once; later updates skip it. Without it PPTimer only answers on localhost.

  Re-run after every new build.
  -Network Setup|Remove is used internally (elevated re-launch) and by uninstall.ps1.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$NoAutostart,
    [ValidateSet('Setup', 'Remove')]
    [string]$Network = '',
    [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
$ScriptPath = $MyInvocation.MyCommand.Path

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Admin part: URL reservation + firewall rule. Runs in the elevated copy of this script.
function Set-NetworkAccess {
    param([int]$Port, [switch]$Remove)
    $url = "http://+:$Port/"
    & netsh http delete urlacl url=$url 2>&1 | Out-Null
    Get-NetFirewallRule -DisplayName 'PPTimer (TCP *)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    if ($Remove) { return }

    # D:(A;;GX;;;WD) = allow Everyone to listen on this URL (language-independent SDDL).
    & netsh http add urlacl "url=$url" "sddl=D:(A;;GX;;;WD)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "netsh http add urlacl failed ($LASTEXITCODE)" }
    New-NetFirewallRule -DisplayName "PPTimer (TCP $Port)" -Direction Inbound -Protocol TCP -LocalPort $Port `
        -Action Allow -Profile Any -Description 'PPTimer remote control (Companion, browser)' | Out-Null
}

if ($Network) {
    # Elevated re-launch: do only the admin part. The window stays open only if something failed.
    try {
        Set-NetworkAccess -Port $Port -Remove:($Network -eq 'Remove')
        exit 0
    }
    catch {
        Write-Host "Network setup failed: $_" -ForegroundColor Red
        Read-Host 'Press Enter to close' | Out-Null
        exit 1
    }
}

function Test-NetworkAccess {
    param([int]$Port)
    $reserved = (& netsh http show urlacl url="http://+:$Port/" 2>&1 | Out-String) -match [regex]::Escape("http://+:$Port/")
    $rule = $null
    try { $rule = Get-NetFirewallRule -DisplayName "PPTimer (TCP $Port)" -ErrorAction SilentlyContinue } catch { }
    return ($reserved -and $rule)
}

# Runs the admin part, elevated if needed (Windows shows a UAC prompt). Returns $true on success.
function Invoke-NetworkAccess {
    param([int]$Port, [string]$Mode)
    if (Test-IsAdmin) {
        Set-NetworkAccess -Port $Port -Remove:($Mode -eq 'Remove')
        return $true
    }
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$ScriptPath`"",
                   '-Network', $Mode, '-Port', $Port)
    try {
        $p = Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
        return ($p.ExitCode -eq 0)
    }
    catch {
        # UAC prompt declined, or no admin account available.
        Write-Warning "Admin permission was not given: $($_.Exception.Message)"
        return $false
    }
}

# Removes the COM add-in registration of PPTimer 1.0.x. Returns $true if there was one.
function Remove-OldAddin {
    $clsid = '{39674B3C-941D-4E54-9410-B0F926196B0B}'
    $progId = 'PPTimer.Connect'
    $found = $false
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
        try {
            foreach ($sub in @("Software\Classes\CLSID\$clsid", "Software\Classes\$progId", "Software\Microsoft\Office\PowerPoint\Addins\$progId")) {
                $key = $base.OpenSubKey($sub)
                if ($key) {
                    $key.Close()
                    $found = $true
                    $base.DeleteSubKeyTree($sub, $false)
                }
            }
            $list = $base.OpenSubKey('Software\Microsoft\Office\16.0\PowerPoint\Resiliency\DoNotDisableAddinList', $true)
            if ($list) { $list.DeleteValue($progId, $false); $list.Close() }
        }
        finally { $base.Close() }
    }
    return $found
}

# Not a param default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there when run via -File.
if (-not $Source) { $Source = Split-Path -Parent $ScriptPath }

$DataDir = Join-Path $env:LOCALAPPDATA 'PPTimer'
$InstallDir = Join-Path $DataDir 'bin'
$Exe = Join-Path $InstallDir 'PPTimer.exe'

$exeSource = Join-Path $Source 'PPTimer.exe'
if (-not (Test-Path $exeSource)) { throw "PPTimer.exe not found in $Source" }

# --- The old PowerPoint add-in (1.0.x) ---
if (Remove-OldAddin) {
    Write-Host 'Removed the PPTimer PowerPoint add-in (1.0.x); PPTimer is now its own app.' -ForegroundColor Yellow
    if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
        Write-Host 'PowerPoint is running with the old add-in still loaded: close PowerPoint once, or the two will compete for the network port.' -ForegroundColor Yellow
    }
}

# --- Quit the running copy, then copy the new one ---
$running = Get-Process PPTimer -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
foreach ($name in @('PPTimer.exe', 'PPTimer.exe.config', 'PPTimer.pdb')) {
    $file = Join-Path $Source $name
    if (Test-Path $file) { Copy-Item $file $InstallDir -Force }
}
$oldDll = Join-Path $InstallDir 'PPTimer.dll'
if (Test-Path $oldDll) { Remove-Item $oldDll -Force -ErrorAction SilentlyContinue }

# Files that came from another machine / a zip carry a "downloaded" mark; Windows would warn on every start.
Get-ChildItem $InstallDir | Unblock-File

# --- Start menu shortcut and start with Windows ---
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'PPTimer.lnk'))
$lnk.TargetPath = $Exe
$lnk.WorkingDirectory = $InstallDir
$lnk.Description = 'Countdown on the presenter view or a screen, controlled by Companion'
$lnk.Save()

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if ($NoAutostart) {
    Remove-ItemProperty -Path $runKey -Name 'PPTimer' -ErrorAction SilentlyContinue
}
else {
    Set-ItemProperty -Path $runKey -Name 'PPTimer' -Value "`"$Exe`""
}

Write-Host ''
Write-Host "PPTimer installed to $InstallDir" -ForegroundColor Green
Write-Host "  Version  : $((Get-Item $Exe).VersionInfo.ProductVersion)"
Write-Host "  Starts   : $(if ($NoAutostart) { 'from the Start menu' } else { 'with Windows, and from the Start menu' })"
Write-Host "  Config   : $(Join-Path $DataDir 'config.json') (created on first start)"
Write-Host "  Log      : $(Join-Path $DataDir 'pptimer.log')"

# --- Network access (admin, only when missing) ---

# Port from this user's config, read before elevating (the admin may be another account).
$port = 9595
$config = Join-Path $DataDir 'config.json'
if (Test-Path $config) {
    try {
        $configured = (Get-Content $config -Raw | ConvertFrom-Json).port
        if ($configured) { $port = [int]$configured }
    }
    catch { Write-Warning "Could not read $config, using port $port" }
}

Write-Host ''
if (Test-NetworkAccess $port) {
    Write-Host "Network access on port $port is already set up." -ForegroundColor Green
}
else {
    Write-Host "Setting up network access on port $port. Windows will ask for admin permission..."
    Invoke-NetworkAccess -Port $port -Mode 'Setup' | Out-Null
    if (Test-NetworkAccess $port) {
        Write-Host "Network access on port $port set up." -ForegroundColor Green
    }
    else {
        Write-Host "Network access is NOT set up: Companion can't connect, only this PC (http://localhost:$port/)." -ForegroundColor Yellow
        Write-Host 'Run install.cmd again and accept the admin prompt to fix it.' -ForegroundColor Yellow
    }
}
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    ForEach-Object { Write-Host "  Remote   : http://$($_.IPAddress):$port/  ($($_.InterfaceAlias))" }

Start-Process $Exe
Write-Host ''
Write-Host 'PPTimer is running: its icon (a ring) is in the notification area, bottom right (maybe under ^).'
