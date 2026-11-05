@echo off
title CS2 Prac - prefire
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action start -Mode prefire %*
if errorlevel 1 pause
