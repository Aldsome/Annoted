@echo off
REM ---- Run Annoted WPF locally ----
REM Tries system dotnet first; falls back to portable SDK if present.

set "PORTABLE_SDK=G:\00testfiles\dotnet-sdk-8.0.421-win-x64"

where dotnet >nul 2>nul
if errorlevel 1 (
  if exist "%PORTABLE_SDK%\dotnet.exe" (
    echo System .NET SDK not found - using portable SDK at %PORTABLE_SDK%
    set "DOTNET_ROOT=%PORTABLE_SDK%"
    set "PATH=%PORTABLE_SDK%;%PATH%"
  ) else (
    echo No .NET SDK found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
    pause
    exit /b 1
  )
)

echo Building and launching Annoted WPF...
dotnet run --project "%~dp0Annoted.Wpf\Annoted.Wpf.csproj" -c Debug
if errorlevel 1 (
  echo.
  echo Build or run failed - see messages above.
  pause
)
