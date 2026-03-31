"""PowerShell script execution for local and remote hosts."""

import subprocess
from typing import Optional


def run_powershell(
    script: str,
    host: Optional[str] = None,
    timeout: int = 60,
) -> dict:
    """
    Execute a PowerShell script locally or remotely.

    Args:
        script: PowerShell script content to execute.
        host: Remote hostname. If None, runs locally.
        timeout: Execution timeout in seconds.

    Returns:
        Dict with keys: success (bool), stdout (str), stderr (str), exit_code (int).
    """
    if host:
        # Wrap in Invoke-Command for remote execution
        escaped_script = script.replace("'", "''")
        wrapped = (
            f"Invoke-Command -ComputerName '{host}' "
            f"-ScriptBlock {{ {escaped_script} }}"
        )
    else:
        wrapped = script

    try:
        result = subprocess.run(
            ["powershell", "-NoProfile", "-Command", wrapped],
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        return {
            "success": result.returncode == 0,
            "stdout": result.stdout,
            "stderr": result.stderr,
            "exit_code": result.returncode,
        }
    except subprocess.TimeoutExpired:
        return {
            "success": False,
            "stdout": "",
            "stderr": f"Command timed out after {timeout} seconds",
            "exit_code": -1,
        }
    except FileNotFoundError:
        return {
            "success": False,
            "stdout": "",
            "stderr": "PowerShell executable not found",
            "exit_code": -1,
        }
