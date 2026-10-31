@echo off
rem Lanza la version portable autocontenida de GestoClip (no requiere .NET en el PATH)
if exist "%~dp0dist\GestoClip-win-x64\GestoClip.App.exe" (
    start "" "%~dp0dist\GestoClip-win-x64\GestoClip.App.exe"
) else (
    set DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet
    start "" "%~dp0GestoClip.App\bin\Debug\net9.0-windows\GestoClip.App.exe"
)
