"""VBAWS (Veeam Backup for AWS) REST API client."""

import logging
import time
from datetime import datetime
from typing import Any

import httpx

from .auth import VBAWSAuth

logger = logging.getLogger("vhc_monitor.client.vbaws")


class VBAWSClient:
    """REST client for Veeam Backup for AWS API."""

    def __init__(
        self,
        base_url: str,
        auth: VBAWSAuth,
        verify_ssl: bool = True,
        timeout: int = 30,
        retry_count: int = 2,
        retry_delay: int = 5,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.auth = auth
        self.verify_ssl = verify_ssl
        self.timeout = timeout
        self.retry_count = retry_count
        self.retry_delay = retry_delay

    def _request(self, method: str, path: str, **kwargs: Any) -> dict:
        """Make an authenticated API request with retry logic."""
        url = f"{self.base_url}{path}"
        headers = self.auth.get_headers()

        if "headers" in kwargs:
            headers.update(kwargs.pop("headers"))

        last_error: Exception | None = None
        for attempt in range(self.retry_count + 1):
            req_start = time.monotonic()
            try:
                with httpx.Client(
                    verify=self.verify_ssl, timeout=self.timeout
                ) as client:
                    response = client.request(
                        method, url, headers=headers, **kwargs
                    )
                    duration_ms = int((time.monotonic() - req_start) * 1000)
                    logger.debug(
                        "VBAWS %s %s -> %d (%dms)",
                        method, path, response.status_code, duration_ms,
                    )
                    response.raise_for_status()
                    if response.status_code == 204:
                        return {}
                    return response.json()
            except (httpx.HTTPStatusError, httpx.ConnectError, httpx.TimeoutException) as e:
                duration_ms = int((time.monotonic() - req_start) * 1000)
                last_error = e
                if attempt < self.retry_count:
                    logger.warning(
                        "VBAWS %s %s failed (attempt %d/%d, %dms): %s — retrying in %ds",
                        method, path, attempt + 1, self.retry_count + 1,
                        duration_ms, e, self.retry_delay,
                    )
                    time.sleep(self.retry_delay)
                else:
                    logger.error(
                        "VBAWS %s %s failed after %d attempts (%dms): %s",
                        method, path, self.retry_count + 1, duration_ms, e,
                    )

        raise last_error  # type: ignore[misc]

    def get_sessions(self, from_dt: datetime, to_dt: datetime) -> list[dict]:
        """GET /api/v1/sessions sorted newest-first.

        VBAWS API ignores from/to date params and has a hard cap of ~8269 sessions.
        Sorting DESC by CreationTime ensures we get the most recent sessions
        (including recent failures) rather than the oldest ones.
        Client-side date filtering is applied by the worker_health monitor.
        """
        params = {
            "from": from_dt.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "to": to_dt.strftime("%Y-%m-%dT%H:%M:%SZ"),
            "orderColumn": "CreationTime",
            "orderAsc": "false",
        }
        result = self._request("GET", "/api/v1/sessions", params=params)
        return result.get("data", result.get("Data", []))

    def get_session_details(self, session_id: str) -> dict:
        """GET /api/v1/sessions/{id}"""
        return self._request("GET", f"/api/v1/sessions/{session_id}")

    def get_session_logs(self, session_id: str) -> list[dict]:
        """GET /api/v1/sessions/{id}/logs — returns log entries with error details."""
        result = self._request("GET", f"/api/v1/sessions/{session_id}/logs")
        return result.get("data", result.get("Data", []))

    def get_health_check_sessions(self) -> list[dict]:
        """GET /api/v1/sessions?type=HealthCheck"""
        params = {"type": "HealthCheck"}
        result = self._request("GET", "/api/v1/sessions", params=params)
        return result.get("data", result.get("Data", []))
