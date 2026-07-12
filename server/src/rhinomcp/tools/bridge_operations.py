"""Control-plane tools for tracked Rhino operations."""

from typing import Any, Dict

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import RHINO_CONTROL_TIMEOUT, get_rhino_connection, mcp


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def get_operation_status(
    ctx: Context,
    operation_id: str,
    include_result: bool = True,
) -> Dict[str, Any]:
    """Poll a tracked Rhino operation without waiting for Rhino's UI thread."""
    return get_rhino_connection().send_command(
        "get_operation_status",
        {"operation_id": operation_id, "include_result": include_result},
        timeout=RHINO_CONTROL_TIMEOUT,
    )


@mcp.tool()
def cancel_operation(ctx: Context, operation_id: str) -> Dict[str, Any]:
    """Cancel a queued operation or request cooperative cancellation if running."""
    return get_rhino_connection().send_command(
        "cancel_operation",
        {"operation_id": operation_id},
        timeout=RHINO_CONTROL_TIMEOUT,
    )
