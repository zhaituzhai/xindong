@echo off
setlocal
cd /d "%~dp0"

echo ============================================================
echo   XinDong - portable publish
echo   Output: %~dp0publish\XinDong
echo ============================================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] dotnet not found. Install .NET 8 SDK first:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
)

echo [1/3] Publishing Release build ...
dotnet publish "%~dp0XinDong.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "%~dp0publish\XinDong"
if errorlevel 1 (
    echo.
    echo [FAILED] Publish failed. Please copy the errors above.
    pause
    exit /b 1
)

echo.
echo [2/3] Cleaning symbols ...
if exist "%~dp0publish\XinDong\*.pdb" del /q "%~dp0publish\XinDong\*.pdb" >nul 2>nul

echo [3/3] Done.
echo.
echo Portable folder: %~dp0publish\XinDong
echo Copy the whole folder to any Windows 10/11 x64 machine and run XinDong.exe.
echo Config and ledger files will be created next to the exe.
echo.
pause
