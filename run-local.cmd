@echo off
REM ---- Run Annoted WPF locally using the portable .NET SDK (no system install) ----
REM Edit this one line if you move the portable SDK folder:
set "DOTNET_ROOT=G:\00testfiles\dotnet-sdk-8.0.421-win-x64"
set "PATH=%DOTNET_ROOT%;%PATH%"

echo Building and launching Annoted WPF...
"%DOTNET_ROOT%\dotnet.exe" run --project "%~dp0Annoted.Wpf\Annoted.Wpf.csproj" -c Debug
if errorlevel 1 (
  echo.
  echo Build or run failed - see messages above.
  pause
)
