@echo off
title CS2 Prac - plugin update
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action plugin
echo.
pause
