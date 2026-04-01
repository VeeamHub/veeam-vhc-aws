"""Logging configuration for vhc-monitor."""

import logging
import logging.handlers
import os
import re
import shutil
import sys


_SENSITIVE_PATTERNS = [
    re.compile(r'(password\s*[=:]\s*)[^\s,\'"}\]]+', re.IGNORECASE),
    re.compile(r'(Authorization:\s*(?:Bearer|Basic)\s+)[^\s,\'"}\]]+', re.IGNORECASE),
    re.compile(r'(access_token["\']?\s*[=:]\s*["\']?)[^\s,\'"}\]]+', re.IGNORECASE),
]


class SensitiveFilter(logging.Filter):
    """Redact passwords and tokens from log records."""

    def filter(self, record: logging.LogRecord) -> bool:
        if isinstance(record.msg, str):
            for pattern in _SENSITIVE_PATTERNS:
                record.msg = pattern.sub(r'\1***REDACTED***', record.msg)
        if record.args:
            new_args = []
            for arg in (record.args if isinstance(record.args, tuple) else (record.args,)):
                if isinstance(arg, str):
                    for pattern in _SENSITIVE_PATTERNS:
                        arg = pattern.sub(r'\1***REDACTED***', arg)
                new_args.append(arg)
            record.args = tuple(new_args)
        return True


def _check_disk_space(log_dir: str, warning_mb: int) -> None:
    """Emit a warning if disk free space is below threshold."""
    try:
        usage = shutil.disk_usage(log_dir)
        free_mb = usage.free / (1024 * 1024)
        if free_mb < warning_mb:
            logger = logging.getLogger("vhc_monitor")
            logger.warning(
                "Log directory disk space low: %.0f MB free (threshold: %d MB) at %s",
                free_mb, warning_mb, log_dir,
            )
    except OSError:
        pass


def setup_logging(config: dict) -> None:
    """Configure logging from the global config section.

    Config keys under global.logging (defaults provided by config.py _DEFAULTS):
        level, file, rotation_when, rotation_interval, rotation_keep, console, disk_warning_mb
    """
    global_cfg = config.get("global", {})
    log_cfg = global_cfg.get("logging", {})

    level_name = log_cfg["level"].upper()
    level = getattr(logging, level_name, logging.DEBUG)

    log_file = log_cfg["file"]
    rotation_when = log_cfg["rotation_when"]
    rotation_interval = log_cfg["rotation_interval"]
    rotation_keep = log_cfg["rotation_keep"]
    console_enabled = log_cfg["console"]
    disk_warning_mb = log_cfg["disk_warning_mb"]

    formatter = logging.Formatter(
        "%(asctime)s [%(levelname)-8s] %(name)s: %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    root_logger = logging.getLogger("vhc_monitor")
    root_logger.setLevel(level)
    root_logger.handlers.clear()

    sensitive_filter = SensitiveFilter()

    # File handler with time-based rotation
    log_dir = os.path.dirname(os.path.abspath(log_file))
    try:
        os.makedirs(log_dir, exist_ok=True)
        file_handler = logging.handlers.TimedRotatingFileHandler(
            filename=log_file,
            when=rotation_when,
            interval=rotation_interval,
            backupCount=rotation_keep,
            encoding="utf-8",
        )
        file_handler.setFormatter(formatter)
        file_handler.addFilter(sensitive_filter)
        root_logger.addHandler(file_handler)
    except (PermissionError, OSError) as e:
        # Fall back to stderr if the log file can't be opened (e.g. non-elevated run)
        stderr_fallback = logging.StreamHandler(sys.stderr)
        stderr_fallback.setFormatter(formatter)
        stderr_fallback.addFilter(sensitive_filter)
        root_logger.addHandler(stderr_fallback)
        root_logger.warning(
            "Cannot write log file '%s' (%s). Logging to stderr instead. "
            "Run with admin/elevated privileges to write to this path.",
            log_file, e,
        )

    # Console handler — stderr so it doesn't pollute JSON stdout output
    if console_enabled:
        console_handler = logging.StreamHandler(sys.stderr)
        console_handler.setFormatter(formatter)
        console_handler.addFilter(sensitive_filter)
        console_handler.setLevel(level)
        root_logger.addHandler(console_handler)

    # Check disk space on startup
    _check_disk_space(log_dir, disk_warning_mb)

    # Suppress third-party HTTP loggers that may leak credentials in request bodies
    logging.getLogger("httpx").setLevel(logging.WARNING)
    logging.getLogger("httpcore").setLevel(logging.WARNING)

    root_logger.debug("Logging initialized: level=%s, file=%s, rotation=%s every %d, keep=%d",
                      level_name, log_file, rotation_when, rotation_interval, rotation_keep)
