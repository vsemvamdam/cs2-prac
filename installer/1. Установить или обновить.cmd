@echo off
title CS2 Prac - install / update
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action update %*
echo.
pause
