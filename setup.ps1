# setup.ps1 - Sets up Veeam VHC AWS on Windows
# Usage: .\setup.ps1 -AlertUrl "https://ntfy.example.com/veeam-alerts"
# Upgrade: .\setup.ps1 -Upgrade
# Requires: veeam-vhc-aws.exe in the same directory as this script, or in dist\

param(
    [Parameter(Mandatory=$false)]
    [string]$AlertUrl,

    [Parameter(Mandatory=$false)]
    [int]$IntervalMinutes = 5,

    [Parameter(Mandatory=$false)]
    [string]$InstallDir = "$env:ProgramFiles\VHC",

    [Parameter(Mandatory=$false)]
    [switch]$Upgrade,

    [Parameter(Mandatory=$false)]
    [switch]$Uninstall,

    [Parameter(Mandatory=$false)]
    [string]$SummaryTime = "",

    [Parameter(Mandatory=$false)]
    [switch]$NoSummary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
if ($Uninstall) {
    Write-Host "  Veeam VHC AWS Uninstall" -ForegroundColor Cyan
} elseif ($Upgrade) {
    Write-Host "  Veeam VHC AWS Upgrade" -ForegroundColor Cyan
} else {
    Write-Host "  Veeam VHC AWS Setup" -ForegroundColor Cyan
}
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# --- UNINSTALL MODE ---
if ($Uninstall) {
    $taskNames = @("Veeam VHC AWS", "Veeam VHC AWS Daily Summary")
    foreach ($tn in $taskNames) {
        if (Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue) {
            Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
            Unregister-ScheduledTask -TaskName $tn -Confirm:$false
            Write-Host "  -> Removed scheduled task '$tn'" -ForegroundColor Green
        } else {
            Write-Host "  -> Task '$tn' not found (already removed)" -ForegroundColor DarkGray
        }
    }

    $exePath = Join-Path $InstallDir "veeam-vhc-aws.exe"
    if (Test-Path $exePath) {
        Remove-Item $exePath -Force
        Write-Host "  -> Removed $exePath" -ForegroundColor Green
    }

    $dataDir = Join-Path $env:ProgramData "VHC"
    Write-Host ""
    Write-Host "  Config and data are preserved at:" -ForegroundColor Yellow
    Write-Host "    Config: $(Join-Path $InstallDir 'veeam-vhc-aws.yaml')" -ForegroundColor White
    Write-Host "    State:  $(Join-Path $dataDir 'veeam-vhc-aws-state.json')" -ForegroundColor White
    Write-Host "    Logs:   $(Join-Path $dataDir 'logs\veeam-vhc-aws*.log')" -ForegroundColor White

    $removeData = Read-Host "  Remove config, state, and logs too? (y/n) [n]"
    if ($removeData -eq "y") {
        if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
        if (Test-Path $dataDir) { Remove-Item $dataDir -Recurse -Force }
        Write-Host "  -> Removed all data" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Uninstall complete." -ForegroundColor Green
    Write-Host ""
    exit 0
}

# --- UPGRADE MODE: swap exe + check for new/missing features ---
if ($Upgrade) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $exeCandidates = @(
        (Join-Path $scriptDir "veeam-vhc-aws.exe"),
        (Join-Path $scriptDir "dist\veeam-vhc-aws.exe")
    )
    $exeSource = $null
    foreach ($candidate in $exeCandidates) {
        if (Test-Path $candidate) { $exeSource = $candidate; break }
    }
    if (-not $exeSource) {
        Write-Host "ERROR: veeam-vhc-aws.exe not found next to this script." -ForegroundColor Red
        exit 1
    }

    $exeDest = Join-Path $InstallDir "veeam-vhc-aws.exe"
    if (-not (Test-Path $InstallDir)) {
        Write-Host "ERROR: Install dir $InstallDir not found. Run setup.ps1 without -Upgrade first." -ForegroundColor Red
        exit 1
    }

    # Stop tasks to release file lock on the exe (fixes #3)
    $taskNames = @("Veeam VHC AWS", "Veeam VHC AWS Daily Summary")
    foreach ($tn in $taskNames) {
        $t = Get-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
        if ($t -and $t.State -eq 'Running') {
            Stop-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue
            Write-Host "  -> Stopped running task '$tn'" -ForegroundColor Yellow
        }
        Disable-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue | Out-Null
    }

    Write-Host "Replacing $exeDest ..." -ForegroundColor Yellow
    Unblock-File -Path $exeSource
    Copy-Item $exeSource $exeDest -Force
    $newVersion = & $exeDest version 2>&1
    Write-Host "  -> $newVersion" -ForegroundColor Green

    # --- Feature gap detection ---
    # HOW TO ADD A NEW FEATURE CHECK:
    #   1. Add a detection block below (before the "Add future feature checks here" comment)
    #   2. Use regex to check if the feature's key config line exists in $configText
    #   3. Show [NEW] banner, explain the feature, prompt the user
    #   4. For top-level sections: append to end of file
    #   5. For sub-section options: use Insert-ConfigBlock to inject under parent section
    #   6. Set $anyUpdates = $true if changes were made
    #   7. Re-read $configText after modification so subsequent checks see updated content
    Write-Host ""
    Write-Host "Checking for new features..." -ForegroundColor Yellow

    $configPath = Join-Path $InstallDir "veeam-vhc-aws.yaml"
    $anyUpdates = $false

    if (Test-Path $configPath) {
        $configText = Get-Content $configPath -Raw -Encoding UTF8
        $backupPath = $null
        $dataDir = Join-Path $env:ProgramData "VHC"

        # Helper: write config and create backup on first modification
        function Save-Config {
            param([string]$Content)
            if (-not $backupPath) {
                $script:backupPath = $configPath + "." + (Get-Date -Format "yyyyMMdd-HHmmss") + ".bak"
                Copy-Item $configPath $script:backupPath -Force
                Write-Host "  -> Config backed up to $script:backupPath" -ForegroundColor Green
            }
            [IO.File]::WriteAllText($configPath, $Content, [System.Text.UTF8Encoding]::new($false))
        }

        # Helper: insert a YAML block after the last line of a parent section.
        # Finds the parent key (e.g. "retention:") and inserts $Block before the next
        # top-level key (a line starting with a non-space, non-comment character followed by ":").
        function Insert-ConfigBlock {
            param([string]$ConfigText, [string]$ParentKey, [string]$Block)
            # Find where the parent section starts
            if ($ConfigText -notmatch "(?m)^${ParentKey}:") { return $ConfigText }
            $parentIdx = $ConfigText.IndexOf("${ParentKey}:")
            # Find the next top-level key after the parent
            $afterParent = $ConfigText.Substring($parentIdx + $ParentKey.Length + 1)
            if ($afterParent -match '(?m)^\r?\n[a-zA-Z_]') {
                $nextKeyOffset = $afterParent.IndexOf($Matches[0])
                $insertAt = $parentIdx + $ParentKey.Length + 1 + $nextKeyOffset
                return $ConfigText.Substring(0, $insertAt) + "`r`n" + $Block + "`r`n" + $ConfigText.Substring($insertAt)
            } else {
                # Parent is last section — append to end
                return $ConfigText.TrimEnd() + "`r`n" + $Block + "`r`n"
            }
        }

        # ---- Feature: daily_summary ----
        if ($configText -notmatch '(?m)^daily_summary:') {
            Write-Host ""
            Write-Host "  [NEW] Daily health summary is not configured." -ForegroundColor Cyan
            Write-Host "        Sends a full status digest once per day (separate from alert notifications)." -ForegroundColor DarkGray
            $dsChoice = Read-Host "  Set it up now? (y/n) [y]"
            if ($dsChoice -ne "n") {
                $dsTime = Read-Host "  Daily summary time (HH:MM, 24-hour local) [08:00]"
                if (-not $dsTime) { $dsTime = "08:00" }

                # Append daily_summary block to config
                $dsBlock = "`r`n`r`ndaily_summary:`r`n  enabled: true`r`n"
                $configText = $configText.TrimEnd() + $dsBlock
                Save-Config $configText
                Write-Host "  -> daily_summary added to config" -ForegroundColor Green

                # Create the scheduled task
                $dsTaskName = "Veeam VHC AWS Daily Summary"
                $dsAction = New-ScheduledTaskAction `
                    -Execute $exeDest `
                    -Argument "summary --config `"$configPath`"" `
                    -WorkingDirectory $InstallDir
                try {
                    $dsTrigger = New-ScheduledTaskTrigger -Daily -At $dsTime
                } catch {
                    Write-Host "  WARNING: Invalid time '$dsTime', defaulting to 08:00" -ForegroundColor Yellow
                    $dsTime = "08:00"
                    $dsTrigger = New-ScheduledTaskTrigger -Daily -At $dsTime
                }
                $dsSettings = New-ScheduledTaskSettingsSet `
                    -StartWhenAvailable -DontStopOnIdleEnd `
                    -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
                    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
                $dsPrincipal = New-ScheduledTaskPrincipal `
                    -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest

                if (Get-ScheduledTask -TaskName $dsTaskName -ErrorAction SilentlyContinue) {
                    Unregister-ScheduledTask -TaskName $dsTaskName -Confirm:$false
                }
                Register-ScheduledTask `
                    -TaskName $dsTaskName -Action $dsAction -Trigger $dsTrigger `
                    -Settings $dsSettings -Principal $dsPrincipal `
                    -Description "Daily Veeam VHC AWS health summary at $dsTime" | Out-Null

                Write-Host "  -> Scheduled task '$dsTaskName' created (daily at $dsTime)" -ForegroundColor Green
                $anyUpdates = $true
            } else {
                Write-Host "  -> Skipped. Enable later by adding 'daily_summary:' to $configPath" -ForegroundColor DarkGray
            }
        } else {
            Write-Host "  daily_summary: already configured" -ForegroundColor DarkGray
        }

        # ---- Feature: retention session failure detection ----
        if ($configText -match '(?m)^retention:' -and $configText -notmatch 'session_lookback_hours') {
            Write-Host ""
            Write-Host "  [NEW] Retention session failure detection" -ForegroundColor Cyan
            Write-Host "        Detects failed retention tasks like 'Cannot find full backup'" -ForegroundColor DarkGray
            Write-Host "        and 'Failed to get last nas backup'. Reports all failures by" -ForegroundColor DarkGray
            Write-Host "        default — you can mute specific jobs or error messages." -ForegroundColor DarkGray
            Write-Host ""
            $rsChoice = Read-Host "  Enable retention session monitoring? (y/n) [y]"
            if ($rsChoice -ne "n") {
                $rsLookback = Read-Host "  Session lookback hours (how far back to scan) [48]"
                if (-not $rsLookback) { $rsLookback = "48" }

                $rsBlock = "  session_lookback_hours: $rsLookback"

                # Prompt for jobs to exclude
                Write-Host ""
                Write-Host "  You can mute specific jobs that always fail and you don't care about." -ForegroundColor DarkGray
                Write-Host "  Uses substring matching (e.g. 'Laptop Backups' matches 'PROD - Physical Laptop Backups')." -ForegroundColor DarkGray
                $exJobs = Read-Host "  Job names to exclude (comma-separated, or blank to skip)"
                if ($exJobs) {
                    $rsBlock += "`r`n  exclude_jobs:"
                    foreach ($job in ($exJobs -split ',')) {
                        $job = $job.Trim()
                        if ($job) { $rsBlock += "`r`n    - `"$job`"" }
                    }
                }

                # Prompt for error patterns to exclude
                Write-Host ""
                Write-Host "  You can also mute specific error messages by pattern (regex)." -ForegroundColor DarkGray
                Write-Host "  Example: 'Cannot find full backup' or 'Failed to get last nas backup'" -ForegroundColor DarkGray
                $exErrors = Read-Host "  Error patterns to exclude (comma-separated, or blank to skip)"
                if ($exErrors) {
                    $rsBlock += "`r`n  exclude_session_errors:"
                    foreach ($pat in ($exErrors -split ',')) {
                        $pat = $pat.Trim()
                        if ($pat) { $rsBlock += "`r`n    - `"$pat`"" }
                    }
                }

                $configText = Insert-ConfigBlock $configText "retention" $rsBlock
                Save-Config $configText
                Write-Host "  -> Retention session monitoring added to config" -ForegroundColor Green
                $anyUpdates = $true
            } else {
                Write-Host "  -> Skipped. Enable later by adding 'session_lookback_hours: 48' under retention: in $configPath" -ForegroundColor DarkGray
            }
        } else {
            if ($configText -match 'session_lookback_hours') {
                Write-Host "  retention session monitoring: already configured" -ForegroundColor DarkGray
            }
        }

        # ---- Feature: log subfolder migration ----
        if ($configText -match 'logging:' -and $configText -match '(?m)file:.*\\VHC\\veeam-vhc-aws' -and $configText -notmatch '\\logs\\') {
            Write-Host ""
            Write-Host "  [NEW] Log file organization" -ForegroundColor Cyan
            Write-Host "        Moves log files into a dedicated logs\ subfolder under ProgramData\VHC\" -ForegroundColor DarkGray
            $logChoice = Read-Host "  Migrate logs to VHC\logs\ subfolder? (y/n) [y]"
            if ($logChoice -ne "n") {
                $logDir = Join-Path $dataDir "logs"
                if (-not (Test-Path $logDir)) {
                    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
                    icacls $logDir /grant "Users:(OI)(CI)M" 2>$null | Out-Null
                }
                # Update the config file path
                $configText = $configText -replace '(file:\s*")[^"]*\\VHC\\(veeam-vhc-aws)', "`$1$($dataDir -replace '\\', '\\')\\logs\\`$2"
                Save-Config $configText
                # Move existing log files
                $existingLogs = Get-ChildItem -Path $dataDir -Filter "veeam-vhc-aws*.log" -ErrorAction SilentlyContinue
                foreach ($log in $existingLogs) {
                    try {
                        Move-Item $log.FullName (Join-Path $logDir $log.Name) -Force -ErrorAction Stop
                    } catch {
                        Write-Host "  WARNING: Could not move $($log.Name) — file may be in use" -ForegroundColor Yellow
                    }
                }
                Write-Host "  -> Logs migrated to $logDir" -ForegroundColor Green
                $anyUpdates = $true
            } else {
                Write-Host "  -> Skipped. Logs will remain in current location." -ForegroundColor DarkGray
            }
        } else {
            if ($configText -match '\\logs\\') {
                Write-Host "  log subfolder: already configured" -ForegroundColor DarkGray
            }
        }

        # ---- Add future feature checks here ----

    } else {
        Write-Host "  Config not found at $configPath — skipping feature check." -ForegroundColor Yellow
        Write-Host "  Run setup.ps1 without -Upgrade to do a fresh install." -ForegroundColor Yellow
    }

    # Re-enable tasks after upgrade
    foreach ($tn in $taskNames) {
        Enable-ScheduledTask -TaskName $tn -ErrorAction SilentlyContinue | Out-Null
    }

    Write-Host ""
    if ($anyUpdates) {
        Write-Host "Upgrade complete. New features configured." -ForegroundColor Green
    } else {
        Write-Host "Upgrade complete. All features already up to date." -ForegroundColor Green
    }
    Write-Host "Scheduled task will use the new version on its next run." -ForegroundColor Green
    Write-Host "Manual run: & '$exeDest' all --config '$configPath'" -ForegroundColor Cyan
    Write-Host ""
    exit 0
}

# --- 0. Unblock all files in the script directory ---
# Files downloaded from the internet are marked with Zone.Identifier which can
# cause SmartScreen / Defender to block execution. Unblock everything up front.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Get-ChildItem -Path $scriptDir -File | ForEach-Object { Unblock-File -Path $_.FullName }
Write-Host "[0/5] Unblocked files in $scriptDir" -ForegroundColor Green

# --- 1. Locate and copy veeam-vhc-aws.exe ---
$exeCandidates = @(
    (Join-Path $scriptDir "veeam-vhc-aws.exe"),
    (Join-Path $scriptDir "dist\veeam-vhc-aws.exe")
)

$exeSource = $null
foreach ($candidate in $exeCandidates) {
    if (Test-Path $candidate) {
        $exeSource = $candidate
        break
    }
}

if (-not $exeSource) {
    Write-Host "ERROR: veeam-vhc-aws.exe not found." -ForegroundColor Red
    Write-Host "Place it next to this script or in dist\veeam-vhc-aws.exe" -ForegroundColor Red
    Write-Host "Build it first with: .\build.ps1" -ForegroundColor Yellow
    exit 1
}

Write-Host "[1/5] Copying veeam-vhc-aws.exe to $InstallDir" -ForegroundColor Yellow
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item $exeSource (Join-Path $InstallDir "veeam-vhc-aws.exe") -Force
Write-Host "  -> Copied to $InstallDir\veeam-vhc-aws.exe" -ForegroundColor Green

# Add Defender exclusion for the installed exe so SmartScreen / Smart App Control
# won't block an unsigned binary that has no ISG cloud reputation yet.
$exeDest = Join-Path $InstallDir "veeam-vhc-aws.exe"
try {
    $existing = (Get-MpPreference).ExclusionPath
    if ($existing -notcontains $exeDest) {
        Add-MpPreference -ExclusionPath $exeDest -ErrorAction Stop
        Write-Host "  -> Added Defender exclusion for $exeDest" -ForegroundColor Green
    }
} catch {
    Write-Host "  -> Could not add Defender exclusion (may need manual step if Smart App Control blocks the exe)" -ForegroundColor Yellow
    Write-Host "     Run:  Add-MpPreference -ExclusionPath '$exeDest'" -ForegroundColor Yellow
}

# --- 2. Generate config from user input ---
Write-Host ""
Write-Host "[2/5] Configuring monitoring targets" -ForegroundColor Yellow

$configPath = Join-Path $InstallDir "veeam-vhc-aws.yaml"

# Prompt for VBR server
$vbrUrl = Read-Host "  VBR server URL (e.g. https://vbr-server:9419, or blank to skip)"
$vbrBlock = ""
if ($vbrUrl) {
    $vbrUser = Read-Host "  VBR username (e.g. DOMAIN\backupadmin)"
    $vbrPass = Read-Host "  VBR password" -AsSecureString
    $vbrPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($vbrPass)
    )
    $vbrBlock = @"
  - name: vbr
    type: vbr
    url: $vbrUrl
    username: "$vbrUser"
    password: "$vbrPassPlain"
    verify_ssl: false
"@
}

# Prompt for VBAWS server
$vbawsUrl = Read-Host "  VBAWS server URL (e.g. https://vbaws-appliance, or blank to skip)"
$vbawsBlock = ""
if ($vbawsUrl) {
    $vbawsUser = Read-Host "  VBAWS username"
    $vbawsPass = Read-Host "  VBAWS password" -AsSecureString
    $vbawsPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($vbawsPass)
    )
    $vbawsBlock = @"
  - name: vbaws
    type: vbaws
    url: $vbawsUrl
    username: "$vbawsUser"
    password: "$vbawsPassPlain"
    verify_ssl: false
"@
}

if (-not $vbrUrl -and -not $vbawsUrl) {
    Write-Host "  WARNING: No servers configured. Edit $configPath manually." -ForegroundColor Yellow
}

# --- 2b. Configure notifications ---
Write-Host ""
Write-Host "[2b/5] Setting up notifications" -ForegroundColor Yellow
Write-Host ""
Write-Host "  How do you want to be notified of issues?" -ForegroundColor White
Write-Host ""
Write-Host "  [1] ntfy.sh push notifications (free, simple, recommended)" -ForegroundColor White
Write-Host "  [2] Slack webhook" -ForegroundColor White
Write-Host "  [3] Microsoft Teams webhook" -ForegroundColor White
Write-Host "  [4] PagerDuty" -ForegroundColor White
Write-Host "  [5] Email (SMTP)" -ForegroundColor White
Write-Host "  [6] Multiple (configure several)" -ForegroundColor White
Write-Host "  [7] None / I'll configure later" -ForegroundColor DarkGray
Write-Host ""

$outputBlock = "output:`n  - type: json_stdout"

function Add-NotificationBlock {
    param([string]$CurrentBlock)

    $choice = Read-Host "  Select notification type (1-7)"

    switch ($choice) {
        "1" {
            if ($AlertUrl) {
                $url = $AlertUrl
            } else {
                Write-Host ""
                Write-Host "  ntfy sends push notifications to your phone or desktop." -ForegroundColor DarkGray
                Write-Host "  Free hosted: https://ntfy.sh/<your-topic-name>" -ForegroundColor DarkGray
                Write-Host "  Self-hosted: https://your-server/your-topic" -ForegroundColor DarkGray
                Write-Host ""
                $url = Read-Host "  ntfy topic URL"
            }
            if ($url) {
                $severity = Read-Host "  Minimum severity to notify (ok/warning/critical) [warning]"
                if (-not $severity) { $severity = "warning" }
                $CurrentBlock += @"

  - type: webhook
    url: $url
    template: ntfy
    min_severity: $severity
    deduplicate: true
"@
                Write-Host "  -> ntfy notifications configured" -ForegroundColor Green
            }
        }
        "2" {
            Write-Host ""
            Write-Host "  Create an Incoming Webhook in Slack:" -ForegroundColor DarkGray
            Write-Host "  Apps > Incoming Webhooks > Add to Slack > Choose channel" -ForegroundColor DarkGray
            Write-Host ""
            $url = Read-Host "  Slack webhook URL"
            if ($url) {
                $severity = Read-Host "  Minimum severity to notify (ok/warning/critical) [warning]"
                if (-not $severity) { $severity = "warning" }
                $CurrentBlock += @"

  - type: webhook
    url: $url
    template: slack
    min_severity: $severity
"@
                Write-Host "  -> Slack notifications configured" -ForegroundColor Green
            }
        }
        "3" {
            Write-Host ""
            Write-Host "  Create a Workflow webhook in Teams:" -ForegroundColor DarkGray
            Write-Host "  Channel > Manage > Connectors > Incoming Webhook" -ForegroundColor DarkGray
            Write-Host ""
            $url = Read-Host "  Teams webhook URL"
            if ($url) {
                $severity = Read-Host "  Minimum severity to notify (ok/warning/critical) [warning]"
                if (-not $severity) { $severity = "warning" }
                $CurrentBlock += @"

  - type: webhook
    url: $url
    template: teams
    min_severity: $severity
"@
                Write-Host "  -> Teams notifications configured" -ForegroundColor Green
            }
        }
        "4" {
            Write-Host ""
            Write-Host "  Use your PagerDuty Events API v2 integration key." -ForegroundColor DarkGray
            Write-Host ""
            $url = Read-Host "  PagerDuty Events URL [https://events.pagerduty.com/v2/enqueue]"
            if (-not $url) { $url = "https://events.pagerduty.com/v2/enqueue" }
            $severity = Read-Host "  Minimum severity to notify (ok/warning/critical) [critical]"
            if (-not $severity) { $severity = "critical" }
            $CurrentBlock += @"

  - type: webhook
    url: $url
    template: pagerduty
    min_severity: $severity
"@
            Write-Host "  -> PagerDuty notifications configured" -ForegroundColor Green
        }
        "5" {
            Write-Host ""
            $smtpHost = Read-Host "  SMTP host (e.g. smtp.office365.com)"
            $smtpPort = Read-Host "  SMTP port [587]"
            if (-not $smtpPort) { $smtpPort = "587" }
            $fromAddr = Read-Host "  From email address"
            $toAddrs = Read-Host "  To email address(es), comma-separated"
            $smtpUser = Read-Host "  SMTP username (blank if none)"
            $smtpPassBlock = ""
            if ($smtpUser) {
                $smtpPass = Read-Host "  SMTP password" -AsSecureString
                $smtpPassPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                    [Runtime.InteropServices.Marshal]::SecureStringToBSTR($smtpPass)
                )
                $smtpPassBlock = @"

    smtp_username: "$smtpUser"
    smtp_password: "$smtpPassPlain"
"@
            }
            $severity = Read-Host "  Minimum severity to notify (ok/warning/critical) [critical]"
            if (-not $severity) { $severity = "critical" }

            $toList = ($toAddrs -split ',' | ForEach-Object { "      - $($_.Trim())" }) -join "`n"
            $CurrentBlock += @"

  - type: email
    smtp_host: $smtpHost
    smtp_port: $smtpPort
    from_addr: $fromAddr
    to_addrs:
$toList
    min_severity: $severity
    use_tls: true$smtpPassBlock
"@
            Write-Host "  -> Email notifications configured" -ForegroundColor Green
        }
        "6" {
            Write-Host "  Configure each notification type. Enter 7 when done." -ForegroundColor DarkGray
            $done = $false
            while (-not $done) {
                Write-Host ""
                $CurrentBlock = Add-NotificationBlock $CurrentBlock
                $more = Read-Host "  Add another notification? (y/n)"
                if ($more -ne "y") { $done = $true }
            }
        }
        "7" {
            Write-Host "  -> Skipping notifications. Edit config later to add them." -ForegroundColor DarkGray
        }
        default {
            Write-Host "  -> Invalid choice, skipping notifications." -ForegroundColor Yellow
        }
    }

    return $CurrentBlock
}

$outputBlock = Add-NotificationBlock $outputBlock

# --- Daily summary preference ---
$setupSummary = $true
$summaryTimeVal = "08:00"
if ($NoSummary) {
    $setupSummary = $false
} else {
    if (-not $SummaryTime) {
        Write-Host ""
        $summaryChoice = Read-Host "  Enable daily health summary? A full status report sent once per day (y/n) [y]"
        if ($summaryChoice -eq "n") {
            $setupSummary = $false
        } else {
            $summaryInput = Read-Host "  Daily summary time (HH:MM, 24-hour local) [08:00]"
            if ($summaryInput) { $summaryTimeVal = $summaryInput }
        }
    } else {
        $summaryTimeVal = $SummaryTime
    }
}

# Logs and state go in ProgramData so non-elevated manual runs can also write
$dataDir = Join-Path $env:ProgramData "VHC"
if (-not (Test-Path $dataDir)) {
    New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
    # Grant Users modify access so non-elevated runs can write logs
    icacls $dataDir /grant "Users:(OI)(CI)M" | Out-Null
}
$logDir = Join-Path $dataDir "logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    icacls $logDir /grant "Users:(OI)(CI)M" 2>$null | Out-Null
}
$logPath = Join-Path $logDir "veeam-vhc-aws.log"
$statePath = Join-Path $dataDir "veeam-vhc-aws-state.json"

$configContent = @"
# Veeam VHC AWS configuration
# Generated by setup.ps1 on $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

global:
  timeout_seconds: 30
  retry_count: 2
  retry_delay_seconds: 5
  state_file: "$($statePath -replace '\\', '\\')"
  logging:
    level: INFO
    file: "$($logPath -replace '\\', '\\')"
    rotation_when: midnight
    rotation_interval: 1
    rotation_keep: 30
    console: false
    disk_warning_mb: 500

servers:
$vbrBlock
$vbawsBlock

$outputBlock

repo_health:
  enabled: true
  thresholds:
    free_space_warning_pct: 15
    free_space_critical_pct: 5

retention:
  enabled: true
  thresholds:
    overage_multiplier: 1.5
    max_age_multiplier: 1.5
    orphan_detection: true
  session_lookback_hours: 48
  # exclude_jobs:
  #   - "job name substring to mute"
  # exclude_session_errors:
  #   - "error message regex to mute"

worker_health:
  enabled: true
  lookback_hours: 24
  thresholds:
    session_failure_rate_warning: 0.1
    session_failure_rate_critical: 0.3
    zero_deleted_items_warning: true
    recurring_failure_threshold: 3

error_patterns:
  - pattern: "Access Key Id you provided does not exist"
    severity: critical
    message: "AWS Access Key expired or deleted"
    category: credential
  - pattern: "not enough free addresses in subnet"
    severity: critical
    message: "Subnet IP exhaustion"
    category: network
  - pattern: "Invalid credentials"
    severity: critical
    message: "Invalid credentials"
    category: credential

daily_summary:
  enabled: $($setupSummary.ToString().ToLower())
"@

# Use WriteAllText to avoid UTF-8 BOM that PowerShell 5.1's Out-File adds.
# PyYAML can fail or produce unexpected results when parsing a BOM-prefixed file.
[IO.File]::WriteAllText($configPath, $configContent, [System.Text.UTF8Encoding]::new($false))
Write-Host "  -> Config written to $configPath" -ForegroundColor Green

# --- 3. Create Task Scheduler job ---
Write-Host ""
Write-Host "[3/5] Creating scheduled task" -ForegroundColor Yellow

$taskName = "Veeam VHC AWS"
$exeFullPath = Join-Path $InstallDir "veeam-vhc-aws.exe"
$taskAction = New-ScheduledTaskAction `
    -Execute $exeFullPath `
    -Argument "all --config `"$configPath`"" `
    -WorkingDirectory $InstallDir

$taskTrigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes $IntervalMinutes) `
    -RepetitionDuration (New-TimeSpan -Days 9999)

$taskSettings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -DontStopOnIdleEnd `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10)

$taskPrincipal = New-ScheduledTaskPrincipal `
    -UserId "SYSTEM" `
    -LogonType ServiceAccount `
    -RunLevel Highest

# Remove existing task if present
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "  -> Removed existing '$taskName' task" -ForegroundColor Yellow
}

Register-ScheduledTask `
    -TaskName $taskName `
    -Action $taskAction `
    -Trigger $taskTrigger `
    -Settings $taskSettings `
    -Principal $taskPrincipal `
    -Description "Monitors Veeam backup infrastructure health every $IntervalMinutes minutes" | Out-Null

Write-Host "  -> Scheduled task '$taskName' created (every $IntervalMinutes min)" -ForegroundColor Green

# --- 3b. Daily summary scheduled task ---
if ($setupSummary) {
    $summaryTaskName = "Veeam VHC AWS Daily Summary"
    $summaryAction = New-ScheduledTaskAction `
        -Execute $exeFullPath `
        -Argument "summary --config `"$configPath`"" `
        -WorkingDirectory $InstallDir

    try {
        $summaryTrigger = New-ScheduledTaskTrigger -Daily -At $summaryTimeVal
    } catch {
        Write-Host "  WARNING: Invalid time '$summaryTimeVal', defaulting to 08:00" -ForegroundColor Yellow
        $summaryTimeVal = "08:00"
        $summaryTrigger = New-ScheduledTaskTrigger -Daily -At $summaryTimeVal
    }

    if (Get-ScheduledTask -TaskName $summaryTaskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $summaryTaskName -Confirm:$false
        Write-Host "  -> Removed existing '$summaryTaskName' task" -ForegroundColor Yellow
    }

    Register-ScheduledTask `
        -TaskName $summaryTaskName `
        -Action $summaryAction `
        -Trigger $summaryTrigger `
        -Settings $taskSettings `
        -Principal $taskPrincipal `
        -Description "Daily Veeam VHC AWS health summary at $summaryTimeVal" | Out-Null

    Write-Host "  -> Daily summary task '$summaryTaskName' created (daily at $summaryTimeVal)" -ForegroundColor Green
}

# --- 4. Run once to test ---
Write-Host ""
Write-Host "[4/5] Running initial test..." -ForegroundColor Yellow

try {
    $testResult = & $exeFullPath version 2>&1
    Write-Host "  -> $testResult" -ForegroundColor Green
} catch {
    Write-Host "  -> WARNING: Test run failed: $_" -ForegroundColor Yellow
    Write-Host "  -> The scheduled task is still configured. Check config and try manually." -ForegroundColor Yellow
}

# --- 5. Success ---
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Setup Complete" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  Install dir:  $InstallDir" -ForegroundColor White
Write-Host "  Config:       $configPath" -ForegroundColor White
Write-Host "  Log file:     $logPath" -ForegroundColor White
Write-Host "  Schedule:     Every $IntervalMinutes minutes" -ForegroundColor White
if ($setupSummary) {
    Write-Host "  Daily summary: $summaryTimeVal" -ForegroundColor White
}
if ($AlertUrl) {
    Write-Host "  Alerts:       $AlertUrl" -ForegroundColor White
}
Write-Host ""
Write-Host "  Manual run:   & '$exeFullPath' all --config '$configPath'" -ForegroundColor Cyan
Write-Host "  View logs:    Get-Content '$logPath' -Tail 50" -ForegroundColor Cyan
Write-Host "  Edit config:  notepad '$configPath'" -ForegroundColor Cyan
Write-Host ""

# TODO: Send a notification (via configured output handlers) when uninstall completes,
#       so monitoring recipients know alerting has stopped intentionally.
