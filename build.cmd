@echo off
setlocal

rem Default entry point: double-click this file to build a provisioned CyanDeck.exe.
rem build.ps1 requires json\client_secret.json (Google Desktop OAuth) when started
rem from here, so a double-click never produces an unprovisioned executable.
rem Use this script's directory, even when Explorer starts it from elsewhere.
set "RUNTIME=%~1"
if not defined RUNTIME set "RUNTIME=win-x64"

echo [CyanDeck] Building %RUNTIME% from:
echo "%~dp0"
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" -Runtime "%RUNTIME%" -RequireGoogleOAuth
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
    echo.
    echo [ERROR] CyanDeck build failed ^(code %EXITCODE%^).
    echo Check the error above. Press any key to close this window.
    pause >nul
)

endlocal & exit /b %EXITCODE%
