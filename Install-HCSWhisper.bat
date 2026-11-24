@echo off
title HCSWhisper One-Click Installer
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/lordryn/HCSWhisper/main/install.ps1 | iex"
echo.
pause
