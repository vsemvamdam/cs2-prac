@echo off
title CS2 Prac - selftest
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0cs2prac.ps1" -Action selftest
