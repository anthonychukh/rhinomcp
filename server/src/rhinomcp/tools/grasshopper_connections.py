"""Grasshopper component connection tools."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool()
def gh_connect_components(
    ctx: Context,
    source_instance_id: Optional[str] = None,
    source_nickname: Optional[str] = None,
    source_output_index: Optional[int] = None,
    source_output_name: Optional[str] = None,
    target_instance_id: Optional[str] = None,
    target_nickname: Optional[str] = None,
    target_input_index: Optional[int] = None,
    target_input_name: Optional[str] = None,
    recompute: bool = True,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Connect one Grasshopper output parameter to one input parameter."""
    params: Dict[str, Any] = {"recompute": recompute}
    if source_instance_id:
        params["source_instance_id"] = source_instance_id
    if source_nickname:
        params["source_nickname"] = source_nickname
    if source_output_index is not None:
        params["source_output_index"] = source_output_index
    if source_output_name:
        params["source_output_name"] = source_output_name
    if target_instance_id:
        params["target_instance_id"] = target_instance_id
    if target_nickname:
        params["target_nickname"] = target_nickname
    if target_input_index is not None:
        params["target_input_index"] = target_input_index
    if target_input_name:
        params["target_input_name"] = target_input_name
    return send_grasshopper_command(
        "gh_connect_components",
        params,
        hybrid=recompute,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool()
def gh_disconnect_components(
    ctx: Context,
    source_instance_id: Optional[str] = None,
    source_nickname: Optional[str] = None,
    source_output_index: Optional[int] = None,
    source_output_name: Optional[str] = None,
    target_instance_id: Optional[str] = None,
    target_nickname: Optional[str] = None,
    target_input_index: Optional[int] = None,
    target_input_name: Optional[str] = None,
    disconnect_all: bool = False,
    recompute: bool = True,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Disconnect one Grasshopper wire, or all sources from a target input."""
    params: Dict[str, Any] = {
        "disconnect_all": disconnect_all,
        "recompute": recompute,
    }
    if source_instance_id:
        params["source_instance_id"] = source_instance_id
    if source_nickname:
        params["source_nickname"] = source_nickname
    if source_output_index is not None:
        params["source_output_index"] = source_output_index
    if source_output_name:
        params["source_output_name"] = source_output_name
    if target_instance_id:
        params["target_instance_id"] = target_instance_id
    if target_nickname:
        params["target_nickname"] = target_nickname
    if target_input_index is not None:
        params["target_input_index"] = target_input_index
    if target_input_name:
        params["target_input_name"] = target_input_name
    return send_grasshopper_command(
        "gh_disconnect_components",
        params,
        hybrid=recompute,
        wait_ms=wait_ms,
        request_id=request_id,
    )
