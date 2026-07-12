using System;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("gh_trigger_button")]
    public JObject GhTriggerButton(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        var obj = FindGhObject(doc, parameters);
        if (obj is not GH_ButtonObject button)
        {
            throw new InvalidOperationException(
                $"Grasshopper object '{obj.NickName}' is {obj.GetType().Name}, not a standard Button component.");
        }

        // Match the normal mouse interaction: solve once while pressed, then
        // solve again after release so downstream components observe both edges.
        try
        {
            button.ButtonDown = true;
            button.ExpireSolution(true);
            RunGrasshopperSolution(doc, false);
        }
        finally
        {
            button.ButtonDown = false;
            button.ExpireSolution(true);
            RunGrasshopperSolution(doc, false);
        }
        RedrawGrasshopperCanvas();

        return new JObject
        {
            ["instance_id"] = button.InstanceGuid.ToString(),
            ["nickname"] = button.NickName,
            ["pressed"] = true,
            ["released"] = true,
            ["message"] = $"Triggered button '{button.NickName}'"
        };
    }

    [McpCommand("gh_set_toggle")]
    public JObject GhSetToggle(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        var obj = FindGhObject(doc, parameters);
        if (obj is not GH_BooleanToggle toggle)
        {
            throw new InvalidOperationException(
                $"Grasshopper object '{obj.NickName}' is {obj.GetType().Name}, not a Boolean Toggle.");
        }

        bool previous = toggle.Value;
        toggle.Value = parameters["value"]?.ToObject<bool>() ?? !previous;
        toggle.ExpireSolution(true);
        RunGrasshopperSolution(doc, false);
        RedrawGrasshopperCanvas();

        return new JObject
        {
            ["instance_id"] = toggle.InstanceGuid.ToString(),
            ["nickname"] = toggle.NickName,
            ["previous_value"] = previous,
            ["value"] = toggle.Value,
            ["message"] = $"Set toggle '{toggle.NickName}' to {toggle.Value}"
        };
    }
}
