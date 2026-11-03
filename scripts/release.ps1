#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and packages Clicky for Windows as a single-file executable
    and optionally wraps it in an Inno Setup installer.

.DESCRIPTION
    1. Runs dotnet publish to produce a self-contained single-file exe.
    2. If Inno Setup (iscc.exe) is on PATH, compiles the .iss script into
       a Setup_Clicky.exe installer.

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER Runtime
    Target runtime identifier. Default: win-x64

.PARAMETER SkipInstaller
    If set, skips the Inno Setup step and only produces the publish output.

.EXAMPLE
    .\release.ps1
    .\release.ps1 -SkipInstaller
    .\release.ps1 -Configuration Debug -Runtime win-arm64
#>
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$windowsRoot = Join-Path $repoRoot "windows"
$solutionPath = Join-Path $windowsRoot "Clicky.sln"
$appProjectPath = Join-Path $windowsRoot "src\Clicky.App\Clicky.App.csproj"
$publishDir = Join-Path $windowsRoot "publish\$Runtime"
$installerScript = Join-Path $windowsRoot "scripts\clicky-installer.iss"
$installerOutputDir = Join-Path $windowsRoot "installer"

Write-Host "=== Clicky for Windows - Release Build ===" -ForegroundColor Cyan
Write-Host "Configuration : $Configuration"
Write-Host "Runtime       : $Runtime"
Write-Host "Publish dir   : $publishDir"
Write-Host ""

# Step 1: Clean previous publish output
if (Test-Path $publishDir) {
    Write-Host "Cleaning previous publish output..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $publishDir
}

# Step 2: Publish as a single-file executable (self-contained)
Write-Host "Publishing Clicky.App..." -ForegroundColor Green
dotnet publish $appProjectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Publish succeeded. Output:" -ForegroundColor Green
Get-ChildItem $publishDir | ForEach-Object {
    Write-Host "  $($_.Name) ($([math]::Round($_.Length / 1MB, 2)) MB)"
}

# Step 3: Build Inno Setup installer (if available and not skipped)
if ($SkipInstaller) {
    Write-Host ""
    Write-Host "Skipping installer (SkipInstaller flag set)." -ForegroundColor Yellow
} else {
    $iscc = Get-Command "iscc" -ErrorAction SilentlyContinue
    if (-not $iscc) {
        # Try common Inno Setup install locations
        $commonPaths = @(
            "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
            "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
        )
        foreach ($p in $commonPaths) {
            if (Test-Path $p) {
                $iscc = Get-Item $p
                break
            }
        }
    }

    if ($iscc) {
        Write-Host ""
        Write-Host "Building Inno Setup installer..." -ForegroundColor Green

        if (-not (Test-Path $installerOutputDir)) {
            New-Item -ItemType Directory -Path $installerOutputDir | Out-Null
        }

        & $iscc.FullName `
            "/DPublishDir=$publishDir" `
            "/DOutputDir=$installerOutputDir" `
            $installerScript

        if ($LASTEXITCODE -ne 0) {
            Write-Error "Inno Setup compilation failed with exit code $LASTEXITCODE"
            exit $LASTEXITCODE
        }

        Write-Host ""
        Write-Host "Installer built:" -ForegroundColor Green
        Get-ChildItem $installerOutputDir -Filter "*.exe" | ForEach-Object {
            Write-Host "  $($_.FullName) ($([math]::Round($_.Length / 1MB, 2)) MB)"
        }
    } else {
        Write-Host ""
        Write-Host "Inno Setup (iscc.exe) not found. Skipping installer step." -ForegroundColor Yellow
        Write-Host "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php to build the installer."
    }
}

Write-Host ""
Write-Host "=== Done ===" -ForegroundColor Cyan
