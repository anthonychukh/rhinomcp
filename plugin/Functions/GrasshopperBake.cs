using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.DocObjects;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("gh_bake_objects")]
    public JObject GhBakeObjects(JObject parameters)
    {
        var ghDoc = GetActiveGrasshopperDocument();
        var rhinoDoc = RhinoDoc.ActiveDoc
            ?? throw new InvalidOperationException("No active Rhino document.");
        var targets = ResolveBakeTargets(ghDoc, parameters);
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("No Grasshopper objects matched the bake selector.");
        }

        if (OptionalBool(parameters, "recompute", true))
        {
            RunGrasshopperSolution(ghDoc, false);
        }

        ObjectAttributes attributes = CreateBakeAttributes(rhinoDoc, parameters);
        bool hasOutputSelector = parameters["output_index"] != null || parameters["output_name"] != null;
        var bakedIds = new List<Guid>();
        var skipped = new JArray();

        foreach (var target in targets.Distinct())
        {
            int countBefore = bakedIds.Count;
            if (hasOutputSelector)
            {
                var output = ResolveBakeOutput(target, parameters);
                foreach (var path in output.VolatileData.Paths)
                {
                    foreach (var item in output.VolatileData.get_Branch(path))
                    {
                        if (item is IGH_BakeAwareData bakeData &&
                            bakeData.BakeGeometry(rhinoDoc, attributes, out Guid bakedId))
                        {
                            bakedIds.Add(bakedId);
                        }
                    }
                }
            }
            else if (target is IGH_BakeAwareObject bakeObject && bakeObject.IsBakeCapable)
            {
                if (parameters["layer"] != null)
                {
                    bakeObject.BakeGeometry(rhinoDoc, attributes, bakedIds);
                }
                else
                {
                    bakeObject.BakeGeometry(rhinoDoc, bakedIds);
                }
            }

            if (bakedIds.Count == countBefore)
            {
                skipped.Add(new JObject
                {
                    ["instance_id"] = target.InstanceGuid.ToString(),
                    ["nickname"] = target.NickName,
                    ["reason"] = hasOutputSelector
                        ? "Selected output contained no bake-aware geometry"
                        : "Object is not bake-capable or produced no geometry"
                });
            }
        }

        rhinoDoc.Views.Redraw();
        return new JObject
        {
            ["baked_count"] = bakedIds.Count,
            ["baked_ids"] = new JArray(bakedIds.Select(id => id.ToString())),
            ["target_count"] = targets.Count,
            ["skipped"] = skipped,
            ["layer"] = OptionalString(parameters, "layer"),
            ["message"] = $"Baked {bakedIds.Count} Rhino object(s) from {targets.Count} Grasshopper target(s)"
        };
    }

    private static List<IGH_DocumentObject> ResolveBakeTargets(GH_Document doc, JObject parameters)
    {
        var instanceIds = parameters["instance_ids"]?.ToObject<List<string>>() ?? new List<string>();
        if (instanceIds.Count > 0)
        {
            var result = new List<IGH_DocumentObject>();
            foreach (string id in instanceIds)
            {
                if (!Guid.TryParse(id, out Guid guid))
                {
                    throw new ArgumentException($"Invalid Grasshopper instance GUID: {id}");
                }
                var obj = doc.FindObject(guid, true)
                    ?? throw new InvalidOperationException($"Grasshopper object '{id}' not found.");
                result.Add(obj);
            }
            return result;
        }

        string graphId = OptionalString(parameters, "graph_id");
        if (!string.IsNullOrWhiteSpace(graphId))
        {
            return GetGraphObjects(doc, graphId)
                .Where(obj => obj is not GH_Group)
                .ToList();
        }

        if (parameters["instance_id"] == null && parameters["nickname"] == null && parameters["alias"] == null)
        {
            throw new ArgumentException(
                "Select bake targets with instance_id, nickname, alias, graph_id, or instance_ids.");
        }
        return new List<IGH_DocumentObject> { FindGhObject(doc, parameters) };
    }

    private static IGH_Param ResolveBakeOutput(IGH_DocumentObject target, JObject parameters)
    {
        if (target is IGH_Component component)
        {
            var output = FindOutputParam(
                component,
                GetParamIndex(parameters, isOutput: true),
                GetParamName(parameters, isOutput: true));
            return output ?? throw new InvalidOperationException(
                $"Could not find the requested output on '{target.NickName}'.");
        }
        if (target is IGH_Param param)
        {
            return param;
        }
        throw new InvalidOperationException(
            $"Grasshopper object '{target.NickName}' has no output parameters to bake.");
    }

    private ObjectAttributes CreateBakeAttributes(RhinoDoc doc, JObject parameters)
    {
        var attributes = new ObjectAttributes { LayerIndex = doc.Layers.CurrentLayerIndex };
        string layerName = OptionalString(parameters, "layer");
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return attributes;
        }

        var layer = FindLayerByNameOrFullPath(doc, layerName);
        if (layer == null && OptionalBool(parameters, "create_layer_if_missing", false))
        {
            int index = doc.Layers.Add(layerName, Color.Black);
            if (index >= 0)
            {
                layer = doc.Layers[index];
            }
        }
        if (layer == null)
        {
            throw new InvalidOperationException(
                $"Rhino layer '{layerName}' was not found. Set create_layer_if_missing=true to create it.");
        }
        attributes.LayerIndex = layer.Index;
        return attributes;
    }
}
