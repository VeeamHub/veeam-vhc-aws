"""Enterprise Manager REST API client (placeholder for future use)."""


class EMClient:
    """Minimal Enterprise Manager client — reserved for future implementation."""

    def __init__(
        self,
        base_url: str = "",
        verify_ssl: bool = True,
        timeout: int = 30,
    ) -> None:
        self.base_url = base_url.rstrip("/") if base_url else ""
        self.verify_ssl = verify_ssl
        self.timeout = timeout
