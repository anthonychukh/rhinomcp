"""Grasshopper-to-Rhino baking tools."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True))
def gh_bake_objects(
    ctx: Context,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    alias: Optional[str] = None,
    graph_id: Optional[str] = None,
    instance_ids: Optional[List[str]] = None,
    output_index: Optional[int] = None,
    output_name: Optional[str] = None,
    layer: Optional[str] = None,
    create_layer_if_missing: bool = False,
    recompute: bool = True,
) -> Dict[str, Any]:
    """Bake selected Grasshopper output into the active Rhino document.

    Select one object by instance id, nickname, or graph alias; select a set by
    graph_id or instance_ids. Set output_index/output_name to bake only one
    component output. Without an output selector, each target's normal
    IGH_BakeAwareObject behavior is used.
    """
    params: Dict[str, Any] = {
        "create_layer_if_missing": create_layer_if_missing,
        "recompute": recompute,
    }
    for key, value in {
        "instance_id": instance_id,
        "nickname": nickname,
        "alias": alias,
        "graph_id": graph_id,
        "instance_ids": instance_ids,
        "output_index": output_index,
        "output_name": output_name,
        "layer": layer,
    }.items():
        if value is not None:
            params[key] = value
    return send_grasshopper_command("gh_bake_objects", params)
