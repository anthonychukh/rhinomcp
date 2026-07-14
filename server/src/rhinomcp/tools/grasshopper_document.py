"""Grasshopper document lifecycle and canvas inspection tools."""

from typing import Any, Dict, Literal, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool()
def gh_create_document(
    ctx: Context,
    new_if_missing: bool = True,
    make_active: bool = True,
    open_canvas: bool = True,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Create or activate a Grasshopper document through a tracked operation."""
    return send_grasshopper_command(
        "gh_create_document",
        {
            "new_if_missing": new_if_missing,
            "make_active": make_active,
            "open_canvas": open_canvas,
        },
        hybrid=True,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool(annotations=ToolAnnotations(openWorldHint=True))
def gh_open_document(
    ctx: Context,
    path: str,
    make_active: bool = True,
    open_canvas: bool = True,
    reuse_if_open: bool = True,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Open a .gh/.ghx file, returning an operation if loading stays busy.

    Set ``wait_ms`` to zero to return an operation id immediately. Reuse an
    optional ``request_id`` when retrying to prevent duplicate opens.
    """
    return send_grasshopper_command(
        "gh_open_document",
        {
            "path": path,
            "make_active": make_active,
            "open_canvas": open_canvas,
            "reuse_if_open": reuse_if_open,
        },
        hybrid=True,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True, openWorldHint=True))
def gh_save_document(
    ctx: Context,
    path: Optional[str] = None,
    overwrite: bool = False,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Save the active Grasshopper document, optionally to a .gh or .ghx path."""
    params: Dict[str, Any] = {"overwrite": overwrite}
    if path is not None:
        params["path"] = path
    return send_grasshopper_command(
        "gh_save_document",
        params,
        hybrid=True,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True, openWorldHint=True))
def gh_close_document(
    ctx: Context,
    save_changes: Literal["refuse", "save", "discard"] = "refuse",
    save_path: Optional[str] = None,
    overwrite: bool = False,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Close the active Grasshopper document with explicit modified-file handling.

    The default ``refuse`` policy prevents accidental loss. Use ``save`` to
    persist changes first (with save_path for an unsaved document), or
    ``discard`` to close without saving.
    """
    params: Dict[str, Any] = {
        "save_changes": save_changes,
        "overwrite": overwrite,
    }
    if save_path is not None:
        params["save_path"] = save_path
    return send_grasshopper_command(
        "gh_close_document",
        params,
        hybrid=True,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def gh_get_document_info(ctx: Context) -> Dict[str, Any]:
    """Get information about the active Grasshopper document."""
    return send_grasshopper_command("gh_get_document_info", {})


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def gh_get_canvas_state(
    ctx: Context,
    include_connections: bool = True,
    include_values: bool = False,
    max_items: int = 20,
) -> Dict[str, Any]:
    """Return a structured snapshot of the active Grasshopper canvas."""
    return send_grasshopper_command(
        "gh_get_canvas_state",
        {
            "include_connections": include_connections,
            "include_values": include_values,
            "max_items": max_items,
        },
    )
