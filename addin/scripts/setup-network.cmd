@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-network.ps1" %*
pause
