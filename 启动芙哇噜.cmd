@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\launch-dragon-girl.ps1"
if errorlevel 1 pause
