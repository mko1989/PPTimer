<#
.SYNOPSIS
  Installs (or updates) the PPTimer PowerPoint add-in for the current user, including network access.

.DESCRIPTION
  1. Copies PPTimer.dll to %LOCALAPPDATA%\PPTimer\bin, registers it as a per-user COM class
     (both 64- and 32-bit registry views, so it works with either Office bitness) and registers
     it as a PowerPoint COM add-in. No admin rights needed for this part.
  2. If they are missing, adds an HTTP URL reservation for http://+:PORT/ and an inbound firewall
     rule for that TCP port, so other machines (Companion) can connect. That part needs admin, so
     Windows asks once; later updates skip it. Without it the add-in only answers on localhost.

  Re-run after every new build; PowerPoint must be closed.
  -Network Setup|Remove is used internally (elevated re-launch) and by uninstall.ps1.
#>
[CmdletBinding()]
param(
    [string]$Source,
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
        -Action Allow -Profile Any -Description 'PPTimer PowerPoint add-in remote control' | Out-Null
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

# Not a param default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there when run via -File.
if (-not $Source) { $Source = Split-Path -Parent $ScriptPath }

# Must match [Guid]/[ProgId] on PPTimer.Windows.Connect.
$Clsid = '{39674B3C-941D-4E54-9410-B0F926196B0B}'
$ProgId = 'PPTimer.Connect'
$ClassName = 'PPTimer.Windows.Connect'
$InstallDir = Join-Path $env:LOCALAPPDATA 'PPTimer\bin'

if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    throw 'PowerPoint is running. Close it (check the tray / Task Manager too) and run this again.'
}

$dllSource = Join-Path $Source 'PPTimer.dll'
if (-not (Test-Path $dllSource)) { throw "PPTimer.dll not found in $Source" }

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item $dllSource $InstallDir -Force
$pdb = Join-Path $Source 'PPTimer.pdb'
if (Test-Path $pdb) { Copy-Item $pdb $InstallDir -Force }

# Files that came from another machine / a zip carry a "downloaded" mark and .NET refuses to load them.
Get-ChildItem $InstallDir | Unblock-File

$dll = Join-Path $InstallDir 'PPTimer.dll'
$assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($dll)
$codeBase = ([Uri]$dll).AbsoluteUri

function Set-RegistryValues {
    param([Microsoft.Win32.RegistryView]$View, [string]$SubKey, [hashtable]$Values)
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $View)
    $key = $base.CreateSubKey($SubKey)
    try {
        foreach ($name in $Values.Keys) {
            $value = $Values[$name]
            $kind = if ($value -is [int]) { [Microsoft.Win32.RegistryValueKind]::DWord } else { [Microsoft.Win32.RegistryValueKind]::String }
            $key.SetValue($name, $value, $kind)
        }
    }
    finally {
        $key.Close()
        $base.Close()
    }
}

$comValues = @{
    'Class'          = $ClassName
    'Assembly'       = $assemblyName.FullName
    'RuntimeVersion' = 'v4.0.30319'
    'CodeBase'       = $codeBase
}

foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $clsidKey = "Software\Classes\CLSID\$Clsid"
    Set-RegistryValues $view $clsidKey @{ '' = $ProgId }
    Set-RegistryValues $view "$clsidKey\InprocServer32" ($comValues + @{ '' = 'mscoree.dll'; 'ThreadingModel' = 'Both' })
    Set-RegistryValues $view "$clsidKey\InprocServer32\$($assemblyName.Version)" $comValues
    Set-RegistryValues $view "$clsidKey\ProgId" @{ '' = $ProgId }
    Set-RegistryValues $view "$clsidKey\Implemented Categories\{62C8FE65-4EBB-45E7-B440-6E39B2CDBF29}" @{}
    Set-RegistryValues $view "Software\Classes\$ProgId" @{ '' = $ProgId }
    Set-RegistryValues $view "Software\Classes\$ProgId\CLSID" @{ '' = $Clsid }
}

$office = [Microsoft.Win32.RegistryView]::Registry64
Set-RegistryValues $office "Software\Microsoft\Office\PowerPoint\Addins\$ProgId" @{
    'FriendlyName' = 'PPTimer'
    'Description'  = 'Network-controlled countdown on the presenter view'
    'LoadBehavior' = 3
}
# Stop Office from auto-disabling the add-in if PowerPoint ever blames it for a slow start.
Set-RegistryValues $office 'Software\Microsoft\Office\16.0\PowerPoint\Resiliency\DoNotDisableAddinList' @{ $ProgId = 1 }

Write-Host ''
Write-Host "PPTimer installed to $InstallDir" -ForegroundColor Green
Write-Host "  Version  : $((Get-Item $dll).VersionInfo.ProductVersion)"
Write-Host "  Config   : $(Join-Path $env:LOCALAPPDATA 'PPTimer\config.json') (created on first start)"
Write-Host "  Log      : $(Join-Path $env:LOCALAPPDATA 'PPTimer\pptimer.log')"

# --- Network access (admin, only when missing) ---

# Port from this user's config, read before elevating (the admin may be another account).
$port = 9595
$config = Join-Path $env:LOCALAPPDATA 'PPTimer\config.json'
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

Write-Host ''
Write-Host 'Next: start PowerPoint and check File > Options > Add-ins > Manage: COM Add-ins shows PPTimer (ticked).'
