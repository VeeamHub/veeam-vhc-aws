from .repo_health import RepoHealthMonitor
from .retention import RetentionMonitor
from .worker_health import WorkerHealthMonitor

MONITOR_REGISTRY: dict = {
    "repo_health": RepoHealthMonitor,
    "retention": RetentionMonitor,
    "worker_health": WorkerHealthMonitor,
}
