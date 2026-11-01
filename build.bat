@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Chua cai .NET 8 SDK. Cai tu https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo === RESTORE ===
dotnet restore "LangKinhHuKhong.Editer.csproj"
if errorlevel 1 goto fail
echo === BUILD RELEASE ===
dotnet build "LangKinhHuKhong.Editer.csproj" -c Release --no-restore
if errorlevel 1 goto fail
echo.
echo EXE: %~dp0bin\Release\net8.0-windows\LangKinhHuKhong.Editer.exe
pause
exit /b 0
:fail
echo.
echo Build FAILED. Xem chi tiet phia tren.
pause
exit /b 1
