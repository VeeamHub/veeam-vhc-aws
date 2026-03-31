"""Output handler framework for dispatching monitor results."""

from abc import ABC, abstractmethod
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from .models import MonitorResult


class OutputHandler(ABC):
    """Abstract base class for output handlers."""

    @abstractmethod
    def emit(self, results: list["MonitorResult"]) -> None:
        """Emit monitor results to the configured destination."""
        ...


class OutputDispatcher:
    """Dispatches monitor results to multiple output handlers."""

    def __init__(self, handlers: list[OutputHandler]) -> None:
        self._handlers = handlers

    def emit(self, results: list["MonitorResult"]) -> None:
        """Call emit() on each registered handler."""
        for handler in self._handlers:
            handler.emit(results)


def create_handlers(config: dict) -> list[OutputHandler]:
    """
    Factory function to create output handlers from the config's output section.

    Config format:
        output:
          - type: json_stdout
          - type: json_file
            path: /var/log/vhc-monitor.json
          - type: webhook
            url: https://hooks.slack.com/...
            template: slack
    """
    from vhc_monitor.outputs import HANDLER_REGISTRY

    output_configs = config.get("output", [{"type": "json_stdout"}])
    handlers: list[OutputHandler] = []

    for entry in output_configs:
        handler_type = entry.get("type", "json_stdout")
        handler_cls = HANDLER_REGISTRY.get(handler_type)
        if handler_cls is None:
            raise ValueError(f"Unknown output handler type: {handler_type}")

        # Pass all config keys except 'type' as kwargs
        kwargs = {k: v for k, v in entry.items() if k != "type"}
        handlers.append(handler_cls(**kwargs))

    return handlers
