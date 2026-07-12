"""Shared helpers for Grasshopper MCP tool wrappers."""

import os
import time
import uuid
from typing import Any, Dict, List, Optional, Union

from rhinomcp.server import (
    RHINO_CONTROL_TIMEOUT,
    RHINO_LONG_TIMEOUT,
    get_rhino_connection,
)


JsonValue = Union[float, int, str, bool, List[Any], Dict[str, Any]]


DEFAULT_HYBRID_WAIT_MS = int(os.getenv("RHINO_MCP_HYBRID_WAIT_MS", "5000"))


def send_grasshopper_command(
    command: str,
    params: Dict[str, Any],
    *,
    hybrid: bool = False,
    wait_ms: Optional[int] = None,
    request_id: Optional[str] = None,
    timeout: Optional[float] = None,
) -> Dict[str, Any]:
    """Send a Grasshopper command, optionally using hybrid async execution.

    Hybrid calls are acknowledged by the plugin before Rhino's UI thread starts
    the command. This helper waits briefly for the common fast case, then returns
    an operation record that can be polled without blocking behind Rhino's UI.
    Older plugins ignore the execution envelope and return the normal result, so
    the server remains backwards-compatible during staggered upgrades.
    """
    rhino = get_rhino_connection()
    if not hybrid:
        if timeout is None:
            return rhino.send_command(command, params)
        return rhino.send_command(command, params, timeout=timeout)

    wait_ms = DEFAULT_HYBRID_WAIT_MS if wait_ms is None else wait_ms
    wait_ms = max(0, min(int(wait_ms), 30_000))
    if request_id is None:
        request_id = str(uuid.uuid4())
    else:
        try:
            request_id = str(uuid.UUID(request_id))
        except (ValueError, AttributeError) as exc:
            raise ValueError("request_id must be a UUID") from exc
    accepted = rhino.send_command(
        command,
        params,
        timeout=RHINO_LONG_TIMEOUT,
        envelope={
            "execution": {
                "mode": "async",
                "request_id": request_id,
            }
        },
    )

    operation_id = accepted.get("operation_id") if isinstance(accepted, dict) else None
    if not operation_id:
        return accepted
    if wait_ms == 0:
        return accepted

    deadline = time.monotonic() + (wait_ms / 1000.0)
    delay = 0.1
    status = accepted
    while time.monotonic() < deadline:
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(delay, remaining))
        status = rhino.send_command(
            "get_operation_status",
            {"operation_id": operation_id, "include_result": True},
            timeout=RHINO_CONTROL_TIMEOUT,
        )
        execution_state = status.get("execution_state", status.get("state"))
        if execution_state == "completed":
            return status.get("result", {})
        if execution_state == "failed":
            raise RuntimeError(status.get("error", f"{command} failed"))
        if execution_state == "cancelled":
            raise RuntimeError(f"{command} was cancelled")
        if status.get("state") == "waiting_for_user":
            return status
        delay = min(delay * 1.7, 1.0)

    status["poll_after_ms"] = max(250, int(delay * 1000))
    return status
