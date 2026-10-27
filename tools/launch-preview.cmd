@echo off
rem Packaging template: place beside TablePet.exe as Start-Preview.cmd and copy launch-preview.ps1 as Start-Preview.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0Start-Preview.ps1"
