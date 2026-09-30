<#
.SYNOPSIS
  Installs (or updates) the PPTimer PowerPoint add-in for the current user. No admin rights needed.

.DESCRIPTION
  Copies PPTimer.dll to %LOCALAPPDATA%\PPTimer\bin, registers it as a per-user COM class
  (both 64- and 32-bit registry views, so it works with either Office bitness) and registers
  it as a PowerPoint COM add-in. Re-run after every new build; PowerPoint must be closed.
#>
[CmdletBinding()]
param(
    [string]$Source
)
$ErrorActionPreference = 'Stop'

# Not a param default: Windows PowerShell 5.1 leaves $PSScriptRoot empty there when run via -File.
if (-not $Source) { $Source = Split-Path -Parent $MyInvocation.MyCommand.Path }

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
Write-Host "  Assembly : $($assemblyName.FullName)"
Write-Host "  Config   : $(Join-Path $env:LOCALAPPDATA 'PPTimer\config.json') (created on first start)"
Write-Host "  Log      : $(Join-Path $env:LOCALAPPDATA 'PPTimer\pptimer.log')"
Write-Host ''
Write-Host 'Next: start PowerPoint and check File > Options > Add-ins > Manage: COM Add-ins shows PPTimer (ticked).'
Write-Host 'For network control run setup-network.cmd once (asks for admin).'
