# build.ps1 - Build vhc-monitor for Windows (win-x64)
# Usage:
#   .\build.ps1           # Build + package zip
#   .\build.ps1 -NoZip    # Build exe only, skip zip
#
# For Mac/Linux dev builds, use: ./build.sh --local

param(
    [switch]$NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ProjectPath = "src\VhcMonitor\VhcMonitor.csproj"
$OutDir      = "dist\win-x64"
$ExePath     = "$OutDir\vhc-monitor.exe"
$Version     = (Select-String '<Version>(.*)</Version>' $ProjectPath | ForEach-Object { $_.Matches.Groups[1].Value })

Write-Host ""
Write-Host "=== VHC Monitor Build v$Version (win-x64) ===" -ForegroundColor Cyan
Write-Host ""

if (Test-Path "dist") { Remove-Item -Recurse -Force "dist" }

Write-Host "Publishing win-x64..." -ForegroundColor Yellow

dotnet publish $ProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $OutDir `
    --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ExePath)) {
    Write-Host "ERROR: Expected $ExePath not found" -ForegroundColor Red
    exit 1
}

$sizeMB = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host "  -> $ExePath ($sizeMB MB)" -ForegroundColor Green

# Verify it's a single file (no stray .pdb or dlls)
$fileCount = (Get-ChildItem $OutDir).Count
if ($fileCount -ne 1) {
    Write-Host "  WARNING: Expected 1 file in output, found $fileCount" -ForegroundColor Yellow
    Get-ChildItem $OutDir | ForEach-Object { Write-Host "    $_" }
}

if ($NoZip) {
    Write-Host ""
    Write-Host "=== Done ===" -ForegroundColor Green
    Write-Host "  Exe: $ExePath ($sizeMB MB)" -ForegroundColor White
    exit 0
}

# --- Package zip bundle ---
$ZipName = "dist\vhc-monitor-v$Version-windows.zip"
Write-Host ""
Write-Host "Packaging -> $ZipName" -ForegroundColor Yellow

$tmpDir = "dist\_bundle"
New-Item -ItemType Directory -Path $tmpDir | Out-Null
Copy-Item $ExePath                $tmpDir
Copy-Item "setup.ps1"             $tmpDir
Copy-Item "config\example.yaml"   $tmpDir

@"
vhc-monitor — Veeam Health Check Monitor
=========================================

Quick Start
-----------
1. Right-click setup.ps1 → Run as Administrator
   (Configures servers, notifications, and creates a scheduled task)

2. Or run manually:
   .\vhc-monitor.exe all -c vhc-monitor.yaml

Commands
--------
.\vhc-monitor.exe setup                        Interactive config wizard
.\vhc-monitor.exe all -c vhc-monitor.yaml      Run all monitors
.\vhc-monitor.exe summary -c vhc-monitor.yaml  Daily summary
.\vhc-monitor.exe test-connection -c config    Test server connectivity
.\vhc-monitor.exe serve -c config -p 9100      Prometheus metrics server
.\vhc-monitor.exe --help                       Show all commands

Docs: https://github.com/VeeamHub/veeam-vhc-monitor
"@ | Out-File -FilePath "$tmpDir\README.txt" -Encoding utf8NoBOM

Compress-Archive -Path "$tmpDir\*" -DestinationPath $ZipName -Force
Remove-Item -Recurse -Force $tmpDir

$zipMB = [math]::Round((Get-Item $ZipName).Length / 1MB, 1)

Write-Host ""
Write-Host "=== Build Complete ===" -ForegroundColor Green
Write-Host "  Exe: $ExePath ($sizeMB MB)" -ForegroundColor White
Write-Host "  Zip: $ZipName ($zipMB MB)" -ForegroundColor White
Write-Host ""
Write-Host "  Zip contents:" -ForegroundColor Cyan
Write-Host "    vhc-monitor.exe   <- the monitor" -ForegroundColor White
Write-Host "    setup.ps1         <- run as admin to install" -ForegroundColor White
Write-Host "    example.yaml      <- config reference" -ForegroundColor White
Write-Host "    README.txt        <- quick start" -ForegroundColor White
Write-Host ""
Write-Host "  Test: .\$ExePath version" -ForegroundColor Cyan
