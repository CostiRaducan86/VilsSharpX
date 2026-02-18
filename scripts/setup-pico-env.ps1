<#
.SYNOPSIS
  Setup Pico 2 firmware build environment on HELLA HRO network.
  Downloads ARM GNU Toolchain + Pico SDK through corporate proxy.
#>
$ErrorActionPreference = "Stop"
$proxy = "http://roshroproxy01v.hro.hella.com:3128"
//$curlProxy = "--proxy `"$proxy`" --proxy-ntlm -U :"

$installBase = "$env:USERPROFILE\pico-dev"
$armDir = "$installBase\arm-gnu-toolchain"
$sdkDir = "$installBase\pico-sdk"

Write-Host "========================================"
Write-Host "  Pico 2 Build Environment Setup"
Write-Host "  Proxy: $proxy (NTLM)"
Write-Host "========================================"
Write-Host ""

# Create base directory
if (!(Test-Path $installBase)) { New-Item -ItemType Directory $installBase -Force | Out-Null }

# ── Step 1: ARM GNU Toolchain ─────────────────────────────────────
Write-Host "[1/4] Checking ARM GNU Toolchain..." -ForegroundColor Cyan

$armGcc = Get-Command arm-none-eabi-gcc -ErrorAction SilentlyContinue
if ($armGcc) {
    Write-Host "  Already installed: $($armGcc.Source)" -ForegroundColor Green
}
else {
    # Use xPack ARM GCC from GitHub (GitHub is allowed through HELLA proxy)
    $zipUrl = "https://github.com/xpack-dev-tools/arm-none-eabi-gcc-xpack/releases/download/v13.3.1-1.1/xpack-arm-none-eabi-gcc-13.3.1-1.1-win32-x64.zip"
    $zipFile = "$installBase\arm-gcc.zip"

    if (!(Test-Path $zipFile)) {
        Write-Host "  Downloading xPack ARM GCC 13.3.1 from GitHub (~330 MB)..."
        Write-Host "  This may take 5-10 minutes..."
        # Use git's credential helper / proxy which is known to work
        # But for download we need a different approach - try .NET with git proxy
        $wc = New-Object System.Net.WebClient
        $wp = New-Object System.Net.WebProxy($proxy)
        $wp.Credentials = [System.Net.CredentialCache]::DefaultNetworkCredentials
        $wc.Proxy = $wp
        try {
            $wc.DownloadFile($zipUrl, $zipFile)
        } catch {
            Write-Host "  .NET WebClient failed, trying curl..." -ForegroundColor Yellow
            # Try curl with NTLM
            $ErrorActionPreference = "Continue"
            & curl.exe --proxy $proxy --proxy-ntlm -U : -L -o $zipFile $zipUrl --silent --show-error 2>&1 | Out-Host
            $ErrorActionPreference = "Stop"
        }
        if (!(Test-Path $zipFile) -or (Get-Item $zipFile).Length -lt 10000000) {
            Write-Host "  ERROR: Download failed." -ForegroundColor Red
            Write-Host "  Manual alternative: download from" -ForegroundColor Yellow
            Write-Host "    $zipUrl" -ForegroundColor Yellow
            Write-Host "  and save as: $zipFile" -ForegroundColor Yellow
            exit 1
        }
    }

    $size = [math]::Round((Get-Item $zipFile).Length / 1MB, 1)
    Write-Host "  Downloaded: $size MB" -ForegroundColor Green

    Write-Host "  Extracting to $armDir ..."
    Expand-Archive -Path $zipFile -DestinationPath $installBase -Force
    # The ZIP extracts to a subfolder like xpack-arm-none-eabi-gcc-13.3.1-1.1 or arm-gnu-toolchain*
    $extracted = Get-ChildItem $installBase -Directory | Where-Object {
        $_.Name -like "arm-gnu-toolchain*" -or $_.Name -like "xpack-arm-none-eabi*"
    } | Select-Object -First 1
    if ($extracted -and $extracted.FullName -ne $armDir) {
        if (Test-Path $armDir) { Remove-Item $armDir -Recurse -Force }
        Rename-Item $extracted.FullName $armDir
    }
    Write-Host "  Extracted to: $armDir" -ForegroundColor Green

    # Add to PATH for this session
    $env:PATH = "$armDir\bin;$env:PATH"

    # Verify
    $gcc = & "$armDir\bin\arm-none-eabi-gcc.exe" --version 2>&1 | Select-Object -First 1
    Write-Host "  Verified: $gcc" -ForegroundColor Green
}

# ── Step 2: Pico SDK ─────────────────────────────────────────────
Write-Host ""
Write-Host "[2/4] Checking Pico SDK..." -ForegroundColor Cyan

if (Test-Path "$sdkDir\pico_sdk_init.cmake") {
    Write-Host "  Already cloned: $sdkDir" -ForegroundColor Green
}
else {
    Write-Host "  Cloning pico-sdk (with TinyUSB submodule)..."
    # Set git proxy
    git config --global http.proxy $proxy
    git config --global https.proxy $proxy

    $ErrorActionPreference = "Continue"
    & git clone --depth 1 --branch 2.1.0 "https://github.com/raspberrypi/pico-sdk.git" $sdkDir 2>&1 | Out-Host
    $ErrorActionPreference = "Stop"
    if (!(Test-Path "$sdkDir\pico_sdk_init.cmake")) {
        Write-Host "  ERROR: git clone failed" -ForegroundColor Red
        exit 1
    }

    # Init TinyUSB submodule (required for USB CDC)
    Push-Location $sdkDir
    $ErrorActionPreference = "Continue"
    & git submodule update --init --depth 1 lib/tinyusb 2>&1 | Out-Host
    $ErrorActionPreference = "Stop"
    Pop-Location

    Write-Host "  Pico SDK 2.1.0 cloned to: $sdkDir" -ForegroundColor Green
}

# ── Step 3: Set environment variables ─────────────────────────────
Write-Host ""
Write-Host "[3/4] Setting environment variables..." -ForegroundColor Cyan

$env:PICO_SDK_PATH = $sdkDir
if (!(Test-Path "$armDir\bin\arm-none-eabi-gcc.exe" -ErrorAction SilentlyContinue)) {
    # ARM was already on PATH from previous install
    $armBin = (Get-Command arm-none-eabi-gcc -ErrorAction SilentlyContinue).Source
    if ($armBin) { Write-Host "  ARM GCC on PATH: $armBin" }
} else {
    $env:PATH = "$armDir\bin;$env:PATH"
}

Write-Host "  PICO_SDK_PATH = $env:PICO_SDK_PATH" -ForegroundColor Green

# Persist for future sessions
[System.Environment]::SetEnvironmentVariable("PICO_SDK_PATH", $sdkDir, "User")
Write-Host "  PICO_SDK_PATH persisted to user environment." -ForegroundColor Green

# ── Step 4: Build firmware ────────────────────────────────────────
Write-Host ""
Write-Host "[4/4] Building firmware..." -ForegroundColor Cyan

$fwDir = Join-Path $PSScriptRoot "..\firmware\pico2-lvds-bridge"
$buildDir = Join-Path $fwDir "build"

if (!(Test-Path $buildDir)) { New-Item -ItemType Directory $buildDir -Force | Out-Null }

Push-Location $buildDir

Write-Host "  CMake configure..."
$ErrorActionPreference = "Continue"
& cmake -G Ninja -DPICO_BOARD=pico2 -DPICO_SDK_PATH="$env:PICO_SDK_PATH" .. 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ERROR: CMake configure failed" -ForegroundColor Red
    $ErrorActionPreference = "Stop"
    Pop-Location
    exit 1
}

Write-Host "  Building..."
& ninja 2>&1 | Out-Host
$ErrorActionPreference = "Stop"
if ($LASTEXITCODE -ne 0) {
    Write-Host "  ERROR: Build failed" -ForegroundColor Red
    Pop-Location
    exit 1
}

Pop-Location

$uf2 = Join-Path $buildDir "pico2_lvds_bridge.uf2"
if (Test-Path $uf2) {
    $uf2Size = [math]::Round((Get-Item $uf2).Length / 1KB, 1)
    Write-Host ""
    Write-Host "========================================"  -ForegroundColor Green
    Write-Host "  BUILD SUCCESSFUL!"                       -ForegroundColor Green
    Write-Host "  UF2: $uf2"                               -ForegroundColor Green
    Write-Host "  Size: $uf2Size KB"                       -ForegroundColor Green
    Write-Host "========================================"  -ForegroundColor Green
    Write-Host ""
    Write-Host "Next: Hold BOOTSEL pe Pico 2, conecteaza USB,"
    Write-Host "      apoi ruleaza:  .\flash-firmware.ps1"
} else {
    Write-Host "WARNING: UF2 not found at $uf2" -ForegroundColor Yellow
}

# Clean up git proxy
git config --global --unset http.proxy 2>$null
git config --global --unset https.proxy 2>$null
