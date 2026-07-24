@echo off
setlocal enabledelayedexpansion

:: ===========================================================================
::  Builds the LanLink Android APK (Release, signed).
::
::  Version comes from the VERSION file next to this script (single source of
::  truth).  The 4-part version becomes the Android versionName; a numeric
::  versionCode is derived from it so Android recognises upgrades.
::
::  Requires: dotnet workload install maui-android
:: ===========================================================================

set "SCRIPT=%~dp0"
set "PROJECT=D:\visual studio projects\LanLink.Mobile\LanLink.Mobile\LanLink.Mobile.csproj"
set "APKDIR=D:\visual studio projects\LanLink.Mobile\LanLink.Mobile\bin\Release\net9.0-android"

:: --- Read the version from the VERSION file ---
if not exist "%SCRIPT%VERSION" ( echo ERROR: VERSION file not found at %SCRIPT%VERSION & exit /b 1 )
set /p VERSION=<"%SCRIPT%VERSION"
set "VERSION=%VERSION: =%"
if "%VERSION%"=="" ( echo ERROR: VERSION file is empty & exit /b 1 )

:: --- Derive an integer versionCode from the 4-part version (a.b.c.d) ---
for /f "tokens=1-4 delims=." %%a in ("%VERSION%") do (
    set "V1=%%a" & set "V2=%%b" & set "V3=%%c" & set "V4=%%d"
)
if "%V2%"=="" set "V2=0"
if "%V3%"=="" set "V3=0"
if "%V4%"=="" set "V4=0"
set /a VCODE=%V1%*1000000 + %V2%*10000 + %V3%*100 + %V4%

:: --- Point at an Android SDK that has the required platform.  The per-user
::     SDK is preferred (it carries android-35 that net9.0-android needs); fall
::     back to whatever the environment already provides. ---
if exist "%LOCALAPPDATA%\Android\Sdk\platforms\android-35" (
    set "ANDROID_HOME=%LOCALAPPDATA%\Android\Sdk"
    set "ANDROID_SDK_ROOT=%LOCALAPPDATA%\Android\Sdk"
    set "SDKARG=-p:AndroidSdkDirectory=%LOCALAPPDATA%\Android\Sdk"
) else (
    set "SDKARG="
)

echo === Building LanLink Android APK %VERSION% (versionCode %VCODE%) ===
echo.

dotnet publish "%PROJECT%" -c Release -f net9.0-android ^
    -p:ApplicationDisplayVersion=%VERSION% -p:ApplicationVersion=%VCODE% ^
    %SDKARG%
if %errorlevel% neq 0 (
    echo.
    echo BUILD FAILED.
    echo.
    echo If you see a workload error, run:
    echo   dotnet workload install maui-android
    exit /b 1
)

echo.
echo === Build succeeded ===
echo.
echo APK files:
for /r "%APKDIR%" %%f in (*.apk) do echo   %%f
echo.
echo Install on a connected device:
echo   adb install -r "path\to\apk"

endlocal
