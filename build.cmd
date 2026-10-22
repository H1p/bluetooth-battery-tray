@echo off
cd /d "%~dp0"

echo ========================================
echo   MouseBattery - Build
echo ========================================
echo.

echo [1/2] Building slim version (needs .NET 8 Desktop Runtime)...
dotnet publish -c Release -o dist
if errorlevel 1 goto fail

echo.
echo [2/2] Building portable version (self-contained, single file)...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -o dist-portable
if errorlevel 1 goto fail

echo.
echo ========================================
echo   Done
echo ========================================
echo   slim     : %~dp0dist\MouseBattery.exe
echo   portable : %~dp0dist-portable\MouseBattery.exe
echo.
echo   When sharing, send ONLY the exe from dist-portable.
echo   Do NOT include settings.json - it contains your device address.
echo.
pause
exit /b 0

:fail
echo.
echo [FAILED] Build error, check the messages above.
pause
exit /b 1
