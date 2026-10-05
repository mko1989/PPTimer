<#
.SYNOPSIS
  Removes the PPTimer add-in registration for the current user, and the URL reservation and
  firewall rule that install.ps1 added (asks for admin permission if they exist).
  -RemoveFiles also deletes %LOCALAPPDATA%\PPTimer\bin (config.json and the log are kept).
#>
[CmdletBinding()]
param(
    [switch]$RemoveFiles
)
$ErrorActionPreference = 'Stop'

$Clsid = '{39674B3C-941D-4E54-9410-B0F926196B0B}'
$ProgId = 'PPTimer.Connect'

if (Get-Process POWERPNT -ErrorAction SilentlyContinue) {
    throw 'PowerPoint is running. Close it and run this again.'
}

foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32)) {
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, $view)
    try {
        $base.DeleteSubKeyTree("Software\Classes\CLSID\$Clsid", $false)
        $base.DeleteSubKeyTree("Software\Classes\$ProgId", $false)
        if ($view -eq [Microsoft.Win32.RegistryView]::Registry64) {
            $base.DeleteSubKeyTree("Software\Microsoft\Office\PowerPoint\Addins\$ProgId", $false)
            $list = $base.OpenSubKey('Software\Microsoft\Office\16.0\PowerPoint\Resiliency\DoNotDisableAddinList', $true)
            if ($list) {
                $list.DeleteValue($ProgId, $false)
                $list.Close()
            }
        }
    }
    finally {
        $base.Close()
    }
}

if ($RemoveFiles) {
    $bin = Join-Path $env:LOCALAPPDATA 'PPTimer\bin'
    if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
}

Write-Host 'PPTimer unregistered.' -ForegroundColor Green

# Network access (admin part, done by install.ps1 elevated).
$port = 9595
$config = Join-Path $env:LOCALAPPDATA 'PPTimer\config.json'
if (Test-Path $config) {
    try {
        $configured = (Get-Content $config -Raw | ConvertFrom-Json).port
        if ($configured) { $port = [int]$configured }
    }
    catch { }
}
$reserved = (& netsh http show urlacl url="http://+:$port/" 2>&1 | Out-String) -match [regex]::Escape("http://+:$port/")
$rule = Get-NetFirewallRule -DisplayName 'PPTimer (TCP *)' -ErrorAction SilentlyContinue
if ($reserved -or $rule) {
    $install = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'install.ps1'
    Write-Host "Removing network access on port $port. Windows will ask for admin permission..."
    try {
        $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$install`"", '-Network', 'Remove', '-Port', $port)
        if ($p.ExitCode -eq 0) { Write-Host 'Network access removed.' -ForegroundColor Green }
    }
    catch {
        Write-Warning "Admin permission was not given; the URL reservation and firewall rule for port $port are still there."
    }
}
