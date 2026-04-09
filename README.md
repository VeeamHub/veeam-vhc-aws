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

## Features

- **Multi-Server** -- Monitor multiple VBR and VBAWS servers from a single config with dynamic parallelism
- **Repository Health** -- Monitors repo capacity (including SOBR extents), detects unreachable repos, checks all repo-related sessions for credential/S3/connectivity failures
- **Retention Compliance** -- Validates restore point counts and ages against policy, detects orphaned backups (with Kasten/external policy awareness and configurable exclusions)
- **Worker Health** -- Analyzes VBAWS session failure rates, detects subnet exhaustion, credential failures, and silent retention failures
- **Infrastructure Health** -- Query managed servers (vCenter, ESXi, Windows/Linux hosts) for availability status, browse vCenter inventory (VMs, clusters, datastores), and check TLS certificate expiry via the VBR REST API
- **Cross-Correlation** -- Links findings across monitors per-server to surface root causes (e.g., credential expiry causing retention failures)
- **Flexible Output** -- JSON, webhooks (Slack/Teams/PagerDuty/ntfy), Prometheus metrics, email
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
# Interactive setup — creates config file
veeam-vhc-aws setup

# Run all monitors on all servers
veeam-vhc-aws all -c veeam-vhc-aws.yaml

# Run individual monitors
veeam-vhc-aws repo-health -c config.yaml
veeam-vhc-aws retention -c config.yaml
veeam-vhc-aws worker-health -c config.yaml

# Prometheus HTTP server (long-running mode)
veeam-vhc-aws serve -c config.yaml --port 9100 --interval 300

# Test connectivity to all configured servers
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
```

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
- Swap in the new executable
- Scan your config for any features added since your last install and offer to configure them (e.g., if you're missing `daily_summary`, it will ask if you'd like to set it up)

#### Manual alternative

If you prefer, just copy the new `veeam-vhc-aws.exe` over the existing one:

```powershell
Copy-Item .\veeam-vhc-aws.exe "$env:ProgramFiles\VHC\veeam-vhc-aws.exe" -Force
```

> [!NOTE]
> Your config (`C:\ProgramData\VHC\veeam-vhc-aws.yaml`), alert state (`veeam-vhc-aws-state.json`), and logs are stored separately and are never touched by an upgrade.

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
