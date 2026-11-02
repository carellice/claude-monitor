@echo off
rem Publishes a new GitHub release with the Windows exe and the Android APK.
rem Usage: release.bat            (1.0.0 -> 1.0.1)
rem        release.bat minor      (1.0.1 -> 1.1.0)
rem        release.bat major      (1.1.0 -> 2.0.0)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\release.ps1" %*
if errorlevel 1 (
    echo.
    echo Release non riuscita.
    pause
    exit /b 1
)
pause
