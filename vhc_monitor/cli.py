"""VHC monitoring CLI built with Typer."""

from __future__ import annotations

import json
import logging
import os
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

from vhc_monitor import __version__
from vhc_monitor.core.config import load_config, get_config_path
from vhc_monitor.core.logging import setup_logging
from vhc_monitor.core.models import MonitorResult, MonitorType, Severity, Finding
from vhc_monitor.core.auth import VBRAuth, VBAWSAuth
from vhc_monitor.core.client_vbr import VBRClient
from vhc_monitor.core.client_vbaws import VBAWSClient
from vhc_monitor.core.client_em import EMClient
from vhc_monitor.core.patterns import PatternEngine
from vhc_monitor.core.output import OutputDispatcher, create_handlers
from vhc_monitor.core.state import FindingState

logger = logging.getLogger("vhc_monitor.cli")

app = typer.Typer(name="vhc-monitor", help="VHC monitoring toolkit")
console = Console()

_SEVERITY_EXIT_CODES = {
    Severity.OK: 0,
    Severity.WARNING: 1,
    Severity.CRITICAL: 2,
    Severity.ERROR: 3,
}

# Dynamic parallelism threshold — servers above this count run in parallel
_PARALLEL_THRESHOLD = 2


@dataclass
class ServerContext:
    """Holds a named server's clients for monitor execution."""
    name: str
    server_type: str
    vbr_client: Optional[VBRClient] = None
    vbaws_client: Optional[VBAWSClient] = None
    em_client: Optional[EMClient] = None


def _worst_exit_code(results: list[MonitorResult]) -> int:
    """Determine exit code from worst severity across all results."""
    worst = 0
    for result in results:
        code = _SEVERITY_EXIT_CODES.get(result.overall_severity, 0)
        if code > worst:
            worst = code
        for finding in result.findings:
            code = _SEVERITY_EXIT_CODES.get(finding.severity, 0)
            if code > worst:
                worst = code
    return worst


def _build_server_context(server_cfg: dict, global_cfg: dict) -> ServerContext:
    """Build a ServerContext from a single server config entry."""
    name = server_cfg["name"]
    server_type = server_cfg.get("type", "vbr").lower()
    url = server_cfg.get("url", "")
    username = server_cfg.get("username", "")
    password = server_cfg.get("password", "")
    verify_ssl = server_cfg.get("verify_ssl", True)
    timeout = global_cfg.get("timeout_seconds", 30)
    retry_count = global_cfg.get("retry_count", 2)
    retry_delay = global_cfg.get("retry_delay_seconds", 5)

    ctx = ServerContext(name=name, server_type=server_type, em_client=EMClient())

    if server_type == "vbr":
        api_version = server_cfg.get("api_version", "1.3-rev1")
        auth = VBRAuth(
            base_url=url, username=username, password=password,
            api_version=api_version, verify_ssl=verify_ssl,
        )
        ctx.vbr_client = VBRClient(
            base_url=url, auth=auth, verify_ssl=verify_ssl,
            timeout=timeout, retry_count=retry_count, retry_delay=retry_delay,
        )
    elif server_type == "vbaws":
        auth = VBAWSAuth(
            base_url=url, username=username, password=password,
            verify_ssl=verify_ssl,
        )
        ctx.vbaws_client = VBAWSClient(
            base_url=url, auth=auth, verify_ssl=verify_ssl,
            timeout=timeout, retry_count=retry_count, retry_delay=retry_delay,
        )

    return ctx


def _build_all_servers(config: dict) -> list[ServerContext]:
    """Build ServerContext for each entry in config['servers']."""
    servers_cfg = config.get("servers") or []
    global_cfg = config.get("global", {})
    contexts = []
    for server_cfg in servers_cfg:
        if not server_cfg.get("name") or not server_cfg.get("url"):
            logger.warning("Skipping server entry missing name or url: %s", server_cfg)
            continue
        try:
            ctx = _build_server_context(server_cfg, global_cfg)
            contexts.append(ctx)
            logger.debug("Server '%s' (%s) configured at %s",
                         ctx.name, ctx.server_type, server_cfg.get("url"))
        except Exception as e:
            logger.error("Failed to configure server '%s': %s",
                         server_cfg.get("name", "unknown"), e)
    return contexts


def _build_pattern_engine(config: dict) -> Optional[PatternEngine]:
    """Build a PatternEngine from config error_patterns section."""
    patterns_cfg = config.get("error_patterns", [])
    if not patterns_cfg:
        return None
    return PatternEngine.from_config(patterns_cfg)


def _prefix_findings(result: MonitorResult, server_name: str) -> MonitorResult:
    """Prefix all finding resources with the server name."""
    for f in result.findings:
        f.resource = f"[{server_name}] {f.resource}"
    result.server = server_name
    return result


def _run_monitor(
    monitor_cls: type,
    config: dict,
    server_ctx: ServerContext,
    pattern_engine: Optional[PatternEngine] = None,
) -> MonitorResult:
    """Instantiate and run a monitor for a specific server."""
    monitor_name = monitor_cls.__name__
    logger.info("Starting %s on server '%s'", monitor_name, server_ctx.name)
    monitor = monitor_cls(
        config=config,
        vbr_client=server_ctx.vbr_client,
        vbaws_client=server_ctx.vbaws_client,
        em_client=server_ctx.em_client,
        pattern_engine=pattern_engine,
    )
    result = monitor.run()
    result = _prefix_findings(result, server_ctx.name)
    logger.info(
        "%s on '%s' completed: severity=%s, findings=%d, errors=%d, duration=%dms",
        monitor_name, server_ctx.name, result.overall_severity.value,
        len(result.findings), len(result.errors), result.duration_ms,
    )
    return result


def _get_applicable_monitors(
    monitor_filter: Optional[str],
    server_ctx: ServerContext,
    monitor_registry: dict,
    config: dict,
) -> list[tuple[str, type]]:
    """Get monitors applicable to a server based on its type and config."""
    # Which monitors need which server type
    monitor_server_types = {
        "repo_health": "vbr",
        "retention": "vbr",
        "worker_health": "vbaws",
    }

    applicable = []
    for name, cls in monitor_registry.items():
        # If a specific monitor was requested, only run that one
        if monitor_filter and name != monitor_filter:
            continue

        # Check if this monitor applies to this server type
        required_type = monitor_server_types.get(name)
        if required_type and required_type != server_ctx.server_type:
            continue

        # Check if monitor is enabled in config
        monitor_cfg = config.get(name, {})
        if not monitor_cfg.get("enabled", True):
            continue

        applicable.append((name, cls))

    return applicable


def _run_monitors_for_server(
    server_ctx: ServerContext,
    config: dict,
    pattern_engine: Optional[PatternEngine],
    monitor_filter: Optional[str],
    monitor_registry: dict,
) -> list[MonitorResult]:
    """Run all applicable monitors for a single server, catching errors."""
    applicable = _get_applicable_monitors(
        monitor_filter, server_ctx, monitor_registry, config
    )

    if not applicable:
        return []

    results = []
    for name, cls in applicable:
        try:
            result = _run_monitor(cls, config, server_ctx, pattern_engine)

            # If a monitor returned errors but severity is OK, it means
            # API calls failed but the monitor didn't escalate — fix that
            if result.errors and result.overall_severity == Severity.OK:
                result.overall_severity = Severity.WARNING
                for error_msg in result.errors:
                    result.findings.insert(0, Finding(
                        severity=Severity.WARNING,
                        resource=f"[{server_ctx.name}] connection",
                        message=error_msg[:200],
                    ))

            results.append(result)
        except Exception as e:
            logger.error("Monitor %s failed on server '%s': %s", name, server_ctx.name, e)
            # Create an ERROR result so the failure is visible in output/alerts
            results.append(MonitorResult(
                monitor=MonitorType(name) if name in [t.value for t in MonitorType] else MonitorType.REPO_HEALTH,
                timestamp=datetime.now(timezone.utc),
                duration_ms=0,
                overall_severity=Severity.ERROR,
                findings=[Finding(
                    severity=Severity.ERROR,
                    resource=f"[{server_ctx.name}] connection",
                    message=f"Server unreachable: {str(e)[:150]}",
                )],
                server=server_ctx.name,
                errors=[str(e)],
            ))
    return results


def _run_all_servers(
    servers: list[ServerContext],
    config: dict,
    pattern_engine: Optional[PatternEngine],
    monitor_filter: Optional[str] = None,
) -> list[MonitorResult]:
    """Run monitors across all servers with dynamic parallelism."""
    from vhc_monitor.monitors import MONITOR_REGISTRY

    all_results: list[MonitorResult] = []

    if len(servers) > _PARALLEL_THRESHOLD:
        logger.info("Running %d servers in parallel (threshold=%d)",
                     len(servers), _PARALLEL_THRESHOLD)
        max_workers = min(len(servers), 20)
        with ThreadPoolExecutor(max_workers=max_workers) as executor:
            futures = {
                executor.submit(
                    _run_monitors_for_server,
                    ctx, config, pattern_engine, monitor_filter, MONITOR_REGISTRY,
                ): ctx.name
                for ctx in servers
            }
            for future in as_completed(futures):
                server_name = futures[future]
                try:
                    all_results.extend(future.result())
                except Exception as e:
                    logger.error("Server '%s' failed entirely: %s", server_name, e)
    else:
        for ctx in servers:
            results = _run_monitors_for_server(
                ctx, config, pattern_engine, monitor_filter, MONITOR_REGISTRY,
            )
            all_results.extend(results)

    return all_results


def _cross_correlate(results: list[MonitorResult]) -> list[MonitorResult]:
    """Apply cross-correlation rules across monitor results, per server."""
    # Group results by server
    by_server: dict[str, list[MonitorResult]] = {}
    for r in results:
        by_server.setdefault(r.server, []).append(r)

    for server_name, server_results in by_server.items():
        all_findings = []
        for r in server_results:
            for f in r.findings:
                all_findings.append((r.monitor.value, f))

        correlations: list[Finding] = []

        has_credential_failure = any(
            "credential" in f.message.lower() or "access key" in f.message.lower()
            for _, f in all_findings
        )
        has_retention_failure = any(
            m == MonitorType.WORKER_HEALTH.value and f.severity in (Severity.CRITICAL, Severity.ERROR)
            for m, f in all_findings
        )
        if has_credential_failure and has_retention_failure:
            correlations.append(Finding(
                severity=Severity.CRITICAL,
                resource=f"[{server_name}] cross-correlation",
                message="Credential expiry cascade — credential failure is likely causing retention failures",
                details={"correlation": "M1+M3"},
            ))

        has_retention_violation = any(
            m == MonitorType.RETENTION.value and f.severity in (Severity.WARNING, Severity.CRITICAL)
            for m, f in all_findings
        )
        has_subnet_exhaustion = any(
            "subnet" in f.message.lower() and "exhaustion" in f.message.lower()
            for _, f in all_findings
        )
        if has_retention_violation and has_subnet_exhaustion:
            correlations.append(Finding(
                severity=Severity.CRITICAL,
                resource=f"[{server_name}] cross-correlation",
                message="Root cause: subnet IP exhaustion — workers cannot deploy, causing retention violations",
                details={"correlation": "M2+M3"},
            ))

        has_orphaned = any(
            "orphan" in f.message.lower()
            for _, f in all_findings
        )
        if has_orphaned:
            correlations.append(Finding(
                severity=Severity.WARNING,
                resource=f"[{server_name}] cross-correlation",
                message="No active job for cleanup — orphaned backups detected without associated jobs",
                details={"correlation": "M2"},
            ))

        if correlations:
            results.append(MonitorResult(
                monitor=MonitorType.CROSS_CORRELATION,
                timestamp=datetime.now(timezone.utc),
                duration_ms=0,
                overall_severity=max(
                    (c.severity for c in correlations),
                    key=lambda s: _SEVERITY_EXIT_CODES.get(s, 0),
                ),
                findings=correlations,
                server=server_name,
                metadata={"type": "cross_correlation"},
            ))

    return results


def _load_and_setup(
    config_arg: Optional[str],
) -> tuple[dict, list[ServerContext], Optional[PatternEngine], OutputDispatcher, FindingState]:
    """Load config and build all server contexts and handlers."""
    config_path = get_config_path(config_arg)
    config = load_config(config_path)
    setup_logging(config)
    logger.info("vhc-monitor v%s starting", __version__)
    servers = _build_all_servers(config)
    pattern_engine = _build_pattern_engine(config)
    handlers = create_handlers(config)
    dispatcher = OutputDispatcher(handlers)
    state_file = config.get("global", {}).get("state_file", "./vhc-monitor-state.json")
    state = FindingState(state_file)
    logger.info("Configured %d servers, %d output handlers",
                len(servers), len(handlers))
    return config, servers, pattern_engine, dispatcher, state


def _emit_with_state(
    dispatcher: OutputDispatcher, results: list[MonitorResult], state: FindingState,
) -> None:
    """Process results through state tracking, then emit."""
    results, resolved = state.process_results(results)

    if resolved:
        logger.info("%d findings resolved", len(resolved))

    # Emit current results (with _seen_before markers for dedup)
    dispatcher.emit(results)

    # If there are resolved findings, emit a separate resolved notification
    if resolved:
        resolved_result = MonitorResult(
            monitor=MonitorType.CROSS_CORRELATION,
            timestamp=datetime.now(timezone.utc),
            duration_ms=0,
            overall_severity=Severity.OK,
            findings=resolved,
            server="all",
            metadata={"type": "resolved"},
        )
        dispatcher.emit([resolved_result])


def _run_single_monitor(
    config_arg: Optional[str], monitor_name: str, no_servers_msg: str,
) -> None:
    """Run a specific monitor on all applicable servers."""
    try:
        cfg, servers, pe, dispatcher, state = _load_and_setup(config_arg)
    except Exception as e:
        typer.echo(f"ERROR: Startup failed: {e}", err=True)
        raise typer.Exit(3)
    results = _run_all_servers(servers, cfg, pe, monitor_filter=monitor_name)
    if not results:
        console.print(f"[yellow]{no_servers_msg}[/yellow]")
        raise typer.Exit(0)
    _emit_with_state(dispatcher, results, state)
    raise typer.Exit(_worst_exit_code(results))


@app.command()
def repo_health(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Run the repository health monitor on all VBR servers."""
    _run_single_monitor(config, "repo_health", "No VBR servers configured for repo-health.")


@app.command()
def retention(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Run the retention monitor on all VBR servers."""
    _run_single_monitor(config, "retention", "No VBR servers configured for retention.")


@app.command()
def worker_health(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Run the worker health monitor on all VBAWS servers."""
    _run_single_monitor(config, "worker_health", "No VBAWS servers configured for worker-health.")


@app.command(name="all")
def run_all(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Run all enabled monitors on all servers, cross-correlate findings."""
    try:
        cfg, servers, pe, dispatcher, state = _load_and_setup(config)
    except Exception as e:
        typer.echo(f"ERROR: Startup failed: {e}", err=True)
        raise typer.Exit(3)

    if not servers:
        console.print("[yellow]No servers configured.[/yellow]")
        raise typer.Exit(0)

    results = _run_all_servers(servers, cfg, pe)
    results = _cross_correlate(results)
    _emit_with_state(dispatcher, results, state)
    raise typer.Exit(_worst_exit_code(results))


@app.command()
def serve(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
    port: int = typer.Option(9100, "--port", "-p", help="Prometheus HTTP server port"),
    interval: int = typer.Option(300, "--interval", "-i", help="Scrape interval in seconds"),
) -> None:
    """Start Prometheus HTTP server and run monitors periodically on all servers."""
    from prometheus_client import start_http_server as prom_start

    cfg, servers, pe, dispatcher, state = _load_and_setup(config)

    console.print(f"Starting Prometheus HTTP server on port {port}")
    prom_start(port)

    console.print(f"Monitoring {len(servers)} servers every {interval}s. Press Ctrl+C to stop.")
    while True:
        cycle_start = time.monotonic()
        results = _run_all_servers(servers, cfg, pe)
        results = _cross_correlate(results)
        _emit_with_state(dispatcher, results, state)
        cycle_duration = time.monotonic() - cycle_start
        logger.info("Monitor cycle completed in %.1fs for %d servers",
                    cycle_duration, len(servers))
        if cycle_duration > interval * 0.8:
            logger.warning(
                "Monitor cycle (%.1fs) approaching interval (%ds) — "
                "consider increasing interval or reducing server count",
                cycle_duration, interval,
            )
        time.sleep(max(0, interval - cycle_duration))


@app.command()
def test_connection(
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Test connectivity to all configured servers."""
    config_path = get_config_path(config)
    cfg = load_config(config_path)
    setup_logging(cfg)
    servers = _build_all_servers(cfg)

    table = Table(title="Connection Test Results")
    table.add_column("Server", style="bold")
    table.add_column("Type")
    table.add_column("Status")
    table.add_column("Details")

    for ctx in servers:
        if ctx.vbr_client:
            try:
                info = ctx.vbr_client.get_server_info()
                table.add_row(
                    ctx.name, "VBR",
                    "[green]Connected[/green]",
                    json.dumps(info, indent=2)[:200],
                )
            except Exception as e:
                table.add_row(ctx.name, "VBR", "[red]Failed[/red]", str(e)[:200])
        elif ctx.vbaws_client:
            try:
                sessions = ctx.vbaws_client.get_health_check_sessions()
                table.add_row(
                    ctx.name, "VBAWS",
                    "[green]Connected[/green]",
                    f"Retrieved {len(sessions)} health check sessions",
                )
            except Exception as e:
                table.add_row(ctx.name, "VBAWS", "[red]Failed[/red]", str(e)[:200])

    if not servers:
        console.print("[yellow]No servers configured.[/yellow]")
    else:
        console.print(table)


@app.command()
def diagnose(
    list_patterns: bool = typer.Option(False, "--list-patterns", "-l", help="List all known error patterns"),
    match: Optional[str] = typer.Option(None, "--match", "-m", help="Match an error string against patterns"),
    config: Optional[str] = typer.Option(None, "--config", "-c", help="Path to config file"),
) -> None:
    """Show known error patterns or match an error string against them."""
    config_path = get_config_path(config)
    cfg = load_config(config_path)
    pe = _build_pattern_engine(cfg)

    if pe is None:
        console.print("[yellow]No error patterns configured.[/yellow]")
        raise typer.Exit(0)

    if list_patterns:
        table = Table(title="Known Error Patterns")
        table.add_column("Category", style="bold")
        table.add_column("Severity")
        table.add_column("Pattern")
        table.add_column("Message")
        table.add_column("Source")

        for pattern in pe._patterns:
            sev_style = {
                Severity.OK: "green",
                Severity.WARNING: "yellow",
                Severity.CRITICAL: "red",
                Severity.ERROR: "red bold",
            }.get(pattern.severity, "white")

            table.add_row(
                pattern.category,
                f"[{sev_style}]{pattern.severity.value}[/{sev_style}]",
                pattern.pattern[:60],
                pattern.message[:80],
                pattern.source,
            )

        console.print(table)

    if match:
        result = pe.classify(match)
        if result:
            console.print(f"\n[bold]Match found:[/bold]")
            console.print(f"  Category: {result.category}")
            console.print(f"  Severity: {result.severity.value}")
            console.print(f"  Message:  {result.message}")
            if result.source:
                console.print(f"  Source:   {result.source}")
            if result.log_hint:
                console.print(f"  Log hint: {result.log_hint}")
            if result.remediation:
                console.print(f"  Fix:      {result.remediation}")

            extracted = pe.extract(match, result)
            if extracted:
                console.print(f"  Extracted: {json.dumps(extracted)}")
        else:
            console.print("[yellow]No matching pattern found.[/yellow]")


@app.command()
def version() -> None:
    """Print the vhc-monitor version."""
    typer.echo(f"vhc-monitor v{__version__}")


@app.command()
def setup(
    config_path: Optional[str] = typer.Option(None, "--output", "-o", help="Config file output path"),
) -> None:
    """Interactive first-time setup — creates a config file."""
    import shutil

    if config_path is None:
        config_path = "./vhc-monitor.yaml"

    if os.path.exists(config_path):
        overwrite = typer.confirm(f"Config file {config_path} already exists. Overwrite?", default=False)
        if not overwrite:
            console.print("[yellow]Setup cancelled.[/yellow]")
            raise typer.Exit(0)

    # Find bundled example config (check PyInstaller _MEIPASS, then relative paths)
    base_dirs = [os.path.dirname(os.path.dirname(__file__))]
    if getattr(sys, '_MEIPASS', None):
        base_dirs.insert(0, sys._MEIPASS)
    base_dirs.append(os.path.dirname(sys.executable))

    example_candidates = []
    for base in base_dirs:
        example_candidates.append(os.path.join(base, "config", "example.yaml"))
        example_candidates.append(os.path.join(base, "example.yaml"))

    example_path = None
    for candidate in example_candidates:
        if os.path.exists(candidate):
            example_path = candidate
            break

    if example_path:
        shutil.copy2(example_path, config_path)
        console.print(f"\n[green]Config file created:[/green] {os.path.abspath(config_path)}")
    else:
        # Generate minimal config inline
        minimal = """# vhc-monitor.yaml — Edit this file with your server details
global:
  timeout_seconds: 30
  retry_count: 2
  logging:
    level: INFO
    file: ./vhc-monitor.log

servers:
  - name: my-vbr
    type: vbr
    url: https://vbr-server:9419
    username: DOMAIN\\\\backupadmin
    password: ""
    api_version: "1.3-rev1"
    verify_ssl: false

  # Uncomment for VBAWS:
  # - name: my-vbaws
  #   type: vbaws
  #   url: https://vbaws-appliance
  #   username: admin
  #   password: ""
  #   verify_ssl: false

output:
  - type: json_stdout

repo_health:
  enabled: true
  thresholds:
    free_space_warning_pct: 15
    free_space_critical_pct: 5

retention:
  enabled: true
  thresholds:
    overage_multiplier: 1.5
    orphan_detection: true

worker_health:
  enabled: true
  lookback_hours: 24
"""
        with open(config_path, "w") as f:
            f.write(minimal)
        console.print(f"\n[green]Config file created:[/green] {os.path.abspath(config_path)}")

    console.print("\n[bold]Next steps:[/bold]")
    console.print(f"  1. Edit [cyan]{config_path}[/cyan] with your server details")
    console.print(f"  2. Test connectivity:  [cyan]vhc-monitor test-connection -c {config_path}[/cyan]")
    console.print(f"  3. Run all monitors:   [cyan]vhc-monitor all -c {config_path}[/cyan]")
    console.print()


@app.callback(invoke_without_command=True)
def main(ctx: typer.Context) -> None:
    """VHC monitoring toolkit for Veeam backup infrastructure."""
    if ctx.invoked_subcommand is not None:
        return

    # No subcommand — show friendly welcome (useful when double-clicking exe)
    console.print(f"\n[bold cyan]vhc-monitor v{__version__}[/bold cyan]")
    console.print("Continuous monitoring for Veeam backup infrastructure.\n")

    # Check if config exists
    config_exists = os.path.exists("./vhc-monitor.yaml")

    if config_exists:
        console.print("[green]Config found:[/green] ./vhc-monitor.yaml\n")
        console.print("Commands:")
        console.print("  [cyan]vhc-monitor all -c vhc-monitor.yaml[/cyan]     Run all monitors")
        console.print("  [cyan]vhc-monitor test-connection -c vhc-monitor.yaml[/cyan]  Test connectivity")
        console.print("  [cyan]vhc-monitor serve -c vhc-monitor.yaml[/cyan]   Start Prometheus server")
        console.print("  [cyan]vhc-monitor --help[/cyan]                      Show all commands")
    else:
        console.print("[yellow]No config file found.[/yellow]\n")
        console.print("Get started:")
        console.print("  [cyan]vhc-monitor setup[/cyan]          Create a config file")
        console.print("  [cyan]vhc-monitor --help[/cyan]         Show all commands")

    console.print()

    # If running as exe (not in a terminal with args), pause so window stays open
    if getattr(sys, 'frozen', False):
        console.input("[dim]Press Enter to exit...[/dim]")
