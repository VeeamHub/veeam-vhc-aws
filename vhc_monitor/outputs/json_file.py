"""JSON file output handler with optional rotation."""

import json
import os
import shutil

from vhc_monitor.core.output import OutputHandler
from vhc_monitor.core.models import MonitorResult


class JsonFileHandler(OutputHandler):
    """Writes monitor results as JSON to a file, with optional rotation."""

    def __init__(
        self,
        path: str = "/var/log/vhc-monitor.json",
        rotate: bool = False,
        max_files: int = 30,
    ) -> None:
        self.path = path
        self.rotate = rotate
        self.max_files = max_files

    def _rotate_files(self) -> None:
        """Rotate existing log files, removing oldest beyond max_files."""
        if not os.path.exists(self.path):
            return

        # Remove the oldest rotated file if at limit
        for i in range(self.max_files - 1, 0, -1):
            src = f"{self.path}.{i}"
            dst = f"{self.path}.{i + 1}"
            if os.path.exists(src):
                if i + 1 > self.max_files:
                    os.remove(src)
                else:
                    shutil.move(src, dst)

        # Rotate current file to .1
        if os.path.exists(self.path):
            shutil.move(self.path, f"{self.path}.1")

    def emit(self, results: list[MonitorResult]) -> None:
        """Write results as JSON to file, rotating if configured."""
        if self.rotate:
            self._rotate_files()

        output = [r.to_dict() for r in results]

        # Ensure parent directory exists
        parent = os.path.dirname(self.path)
        if parent:
            os.makedirs(parent, exist_ok=True)

        with open(self.path, "w") as f:
            json.dump(output, f, indent=2, default=str)
            f.write("\n")
