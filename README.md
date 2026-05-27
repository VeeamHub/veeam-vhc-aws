<p align="center">
  <img src="https://raw.githubusercontent.com/VeeamHub/veeam-healthcheck/dev/docs/images/health-check-icon.png" alt="Veeam Health Check" width="100">
</p>

<h1 align="center">Veeam VHC AWS</h1>

<p align="center">
  <strong>Continuous monitoring and alerting for your Veeam backup infrastructure.</strong>
</p>

<p align="center">
  <a href="https://github.com/VeeamHub/veeam-vhc-aws/actions/workflows/release.yml"><img src="https://github.com/VeeamHub/veeam-vhc-aws/actions/workflows/release.yml/badge.svg" alt="Release"></a>
  <a href="https://github.com/VeeamHub/veeam-vhc-aws/releases/latest"><img src="https://img.shields.io/github/v/release/VeeamHub/veeam-vhc-aws?label=Latest%20Release" alt="Latest Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/VeeamHub/veeam-vhc-aws" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10">
</p>

<p align="center">
  <a href="https://github.com/VeeamHub/veeam-vhc-aws/releases/latest"><strong>Download Latest Release</strong></a> &nbsp;&middot;&nbsp;
  <a href="https://github.com/VeeamHub/veeam-healthcheck"><strong>Veeam Health Check</strong></a> &nbsp;&middot;&nbsp;
  <a href="https://github.com/VeeamHub/veeam-vhc-aws/issues/new/choose"><strong>Report an Issue</strong></a>
</p>

---

> [!NOTE]
> Veeam VHC AWS is part of the [Veeam Health Check](https://github.com/VeeamHub/veeam-healthcheck) ecosystem — a community-supported suite of tools from [VeeamHub](https://github.com/VeeamHub) for assessing and monitoring Veeam backup infrastructure health. Where Veeam Health Check gives you a point-in-time report, Veeam VHC AWS runs continuously and alerts you the moment something goes wrong.

> This is a community-supported tool and is not an officially supported Veeam product.

## ⚡ Quickstart (60 seconds)

```powershell
# 1. Download the latest release zip, extract it, then in PowerShell as Admin:
.\setup.ps1

# 2. Launch the web admin GUI to add servers and view alerts:
veeam-vhc-aws ui

#    Your browser opens to http://127.0.0.1:9101
#    -> click "+ Add Server" to onboard your first VBR or VBAWS server
#    -> the Dashboard shows live status; the Alerts page lets you suppress with one click

# 3. The scheduled task runs every 5 minutes in the background.
#    Open the UI any time at:  veeam-vhc-aws ui
```

**Already installed?** Just run `veeam-vhc-aws ui` and open <http://127.0.0.1:9101>.

**Prefer YAML?** Run `veeam-vhc-aws setup` for the interactive terminal wizard, or edit `C:\Program Files\VHC\veeam-vhc-aws.yaml` directly.

**No web console / headless?** Skip the UI entirely — the scheduled task runs `veeam-vhc-aws all` in the background and sends alerts to your configured outputs (webhook, email, Prometheus, etc.). The web UI is optional and purely an operator console; it has no effect on alerting.

```powershell
# Run all monitors once, send alerts, exit
veeam-vhc-aws all -c "C:\Program Files\VHC\veeam-vhc-aws.yaml"

# Same, but suppress all terminal output (for scripts or CI)
veeam-vhc-aws all -c config.yaml --no-interactive
```

## Features

- **Multi-Server** -- Monitor multiple VBR and VBAWS servers from a single config with dynamic parallelism
- **Repository Health** -- Monitors repo capacity (including SOBR extents), detects unreachable repos, checks all repo-related sessions for credential/S3/connectivity failures
- **Retention Compliance** -- Validates restore point counts and ages against policy, detects orphaned backups (with Kasten/external policy awareness and configurable exclusions)
- **Worker Health** -- Analyzes VBAWS session failure rates, detects subnet exhaustion, credential failures, and silent retention failures
- **Cross-Correlation** -- Links findings across monitors per-server to surface root causes (e.g., credential expiry causing retention failures)
- **Adaptive Lookback** -- After each successful run, the session lookback window narrows to just since the last success + overlap buffer, dramatically reducing API calls on busy VBR servers. Falls back to full window on failure or first run.
- **Flexible Output** -- JSON, webhooks (Slack/Teams/PagerDuty/ntfy), Prometheus metrics, email
- **Web Admin GUI** -- `veeam-vhc-aws ui` launches a browser-based admin dashboard. View live alerts, suppress with one click, add/edit/delete servers, configure output handlers, and tune thresholds — all without touching YAML
- **Interactive Terminal UX** -- Setup wizard, live monitor progress, severity-coded results table, and actionable connection diagnostics — all auto-disabled for scheduled tasks and scripting
- **Logging** -- Verbose logging with time-based rotation, disk space alerts, and credential redaction

## 📗 Documentation

### Install

#### Option 1: Windows Standalone (Recommended)

1. Download the **zip bundle** from the [latest release](https://github.com/VeeamHub/veeam-vhc-aws/releases/latest)
2. Extract to a folder (e.g., your Desktop or `C:\VHC`)
3. Right-click `setup.ps1` > **Run as Administrator** (required to install to Program Files and create a scheduled task)
4. The setup wizard will walk you through:
   - Configuring your VBR and/or VBAWS servers
   - Choosing how you want to be notified (ntfy, Slack, Teams, PagerDuty, email, or multiple)
   - Creating a Windows Scheduled Task to run automatically every 5 minutes

That's it. The monitor is now running on a schedule and will alert you when issues are detected.

**Or run setup with parameters for automation:**

```powershell
.\setup.ps1 -AlertUrl "https://ntfy.sh/my-veeam-alerts" -IntervalMinutes 10 -InstallDir "C:\VHC"
```

#### Option 2: From Source (any platform)

```bash
# Requires .NET 10 SDK
dotnet build src/VeeamVhcAws/VeeamVhcAws.csproj
dotnet run --project src/VeeamVhcAws -- setup              # Creates a config file interactively
dotnet run --project src/VeeamVhcAws -- all -c veeam-vhc-aws.yaml   # Run all monitors
```

#### Option 3: Docker

```bash
docker build -t veeam-vhc-aws .
docker run -v /path/to/config.yaml:/config/config.yaml veeam-vhc-aws
```

> The Docker image is built with the .NET 10 SDK and runs as a self-contained binary on a minimal `runtime-deps` base image.

### Usage

```bash
# Launch the web admin GUI (browser-based)
veeam-vhc-aws ui

# Interactive terminal setup wizard — guides you through server config, notifications, and thresholds
veeam-vhc-aws setup

# Copy the bundled example config (non-interactive / automation)
veeam-vhc-aws setup --template -o config.yaml

# Run all monitors on all servers (live progress + results table in interactive terminals)
veeam-vhc-aws all -c veeam-vhc-aws.yaml

# Run individual monitors
veeam-vhc-aws repo-health -c config.yaml
veeam-vhc-aws retention -c config.yaml
veeam-vhc-aws worker-health -c config.yaml

# Prometheus HTTP server (long-running mode)
veeam-vhc-aws serve -c config.yaml --port 9100 --interval 300

# Test connectivity to all configured servers (live spinners + actionable error hints)
veeam-vhc-aws test-connection -c config.yaml

# Inspect error patterns or match an error string
veeam-vhc-aws diagnose --list-patterns -c config.yaml
veeam-vhc-aws diagnose --match "Access key has expired" -c config.yaml

# View and manage captured errors and suppressions
veeam-vhc-aws captures list -c config.yaml
veeam-vhc-aws captures suppress <key> -c config.yaml
veeam-vhc-aws captures unsuppress <key> -c config.yaml

# Obfuscate plaintext passwords in config file in-place
veeam-vhc-aws encrypt-config -c config.yaml

# Print version
veeam-vhc-aws version

# Suppress all interactive output (for use in scripts or CI)
veeam-vhc-aws all -c config.yaml --no-interactive
```

### Web Admin GUI

<p align="center">
  <img src="docs/images/web-admin-ui.png" alt="Veeam VHC AWS web admin UI — Dashboard and Alerts pages" width="800">
</p>

<!--
  To replace the screenshot:
  1. Take a PNG capture of the Dashboard (1200-1600px wide is ideal).
  2. Optionally include a second composited shot of the Alerts page.
  3. Save as docs/images/web-admin-ui.png in this repo.
-->

The `ui` command launches a browser-based admin dashboard — embedded ASP.NET Core + Blazor Server hosted by the same `veeam-vhc-aws.exe`. No separate install or web server required.

```bash
# Localhost-only (default) — admin uses browser on the same machine as the tool
veeam-vhc-aws ui -c config.yaml

# Listen on all interfaces with token auth (for remote admin from your workstation)
veeam-vhc-aws ui -c config.yaml --bind 0.0.0.0 --port 9101

# Regenerate the auth token
veeam-vhc-aws ui --regen-token

# Don't auto-open the browser (useful for headless servers)
veeam-vhc-aws ui -c config.yaml --no-browser
```

**Pages:**

| Page | What you can do |
|---|---|
| **Dashboard** | At-a-glance status banner, per-server health cards, last-run time, finding counts |
| **Alerts** | Live table of active findings with severity badges. **Click [Suppress] to silence an alert.** Filter by severity/server, search by resource. Auto-refreshes every 5s. |
| **Servers** | Add/edit/delete VBR and VBAWS servers via forms. Passwords are obfuscated on save via the same `PasswordObfuscator` the CLI uses. |
| **Outputs** | Add/edit/delete notification channels — webhook (Slack/Teams/ntfy/PagerDuty), email (SMTP), Prometheus, JSON file. Per-type form fields. |
| **Thresholds** | Tabbed editor for per-monitor thresholds (Repo Health / Retention / Worker Health). |
| **Captures** | Historical error log with filter (suppressed/unsuppressed). Same suppress/unsuppress controls as Alerts. |
| **Settings** | Global config: logging level, HTTP timeouts, retry counts, page sizes, daily summary toggle. |

**Authentication:**

- **Localhost (default)** — `--bind 127.0.0.1`. No auth — accessible only from the same machine. RDP in, open browser, manage.
- **Remote** — `--bind 0.0.0.0` or specific IP. A 64-char token is auto-generated on first run and stored at `C:\ProgramData\VHC\ui-token.txt` (Windows) or `~/.vhc/ui-token.txt` (others). Token is appended to the URL on startup (`http://server:9101/?t=<token>`) and persisted in a cookie after first visit. Rotate with `veeam-vhc-aws ui --regen-token`.

**Coexistence with the scheduled task:** The UI is purely an operator console — it reads the state file the scheduled task writes. You can run the UI continuously without affecting the every-5-minutes monitor task. Changes to `config.yaml` made via the UI are picked up by the next scheduled run automatically.

**Live updates:** Alerts page auto-refreshes every 5 seconds; Dashboard every 10 seconds. If the scheduled task writes new findings, you'll see them appear without manual refresh.

### Interactive Terminal UX

When run in an interactive terminal, veeam-vhc-aws renders a rich terminal UI using Spectre.Console. When run as a scheduled task, Windows service, or with output piped/redirected, all rich output is automatically suppressed and the tool behaves as a standard JSON-emitting CLI — no configuration required.

**First-run detection:** If you run `veeam-vhc-aws` with no arguments and no config file exists, the tool offers to launch the setup wizard automatically.

**Setup wizard (`setup`):** A 7-step guided wizard that collects server details (with live connectivity test), notification channels, output options, and alert thresholds — then writes an encrypted config file ready to use. Passwords are obfuscated on disk automatically. Use `--template` to get the old behavior (copy the example config file).

**Live monitor progress (`all`):** While monitors run, a live table shows each server × monitor with a real-time status indicator. Only shown when no `json_stdout` output handler is configured (to avoid mixing UI chrome with JSON output).

```
╭─────────────┬───────────────┬──────────────╮
│ Server      │ Monitor       │ Status       │
├─────────────┼───────────────┼──────────────┤
│ prod-vbr    │ repo_health   │ Done — OK    │
│ prod-vbr    │ retention     │ Running...   │
│ prod-vbaws  │ worker_health │ Queued       │
╰─────────────┴───────────────┴──────────────╯
```

**Results table (`all`):** After monitors complete, findings are displayed grouped by severity (errors → critical → warning), with a one-line status summary above the table.

**Connection test (`test-connection`):** Live progress spinner per target, then a results table. Failed connections include actionable hints (e.g., `— check username/password`, `— set verify_ssl: false or trust the cert`).

**`--no-interactive` flag:** Globally suppresses all interactive output regardless of terminal detection. Equivalent to piping output. Useful for automation scripts that run in a terminal but should not trigger the interactive UI.

### Alert Deduplication

veeam-vhc-aws tracks finding state between runs so you only get notified when something changes — not on every scheduled execution.

| Event | Behavior |
|-------|----------|
| Problem first detected | Alert fires |
| Problem persists on next run | Silent — no repeat alert |
| Problem disappears | `RESOLVED` notification fires |
| Problem comes back later | Treated as new — alert fires again |

State is stored in `veeam-vhc-aws-state.json` (default: `C:\ProgramData\VHC\veeam-vhc-aws-state.json` on Windows). Deleting this file resets all state — every existing finding will re-alert on the next run.

To enable deduplication on webhook handlers, add `deduplicate: true` to the handler config:

```yaml
output:
  - type: webhook
    url: https://ntfy.sh/my-veeam-alerts
    template: ntfy
    min_severity: warning
    deduplicate: true
```

### Daily Summary

Veeam VHC AWS can send a daily health digest showing the complete status of all monitors — not just new issues. Unlike alert notifications (which only fire when something changes), the daily summary always sends at the configured time.

**Windows Standalone:** During `setup.ps1`, you'll be prompted to enable the daily summary and choose a time (default 8:00 AM local). This creates a second Windows Scheduled Task named **Veeam VHC AWS Daily Summary**.

**Run on demand:**

```powershell
.\veeam-vhc-aws.exe summary -c C:\ProgramData\VHC\veeam-vhc-aws.yaml
```

**Config options:**

```yaml
daily_summary:
  enabled: true   # set to false to disable
  # output:       # optional: different handlers just for the summary
  #   - type: webhook
  #     url: https://ntfy.sh/my-veeam-daily
  #     template: ntfy
```

The daily summary uses your configured output handlers (same as alerts) but bypasses deduplication — it always sends the full current state.

### Exit Codes

| Code | Severity |
|------|----------|
| 0 | OK |
| 1 | WARNING |
| 2 | CRITICAL |
| 3 | ERROR |

The worst severity across all findings determines the exit code, making it CI/CD-friendly.

### Configuration

Config file resolution order:
1. `-c` / `--config` CLI argument
2. `VHC_MONITOR_CONFIG` environment variable
3. `./veeam-vhc-aws.yaml` in the current directory

See [`config/example.yaml`](config/example.yaml) for the full configuration reference including server setup, thresholds, output handlers, and error patterns.

#### Performance Tuning

For environments with busy VBR servers or large session volumes, these global settings control API pagination and timeout behavior:

```yaml
global:
  timeout_seconds: 30           # Per-request HTTP timeout (default: 30). Avoid values >60 on slow APIs.
  session_timeout_seconds: 600  # Timeout for /api/v1/sessions calls specifically (default: 600).
                                # Raise to 1200+ for very large deployments (20k+ sessions).
  page_size: 500                # Items per API page (default: 500, was 50 in older versions)
  max_pages: 100                # Circuit breaker: max pages before stopping (default: 100)
```

> [!TIP]
> If your VBR server has a large number of sessions and monitor runs are timing out, increase `session_timeout_seconds` first (e.g. `1200` for 20 minutes). The general `timeout_seconds` applies to all other API calls.

**Adaptive lookback** is enabled automatically. After each successful monitor run, the session lookback window narrows from the configured max (e.g. 24h) down to just the time since the last success + a 2-minute overlap buffer. This means a monitor running every 5 minutes only fetches ~5 minutes of sessions instead of 24 hours, reducing API load by orders of magnitude. The overlap buffer is configurable per monitor:

```yaml
repo_health:
  session_lookback_hours: 24      # Max lookback (used on first run or after failure)
  lookback_overlap_minutes: 2     # Overlap buffer for adaptive window (default: 2)
```

If a run fails (errors fetching data), the lookback window stays wide until the next fully clean run.

#### Multi-Server

Define all servers in the `servers:` array. Monitors automatically route to applicable server types (repo-health/retention on VBR, worker-health on VBAWS).

```yaml
servers:
  - name: prod-vbr
    type: vbr
    url: https://vbr-prod:9419
    username: DOMAIN\backupadmin
    password: "secret"
    api_version: "1.3-rev1"
    verify_ssl: false

  - name: aws-backup
    type: vbaws
    url: https://vbaws-appliance
    username: admin
    password: "secret"
    verify_ssl: false
```

> [!TIP]
> **Passwords and usernames with backslashes** (e.g. `DOMAIN\user`, `P@ss\word`): use **single quotes** in your config file to avoid YAML escape interpretation.
> ```yaml
> username: 'DOMAIN\backupadmin'
> password: 'P@ss\word!'
> ```
> Double-quoted values (`"..."`) process backslash sequences — `\n` becomes a newline, `\t` a tab, etc. Single-quoted values are always literal. If you do use double quotes, escape every backslash: `"DOMAIN\\backupadmin"`.

- **Dynamic parallelism:** 2 or fewer servers run sequentially. 3+ run in parallel (up to 20 workers).
- **Failure isolation:** If one server is unreachable, the others continue normally.
- **Server prefixing:** All findings include the server name (e.g., `[prod-vbr] repo:Backup Copy Repo`).

#### Environment Variable Fallback

For single-server setups, use env vars instead of a config file:

```
VEEAM_VBR_URL, VEEAM_VBR_USERNAME, VEEAM_VBR_PASSWORD, VEEAM_VBR_API_VERSION
VEEAM_VBAWS_URL, VEEAM_VBAWS_USERNAME, VEEAM_VBAWS_PASSWORD
```

#### Notifications

The setup wizard configures notifications for you, but you can also edit the config directly. Stack multiple handlers -- all fire on every monitor run.

| Handler | Template | Use Case |
|---------|----------|----------|
| `json_stdout` | -- | Terminal / piping |
| `json_file` | -- | Log aggregation |
| `webhook` | `ntfy` | Push notifications ([free, self-hostable](https://ntfy.sh)) |
| `webhook` | `slack` | Slack channel alerts |
| `webhook` | `teams` | Microsoft Teams alerts |
| `webhook` | `pagerduty` | PagerDuty incident management |
| `webhook` | `generic` | Custom integrations |
| `prometheus` | `pushgateway` | Push metrics to Pushgateway |
| `prometheus` | `server` | Expose `/metrics` endpoint |
| `email` | -- | SMTP email reports |

Each handler supports `min_severity` to control when it fires (`ok`, `warning`, `critical`).

Example stacking multiple outputs:

```yaml
output:
  - type: json_stdout
  - type: webhook
    url: https://ntfy.sh/my-veeam-alerts
    template: ntfy
    min_severity: warning
  - type: email
    smtp_host: smtp.office365.com
    smtp_port: 587
    from_addr: veeam-vhc-aws@example.com
    to_addrs: ["oncall@example.com"]
    min_severity: critical
```

### Logging

Log files rotate daily and are stored alongside the config by default. Key options:

```yaml
global:
  logging:
    level: INFO                     # DEBUG, INFO, WARNING, ERROR (default: DEBUG)
    file: ./veeam-vhc-aws.log       # log file path
    rotation_keep: 30               # number of daily files to retain (default: 30)
    console: true                   # also write to stderr (default: true)
    disk_warning_pct: 20            # warn when drive free space hits this % (default: 20)
```

#### Disk Space Alerts

On every run, veeam-vhc-aws checks the free space on the drive where the log file lives:

| Free Space | Result |
|------------|--------|
| Above threshold | No alert |
| Equal to `disk_warning_pct` | `WARNING` log entry |
| Below `disk_warning_pct` | `ERROR` log entry — fires every run, no deduplication |

The check always fires — it intentionally bypasses the normal finding deduplication so you cannot miss a critical disk condition. Set `disk_warning_pct: 0` to disable.

#### Log Volume Estimates

Logs are small in normal operation. The table below shows estimates for a representative large environment (280 sessions/day, ~4,000 workloads):

| Run Frequency | Log Level | Daily Volume | 30-Day Total |
|---------------|-----------|-------------|--------------|
| Every 15 min  | INFO      | ~1.5 MB     | ~45 MB       |
| Hourly        | INFO      | ~350 KB     | ~10 MB       |
| Hourly        | DEBUG     | ~700 KB     | ~21 MB       |
| Every 15 min  | DEBUG + 30% failure rate | ~13 MB | ~400 MB |

> **Recommendation:** Use `level: INFO` in production. `DEBUG` is useful for troubleshooting but can generate ~400 MB/month on high-failure environments running frequently. If you're on a small Windows VM (< 2 GB free), consider dropping `rotation_keep` to 7–14 days.

### Upgrading

To upgrade without re-running the full setup wizard — your config, state, and logs in `C:\ProgramData\VHC\` are untouched.

#### Windows Standalone (Recommended)

1. Download the new zip bundle from the [latest release](https://github.com/VeeamHub/veeam-vhc-aws/releases/latest)
2. Extract it
3. Open PowerShell **as Administrator** in the extracted folder and run:

```powershell
.\setup.ps1 -Upgrade
```

That's it. The wizard will:
- Stop the scheduled task, swap in the new executable, and restart it
- Scan your existing config for any keys added since your last install and offer to configure them (e.g., if you're missing `session_timeout_seconds`, it will prompt you)

#### Manual alternative

If you prefer, just copy the new `veeam-vhc-aws.exe` over the existing one:

```powershell
# Stop the scheduled task first to avoid replacing a running binary
Stop-ScheduledTask -TaskName "Veeam VHC AWS"
Copy-Item .\veeam-vhc-aws.exe "$env:ProgramFiles\VHC\veeam-vhc-aws.exe" -Force
Start-ScheduledTask -TaskName "Veeam VHC AWS"
```

> [!NOTE]
> Your config (`C:\ProgramData\VHC\veeam-vhc-aws.yaml`), alert state (`veeam-vhc-aws-state.json`), and logs are stored separately and are never touched by an upgrade.

#### Config changes in recent releases

| Version | New config key | Default | Notes |
|---------|---------------|---------|-------|
| v1.0.0.44+ | `global.session_timeout_seconds` | `600` | Timeout for `/api/v1/sessions` calls. Configurable via the web UI Settings page or YAML. |

---

### Uninstall

#### Windows Standalone

```powershell
# 1. Remove the scheduled task
Unregister-ScheduledTask -TaskName "Veeam VHC AWS" -Confirm:$false

# 2. Delete the install directory (default: C:\Program Files\VHC)
Remove-Item -Recurse -Force "$env:ProgramFiles\VHC"
```

If you installed to a custom directory, replace the path accordingly.

#### From Source

```bash
# Remove config and logs
rm -f ./veeam-vhc-aws.yaml ./veeam-vhc-aws.log ./veeam-vhc-aws-state.json
```

#### Docker

```bash
docker rmi veeam-vhc-aws
```

### Building from Source

```powershell
# Build Windows exe + zip bundle
.\build.ps1

# Or just the exe, no zip
.\build.ps1 -NoZip
```

```bash
# Build for current platform
./build.sh --local

# Build all platforms (win-x64, linux-x64, osx-arm64, osx-x64)
./build.sh
```

### Running Tests

```bash
dotnet test tests/VeeamVhcAws.Tests/
```

## ✍ Contributions

We welcome contributions from the community! We encourage you to create [issues](https://github.com/VeeamHub/veeam-vhc-aws/issues/new/choose) for Bugs & Feature Requests and submit Pull Requests. For more detailed information, refer to our [Contributing Guide](CONTRIBUTING.md).

## 🤝🏾 License

* [MIT License](LICENSE)

## 🤔 Questions

If you have any questions or something is unclear, please don't hesitate to [create an issue](https://github.com/VeeamHub/veeam-vhc-aws/issues/new/choose) and let us know!
