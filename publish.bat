@echo off
setlocal
cd /d "%~dp0"

echo Building YtDownloader.exe — 1-2 min on first build.
echo.

dotnet publish YtDownloader -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=none ^
    -p:DebugSymbols=false ^
    -o "%~dp0dist"

if errorlevel 1 (
    echo.
    echo Build FAILED. Scroll up for errors.
    pause
    exit /b 1
)

echo.
echo ============================================
echo  Done. Standalone app:
echo    %~dp0dist\YtDownloader.exe
echo.
echo  Double-click it, pin it to Start or Taskbar,
echo  or copy the .exe to any Windows 10/11 x64 PC
echo  (no .NET install needed on the target PC).
echo ============================================
echo.
pause
