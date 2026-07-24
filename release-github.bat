@echo off
setlocal enabledelayedexpansion

:: ===========================================================================
::  Publishes a GitHub release for LanLink (github.com/inhahe/LanLink).
::
::  * The tag/version comes from the VERSION file next to this script.
::  * If a release for that version already exists, nothing is published.
::  * Otherwise the desktop installer (+ self-contained exe) and, when it can
::    be built, the Android APK are (re)built and attached to a new release
::    tagged v<version>.
::
::  Requirements: the GitHub CLI (`gh`) authenticated with repo scope.
:: ===========================================================================

set "SCRIPT=%~dp0"
set "REPO=inhahe/LanLink"

:: --- Read the version ---
if not exist "%SCRIPT%VERSION" ( echo ERROR: VERSION file not found at %SCRIPT%VERSION & exit /b 1 )
set /p VERSION=<"%SCRIPT%VERSION"
set "VERSION=%VERSION: =%"
if "%VERSION%"=="" ( echo ERROR: VERSION file is empty & exit /b 1 )
set "TAG=v%VERSION%"

echo === LanLink GitHub release ===
echo Repo:    %REPO%
echo Version: %VERSION%   (tag %TAG%)
echo.

:: --- Make sure gh is present and authenticated ---
where gh >nul 2>&1 || ( echo ERROR: GitHub CLI ^(gh^) not found. Install from https://cli.github.com/ & exit /b 1 )
gh auth status >nul 2>&1 || ( echo ERROR: gh is not authenticated. Run: gh auth login & exit /b 1 )

:: --- Bail out if this version was already released ---
gh release view "%TAG%" --repo "%REPO%" >nul 2>&1
if not errorlevel 1 (
    echo Release %TAG% already exists on %REPO% - nothing to do.
    echo Bump the VERSION file to publish a new release.
    exit /b 0
)

:: --- Build the desktop installer (also produces the self-contained exe) ---
echo [1/3] Building Windows installer + exe...
call "%SCRIPT%build-msi.bat"
if errorlevel 1 ( echo ERROR: installer build failed & exit /b 1 )

set "MSI=%SCRIPT%LanLink-%VERSION%.msi"
set "EXE_SRC=%SCRIPT%installer\publish\LanLink.exe"
set "EXE=%SCRIPT%LanLink-%VERSION%.exe"
if not exist "%MSI%" ( echo ERROR: expected MSI not found: %MSI% & exit /b 1 )
if not exist "%EXE_SRC%" ( echo ERROR: expected exe not found: %EXE_SRC% & exit /b 1 )
copy /y "%EXE_SRC%" "%EXE%" >nul

:: --- Build the Android APK (best effort - skipped if it can't build here) ---
echo.
echo [2/3] Building Android APK (optional)...
set "APK="
call "%SCRIPT%build-apk.bat"
if errorlevel 1 (
    echo WARNING: APK build failed or was skipped - releasing without the APK.
) else (
    set "APKDIR=D:\visual studio projects\LanLink.Mobile\LanLink.Mobile\bin\Release\net9.0-android"
    for /f "delims=" %%f in ('dir /b /s /o-d "!APKDIR!\*-Signed.apk" 2^>nul') do (
        if not defined APK set "APK=%%f"
    )
    if defined APK (
        copy /y "!APK!" "%SCRIPT%LanLink-%VERSION%.apk" >nul
        set "APK=%SCRIPT%LanLink-%VERSION%.apk"
    ) else (
        echo WARNING: APK build reported success but no -Signed.apk was found.
    )
)

:: --- Create the release and upload assets ---
echo.
echo [3/3] Creating GitHub release %TAG%...
:: Quote every asset path individually - they live under "D:\visual studio
:: projects\..." which contains spaces.
set "ASSETS="%EXE%" "%MSI%""
set "APK_NOTE="
if defined APK (
    set "ASSETS=%ASSETS% "%APK%""
    set "APK_NOTE=, Android APK (LanLink-%VERSION%.apk)"
)

gh release create "%TAG%" %ASSETS% ^
    --repo "%REPO%" ^
    --title "LanLink %VERSION%" ^
    --notes "LanLink %VERSION%. Assets: self-contained Windows exe (LanLink-%VERSION%.exe), Windows installer (LanLink-%VERSION%.msi)%APK_NOTE%."
if errorlevel 1 ( echo ERROR: gh release create failed & exit /b 1 )

echo.
echo === Done ===
echo Released %TAG% to https://github.com/%REPO%/releases/tag/%TAG%
endlocal
