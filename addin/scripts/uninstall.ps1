<#
.SYNOPSIS
  Removes the PPTimer add-in registration for the current user.
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
Write-Host 'Run "setup-network.cmd -Remove" to also remove the URL reservation and firewall rule.'
