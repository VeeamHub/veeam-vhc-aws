# Starts the admin UI from a published exe the way the Start Menu shortcut does, then
# fetches real pages. File checks alone passed two broken releases: 1.0.0.46 404'd on every
# page, and 1.0.0.51's `ui` exited at once on Windows because a free port looked taken.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$ExampleConfig,
    [int]$Port = 9187
)
$ErrorActionPreference = 'Stop'

$exePath = (Resolve-Path $Exe).Path
$dir = Join-Path ([IO.Path]::GetTempPath()) ("vhc-ui-smoke-" + [guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory $dir | Out-Null
$config = Join-Path $dir 'veeam-vhc-aws.yaml'
Copy-Item $ExampleConfig $config
$uiArgs = @('ui', '-c', "`"$config`"", '--port', "$Port", '--no-browser')

$ui = Start-Process $exePath -ArgumentList $uiArgs -PassThru -WindowStyle Hidden
$null = $ui.Handle  # keeps ExitCode readable after the process ends
try {
    $up = $false
    for ($i = 0; $i -lt 30 -and -not $up; $i++) {
        if ($ui.HasExited) { throw "ui exited with code $($ui.ExitCode) before serving anything" }
        try { $null = Invoke-WebRequest "http://127.0.0.1:$Port/healthz" -UseBasicParsing -TimeoutSec 2; $up = $true }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $up) { throw "ui did not answer /healthz within 30 s" }

    foreach ($path in '/', '/help', '/servers') {
        # Invoke-WebRequest throws on 404/500, which fails the step.
        $r = Invoke-WebRequest "http://127.0.0.1:$Port$path" -UseBasicParsing -TimeoutSec 10
        if ("$($r.Headers['X-VHC-AWS-UI'])" -ne '1') { throw "$path answered $($r.StatusCode) without the admin UI marker header" }
        Write-Host "OK: $path -> $($r.StatusCode)"
    }

    # A second launch must recognise the running UI and exit 0, not call the port taken.
    $second = Start-Process $exePath -ArgumentList $uiArgs -PassThru -WindowStyle Hidden
    $null = $second.Handle
    if (-not $second.WaitForExit(30000)) { Stop-Process -Id $second.Id -Force; throw "second ui launch did not exit within 30 s" }
    if ($second.ExitCode -ne 0) { throw "second ui launch exited with code $($second.ExitCode)" }
    Write-Host "OK: second launch found the running UI and exited 0"
}
finally {
    if (-not $ui.HasExited) { Stop-Process -Id $ui.Id -Force }
    Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
}
