@echo off
chcp 65001 >nul
if "%~1"=="" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\convert-alpha-video.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\convert-alpha-video.ps1" -Source "%~1"
)
pause
