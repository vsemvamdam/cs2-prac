@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action stop
if errorlevel 1 pause
