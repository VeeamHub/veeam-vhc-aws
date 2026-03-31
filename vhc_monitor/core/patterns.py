"""Error pattern engine for classifying and extracting information from error messages."""

import re
from dataclasses import dataclass, field
from typing import Optional

from .models import Severity


@dataclass
class ErrorPattern:
    pattern: str
    severity: Severity
    message: str
    category: str
    extract_fields: list[str] = field(default_factory=list)
    source: str = ""
    log_hint: str = ""
    remediation: str = ""


class PatternEngine:
    """Matches error text against a list of ErrorPatterns."""

    def __init__(self, patterns: list[ErrorPattern]) -> None:
        self._patterns = patterns
        self._compiled: list[tuple[re.Pattern[str], ErrorPattern]] = [
            (re.compile(p.pattern, re.IGNORECASE), p) for p in patterns
        ]

    def classify(self, error_text: str) -> Optional[ErrorPattern]:
        """Return the first matching ErrorPattern, or None."""
        for compiled, pattern in self._compiled:
            if compiled.search(error_text):
                return pattern
        return None

    def extract(self, error_text: str, pattern: ErrorPattern) -> dict:
        """Extract named regex groups from the error text using the pattern."""
        compiled = re.compile(pattern.pattern, re.IGNORECASE)
        match = compiled.search(error_text)
        if match:
            return {
                k: v
                for k, v in match.groupdict().items()
                if k in pattern.extract_fields
            }
        return {}

    @classmethod
    def from_config(cls, config_list: list[dict]) -> "PatternEngine":
        """Build a PatternEngine from a list of config dicts (e.g., from YAML)."""
        severity_map = {s.value: s for s in Severity}
        patterns: list[ErrorPattern] = []
        for entry in config_list:
            sev_str = entry.get("severity", "warning").lower()
            severity = severity_map.get(sev_str, Severity.WARNING)
            patterns.append(
                ErrorPattern(
                    pattern=entry["pattern"],
                    severity=severity,
                    message=entry.get("message", ""),
                    category=entry.get("category", "unknown"),
                    extract_fields=entry.get("extract_fields", []),
                    source=entry.get("source", ""),
                    log_hint=entry.get("log_hint", ""),
                    remediation=entry.get("remediation", ""),
                )
            )
        return cls(patterns)
