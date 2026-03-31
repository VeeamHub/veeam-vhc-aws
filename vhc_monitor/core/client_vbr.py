"""VBR (Veeam Backup & Replication) REST API client."""

import logging
import time
from datetime import datetime, timedelta, timezone
from typing import Any

import httpx

from .auth import VBRAuth

logger = logging.getLogger("vhc_monitor.client.vbr")


class VBRClient:
    """REST client for Veeam Backup & Replication API."""

    def __init__(
        self,
        base_url: str,
        auth: VBRAuth,
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
                        "VBR %s %s -> %d (%dms)",
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
                        "VBR %s %s failed (attempt %d/%d, %dms): %s — retrying in %ds",
                        method, path, attempt + 1, self.retry_count + 1,
                        duration_ms, e, self.retry_delay,
                    )
                    time.sleep(self.retry_delay)
                    headers = self.auth.get_headers()
                else:
                    logger.error(
                        "VBR %s %s failed after %d attempts (%dms): %s",
                        method, path, self.retry_count + 1, duration_ms, e,
                    )

        raise last_error  # type: ignore[misc]

    def get_repository_states(self) -> list[dict]:
        """GET /api/v1/backupInfrastructure/repositories/states"""
        result = self._request("GET", "/api/v1/backupInfrastructure/repositories/states")
        return result.get("data", result.get("Data", []))

    def get_scaleout_repositories(self) -> list[dict]:
        """GET /api/v1/backupInfrastructure/scaleOutRepositories"""
        result = self._request("GET", "/api/v1/backupInfrastructure/scaleOutRepositories")
        return result.get("data", result.get("Data", []))

    def get_sessions(self, lookback_hours: int = 48) -> list[dict]:
        """GET /api/v1/sessions with createdAfter filter."""
        cutoff = datetime.now(timezone.utc) - timedelta(hours=lookback_hours)
        params = {"createdAfter": cutoff.strftime("%Y-%m-%dT%H:%M:%SZ")}
        result = self._request("GET", "/api/v1/sessions", params=params)
        return result.get("data", result.get("Data", []))

    def get_jobs(self) -> list[dict]:
        """GET /api/v1/jobs"""
        result = self._request("GET", "/api/v1/jobs")
        return result.get("data", result.get("Data", []))

    def get_backups(self) -> list[dict]:
        """GET /api/v1/backups with pagination and per-item fallback.

        VBR 13.x can 500 on certain backup types (e.g. ObjectStorageBackup)
        when they appear in a page. When a batch page fails, fall back to
        fetching one item at a time to isolate and skip only the broken entries.
        """
        all_backups: list[dict] = []
        offset = 0
        page_size = 50
        skipped = 0

        while True:
            try:
                result = self._request(
                    "GET", "/api/v1/backups",
                    params={"limit": page_size, "skip": offset},
                )
                page = result.get("data", result.get("Data", []))
                if not page:
                    break
                all_backups.extend(page)
                if len(page) < page_size:
                    break
                offset += page_size
            except httpx.HTTPStatusError as e:
                if e.response.status_code != 500:
                    raise
                logger.warning(
                    "VBR /api/v1/backups batch failed at offset %d — "
                    "falling back to per-item fetch to isolate bad entries",
                    offset,
                )
                for i in range(page_size):
                    try:
                        result = self._request(
                            "GET", "/api/v1/backups",
                            params={"limit": 1, "skip": offset + i},
                        )
                        page = result.get("data", result.get("Data", []))
                        if not page:
                            offset = -1
                            break
                        all_backups.extend(page)
                    except httpx.HTTPStatusError:
                        skipped += 1
                        logger.warning(
                            "VBR /api/v1/backups entry at offset %d "
                            "cannot be serialized by VBR — skipped",
                            offset + i,
                        )
                if offset == -1:
                    break
                offset += page_size

        if skipped:
            logger.info(
                "Fetched %d backups, skipped %d unserializable entries",
                len(all_backups), skipped,
            )
        else:
            logger.debug("Fetched %d backups total (paginated)", len(all_backups))
        return all_backups

    def get_restore_points(self, limit: int = 500, offset: int = 0) -> list[dict]:
        """GET /api/v1/restorePoints with pagination."""
        params = {"limit": limit, "skip": offset}
        result = self._request("GET", "/api/v1/restorePoints", params=params)
        return result.get("data", result.get("Data", []))

    def rescan_repositories(self, repo_ids: list[str]) -> list[dict]:
        """POST rescan for specified repositories."""
        results: list[dict] = []
        for repo_id in repo_ids:
            result = self._request(
                "POST",
                f"/api/v1/backupInfrastructure/repositories/{repo_id}/rescan",
            )
            results.append(result)
        return results

    def get_server_info(self) -> dict:
        """GET /api/v1/serverInfo"""
        return self._request("GET", "/api/v1/serverInfo")
