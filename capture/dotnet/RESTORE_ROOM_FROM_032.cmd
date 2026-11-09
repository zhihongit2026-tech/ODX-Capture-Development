@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0RESTORE_ROOM_FROM_032.ps1" -TargetFolder "%~dp0"
if errorlevel 1 echo ROOM CONFIG RESTORE FAILED. Review the error above.
pause
