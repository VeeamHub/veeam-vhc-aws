# veeam-vhc-monitor

Continuous monitoring toolkit for Veeam backup infrastructure. Tracks health and compliance across Veeam Backup & Replication (VBR) and Veeam Backup for AWS (VBAWS) with alerting, Prometheus metrics, and cross-correlation of findings.

## Features

- **Multi-Server** -- Monitor multiple VBR and VBAWS servers from a single config with dynamic parallelism
- **Repository Health** -- Monitors repo capacity (including SOBR extents), detects unreachable repos, checks all repo-related sessions for credential/S3/connectivity failures
- **Retention Compliance** -- Validates restore point counts and ages against policy, detects orphaned backups (with Kasten/external policy awareness and configurable exclusions)
- **Worker Health** -- Analyzes VBAWS session failure rates, detects subnet exhaustion, credential failures, and silent retention failures
- **Cross-Correlation** -- Links findings across monitors per-server to surface root causes (e.g., credential expiry causing retention failures)
- **Flexible Output** -- JSON, webhooks (Slack/Teams/PagerDuty/ntfy), Prometheus metrics, email
- **Logging** -- Verbose logging with time-based rotation, disk space alerts, and credential redaction

## 📗 Documentation

### Quick Start

**Standalone executable (Windows):**

Download `vhc-monitor.exe` and `setup.ps1` from the [latest release](https://github.com/VeeamHub/veeam-vhc-monitor/releases/latest), then run:

```powershell
.\setup.ps1 -AlertUrl "https://ntfy.example.com/veeam-alerts"
```

**From source (any platform):**

```bash
pip install .
cp config/example.yaml ./vhc-monitor.yaml
# Edit vhc-monitor.yaml with your server details
vhc-monitor all -c vhc-monitor.yaml
```

Requires Python 3.11+.

### Usage

```bash
# Run individual monitors
vhc-monitor repo-health -c config.yaml
vhc-monitor retention -c config.yaml
vhc-monitor worker-health -c config.yaml

# Run all monitors on all servers (parallel auto-enabled for 3+ servers)
vhc-monitor all -c config.yaml

# Prometheus HTTP server (scrape interval in seconds)
vhc-monitor serve -c config.yaml --port 9100 --interval 300

# Test connectivity to VBR and VBAWS
vhc-monitor test-connection -c config.yaml

# Inspect error patterns or match an error string
vhc-monitor diagnose --list-patterns -c config.yaml
vhc-monitor diagnose --match "Access key has expired" -c config.yaml

# Print version
vhc-monitor version
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

#### Output Handlers

Stack multiple outputs in your config. All fire on every monitor run.

| Handler | Template | Use Case |
|---------|----------|----------|
| `json_stdout` | -- | Terminal / piping |
| `json_file` | -- | Log aggregation |
| `webhook` | `ntfy` | Push notifications |
| `webhook` | `slack` | Slack alerts |
| `webhook` | `teams` | Teams alerts |
| `webhook` | `pagerduty` | Incident management |
| `webhook` | `generic` | Custom integrations |
| `prometheus` | `pushgateway` | Push metrics to Pushgateway |
| `prometheus` | `server` | Expose `/metrics` endpoint |
| `email` | -- | SMTP email reports |

### Building Standalone Executable

```powershell
# On Windows
.\build.ps1
# Output: dist/vhc-monitor.exe
```

### Docker

```bash
docker build -t vhc-monitor .
docker run -v /path/to/config.yaml:/config/config.yaml vhc-monitor
```

### Testing

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
