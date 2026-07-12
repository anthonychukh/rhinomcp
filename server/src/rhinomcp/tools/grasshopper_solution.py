"""Grasshopper solution control tools."""

from typing import Any, Dict, List, Optional

from mcp.server.fastmcp import Context

from rhinomcp.server import mcp
from rhinomcp.tools._grasshopper_common import send_grasshopper_command


@mcp.tool()
def gh_run_solution(
    ctx: Context,
    expire_all: bool = False,
    wait_ms: int = 5000,
    request_id: Optional[str] = None,
) -> Dict[str, Any]:
    """Run a solution, returning a tracked operation when it outlasts wait_ms."""
    return send_grasshopper_command(
        "gh_run_solution",
        {"expire_all": expire_all},
        hybrid=True,
        wait_ms=wait_ms,
        request_id=request_id,
    )


@mcp.tool()
def gh_expire_solution(
    ctx: Context,
    instance_id: Optional[str] = None,
    nickname: Optional[str] = None,
    component_ids: Optional[List[str]] = None,
    expire_downstream: bool = True,
    recompute: bool = False,
) -> Dict[str, Any]:
    """Expire the whole Grasshopper solution or selected components."""
    params: Dict[str, Any] = {
        "expire_downstream": expire_downstream,
        "recompute": recompute,
    }
    if instance_id:
        params["instance_id"] = instance_id
    if nickname:
        params["nickname"] = nickname
    if component_ids:
        params["component_ids"] = component_ids
    return send_grasshopper_command("gh_expire_solution", params)
