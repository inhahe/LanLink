@echo off
setlocal enabledelayedexpansion

:: ===========================================================================
::  Builds the LanLink Windows installer (LanLink-<version>.msi) and drops it
::  in the directory this script is run from.
::
::  The version comes from the VERSION file next to this script (single source
::  of truth).  Bump it for each release so Windows sees the new build as an
::  upgrade.
:: ===========================================================================

set "SCRIPT=%~dp0"

:: --- Read the version from the VERSION file ---
if not exist "%SCRIPT%VERSION" ( echo ERROR: VERSION file not found at %SCRIPT%VERSION & exit /b 1 )
set /p VERSION=<"%SCRIPT%VERSION"
set "VERSION=%VERSION: =%"
if "%VERSION%"=="" ( echo ERROR: VERSION file is empty & exit /b 1 )

set "PROJECT=%SCRIPT%LanLink\LanLink.csproj"
set "PUBLISH=%SCRIPT%installer\publish"
set "WXS=%SCRIPT%installer\LanLink.wxs"
set "OUT=%CD%\LanLink-%VERSION%.msi"

echo === Building LanLink %VERSION% installer ===
echo Output: %OUT%
echo.

echo [1/4] Publishing self-contained single-file exe...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true -p:Version=%VERSION% -o "%PUBLISH%"
if errorlevel 1 ( echo ERROR: publish failed & exit /b 1 )

echo.
echo [2/4] Ensuring WiX toolset is installed...
where wix >nul 2>&1 || dotnet tool install --global wix
if errorlevel 1 ( echo ERROR: could not install WiX. Install with: dotnet tool install --global wix & exit /b 1 )

echo.
echo [3/4] Ensuring WiX UI extension is available...
wix extension list --global 2>nul | findstr /i "WixToolset.UI.wixext" >nul || wix extension add --global WixToolset.UI.wixext
if errorlevel 1 ( echo ERROR: could not add WixToolset.UI.wixext & exit /b 1 )

echo.
echo [4/4] Building MSI...
wix build "%WXS%" -arch x64 ^
    -d Version=%VERSION% -d PublishDir="%PUBLISH%" ^
    -b "%SCRIPT%installer" -b "%SCRIPT%LanLink" ^
    -ext WixToolset.UI.wixext ^
    -o "%OUT%"
if errorlevel 1 ( echo ERROR: MSI build failed & exit /b 1 )

echo.
echo === Done ===
echo Installer written to: %OUT%
endlocal
