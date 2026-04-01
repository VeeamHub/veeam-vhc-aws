<p align="center">
  <img src="https://raw.githubusercontent.com/VeeamHub/veeam-healthcheck/dev/docs/images/health-check-icon.png" alt="Veeam Health Check" width="100">
</p>

<h1 align="center">VHC Monitor</h1>

<p align="center">
  <strong>Continuous monitoring and alerting for your Veeam backup infrastructure.</strong>
</p>

<p align="center">
  <a href="https://github.com/VeeamHub/veeam-vhc-monitor/actions/workflows/release.yml"><img src="https://github.com/VeeamHub/veeam-vhc-monitor/actions/workflows/release.yml/badge.svg" alt="Release"></a>
  <a href="https://github.com/VeeamHub/veeam-vhc-monitor/releases/latest"><img src="https://img.shields.io/github/v/release/VeeamHub/veeam-vhc-monitor?label=Latest%20Release" alt="Latest Release"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/VeeamHub/veeam-vhc-monitor" alt="License: MIT"></a>
  <img src="https://img.shields.io/badge/python-3.11%2B-blue" alt="Python 3.11+">
</p>

<p align="center">
  <a href="https://github.com/VeeamHub/veeam-vhc-monitor/releases/latest"><strong>Download Latest Release</strong></a> &nbsp;&middot;&nbsp;
  <a href="https://github.com/VeeamHub/veeam-healthcheck"><strong>Veeam Health Check</strong></a> &nbsp;&middot;&nbsp;
  <a href="https://github.com/VeeamHub/veeam-vhc-monitor/issues/new/choose"><strong>Report an Issue</strong></a>
</p>

---

> [!NOTE]
> VHC Monitor is part of the [Veeam Health Check](https://github.com/VeeamHub/veeam-healthcheck) ecosystem — a community-supported suite of tools from [VeeamHub](https://github.com/VeeamHub) for assessing and monitoring Veeam backup infrastructure health. Where Veeam Health Check gives you a point-in-time report, VHC Monitor runs continuously and alerts you the moment something goes wrong.

> This is a community-supported tool and is not an officially supported Veeam product.

## Features

- **Multi-Server** -- Monitor multiple VBR and VBAWS servers from a single config with dynamic parallelism
- **Repository Health** -- Monitors repo capacity (including SOBR extents), detects unreachable repos, checks all repo-related sessions for credential/S3/connectivity failures
- **Retention Compliance** -- Validates restore point counts and ages against policy, detects orphaned backups (with Kasten/external policy awareness and configurable exclusions)
- **Worker Health** -- Analyzes VBAWS session failure rates, detects subnet exhaustion, credential failures, and silent retention failures
- **Cross-Correlation** -- Links findings across monitors per-server to surface root causes (e.g., credential expiry causing retention failures)
- **Flexible Output** -- JSON, webhooks (Slack/Teams/PagerDuty/ntfy), Prometheus metrics, email
- **Logging** -- Verbose logging with time-based rotation, disk space alerts, and credential redaction

## 📗 Documentation

### Install

#### Option 1: Windows Standalone (Recommended)

1. Download the **zip bundle** from the [latest release](https://github.com/VeeamHub/veeam-vhc-monitor/releases/latest)
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
# Requires Python 3.11+
pip install .
vhc-monitor setup              # Creates a config file interactively
vhc-monitor all -c vhc-monitor.yaml   # Run all monitors
```

#### Option 3: Docker

```bash
docker build -t vhc-monitor .
docker run -v /path/to/config.yaml:/config/config.yaml vhc-monitor
```

### Usage

```bash
# Interactive setup — creates config file
vhc-monitor setup

# Run all monitors on all servers
vhc-monitor all -c vhc-monitor.yaml

# Run individual monitors
vhc-monitor repo-health -c config.yaml
vhc-monitor retention -c config.yaml
vhc-monitor worker-health -c config.yaml

# Prometheus HTTP server (long-running mode)
vhc-monitor serve -c config.yaml --port 9100 --interval 300

# Test connectivity to all configured servers
vhc-monitor test-connection -c config.yaml

# Inspect error patterns or match an error string
vhc-monitor diagnose --list-patterns -c config.yaml
vhc-monitor diagnose --match "Access key has expired" -c config.yaml

# Print version
vhc-monitor version
```

### Alert Deduplication

vhc-monitor tracks finding state between runs so you only get notified when something changes — not on every scheduled execution.

| Event | Behavior |
|-------|----------|
| Problem first detected | Alert fires |
| Problem persists on next run | Silent — no repeat alert |
| Problem disappears | `RESOLVED` notification fires |
| Problem comes back later | Treated as new — alert fires again |

State is stored in `vhc-monitor-state.json` (default: `C:\ProgramData\VHC\vhc-monitor-state.json` on Windows). Deleting this file resets all state — every existing finding will re-alert on the next run.

To enable deduplication on webhook handlers, add `deduplicate: true` to the handler config:

```yaml
output:
  - type: webhook
    url: https://ntfy.sh/my-veeam-alerts
    template: ntfy
    min_severity: warning
    deduplicate: true
```

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
3. `./vhc-monitor.yaml` in the current directory

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
    from_addr: vhc-monitor@example.com
    to_addrs: ["oncall@example.com"]
    min_severity: critical
```

### Uninstall

#### Windows Standalone

```powershell
# 1. Remove the scheduled task
Unregister-ScheduledTask -TaskName "VHC Monitor" -Confirm:$false

# 2. Delete the install directory (default: C:\Program Files\VHC)
Remove-Item -Recurse -Force "$env:ProgramFiles\VHC"
```

If you installed to a custom directory, replace the path accordingly.

#### From Source (pip)

```bash
pip uninstall vhc-monitor -y

# Remove config and logs
rm -f ./vhc-monitor.yaml ./vhc-monitor.log ./vhc-monitor-state.json
```

#### Docker

```bash
docker rmi vhc-monitor
```

### Building from Source

```powershell
# Build standalone Windows executable
.\build.ps1
# Output: dist\vhc-monitor.exe
```

### Running Tests

```bash
pip install -e ".[dev]"
python -m pytest tests/ -v
```

Tests use `respx` for HTTP mocking with fixtures in `tests/fixtures/`.

## ✍ Contributions

We welcome contributions from the community! We encourage you to create [issues](https://github.com/VeeamHub/veeam-vhc-monitor/issues/new/choose) for Bugs & Feature Requests and submit Pull Requests. For more detailed information, refer to our [Contributing Guide](CONTRIBUTING.md).

## 🤝🏾 License

* [MIT License](LICENSE)

## 🤔 Questions

If you have any questions or something is unclear, please don't hesitate to [create an issue](https://github.com/VeeamHub/veeam-vhc-monitor/issues/new/choose) and let us know!
