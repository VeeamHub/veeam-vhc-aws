"""Base monitor abstract class."""

from abc import ABC, abstractmethod
from typing import Optional

from vhc_monitor.core.models import MonitorResult, MonitorType
from vhc_monitor.core.patterns import PatternEngine
from vhc_monitor.core.client_vbr import VBRClient
from vhc_monitor.core.client_vbaws import VBAWSClient
from vhc_monitor.core.client_em import EMClient


class BaseMonitor(ABC):
    """Abstract base class for all VHC monitors."""

    def __init__(
        self,
        config: dict,
        vbr_client: Optional[VBRClient] = None,
        vbaws_client: Optional[VBAWSClient] = None,
        em_client: Optional[EMClient] = None,
        pattern_engine: Optional[PatternEngine] = None,
    ) -> None:
        self.config = config
        self.vbr_client = vbr_client
        self.vbaws_client = vbaws_client
        self.em_client = em_client
        self.pattern_engine = pattern_engine

    @abstractmethod
    def run(self) -> MonitorResult:
        """Execute the monitor and return results."""
        ...

    @property
    @abstractmethod
    def monitor_type(self) -> MonitorType:
        """Return the type of this monitor."""
        ...

    @property
    @abstractmethod
    def required_connections(self) -> list[str]:
        """Return list of required connection types (e.g., ['vbr', 'vbaws'])."""
        ...
