param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('write-wrapper', 'register-daily', 'unregister-daily', 'status', 'task-argument')]
    [string]$Action,

    [string]$InstallDir = '',
    [string]$ExePath = '',
    [string]$ConfigPath = '',
    [string]$Time = '08:00',
    [string]$AlertUrl = '',
    [string]$Command = 'summary'
)

$ErrorActionPreference = 'Stop'
$TaskName = 'Veeam VHC AWS Daily Summary'

function Test-VhcWindows {
    if ($PSVersionTable.PSVersion.Major -ge 6) { return [bool]$IsWindows }
    return $true
}

function Get-VhcTaskArgument {
    param([string]$WrapperPath, [string]$Cmd, [string]$Config)
    $arg = "-NonInteractive -ExecutionPolicy Bypass -File `"$WrapperPath`" -Command $Cmd"
    if ($Config) { $arg += " -ConfigPath `"$Config`"" }
    return $arg
}

function Get-ExistingAlertUrl {
    param([string]$WrapperPath)
    if (-not (Test-Path $WrapperPath)) { return '' }
    $text = [IO.File]::ReadAllText($WrapperPath)
    if ($text -match '\$alertUrl = ''([^'']*)''') { return $Matches[1] }
    if ($text -match '\$alertUrl = "([^"]*)"') { return $Matches[1] }
    return ''
}

function Write-VhcFailureWrapper {
    param([string]$Directory, [string]$Alert)
    $safeAlert = $Alert -replace "'", "''"
    $template = @'
param(
    [ValidateSet('all', 'summary')]
    [string]$Command = 'all',
    [string]$ConfigPath = ''
)
$exePath = Join-Path $PSScriptRoot 'veeam-vhc-aws.exe'
if (-not $ConfigPath) { $ConfigPath = Join-Path $PSScriptRoot 'veeam-vhc-aws.yaml' }
& $exePath $Command --config $ConfigPath
$code = $LASTEXITCODE
if ($null -eq $code) { $code = 0 }
if ($code -ne 0) {
    $alertUrl = '__ALERT_URL__'
    if ($alertUrl) {
        $body = "VHC AWS scheduled task $Command failed with exit code $code on $env:COMPUTERNAME"
        try {
            Invoke-WebRequest -Uri $alertUrl -Method Post -Body $body -UseBasicParsing -ErrorAction Stop | Out-Null
        } catch {
            Write-Warning "Failed to send failure alert: $_"
        }
    }
}
exit $code
'@
    $content = $template.Replace('__ALERT_URL__', $safeAlert)
    $wrapperPath = Join-Path $Directory 'Run-VhcAws.ps1'
    [IO.File]::WriteAllText($wrapperPath, $content, [System.Text.UTF8Encoding]::new($false))
    return $wrapperPath
}

switch ($Action) {
    'task-argument' {
        if (-not $InstallDir) { throw 'InstallDir is required' }
        $wrapper = Join-Path $InstallDir 'Run-VhcAws.ps1'
        Write-Output (Get-VhcTaskArgument -WrapperPath $wrapper -Cmd $Command -Config $ConfigPath)
    }
    'write-wrapper' {
        if (-not $InstallDir) { throw 'InstallDir is required' }
        $wrapper = Join-Path $InstallDir 'Run-VhcAws.ps1'
        $alert = $AlertUrl
        if (-not $alert) { $alert = Get-ExistingAlertUrl $wrapper }
        Write-VhcFailureWrapper -Directory $InstallDir -Alert $alert | Out-Null
        Write-Output $wrapper
    }
    'status' {
        if (-not (Test-VhcWindows)) { exit 2 }
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($task) { exit 0 } else { exit 1 }
    }
    'unregister-daily' {
        if (-not (Test-VhcWindows)) { Write-Output 'skipped-non-windows'; exit 0 }
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }
        Write-Output 'unregistered'
    }
    'register-daily' {
        if (-not (Test-VhcWindows)) { Write-Output 'skipped-non-windows'; exit 0 }
        if (-not $InstallDir) { throw 'InstallDir is required' }
        if (-not $ConfigPath) { throw 'ConfigPath is required' }
        if (-not $ExePath) { $ExePath = Join-Path $InstallDir 'veeam-vhc-aws.exe' }
        $wrapper = Join-Path $InstallDir 'Run-VhcAws.ps1'
        if (-not (Test-Path $wrapper)) {
            Write-VhcFailureWrapper -Directory $InstallDir -Alert $AlertUrl | Out-Null
        }
        $argument = Get-VhcTaskArgument -WrapperPath $wrapper -Cmd 'summary' -Config $ConfigPath
        $action = New-ScheduledTaskAction `
            -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -Argument $argument `
            -WorkingDirectory $InstallDir
        try {
            $trigger = New-ScheduledTaskTrigger -Daily -At $Time
        } catch {
            $Time = '08:00'
            $trigger = New-ScheduledTaskTrigger -Daily -At $Time
        }
        $settings = New-ScheduledTaskSettingsSet `
            -StartWhenAvailable `
            -DontStopOnIdleEnd `
            -ExecutionTimeLimit (New-TimeSpan -Hours 6)
        $principal = New-ScheduledTaskPrincipal `
            -UserId 'SYSTEM' `
            -LogonType ServiceAccount `
            -RunLevel Highest
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        }
        Register-ScheduledTask `
            -TaskName $TaskName `
            -Action $action `
            -Trigger $trigger `
            -Settings $settings `
            -Principal $principal `
            -Description "Daily Veeam VHC AWS health summary at $Time" | Out-Null
        Write-Output "registered $Time"
    }
}
