@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Prepare-Audio.ps1"
if errorlevel 1 (
  echo.
  echo Setup failed. See the message above.
)
pause
