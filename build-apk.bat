@echo off
setlocal

:: Builds the LanLink Android APK.
:: Requires: dotnet workload install maui-android

set "PROJECT=D:\visual studio projects\LanLink.Mobile\LanLink.Mobile\LanLink.Mobile.csproj"

echo === Building LanLink Android APK ===
echo.

dotnet publish "%PROJECT%" -c Release -f net9.0-android
if %errorlevel% neq 0 (
    echo.
    echo BUILD FAILED.
    echo.
    echo If you see a workload error, run:
    echo   dotnet workload install maui-android
    goto :end
)

echo.
echo === Build succeeded ===
echo.
echo APK files:
for /r "D:\visual studio projects\LanLink.Mobile\LanLink.Mobile\bin\Release\net9.0-android" %%f in (*.apk) do (
    echo   %%f
)
echo.
echo Install on a connected device:
echo   adb install -r "path\to\apk"

:end
echo.
pause
