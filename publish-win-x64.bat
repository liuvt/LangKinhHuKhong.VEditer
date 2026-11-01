@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Chua cai .NET 8 SDK. Cai tu https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo === PUBLISH WINDOWS X64, SELF-CONTAINED ===
dotnet publish "LangKinhHuKhong.Editer.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -o ".\publish\win-x64"
if errorlevel 1 (
  echo Publish FAILED.
  pause
  exit /b 1
)
echo.
echo EXE: %~dp0publish\win-x64\LangKinhHuKhong.Editer.exe
echo Nho dat ffmpeg.exe va ffprobe.exe ben canh EXE hoac them vao PATH.
pause
