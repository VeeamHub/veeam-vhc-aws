"""JSON stdout output handler."""

import json
import sys

from vhc_monitor.core.output import OutputHandler
from vhc_monitor.core.models import MonitorResult


class JsonStdoutHandler(OutputHandler):
    """Writes monitor results as JSON to stdout."""

    def emit(self, results: list[MonitorResult]) -> None:
        """Print results as formatted JSON to stdout."""
        output = [r.to_dict() for r in results]
        json.dump(output, sys.stdout, indent=2, default=str)
        sys.stdout.write("\n")
        sys.stdout.flush()
