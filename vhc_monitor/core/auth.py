"""Authentication providers for Veeam APIs."""

import logging
import os
import time
from abc import ABC, abstractmethod

import httpx

logger = logging.getLogger("vhc_monitor.auth")


class CredentialProvider(ABC):
    """Abstract base class for credential providers."""

    @abstractmethod
    def get_credential(self, key: str) -> str:
        """Retrieve a credential by key. Raises KeyError if not found."""
        ...


class EnvVarProvider(CredentialProvider):
    """Reads credentials from environment variables as VEEAM_{KEY}."""

    def get_credential(self, key: str) -> str:
        env_key = f"VEEAM_{key.upper()}"
        value = os.environ.get(env_key)
        if value is None:
            raise KeyError(f"Environment variable {env_key} not set")
        return value


class ConfigFileProvider(CredentialProvider):
    """Reads credentials from a config dictionary."""

    def __init__(self, config: dict) -> None:
        self._config = config

    def get_credential(self, key: str) -> str:
        if key in self._config:
            return str(self._config[key])
        raise KeyError(f"Key '{key}' not found in config")


class ChainedProvider(CredentialProvider):
    """Tries multiple providers in order, raises KeyError if all fail."""

    def __init__(self, providers: list[CredentialProvider]) -> None:
        self._providers = providers

    def get_credential(self, key: str) -> str:
        errors: list[str] = []
        for provider in self._providers:
            try:
                return provider.get_credential(key)
            except KeyError as e:
                errors.append(str(e))
        raise KeyError(
            f"No provider could resolve key '{key}': {'; '.join(errors)}"
        )


class VBRAuth:
    """OAuth2 token authentication for Veeam Backup & Replication REST API."""

    def __init__(
        self,
        base_url: str,
        username: str,
        password: str,
        api_version: str = "1.3-rev1",
        verify_ssl: bool = True,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.api_version = api_version
        self.verify_ssl = verify_ssl
        self._token: str | None = None
        self._token_expiry: float = 0.0

    def _is_token_expired(self) -> bool:
        """Check if the cached token is expired (with 60s buffer)."""
        return self._token is None or time.time() >= (self._token_expiry - 60)

    def get_token(self) -> str:
        """Get a valid OAuth2 token, refreshing if expired."""
        if not self._is_token_expired() and self._token is not None:
            logger.debug("VBR token still valid, reusing cached token")
            return self._token

        url = f"{self.base_url}/api/oauth2/token"
        logger.info("VBR OAuth2 token request to %s for user %s", url, self.username)
        data = {
            "grant_type": "password",
            "username": self.username,
            "password": self.password,
        }

        with httpx.Client(verify=self.verify_ssl) as client:
            response = client.post(
                url,
                data=data,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
            )
            response.raise_for_status()

        token_data = response.json()
        self._token = token_data["access_token"]
        # Default to 15 minutes if expires_in not provided
        expires_in = token_data.get("expires_in", 900)
        self._token_expiry = time.time() + expires_in
        logger.info("VBR OAuth2 token obtained, expires in %ds", expires_in)

        return self._token  # type: ignore[return-value]

    def get_headers(self) -> dict[str, str]:
        """Return authorization headers for VBR API requests."""
        token = self.get_token()
        return {
            "Authorization": f"Bearer {token}",
            "x-api-version": self.api_version,
            "Accept": "application/json",
        }


class VBAWSAuth:
    """OAuth2 token authentication for Veeam Backup for AWS REST API."""

    def __init__(
        self,
        base_url: str,
        username: str,
        password: str,
        verify_ssl: bool = True,
    ) -> None:
        self.base_url = base_url.rstrip("/")
        self.username = username
        self.password = password
        self.verify_ssl = verify_ssl
        self._token: str | None = None
        self._token_expiry: float = 0.0

    def _is_token_expired(self) -> bool:
        """Check if the cached token is expired (with 60s buffer)."""
        return self._token is None or time.time() >= (self._token_expiry - 60)

    def get_token(self) -> str:
        """Get a valid OAuth2 token, refreshing if expired."""
        if not self._is_token_expired() and self._token is not None:
            logger.debug("VBAWS token still valid, reusing cached token")
            return self._token

        url = f"{self.base_url}/api/oauth2/token"
        logger.info("VBAWS OAuth2 token request to %s for user %s", url, self.username)
        data = {
            "grant_type": "password",
            "username": self.username,
            "password": self.password,
        }

        with httpx.Client(verify=self.verify_ssl) as client:
            response = client.post(
                url,
                data=data,
                headers={"Content-Type": "application/x-www-form-urlencoded"},
            )
            response.raise_for_status()

        token_data = response.json()
        self._token = token_data["access_token"]
        expires_in = token_data.get("expires_in", 900)
        self._token_expiry = time.time() + expires_in
        logger.info("VBAWS OAuth2 token obtained, expires in %ds", expires_in)

        return self._token  # type: ignore[return-value]

    def get_headers(self) -> dict[str, str]:
        """Return Bearer auth header for VBAWS API requests."""
        token = self.get_token()
        return {
            "Authorization": f"Bearer {token}",
            "Accept": "application/json",
        }
