<#
.SYNOPSIS
  One-time admin setup so the add-in (running inside PowerPoint as a normal user) can accept
  connections from other machines: an HTTP URL reservation for http://+:PORT/ and an inbound
  firewall rule for that TCP port. Re-launches itself elevated. -Remove undoes both.

  Without this the add-in still works, but only from the same PC (http://localhost:PORT/).
#>
[CmdletBinding()]
param(
    [int]$Port = 0,
    [switch]$Remove,
    [switch]$Elevated
)
$ErrorActionPreference = 'Stop'

if ($Port -eq 0) {
    # Read the port from the invoking user's config before elevating (admin may be another account).
    $Port = 9595
    $config = Join-Path $env:LOCALAPPDATA 'PPTimer\config.json'
    if (Test-Path $config) {
        try {
            $configured = (Get-Content $config -Raw | ConvertFrom-Json).port
            if ($configured) { $Port = [int]$configured }
        }
        catch { Write-Warning "Could not read $config, using port $Port" }
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Port', $Port, '-Elevated')
    if ($Remove) { $arguments += '-Remove' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    return
}

try {
    $url = "http://+:$Port/"
    $ruleName = "PPTimer (TCP $Port)"

    & netsh http delete urlacl url=$url 2>&1 | Out-Null
    Get-NetFirewallRule -DisplayName 'PPTimer (TCP *)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

    if ($Remove) {
        Write-Host "Removed URL reservation and firewall rule for port $Port." -ForegroundColor Green
        return
    }

    # D:(A;;GX;;;WD) = allow Everyone to listen on this URL (language-independent SDDL).
    & netsh http add urlacl "url=$url" "sddl=D:(A;;GX;;;WD)"
    if ($LASTEXITCODE -ne 0) { throw "netsh http add urlacl failed ($LASTEXITCODE)" }

    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port `
        -Action Allow -Profile Any -Description 'PPTimer PowerPoint add-in remote control' | Out-Null

    Write-Host ''
    Write-Host "Network access enabled on port $Port." -ForegroundColor Green
    Write-Host 'Restart PowerPoint if it is running. Test from another machine: http://<this-PC-IP>:' -NoNewline
    Write-Host "$Port/"
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { Write-Host "  this PC: $($_.IPAddress)  ($($_.InterfaceAlias))" }
}
finally {
    if ($Elevated) { Read-Host 'Press Enter to close' | Out-Null }
}
