"""Configuration loading with YAML and environment variable overlay."""

import logging
import os
from typing import Optional

import yaml

logger = logging.getLogger("vhc_monitor.config")


# Env var → single-server convenience mapping
# When set, these create a single-server entry if no servers: array exists
_SINGLE_SERVER_ENV_VARS = {
    "vbr": {
        "VEEAM_VBR_URL": "url",
        "VEEAM_VBR_USERNAME": "username",
        "VEEAM_VBR_PASSWORD": "password",
        "VEEAM_VBR_API_VERSION": "api_version",
    },
    "vbaws": {
        "VEEAM_VBAWS_URL": "url",
        "VEEAM_VBAWS_USERNAME": "username",
        "VEEAM_VBAWS_PASSWORD": "password",
    },
}

# Default thresholds applied when not specified in config
_DEFAULTS: dict = {
    "global": {
        "log_level": "DEBUG",
        "timeout_seconds": 30,
        "retry_count": 2,
        "retry_delay_seconds": 5,
        "logging": {
            "level": "DEBUG",
            "file": "./vhc-monitor.log",
            "rotation_when": "midnight",
            "rotation_interval": 1,
            "rotation_keep": 30,
            "console": True,
            "disk_warning_mb": 500,
        },
    },
    "repo_health": {
        "enabled": True,
        "thresholds": {
            "free_space_warning_pct": 15,
            "free_space_critical_pct": 5,
            "rescan_on_unhealthy": True,
        },
        "include_external_repos": True,
        "check_external_maintenance": True,
        "external_maintenance_lookback_hours": 48,
    },
    "retention": {
        "enabled": True,
        "thresholds": {
            "overage_multiplier": 1.5,
            "max_age_multiplier": 1.5,
            "orphan_detection": True,
        },
    },
    "worker_health": {
        "enabled": True,
        "lookback_hours": 24,
        "thresholds": {
            "session_failure_rate_warning": 0.1,
            "session_failure_rate_critical": 0.3,
            "retention_session_max_failures": 1,
            "zero_deleted_items_warning": True,
            "recurring_failure_threshold": 3,
        },
    },
    "output": [{"type": "json_stdout"}],
}


def _deep_merge(base: dict, overlay: dict) -> dict:
    """Merge overlay into base, recursively for dicts."""
    result = base.copy()
    for key, value in overlay.items():
        if key in result and isinstance(result[key], dict) and isinstance(value, dict):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = value
    return result


def _apply_env_servers(config: dict) -> None:
    """If no servers: array in config, check env vars for single-server convenience."""
    if config.get("servers"):
        return

    servers = []
    for server_type, env_map in _SINGLE_SERVER_ENV_VARS.items():
        server_cfg: dict = {}
        for env_var, key in env_map.items():
            value = os.environ.get(env_var)
            if value is not None:
                server_cfg[key] = value

        if server_cfg.get("url"):
            server_cfg["name"] = f"env-{server_type}"
            server_cfg["type"] = server_type
            servers.append(server_cfg)
            logger.info("Server '%s' configured from environment variables", server_cfg["name"])

    if servers:
        config["servers"] = servers


def load_config(path: str) -> dict:
    """Load YAML config from path, apply defaults, then check env var fallback."""
    logger.info("Loading config from %s", path)
    with open(path, "r") as f:
        raw = yaml.safe_load(f) or {}

    # Apply defaults for missing sections
    config = _deep_merge(_DEFAULTS, raw)

    # If no servers array, check env vars for single-server convenience
    _apply_env_servers(config)

    return config


def get_config_path(config_arg: Optional[str] = None) -> str:
    """Resolve config file path: explicit arg > env var > default."""
    if config_arg:
        logger.debug("Config path from CLI argument: %s", config_arg)
        return config_arg

    env_path = os.environ.get("VHC_MONITOR_CONFIG")
    if env_path:
        logger.debug("Config path from VHC_MONITOR_CONFIG env var: %s", env_path)
        return env_path

    logger.debug("Using default config path: ./vhc-monitor.yaml")
    return "./vhc-monitor.yaml"
