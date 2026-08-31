@echo off
setlocal
cd /d "%~dp0"
dotnet publish FusePlayer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:Version=1.0.0 -o artifacts\single
if errorlevel 1 goto :failed
copy /y artifacts\single\Fuze.exe Fuze.exe >nul
if errorlevel 1 pause
goto :end

:failed
pause

:end
