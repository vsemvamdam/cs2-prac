@echo off
title CS2 Prac - smokes
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action start -Mode smokes %*
if errorlevel 1 pause
