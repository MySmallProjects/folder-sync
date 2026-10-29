@echo off
setlocal enabledelayedexpansion

REM Build and package FolderSyncEr for Windows (win-x64)
REM Usage: scripts\build-windows.bat [--self-contained]

set "ROOT_DIR=%~dp0.."
set "DIST_DIR=%ROOT_DIR%\dist\win-x64"
set "STAGE_DIR=%DIST_DIR%\package"
set "SELF_CONTAINED=true"

if "%1"=="--framework-dependent" (
    set "SELF_CONTAINED=false"
)

echo ==> Building FolderSyncEr for Windows (x64) [Self-Contained: %SELF_CONTAINED%]...
if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
mkdir "%STAGE_DIR%"

echo ==> Publishing FolderSyncEr.Server...
dotnet publish "%ROOT_DIR%\src\FolderSyncEr.Server\FolderSyncEr.Server.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained %SELF_CONTAINED% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%DIST_DIR%\server-publish"

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Failed to publish Server.
    exit /b %ERRORLEVEL%
)

echo ==> Publishing FolderSyncEr.Client.App...
dotnet publish "%ROOT_DIR%\src\FolderSyncEr.Client.App\FolderSyncEr.Client.App.csproj" ^
    -c Release ^
    -r win-x64 ^
    --self-contained %SELF_CONTAINED% ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%DIST_DIR%\client-publish"

if %ERRORLEVEL% neq 0 (
    echo [ERROR] Failed to publish Client.
    exit /b %ERRORLEVEL%
)

echo ==> Assembling release package...
copy "%DIST_DIR%\server-publish\FolderSyncEr.Server.exe" "%STAGE_DIR%\FolderSyncEr.Server.exe" >nul
copy "%DIST_DIR%\client-publish\FolderSyncEr.Client.App.exe" "%STAGE_DIR%\FolderSyncEr.Client.exe" >nul
if exist "%DIST_DIR%\server-publish\appsettings.json" (
    copy "%DIST_DIR%\server-publish\appsettings.json" "%STAGE_DIR%\appsettings.json" >nul
)

echo ==> Windows package successfully assembled at: %STAGE_DIR%
echo Server: %STAGE_DIR%\FolderSyncEr.Server.exe
echo Client: %STAGE_DIR%\FolderSyncEr.Client.exe

