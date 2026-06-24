# build.ps1 - Build veeam-vhc-aws for Windows (win-x64)
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

$ProjectPath = "src\VeeamVhcAws\VeeamVhcAws.csproj"
$OutDir      = "dist\win-x64"
$ExePath     = "$OutDir\veeam-vhc-aws.exe"
$WwwRootPath = "$OutDir\wwwroot"
$Version     = (Select-String '<Version>(.*)</Version>' $ProjectPath | ForEach-Object { $_.Matches.Groups[1].Value })

Write-Host ""
Write-Host "=== Veeam VHC AWS Build v$Version (win-x64) ===" -ForegroundColor Cyan
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

# The publish output is a single exe PLUS a wwwroot\ folder holding the Blazor admin
# UI's static assets (_framework\blazor.web.js, css\app.css, scoped CSS). wwwroot must
# travel with the exe or the `ui` command serves a blank page / 404s on blazor.web.js.
if (-not (Test-Path "$WwwRootPath\_framework\blazor.web.js")) {
    Write-Host "ERROR: $WwwRootPath\_framework\blazor.web.js missing -- the GUI will 404." -ForegroundColor Red
    Write-Host "       Static web assets were not published. Check StaticWebAssetsEnabled / PublishTrimmed." -ForegroundColor Red
    exit 1
}
Write-Host "  -> wwwroot present (blazor.web.js found)" -ForegroundColor Green

# Warn on any stray loose files (managed DLLs/PDBs should be bundled into the single exe)
$stray = Get-ChildItem $OutDir -File | Where-Object { $_.Name -ne "veeam-vhc-aws.exe" }
if ($stray) {
    Write-Host "  WARNING: unexpected loose files in output:" -ForegroundColor Yellow
    $stray | ForEach-Object { Write-Host "    $($_.Name)" }
}

if ($NoZip) {
    Write-Host ""
    Write-Host "=== Done ===" -ForegroundColor Green
    Write-Host "  Exe: $ExePath ($sizeMB MB)" -ForegroundColor White
    exit 0
}

# --- Package zip bundle ---
$ZipName = "dist\veeam-vhc-aws-v$Version-windows.zip"
Write-Host ""
Write-Host "Packaging -> $ZipName" -ForegroundColor Yellow

$tmpDir = "dist\_bundle"
New-Item -ItemType Directory -Path $tmpDir | Out-Null
Copy-Item $ExePath                $tmpDir
Copy-Item $WwwRootPath            $tmpDir -Recurse   # Blazor admin UI static assets (required by `ui`)
Copy-Item "setup.ps1"             $tmpDir
Copy-Item "config\example.yaml"   $tmpDir

@"
veeam-vhc-aws — Veeam VHC AWS
=========================================

Quick Start
-----------
1. Right-click setup.ps1 → Run as Administrator
   (Configures servers, notifications, and creates a scheduled task)

2. Or run manually:
   .\veeam-vhc-aws.exe all -c veeam-vhc-aws.yaml

Commands
--------
.\veeam-vhc-aws.exe setup                        Interactive config wizard
.\veeam-vhc-aws.exe all -c veeam-vhc-aws.yaml      Run all monitors
.\veeam-vhc-aws.exe summary -c veeam-vhc-aws.yaml  Daily summary
.\veeam-vhc-aws.exe ui -c veeam-vhc-aws.yaml       Launch the web admin GUI
.\veeam-vhc-aws.exe test-connection -c config    Test server connectivity
.\veeam-vhc-aws.exe serve -c config -p 9100      Prometheus metrics server
.\veeam-vhc-aws.exe --help                       Show all commands

Note: keep the wwwroot\ folder next to veeam-vhc-aws.exe -- the `ui`
command serves the admin GUI from it. setup.ps1 copies it for you.

Docs: https://github.com/VeeamHub/veeam-veeam-vhc-aws
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
Write-Host "    veeam-vhc-aws.exe   <- the monitor + admin UI" -ForegroundColor White
Write-Host "    wwwroot\          <- admin UI static assets (needed by 'ui')" -ForegroundColor White
Write-Host "    setup.ps1         <- run as admin to install" -ForegroundColor White
Write-Host "    example.yaml      <- config reference" -ForegroundColor White
Write-Host "    README.txt        <- quick start" -ForegroundColor White
Write-Host ""
Write-Host "  Test: .\$ExePath version" -ForegroundColor Cyan
