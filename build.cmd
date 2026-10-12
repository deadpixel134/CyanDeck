@echo off
setlocal

rem Default entry point: double-click this file to build and launch Calendar v26.
rem Use this script's directory, even when Explorer starts it from elsewhere.
set "RUNTIME=%~1"
if not defined RUNTIME set "RUNTIME=win-x64"

echo [DesktopDeck v26] Building and launching from:
echo "%~dp0"
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\build-and-launch-calendar-v26.ps1" -Runtime "%RUNTIME%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo [ERROR] DesktopDeck v26 build or launch failed ^(code %EXITCODE%^).
    echo The old DesktopDeck.exe has NOT been started by this launcher.
    echo Check the error above. Press any key to close this window.
    pause >nul
)

endlocal & exit /b %EXITCODE%
