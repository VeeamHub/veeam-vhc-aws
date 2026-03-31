# build.ps1 - Build vhc-monitor standalone executable
# Usage: .\build.ps1
# Output: dist\vhc-monitor.exe

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "=== VHC Monitor Build ===" -ForegroundColor Cyan

# Ensure pyinstaller is installed
Write-Host "Installing/upgrading PyInstaller..." -ForegroundColor Yellow
pip install pyinstaller --upgrade --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to install PyInstaller" -ForegroundColor Red
    exit 1
}

# Install the package itself (needed for imports to resolve)
Write-Host "Installing vhc-monitor package..." -ForegroundColor Yellow
pip install . --quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to install vhc-monitor" -ForegroundColor Red
    exit 1
}

# Clean previous build artifacts
if (Test-Path "dist") { Remove-Item -Recurse -Force "dist" }
if (Test-Path "build") { Remove-Item -Recurse -Force "build" }

# Build the executable
Write-Host "Building standalone executable..." -ForegroundColor Yellow
pyinstaller vhc-monitor.spec --noconfirm
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: PyInstaller build failed" -ForegroundColor Red
    exit 1
}

# Verify output
$exePath = "dist\vhc-monitor.exe"
if (Test-Path $exePath) {
    $size = (Get-Item $exePath).Length / 1MB
    Write-Host ""
    Write-Host "=== Build Successful ===" -ForegroundColor Green
    Write-Host "Output: $exePath" -ForegroundColor Green
    Write-Host "Size:   $([math]::Round($size, 1)) MB" -ForegroundColor Green
    Write-Host ""
    Write-Host "Test with: .\dist\vhc-monitor.exe version" -ForegroundColor Cyan
} else {
    Write-Host "ERROR: Expected output not found at $exePath" -ForegroundColor Red
    exit 1
}
