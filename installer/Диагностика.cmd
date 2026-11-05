@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action diag
start "" notepad "%~dp0logs\diag.txt"
