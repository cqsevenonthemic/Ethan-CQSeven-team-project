@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start_live_demo.ps1" %*
exit /b %ERRORLEVEL%
