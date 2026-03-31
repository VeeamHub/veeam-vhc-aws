"""Output handler registry."""

from .json_stdout import JsonStdoutHandler
from .json_file import JsonFileHandler
from .webhook import WebhookHandler
from .prometheus import PrometheusHandler
from .email import EmailHandler

HANDLER_REGISTRY: dict[str, type] = {
    "json_stdout": JsonStdoutHandler,
    "json_file": JsonFileHandler,
    "webhook": WebhookHandler,
    "prometheus": PrometheusHandler,
    "email": EmailHandler,
}

__all__ = [
    "HANDLER_REGISTRY",
    "JsonStdoutHandler",
    "JsonFileHandler",
    "WebhookHandler",
    "PrometheusHandler",
    "EmailHandler",
]
