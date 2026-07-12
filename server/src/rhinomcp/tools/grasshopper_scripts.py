"""Grasshopper C# and Python script-component source tools."""

from typing import Any, Dict, Optional

from mcp.server.fastmcp import Context
from mcp.types import ToolAnnotations

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool(annotations=ToolAnnotations(readOnlyHint=True))
def gh_get_script_source(
    ctx: Context,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    alias: Optional[str] = None,
) -> Dict[str, Any]:
    """Read source code from a Rhino 8 Grasshopper C# or Python component."""
    params: Dict[str, Any] = {}
    if instance_id is not None:
        params["instance_id"] = instance_id
    if nickname is not None:
        params["nickname"] = nickname
    if alias is not None:
        params["alias"] = alias
    return send_grasshopper_command("gh_get_script_source", params)


@mcp.tool(annotations=ToolAnnotations(destructiveHint=True, openWorldHint=True))
def gh_set_script_source(
    ctx: Context,
    source: str,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    alias: Optional[str] = None,
    expected_language: Optional[str] = None,
    recompute: bool = True,
) -> Dict[str, Any]:
    """Replace source code in a Rhino 8 Grasshopper C# or Python component."""
    params: Dict[str, Any] = {"source": source, "recompute": recompute}
    if instance_id is not None:
        params["instance_id"] = instance_id
    if nickname is not None:
        params["nickname"] = nickname
    if alias is not None:
        params["alias"] = alias
    if expected_language is not None:
        params["expected_language"] = expected_language
    return send_grasshopper_command("gh_set_script_source", params)
