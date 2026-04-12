#Requires -Version 5.1
<#
.SYNOPSIS
    Aviates Air Flight Tracker - Build Script

.PARAMETER Release
    Publish a self-contained single-file EXE to dist\

.PARAMETER Clean
    Clean before building.

.PARAMETER Run
    Launch the app after building.

.PARAMETER Installer
    Compile the Inno Setup installer after -Release. Requires Inno Setup 6 to be installed.
#>
param(
    [switch]$Release,
    [switch]$Clean,
    [switch]$Run,
    [switch]$Installer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RootDir       = $PSScriptRoot
$ProjectDir    = Join-Path $RootDir  "AviatesAirTracker"
$ProjectFile   = Join-Path $ProjectDir "AviatesAirTracker.csproj"
$LibsDir       = Join-Path $ProjectDir "Libs"
$OutputDir     = Join-Path $RootDir  "dist"
$InstallerDir  = Join-Path $RootDir  "installer"
$InstallerScript = Join-Path $InstallerDir "AviatesAirTracker_Setup.iss"
$Configuration = if ($Release) { "Release" } else { "Debug" }

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Aviates Air Flight Tracker  -  Build    " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  Config  : $Configuration"
Write-Host "  Project : $ProjectFile"
Write-Host ""

# ---------------------------------------------------------------------------
# .NET SDK check
# ---------------------------------------------------------------------------
Write-Host ">> Checking .NET SDK..." -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK not found. Install .NET 8 from https://dotnet.microsoft.com/download"
    exit 1
}
$dotnetVer = & dotnet --version
Write-Host "   .NET SDK: $dotnetVer" -ForegroundColor Green
Write-Host ""

# ---------------------------------------------------------------------------
# SimConnect info (informational only - build always succeeds without DLL)
# ---------------------------------------------------------------------------
$simConnectDll = Join-Path $LibsDir "Microsoft.FlightSimulator.SimConnect.dll"

if (Test-Path $simConnectDll) {
    $dllKB = [math]::Round((Get-Item $simConnectDll).Length / 1KB, 0)
    Write-Host "  SimConnect DLL found ($dllKB KB)." -ForegroundColor Green
    Write-Host "  For live telemetry use the MANAGED wrapper from:" -ForegroundColor Gray
    Write-Host "  [MSFS SDK]\SimConnect SDK\lib\managed" -ForegroundColor Gray
} else {
    Write-Host "  SimConnect DLL not found - building in stub mode." -ForegroundColor Yellow
    Write-Host "  The UI runs fully; place the managed wrapper at:" -ForegroundColor Yellow
    Write-Host "  $simConnectDll" -ForegroundColor Gray
    if (-not (Test-Path $LibsDir)) {
        New-Item -ItemType Directory -Path $LibsDir | Out-Null
    }
}
Write-Host ""

# ---------------------------------------------------------------------------
# Clean
# ---------------------------------------------------------------------------
if ($Clean) {
    Write-Host ">> Cleaning..." -ForegroundColor Yellow
    & dotnet clean "$ProjectFile" -c $Configuration | Out-Null
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
        Write-Host "   Removed dist" -ForegroundColor Gray
    }
    Write-Host "   Clean complete." -ForegroundColor Green
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Restore
# ---------------------------------------------------------------------------
Write-Host ">> Restoring NuGet packages..." -ForegroundColor Yellow
& dotnet restore "$ProjectFile"
if ($LASTEXITCODE -ne 0) {
    Write-Error "NuGet restore failed (exit $LASTEXITCODE)."
    exit 1
}
Write-Host "   Packages restored." -ForegroundColor Green
Write-Host ""

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
Write-Host ">> Building ($Configuration)..." -ForegroundColor Yellow
& dotnet build "$ProjectFile" -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed (exit $LASTEXITCODE)."
    exit 1
}
Write-Host "   Build successful." -ForegroundColor Green
Write-Host ""

# ---------------------------------------------------------------------------
# Publish (Release only)
# ---------------------------------------------------------------------------
if ($Release) {
    Write-Host ">> Publishing single-file EXE..." -ForegroundColor Yellow

    if (-not (Test-Path $OutputDir)) {
        New-Item -ItemType Directory -Path $OutputDir | Out-Null
    }

    $publishArgs = @(
        "publish", "$ProjectFile",
        "-c", "Release",
        "-r", "win-x64",
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o", "$OutputDir"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Publish failed (exit $LASTEXITCODE)."
        exit 1
    }

    $exePath = Join-Path $OutputDir "AviatesAirTracker.exe"
    if (Test-Path $exePath) {
        $sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
        Write-Host ""
        Write-Host "==========================================" -ForegroundColor Green
        Write-Host "  BUILD COMPLETE                          " -ForegroundColor Green
        Write-Host "==========================================" -ForegroundColor Green
        Write-Host "  Output : $exePath" -ForegroundColor White
        Write-Host "  Size   : $sizeMB MB" -ForegroundColor White
        Write-Host ""
        Write-Host "  Start MSFS first, then run AviatesAirTracker.exe" -ForegroundColor Cyan
    } else {
        Write-Warning "EXE not found at expected path: $exePath"
    }
}

# ---------------------------------------------------------------------------
# Installer (requires -Release to have been run first)
# ---------------------------------------------------------------------------
if ($Installer) {
    Write-Host ""
    Write-Host ">> Building installer..." -ForegroundColor Yellow

    # Verify the release EXE exists
    $exePath = Join-Path $OutputDir "AviatesAirTracker.exe"
    if (-not (Test-Path $exePath)) {
        Write-Error "dist\AviatesAirTracker.exe not found. Run .\build.ps1 -Release first."
        exit 1
    }

    # Helper: find ISCC.exe via fixed paths, registry, or PATH
    function Find-ISCC {
        # 1. Fixed candidate paths
        $candidates = @(
            "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
            "C:\Program Files\Inno Setup 6\ISCC.exe",
            "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
            "C:\Program Files\Inno Setup 5\ISCC.exe"
        )
        $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($found) { return $found }

        # 2. Registry — Inno Setup records its install location here
        $regRoots = @(
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
        )
        foreach ($reg in $regRoots) {
            if (Test-Path $reg) {
                $loc = (Get-ItemProperty $reg -ErrorAction SilentlyContinue).InstallLocation
                if ($loc) {
                    $candidate = Join-Path $loc "ISCC.exe"
                    if (Test-Path $candidate) { return $candidate }
                }
            }
        }

        # 3. PATH (refreshed after install)
        $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($cmd) { return $cmd.Source }

        return $null
    }

    $isccPath = Find-ISCC
    if (-not $isccPath) {
        Write-Host "   Inno Setup not found. Attempting auto-install via winget..." -ForegroundColor Yellow
        $winget = Get-Command "winget" -ErrorAction SilentlyContinue
        if ($winget) {
            & winget install --id JRSoftware.InnoSetup --silent --accept-package-agreements --accept-source-agreements
            # winget exits non-zero for "already installed / no upgrade available" — that's fine
            # Refresh PATH then search again
            $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
                        [System.Environment]::GetEnvironmentVariable("Path", "User")
            $isccPath = Find-ISCC
        }
    }
    if (-not $isccPath) {
        Write-Error ("Inno Setup compiler (ISCC.exe) not found.`n" +
                     "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php`n" +
                     "or via: choco install innosetup")
        exit 1
    }
    Write-Host "   ISCC: $isccPath" -ForegroundColor Gray

    # Read version from .csproj
    $csprojContent = Get-Content $ProjectFile -Raw
    $appVersion = "1.0.0"
    if ($csprojContent -match '<AssemblyVersion>(\d+\.\d+\.\d+)') {
        $appVersion = $Matches[1]
    } elseif ($csprojContent -match '<Version>(\d+\.\d+\.\d+)') {
        $appVersion = $Matches[1]
    }
    Write-Host "   Version: $appVersion" -ForegroundColor Gray

    # Compile the installer
    & "$isccPath" "/DAppVersion=$appVersion" "$InstallerScript"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Inno Setup compilation failed (exit $LASTEXITCODE)."
        exit 1
    }

    $installerExe = Join-Path $InstallerDir "AviatesAirTracker_Setup_v$appVersion.exe"
    if (Test-Path $installerExe) {
        $sizeMB = [math]::Round((Get-Item $installerExe).Length / 1MB, 1)
        Write-Host ""
        Write-Host "==========================================" -ForegroundColor Green
        Write-Host "  INSTALLER COMPLETE                      " -ForegroundColor Green
        Write-Host "==========================================" -ForegroundColor Green
        Write-Host "  Output : $installerExe" -ForegroundColor White
        Write-Host "  Size   : $sizeMB MB" -ForegroundColor White
        Write-Host ""
    } else {
        Write-Warning "Installer not found at expected path: $installerExe"
    }
}

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
if ($Run) {
    Write-Host ""
    Write-Host ">> Launching..." -ForegroundColor Cyan
    & dotnet run --project "$ProjectFile" -c $Configuration
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
