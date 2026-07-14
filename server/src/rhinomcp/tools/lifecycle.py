"""Out-of-band bridge readiness and acknowledged Rhino shutdown tools."""

import ctypes
import os
import time
import uuid
from typing import Any, Dict, Literal, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import (
    RHINO_CONTROL_TIMEOUT,
    get_rhino_connection,
    mcp,
)


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def get_bridge_health(ctx: Context) -> Dict[str, Any]:
    """Read listener, UI-thread, Grasshopper, modal, queue, and shutdown state.

    This control-plane request does not wait for Rhino's UI thread, so it remains
    useful while Grasshopper is loading or a tracked operation is queued.
    """
    return get_rhino_connection().send_command(
        "get_bridge_health",
        {},
        timeout=RHINO_CONTROL_TIMEOUT,
    )


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def shutdown_rhino(
    ctx: Context,
    save_changes: Literal["refuse", "save", "discard"] = "refuse",
    force_after_ms: int = 15000,
    wait_for_exit_ms: int = 20000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Close Grasshopper documents and shut Rhino down through an acknowledged operation.

    ``refuse`` is the safe default and reports modified Rhino/Grasshopper documents
    without showing an interactive save prompt. ``save`` requires every modified
    document to already have a path. ``discard`` clears modified flags explicitly.

    The bridge acknowledges the operation before it touches Rhino's UI thread.
    Reuse ``request_id`` when retrying. A graceful Rhino exit is attempted first;
    ``force_after_ms`` is an opt-in watchdog delay within this explicit shutdown
    request (set it to zero to disable the watchdog).
    """
    if save_changes not in ("refuse", "save", "discard"):
        raise ValueError("save_changes must be 'refuse', 'save', or 'discard'")
    if force_after_ms != 0 and not 1000 <= force_after_ms <= 60000:
        raise ValueError("force_after_ms must be 0 or between 1000 and 60000")
    if not 0 <= wait_for_exit_ms <= 65000:
        raise ValueError("wait_for_exit_ms must be between 0 and 65000")

    if request_id is None:
        request_id = str(uuid.uuid4())
    else:
        try:
            request_id = str(uuid.UUID(request_id))
        except (ValueError, AttributeError) as exc:
            raise ValueError("request_id must be a UUID") from exc

    rhino = get_rhino_connection()
    accepted = rhino.send_command(
        "shutdown_rhino",
        {
            "save_changes": save_changes,
            "force_after_ms": force_after_ms,
        },
        timeout=RHINO_CONTROL_TIMEOUT,
        envelope={
            "execution": {
                "mode": "async",
                "request_id": request_id,
            }
        },
    )
    operation_id = accepted.get("operation_id")
    process_id = accepted.get("process_id")
    result = dict(accepted)
    result.update(
        {
            "shutdown_accepted": bool(operation_id),
            "shutdown_state": "shutdown_accepted",
            "expected_disconnect": True,
            "process_alive": _process_is_alive(process_id),
        }
    )
    if not operation_id or wait_for_exit_ms == 0:
        return result

    deadline = time.monotonic() + (wait_for_exit_ms / 1000.0)
    completed_result: Optional[Dict[str, Any]] = None
    connection_error: Optional[str] = None
    while time.monotonic() < deadline:
        if completed_result is not None:
            alive = _process_is_alive(process_id)
            if alive is False:
                return _shutdown_disconnect_result(result, process_id, completed_result)
            time.sleep(0.1)
            continue

        try:
            status = rhino.send_command(
                "get_operation_status",
                {"operation_id": operation_id, "include_result": True},
                timeout=RHINO_CONTROL_TIMEOUT,
            )
            result.update(status)
        except Exception as exc:
            connection_error = str(exc)
            alive = _process_is_alive(process_id)
            if alive is False:
                return _shutdown_disconnect_result(result, process_id)
            time.sleep(0.1)
            continue

        state = status.get("execution_state", status.get("state"))
        if status.get("state") == "waiting_for_user" or status.get("modal_detected"):
            result.update(
                {
                    "shutdown_state": "blocked_by_modal",
                    "process_alive": _process_is_alive(process_id),
                }
            )
            return result
        if state == "failed":
            error = status.get("error", "Shutdown operation failed")
            result.update(
                {
                    "shutdown_state": (
                        "blocked_by_save_policy"
                        if "unsaved" in error.lower() or "refused" in error.lower()
                        else "shutdown_failed"
                    ),
                    "shutdown_accepted": True,
                    "expected_disconnect": False,
                    "process_alive": _process_is_alive(process_id),
                }
            )
            return result
        if state == "cancelled":
            result.update(
                {
                    "shutdown_state": "shutdown_cancelled",
                    "expected_disconnect": False,
                    "process_alive": _process_is_alive(process_id),
                }
            )
            return result
        if state == "completed":
            raw_completed = status.get("result")
            completed_result = raw_completed if isinstance(raw_completed, dict) else {}
            if completed_result.get("process_id"):
                process_id = completed_result["process_id"]
            result.update(completed_result)
            result["shutdown_state"] = "shutdown_accepted"
            continue

        time.sleep(0.1)

    alive = _process_is_alive(process_id)
    result.update(
        {
            "shutdown_state": "shutdown_timed_out",
            "shutdown_accepted": True,
            "expected_disconnect": True,
            "process_alive": alive,
        }
    )
    if connection_error:
        result["connection_error"] = connection_error
    return result


def _shutdown_disconnect_result(
    accepted: Dict[str, Any],
    process_id: Any,
    completed: Optional[Dict[str, Any]] = None,
) -> Dict[str, Any]:
    result = dict(accepted)
    if completed:
        result.update(completed)
    result.update(
        {
            "shutdown_state": "expected_disconnect",
            "shutdown_accepted": True,
            "expected_disconnect": True,
            "process_id": process_id,
            "process_alive": False,
            "message": "Rhino accepted shutdown and the process exited as expected.",
        }
    )
    return result


def _process_is_alive(process_id: Any) -> Optional[bool]:
    """Best-effort process liveness check without an optional psutil dependency."""
    try:
        pid = int(process_id)
    except (TypeError, ValueError):
        return None
    if pid <= 0:
        return None

    if os.name == "nt":
        process_query_limited_information = 0x1000
        still_active = 259
        kernel32 = ctypes.windll.kernel32
        kernel32.OpenProcess.restype = ctypes.c_void_p
        kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel32.GetExitCodeProcess.argtypes = [
            ctypes.c_void_p,
            ctypes.POINTER(ctypes.c_ulong),
        ]
        kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
        handle = kernel32.OpenProcess(process_query_limited_information, False, pid)
        if not handle:
            return False
        try:
            exit_code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(exit_code)):
                return None
            return exit_code.value == still_active
        finally:
            kernel32.CloseHandle(handle)

    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
