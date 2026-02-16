<#
.SYNOPSIS
  Flash the Pico 2 LVDS-to-USB bridge firmware.

.DESCRIPTION
  Detects the RPI-RP2 BOOTSEL drive, copies the UF2 firmware,
  and waits for the device to reboot as a COM port.

.PARAMETER Uf2Path
  Path to the UF2 file. Defaults to build output location.

.EXAMPLE
  .\flash-firmware.ps1
  .\flash-firmware.ps1 -Uf2Path "C:\path\to\firmware.uf2"
#>
param(
    [string]$Uf2Path = ""
)

$ErrorActionPreference = "Stop"

# ── Locate UF2 ──────────────────────────────────────────────────────
if ([string]::IsNullOrWhiteSpace($Uf2Path)) {
    $Uf2Path = Join-Path $PSScriptRoot "..\firmware\pico2-lvds-bridge\build\pico2_lvds_bridge.uf2"
}

if (-not (Test-Path $Uf2Path)) {
    Write-Host "ERROR: UF2 file not found: $Uf2Path" -ForegroundColor Red
    Write-Host ""
    Write-Host "Run .\build-firmware.ps1 first, or specify -Uf2Path." -ForegroundColor Yellow
    exit 1
}

$uf2Size = (Get-Item $Uf2Path).Length
Write-Host "UF2 file: $Uf2Path ($([math]::Round($uf2Size / 1KB, 1)) KB)" -ForegroundColor Green

# ── Snapshot current COM ports (before flash) ────────────────────────
$comPortsBefore = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object
Write-Host "COM ports before flash: $($comPortsBefore -join ', ')" -ForegroundColor Cyan

# ── Find RPI-RP2 drive ──────────────────────────────────────────────
function Find-PicoDrive {
    $drives = Get-CimInstance Win32_Volume |
        Where-Object { $_.Label -eq "RPI-RP2" } |
        Select-Object -ExpandProperty DriveLetter
    return $drives
}

$picoDrive = Find-PicoDrive
if (-not $picoDrive) {
    Write-Host ""
    Write-Host "─── Waiting for RPI-RP2 drive ───" -ForegroundColor Yellow
    Write-Host "Hold BOOTSEL button on Pico 2, then connect USB cable..."
    Write-Host "(Or hold BOOTSEL and press RESET if already connected)"
    Write-Host ""

    $timeout = 60
    $elapsed = 0
    while (-not $picoDrive -and $elapsed -lt $timeout) {
        Start-Sleep -Seconds 1
        $elapsed++
        $picoDrive = Find-PicoDrive
        if ($elapsed % 5 -eq 0) {
            Write-Host "  Still waiting... ($elapsed s)" -ForegroundColor DarkGray
        }
    }

    if (-not $picoDrive) {
        Write-Host "ERROR: Timed out waiting for RPI-RP2 drive ($timeout s)." -ForegroundColor Red
        Write-Host "Make sure you hold BOOTSEL while connecting." -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "Found RPI-RP2 drive: $picoDrive" -ForegroundColor Green

# ── Copy UF2 ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "─── Flashing firmware ───" -ForegroundColor Cyan
$destPath = Join-Path "$picoDrive\" "pico2_lvds_bridge.uf2"
Copy-Item -Path $Uf2Path -Destination $destPath -Force
Write-Host "Copied UF2 to $destPath" -ForegroundColor Green

# ── Wait for device to reboot as CDC COM port ────────────────────────
Write-Host ""
Write-Host "─── Waiting for CDC COM port ───" -ForegroundColor Yellow
$timeout = 15
$elapsed = 0
$newPort = $null

while ($elapsed -lt $timeout) {
    Start-Sleep -Seconds 1
    $elapsed++
    $comPortsAfter = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object
    $newPorts = $comPortsAfter | Where-Object { $_ -notin $comPortsBefore }
    if ($newPorts) {
        $newPort = $newPorts | Select-Object -First 1
        break
    }
}

Write-Host ""
if ($newPort) {
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Green
    Write-Host "  FLASH SUCCESSFUL" -ForegroundColor Green
    Write-Host "  New COM port: $newPort" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Green
    Write-Host ""
    Write-Host "In VilsSharpX:"
    Write-Host "  1. Click 'Refresh' next to the port dropdown"
    Write-Host "  2. Select '$newPort'"
    Write-Host "  3. Choose Nichia or Osram device type"
    Write-Host "  4. Click 'Start' to begin LVDS capture"
}
else {
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host "  UF2 COPIED (device may still be rebooting)" -ForegroundColor Yellow
    Write-Host "═══════════════════════════════════════════" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Check Device Manager for a new COM port."
    Write-Host "COM ports now: $((([System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object) -join ', '))"
}
