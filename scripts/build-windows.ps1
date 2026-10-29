<#
.SYNOPSIS
    Builds and packages FolderSyncEr for Windows (win-x64).
.PARAMETER SelfContained
    Whether to build self-contained packages (default: $true).
#>
param(
    [switch]$SelfContained = $true
)

$ErrorActionPreference = "Stop"
$RootDir = Split-Path -Parent $PSScriptRoot
$DistDir = Join-Path $RootDir "dist\win-x64"
$StageDir = Join-Path $DistDir "package"
$SelfContainedStr = if ($SelfContained) { "true" } else { "false" }

Write-Host "==> Building FolderSyncEr for Windows (x64) [Self-Contained: $SelfContainedStr]..." -ForegroundColor Cyan

if (Test-Path $DistDir) {
    Remove-Item -Recurse -Force $DistDir
}
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

$ServerProj = Join-Path $RootDir "src\FolderSyncEr.Server\FolderSyncEr.Server.csproj"
$ClientProj = Join-Path $RootDir "src\FolderSyncEr.Client.App\FolderSyncEr.Client.App.csproj"

Write-Host "==> Publishing FolderSyncEr.Server..." -ForegroundColor Yellow
dotnet publish $ServerProj `
    -c Release `
    -r win-x64 `
    --self-contained $SelfContainedStr `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$DistDir\server-publish"

Write-Host "==> Publishing FolderSyncEr.Client.App..." -ForegroundColor Yellow
dotnet publish $ClientProj `
    -c Release `
    -r win-x64 `
    --self-contained $SelfContainedStr `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$DistDir\client-publish"

Write-Host "==> Assembling release package..." -ForegroundColor Green
Copy-Item "$DistDir\server-publish\FolderSyncEr.Server.exe" "$StageDir\FolderSyncEr.Server.exe"
Copy-Item "$DistDir\client-publish\FolderSyncEr.Client.App.exe" "$StageDir\FolderSyncEr.Client.exe"
if (Test-Path "$DistDir\server-publish\appsettings.json") {
    Copy-Item "$DistDir\server-publish\appsettings.json" "$StageDir\appsettings.json"
}

Write-Host "==> Windows package successfully assembled at: $StageDir" -ForegroundColor Green

