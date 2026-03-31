# vhc-monitor

A monitoring toolkit for Veeam backup infrastructure that tracks health and compliance across Veeam Backup & Replication (VBR) and Veeam Backup for AWS (VBAWS).

## What It Does

- **Multi-Server** -- Monitor multiple VBR and VBAWS servers from a single config with dynamic parallelism
- **Repository Health** -- Monitors repo capacity (including SOBR extents), detects unreachable repos, checks all repo-related sessions for credential/S3/connectivity failures
- **Retention Compliance** -- Validates restore point counts and ages against policy, detects orphaned backups (with Kasten/external policy awareness and configurable exclusions)
- **Worker Health** -- Analyzes VBAWS session failure rates, detects subnet exhaustion, credential failures, and silent retention failures
- **Cross-Correlation** -- Links findings across monitors per-server to surface root causes (e.g., credential expiry causing retention failures)
- **Flexible Output** -- JSON, webhooks (Slack/Teams/PagerDuty/ntfy), Prometheus metrics, email
- **Logging** -- Verbose logging with time-based rotation, disk space alerts, and credential redaction

## Install

```bash
pip install .

# With dev dependencies
pip install -e ".[dev]"
```

Requires Python 3.11+.

## Usage

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

## Exit Codes

| Code | Severity |
|------|----------|
| 0 | OK |
| 1 | WARNING |
| 2 | CRITICAL |
| 3 | ERROR |

The worst severity across all findings determines the exit code, making it CI/CD-friendly.

## Configuration

Copy the example config to get started:

```bash
# Option 1: Default location (current directory)
cp config/example.yaml ./vhc-monitor.yaml

# Option 2: Custom path (pass with -c flag)
cp config/example.yaml /etc/vhc-monitor/config.yaml
vhc-monitor all -c /etc/vhc-monitor/config.yaml
```

Config file resolution order:
1. `-c` / `--config` CLI argument
2. `VHC_MONITOR_CONFIG` environment variable
3. `./vhc-monitor.yaml` in the current directory

### Multi-Server Configuration

Define all servers to monitor in the `servers:` array. Each entry needs a `name`, `type` (`vbr` or `vbaws`), and connection details. Monitors automatically run against the applicable server type (repo-health and retention on VBR, worker-health on VBAWS).

```yaml
servers:
  - name: prod-vbr
    type: vbr
    url: https://vbr-prod:9419
    username: DOMAIN\backupadmin
    password: "secret"
    api_version: "1.3-rev1"
    verify_ssl: false

  - name: dr-vbr
    type: vbr
    url: https://vbr-dr:9419
    username: DOMAIN\backupadmin
    password: "secret"
    verify_ssl: false

  - name: aws-backup
    type: vbaws
    url: https://vbaws-appliance
    username: admin
    password: "secret"
    verify_ssl: false
```

All findings are prefixed with the server name (e.g. `[prod-vbr] repo:Backup Copy Repo`) so you can tell which server reported what.

**Dynamic parallelism:** 2 or fewer servers run sequentially. 3+ servers automatically run in parallel using a thread pool (up to 20 workers). The `serve` command warns if a monitoring cycle is approaching the configured interval.

**Failure isolation:** If one server is unreachable, the others continue running normally.

#### Environment variable fallback

For single-server setups, you can skip the `servers:` array and use environment variables instead:

```
VEEAM_VBR_URL, VEEAM_VBR_USERNAME, VEEAM_VBR_PASSWORD, VEEAM_VBR_API_VERSION
VEEAM_VBAWS_URL, VEEAM_VBAWS_USERNAME, VEEAM_VBAWS_PASSWORD
```

These auto-create server entries named `env-vbr` and `env-vbaws`.

### Retention Exclusions

Suppress specific backups from orphan detection (e.g. known test backups or intentionally retained data):

```yaml
retention:
  exclude_backups:
    - marvinvmtest
    - old-test-backup
    - colombia
```

Names must match exactly as they appear in VBR. Externally managed backups (Kasten policies, etc.) are automatically excluded -- no need to list them here.

See `config/example.yaml` for the full configuration reference including thresholds, output handlers, and error patterns.

## Output Handlers

Output handlers are configured in the `output:` array in your config YAML. You can stack multiple handlers -- all fire on every monitor run.

Each handler that supports `min_severity` will only fire when the worst finding meets or exceeds that threshold (`ok`, `warning`, `critical`, `error`).

### JSON to stdout (default)

Prints formatted JSON results to stdout. Log messages go to stderr so they don't pollute the JSON.

```yaml
output:
  - type: json_stdout
```

### JSON to file

Writes JSON results to a file with optional rotation.

```yaml
output:
  - type: json_file
    path: /var/log/vhc-monitor.json
    rotate: true       # rotate on each run (default: false)
    max_files: 30      # rotated files to keep (default: 30)
```

### ntfy push notifications

Sends push notifications via [ntfy](https://ntfy.sh). Priority and tags are set automatically based on severity.

```yaml
output:
  - type: webhook
    url: https://ntfy.example.com/veeam-alerts
    template: ntfy
    min_severity: warning
```

### Slack

Posts color-coded messages to a Slack channel via incoming webhook.

```yaml
output:
  - type: webhook
    url: https://hooks.slack.com/services/T.../B.../xxx
    template: slack
    min_severity: warning
```

### Microsoft Teams

Posts MessageCard-formatted alerts to a Teams channel.

```yaml
output:
  - type: webhook
    url: https://outlook.office.com/webhook/...
    template: teams
    min_severity: warning
```

### PagerDuty

Sends Events API v2 payloads. Triggers on warning/critical, resolves when OK.

```yaml
output:
  - type: webhook
    url: https://events.pagerduty.com/v2/enqueue
    template: pagerduty
    min_severity: critical
```

### Generic webhook

Posts raw JSON to any endpoint. Use this for custom integrations.

```yaml
output:
  - type: webhook
    url: https://your-api.example.com/veeam-webhook
    template: generic
    min_severity: warning
```

### Prometheus (pushgateway)

Pushes metrics to a Prometheus Pushgateway after each run. Your Prometheus server scrapes the pushgateway.

```yaml
output:
  - type: prometheus
    mode: pushgateway
    url: localhost:9091        # pushgateway address
    job: vhc_monitor         # job label in Prometheus
```

### Prometheus (HTTP server)

Starts an HTTP server exposing a `/metrics` endpoint for Prometheus to scrape directly. Only works with the `vhc-monitor serve` command (long-running mode).

```yaml
output:
  - type: prometheus
    mode: server
    port: 9100                 # port for /metrics endpoint
```

Add a scrape target in your `prometheus.yml`:

```yaml
scrape_configs:
  - job_name: vhc_monitor
    static_configs:
      - targets: ["vhc-monitor-host:9100"]
```

### Email (SMTP)

Sends an HTML email report via SMTP when severity threshold is met.

```yaml
output:
  - type: email
    smtp_host: smtp.example.com
    smtp_port: 587
    from_addr: vhc-monitor@example.com
    to_addrs:
      - ops@example.com
      - backup-team@example.com
    min_severity: critical
    smtp_username: vhc-monitor@example.com   # optional
    smtp_password: app-password-here           # optional
    use_tls: true                              # default: true
```

### Combining multiple outputs

Stack handlers to get terminal output, push notifications, and metrics all at once:

```yaml
output:
  - type: json_stdout
  - type: webhook
    url: https://ntfy.example.com/veeam-alerts
    template: ntfy
    min_severity: warning
  - type: prometheus
    mode: pushgateway
    url: localhost:9091
  - type: email
    smtp_host: smtp.example.com
    from_addr: vhc-monitor@example.com
    to_addrs: ["oncall@example.com"]
    min_severity: critical
```

## Building Standalone Executable

```powershell
# On Windows
.\build.ps1
# Output: dist/vhc-monitor.exe
```

## Docker

```bash
docker build -t vhc-monitor .
docker run -v /path/to/config.yaml:/config/config.yaml vhc-monitor
```

## Testing

```bash
python -m pytest tests/
```

Tests use `respx` for HTTP mocking with fixtures in `tests/fixtures/`.

## Uninstall

**Virtual environment (cleanest):**
```bash
deactivate
rm -rf /path/to/venv
```

**Direct pip install:**
```bash
# Package and direct dependencies
pip uninstall vhc-monitor httpx typer pyyaml prometheus_client rich -y

# Sub-dependencies
pip uninstall httpcore anyio h11 sniffio idna certifi click shellingham typing-extensions markdown-it-py mdurl pygments -y
```

**Docker:**
```bash
docker rmi vhc-monitor
```

**Config and source files:**
```bash
rm -f ./vhc-monitor.yaml
rm -rf /path/to/vhc-monitor/
```
