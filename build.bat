@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK was not found.
    echo Install the .NET 8 SDK and run this file again.
    pause
    exit /b 1
)

echo Building Keyboard Key Guard v3...
dotnet publish KeyboardKeyGuard.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish

if errorlevel 1 (
    echo.
    echo BUILD FAILED.
    pause
    exit /b 1
)

echo.
echo BUILD SUCCESSFUL.
echo Your EXE is:
echo %cd%\publish\KeyboardKeyGuard.exe
echo.
pause
