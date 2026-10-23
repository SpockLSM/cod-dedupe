@echo off
rem CoD Shared Install: keeps one copy of Call of Duty's game data for the Xbox app, Steam and Battle.net.
rem Run it after every Call of Duty update, once every store has finished updating.
rem Options go after the name, e.g.  "Share CoD files.cmd" -DryRun   (see README.md)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0cod-shared-install.ps1" %*
echo.
pause
