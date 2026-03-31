"""Core module — exports key classes for convenience."""

from .models import Severity, MonitorType, Finding, MonitorResult
from .patterns import ErrorPattern, PatternEngine
from .config import load_config, get_config_path
from .auth import (
    CredentialProvider,
    EnvVarProvider,
    ConfigFileProvider,
    ChainedProvider,
    VBRAuth,
    VBAWSAuth,
)

__all__ = [
    "Severity",
    "MonitorType",
    "Finding",
    "MonitorResult",
    "ErrorPattern",
    "PatternEngine",
    "load_config",
    "get_config_path",
    "CredentialProvider",
    "EnvVarProvider",
    "ConfigFileProvider",
    "ChainedProvider",
    "VBRAuth",
    "VBAWSAuth",
]
