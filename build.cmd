@echo off
cd /d "%~dp0"

echo ========================================
echo   MouseBattery - Build
echo ========================================
echo.

echo [1/3] Building slim version (needs .NET 8 Desktop Runtime)...
dotnet publish -c Release -o dist
if errorlevel 1 goto fail

echo.
echo [2/3] Building portable version (self-contained, single file)...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=none ^
  -o dist-portable
if errorlevel 1 goto fail

echo.
echo [3/3] Cleaning intermediates (bin, obj)...
rem 中间产物有 30+ MB，且每次编译都会重新生成。
rem 留着只会让工作区越来越大（编辑器/备份工具会一并收录），这里直接清掉。
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj

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
