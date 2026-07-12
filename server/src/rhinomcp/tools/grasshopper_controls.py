"""Grasshopper button and toggle control tools."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True, openWorldHint=True))
def gh_trigger_button(
    ctx: Context,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    alias: Optional[str] = None,
) -> Dict[str, Any]:
    """Momentarily press a standard Grasshopper Button component."""
    params: Dict[str, Any] = {}
    if instance_id is not None:
        params["instance_id"] = instance_id
    if nickname is not None:
        params["nickname"] = nickname
    if alias is not None:
        params["alias"] = alias
    return send_grasshopper_command("gh_trigger_button", params)


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True, openWorldHint=True))
def gh_set_toggle(
    ctx: Context,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    alias: Optional[str] = None,
    value: Optional[bool] = None,
    recompute: bool = True,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Set a Boolean Toggle value, optionally deferring recomputation."""
    params: Dict[str, Any] = {"recompute": recompute}
    if instance_id is not None:
        params["instance_id"] = instance_id
    if nickname is not None:
        params["nickname"] = nickname
    if alias is not None:
        params["alias"] = alias
    if value is not None:
        params["value"] = value
    return send_grasshopper_command(
        "gh_set_toggle",
        params,
        hybrid=recompute,
        wait_ms=wait_ms,
        request_id=request_id,
    )
