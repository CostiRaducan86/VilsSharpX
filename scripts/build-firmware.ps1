<#
.SYNOPSIS
  Build the Pico 2 LVDS-to-USB bridge firmware.

.DESCRIPTION
  Locates the Pico SDK, configures CMake for the RP2350 target,
  and builds the UF2 firmware file.

.PARAMETER PicoSdkPath
  Path to pico-sdk. If not provided, uses PICO_SDK_PATH environment variable
  or searches common locations.

.EXAMPLE
  .\build-firmware.ps1
  .\build-firmware.ps1 -PicoSdkPath "C:\pico-sdk"
#>
param(
    [string]$PicoSdkPath = ""
)

$ErrorActionPreference = "Stop"
$FirmwareDir = Join-Path $PSScriptRoot "..\firmware\pico2-lvds-bridge"
$BuildDir    = Join-Path $FirmwareDir "build"

# ── Locate Pico SDK ─────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($PicoSdkPath)) {
    $PicoSdkPath = $env:PICO_SDK_PATH
}

if ([string]::IsNullOrWhiteSpace($PicoSdkPath)) {
    # Search common locations
    $candidates = @(
        "C:\pico-sdk",
        "C:\Program Files\Raspberry Pi\Pico SDK v2.1.0\pico-sdk",
        "$env:USERPROFILE\pico-sdk",
        "$env:USERPROFILE\Documents\pico-sdk"
    )
    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c "pico_sdk_init.cmake")) {
            $PicoSdkPath = $c
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($PicoSdkPath) -or -not (Test-Path (Join-Path $PicoSdkPath "pico_sdk_init.cmake"))) {
    Write-Host ""
    Write-Host "ERROR: Pico SDK not found." -ForegroundColor Red
    Write-Host ""
    Write-Host "Options:" -ForegroundColor Yellow
    Write-Host "  1. Set PICO_SDK_PATH environment variable:"
    Write-Host '     $env:PICO_SDK_PATH = "C:\path\to\pico-sdk"'
    Write-Host ""
    Write-Host "  2. Pass it as parameter:"
    Write-Host '     .\build-firmware.ps1 -PicoSdkPath "C:\path\to\pico-sdk"'
    Write-Host ""
    Write-Host "  3. Install Pico SDK:"
    Write-Host "     https://github.com/raspberrypi/pico-sdk"
    Write-Host "     Or install 'Raspberry Pi Pico Visual Studio Code extension'"
    Write-Host ""
    exit 1
}

Write-Host "Pico SDK: $PicoSdkPath" -ForegroundColor Green

# ── Check for cmake and arm-none-eabi-gcc ────────────────────────────
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    Write-Host "ERROR: cmake not found in PATH. Install CMake from https://cmake.org" -ForegroundColor Red
    exit 1
}

$gcc = Get-Command arm-none-eabi-gcc -ErrorAction SilentlyContinue
if (-not $gcc) {
    Write-Host "WARNING: arm-none-eabi-gcc not found in PATH." -ForegroundColor Yellow
    Write-Host "  Install ARM GNU Toolchain from:"
    Write-Host "  https://developer.arm.com/downloads/-/arm-gnu-toolchain-downloads"
    Write-Host ""
    Write-Host "  Or use a Ninja/toolchain that ships with the Pico SDK extension."
    Write-Host ""
}

# ── Create build directory ──────────────────────────────────────────
if (-not (Test-Path $BuildDir)) {
    New-Item -ItemType Directory -Path $BuildDir -Force | Out-Null
    Write-Host "Created build directory: $BuildDir"
}

# ── Configure with CMake ────────────────────────────────────────────
Write-Host ""
Write-Host "─── CMake Configure ───" -ForegroundColor Cyan

Push-Location $BuildDir
try {
    $env:PICO_SDK_PATH = $PicoSdkPath
    cmake -DPICO_BOARD=pico2 -DPICO_SDK_PATH="$PicoSdkPath" ..
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: CMake configure failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    # ── Build ────────────────────────────────────────────────────────
    Write-Host ""
    Write-Host "─── CMake Build ───" -ForegroundColor Cyan
    cmake --build . --config Release -j $([Environment]::ProcessorCount)
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Build failed." -ForegroundColor Red
        exit $LASTEXITCODE
    }
}
finally {
    Pop-Location
}

# ── Check output ────────────────────────────────────────────────────
$uf2File = Join-Path $BuildDir "pico2_lvds_bridge.uf2"
if (Test-Path $uf2File) {
    $size = (Get-Item $uf2File).Length
    Write-Host ""
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Green
    Write-Host "  BUILD SUCCESSFUL" -ForegroundColor Green
    Write-Host "  Output: $uf2File" -ForegroundColor Green
    Write-Host "  Size:   $($size / 1KB) KB" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
    Write-Host "To flash: Hold BOOTSEL on Pico 2, connect USB,"
    Write-Host "          then copy the UF2 file to the RPI-RP2 drive."
    Write-Host ""
    Write-Host "Or run: .\flash-firmware.ps1"
}
else {
    Write-Host "WARNING: UF2 file not found at expected location." -ForegroundColor Yellow
    Write-Host "Check build output above for the actual file path."
}
