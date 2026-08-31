@echo off
setlocal
cd /d "%~dp0"
dotnet run --project FusePlayer.csproj
if errorlevel 1 pause
