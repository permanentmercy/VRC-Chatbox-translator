@echo off
cd /d "%~dp0"
if exist "bin\Debug\net10.0-windows10.0.26100.0\win-x64\VrcChatboxDemo.exe" (
    start "" /d "%~dp0bin\Debug\net10.0-windows10.0.26100.0\win-x64" "%~dp0bin\Debug\net10.0-windows10.0.26100.0\win-x64\VrcChatboxDemo.exe"
) else (
    dotnet run --no-build
)
