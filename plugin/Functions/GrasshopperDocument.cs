using System;
using System.IO;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Newtonsoft.Json.Linq;
using Rhino;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("gh_create_document")]
    public JObject GhCreateDocument(JObject parameters)
    {
        bool newIfMissing = OptionalBool(parameters, "new_if_missing", true);
        bool makeActive = OptionalBool(parameters, "make_active", true);
        bool openCanvas = OptionalBool(parameters, "open_canvas", true);

        if (openCanvas && Instances.ActiveCanvas == null)
        {
            RhinoApp.RunScript("_Grasshopper", false);
            RhinoApp.Wait();
        }

        var server = Instances.DocumentServer;
        var canvas = Instances.ActiveCanvas;
        var doc = canvas?.Document;
        bool created = false;

        if (doc == null && server.DocumentCount > 0)
        {
            doc = server.NextAvailableDocument();
            if (doc == null && server.DocumentCount == 1)
            {
                doc = server[0];
            }
        }

        if (doc == null && newIfMissing)
        {
            doc = server.AddNewDocument();
            created = doc != null;
        }

        if (doc != null && makeActive)
        {
            server.PromoteDocument(doc);
            if (Instances.ActiveCanvas != null)
            {
                Instances.ActiveCanvas.Document = doc;
            }
            RedrawGrasshopperCanvas();
        }

        bool hasDocument = doc != null;
        return new JObject
        {
            ["has_document"] = hasDocument,
            ["created"] = created,
            ["made_active"] = hasDocument && makeActive,
            ["canvas_open"] = Instances.ActiveCanvas != null,
            ["file_path"] = doc?.FilePath ?? (hasDocument ? "(unsaved)" : null),
            ["object_count"] = doc?.ObjectCount ?? 0,
            ["visibility"] = hasDocument ? GrasshopperVisibilityState(doc) : null,
            ["message"] = hasDocument
                ? (created ? "Created Grasshopper document" : "Grasshopper document is available")
                : "No active Grasshopper document"
        };
    }

    [McpCommand("gh_open_document")]
    public JObject GhOpenDocument(JObject parameters)
    {
        string requestedPath = OptionalString(parameters, "path")
            ?? throw new ArgumentException("path is required.");
        string path = ValidateGrasshopperFilePath(requestedPath, mustExist: true);
        bool makeActive = OptionalBool(parameters, "make_active", true);
        bool openCanvas = OptionalBool(parameters, "open_canvas", true);
        bool reuseIfOpen = OptionalBool(parameters, "reuse_if_open", true);

        if (openCanvas)
        {
            EnsureGrasshopperCanvasOpen();
            RhinoApp.Wait();
        }

        var server = Instances.DocumentServer;
        var existing = Enumerable.Range(0, server.DocumentCount)
            .Select(index => server[index])
            .FirstOrDefault(candidate =>
                !string.IsNullOrWhiteSpace(candidate?.FilePath) &&
                string.Equals(Path.GetFullPath(candidate.FilePath), path, StringComparison.OrdinalIgnoreCase));

        GH_Document doc;
        bool opened = false;
        bool reused = false;
        if (existing != null)
        {
            if (!reuseIfOpen)
            {
                throw new InvalidOperationException($"Grasshopper document is already open: {path}");
            }
            doc = existing;
            reused = true;
        }
        else
        {
            var io = new GH_DocumentIO();
            if (!io.Open(path) || io.Document == null)
            {
                throw new InvalidOperationException($"Grasshopper could not open '{path}'.");
            }
            doc = io.Document;
            server.AddDocument(doc);
            opened = true;
        }

        if (makeActive)
        {
            server.PromoteDocument(doc);
            if (Instances.ActiveCanvas != null)
            {
                Instances.ActiveCanvas.Document = doc;
            }
            RedrawGrasshopperCanvas();
        }

        return new JObject
        {
            ["opened"] = opened,
            ["reused"] = reused,
            ["made_active"] = makeActive,
            ["canvas_open"] = Instances.ActiveCanvas != null,
            ["file_path"] = doc.FilePath,
            ["object_count"] = doc.ObjectCount,
            ["visibility"] = GrasshopperVisibilityState(doc),
            ["message"] = reused ? "Grasshopper document was already open" : "Opened Grasshopper document"
        };
    }

    [McpCommand("gh_save_document")]
    public JObject GhSaveDocument(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        return SaveGrasshopperDocument(
            doc,
            OptionalString(parameters, "path"),
            OptionalBool(parameters, "overwrite", false));
    }

    [McpCommand("gh_close_document")]
    public JObject GhCloseDocument(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        string policy = (OptionalString(parameters, "save_changes") ?? "refuse")
            .Trim()
            .ToLowerInvariant();
        if (policy is not ("refuse" or "save" or "discard"))
        {
            throw new ArgumentException("save_changes must be 'refuse', 'save', or 'discard'.");
        }

        string savePath = OptionalString(parameters, "save_path");
        if (!string.IsNullOrWhiteSpace(savePath) && policy != "save")
        {
            throw new ArgumentException("save_path is only valid when save_changes='save'.");
        }

        bool wasModified = doc.IsModified;
        bool saved = false;
        bool discarded = false;
        JObject saveResult = null;
        if (policy == "save")
        {
            saveResult = SaveGrasshopperDocument(
                doc,
                savePath,
                OptionalBool(parameters, "overwrite", false));
            saved = true;
        }
        else if (wasModified && policy == "refuse")
        {
            throw new InvalidOperationException(
                "The active Grasshopper document has unsaved changes. " +
                "Use save_changes='save' or save_changes='discard' to close it explicitly.");
        }
        else if (wasModified && policy == "discard")
        {
            discarded = true;
        }

        string closedPath = string.IsNullOrWhiteSpace(doc.FilePath) ? "(unsaved)" : doc.FilePath;
        doc.IsModified = false;
        var server = Instances.DocumentServer;
        if (!server.SafeRemoveDocument(doc))
        {
            throw new InvalidOperationException("Grasshopper refused to close the active document.");
        }

        var next = server.NextAvailableDocument();
        if (next != null)
        {
            server.PromoteDocument(next);
        }
        if (Instances.ActiveCanvas != null)
        {
            Instances.ActiveCanvas.Document = next;
            RedrawGrasshopperCanvas();
        }

        return new JObject
        {
            ["closed"] = true,
            ["closed_file_path"] = closedPath,
            ["had_unsaved_changes"] = wasModified,
            ["saved"] = saved,
            ["discarded"] = discarded,
            ["save_result"] = saveResult,
            ["remaining_document_count"] = server.DocumentCount,
            ["active_file_path"] = next == null
                ? JValue.CreateNull()
                : next.FilePath ?? "(unsaved)",
            ["message"] = $"Closed Grasshopper document '{closedPath}'"
        };
    }

    private static JObject SaveGrasshopperDocument(GH_Document doc, string requestedPath, bool overwrite)
    {
        string currentPath = string.IsNullOrWhiteSpace(doc.FilePath) ? null : Path.GetFullPath(doc.FilePath);
        string path = requestedPath == null
            ? currentPath
            : ValidateGrasshopperFilePath(requestedPath, mustExist: false);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("path is required when the active Grasshopper document has not been saved before.");
        }

        bool savingCurrentPath = currentPath != null &&
            string.Equals(currentPath, path, StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path) && !savingCurrentPath && !overwrite)
        {
            throw new IOException($"File already exists: {path}. Set overwrite=true to replace it.");
        }
        string directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
        }

        string previousPath = doc.FilePath;
        doc.FilePath = path;
        var io = new GH_DocumentIO(doc);
        if (!io.Save())
        {
            doc.FilePath = previousPath;
            throw new IOException($"Grasshopper could not save '{path}'.");
        }

        long byteCount = File.Exists(path) ? new FileInfo(path).Length : 0;
        return new JObject
        {
            ["saved"] = true,
            ["file_path"] = path,
            ["format"] = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            ["bytes"] = byteCount,
            ["object_count"] = doc.ObjectCount,
            ["message"] = $"Saved Grasshopper document to '{path}'"
        };
    }

    [McpCommand("gh_get_document_info", ReadOnly = true)]
    public JObject GhGetDocumentInfo(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument(required: false);
        if (doc == null)
        {
            return new JObject
            {
                ["has_document"] = false,
                ["message"] = "No active Grasshopper document"
            };
        }

        var componentsByCategory = doc.Objects
            .OfType<IGH_Component>()
            .GroupBy(c => c.Category ?? "Unknown")
            .ToDictionary(g => g.Key, g => g.Count());

        return new JObject
        {
            ["has_document"] = true,
            ["file_path"] = doc.FilePath ?? "(unsaved)",
            ["is_modified"] = doc.IsModified,
            ["object_count"] = doc.ObjectCount,
            ["component_count"] = doc.Objects.OfType<IGH_Component>().Count(),
            ["parameter_count"] = doc.Objects.OfType<IGH_Param>()
                .Count(p => p.Attributes?.GetTopLevel.DocObject is not IGH_Component),
            ["group_count"] = doc.Objects.OfType<GH_Group>().Count(),
            ["visibility"] = GrasshopperVisibilityState(doc),
            ["components_by_category"] = JObject.FromObject(componentsByCategory)
        };
    }

    [McpCommand("gh_get_canvas_state", ReadOnly = true)]
    public JObject GhGetCanvasState(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        bool includeConnections = OptionalBool(parameters, "include_connections", true);
        bool includeValues = OptionalBool(parameters, "include_values", false);
        int maxItems = Clamp(OptionalInt(parameters, "max_items", 20), 0, 1000);

        var components = new JArray();
        foreach (var component in doc.Objects.OfType<IGH_Component>())
        {
            var compInfo = new JObject
            {
                ["instance_id"] = component.InstanceGuid.ToString(),
                ["name"] = component.Name,
                ["nickname"] = component.NickName,
                ["category"] = component.Category,
                ["subcategory"] = component.SubCategory,
                ["position"] = PivotToJson(component),
                ["runtime_message_level"] = component.RuntimeMessageLevel.ToString(),
                ["inputs"] = ParamsToJson(component.Params.Input, includeConnections, includeValues, maxItems),
                ["outputs"] = ParamsToJson(component.Params.Output, includeConnections, includeValues, maxItems)
            };
            AddSpecialGrasshopperState(compInfo, component);
            AddGraphMetadataFields(compInfo, component);
            components.Add(compInfo);
        }

        var standaloneParams = new JArray();
        foreach (var param in doc.Objects.OfType<IGH_Param>()
                     .Where(p => p.Attributes?.GetTopLevel.DocObject is not IGH_Component))
        {
            var paramInfo = new JObject
            {
                ["instance_id"] = param.InstanceGuid.ToString(),
                ["name"] = param.Name,
                ["nickname"] = param.NickName,
                ["type"] = param.TypeName,
                ["position"] = PivotToJson(param),
                ["source_count"] = param.SourceCount,
                ["recipient_count"] = param.Recipients.Count
            };
            AddSpecialGrasshopperState(paramInfo, param);
            AddGraphMetadataFields(paramInfo, param);
            if (includeValues)
            {
                paramInfo["value_data"] = ParamVolatileDataToJson(param, maxItems);
            }
            standaloneParams.Add(paramInfo);
        }

        var groups = new JArray(doc.Objects.OfType<GH_Group>().Select(g =>
        {
            var groupInfo = new JObject
            {
                ["instance_id"] = g.InstanceGuid.ToString(),
                ["nickname"] = g.NickName,
                ["object_count"] = g.ObjectIDs.Count
            };
            AddGraphMetadataFields(groupInfo, g);
            return groupInfo;
        }));

        return new JObject
        {
            ["file_path"] = doc.FilePath ?? "(unsaved)",
            ["object_count"] = doc.ObjectCount,
            ["component_count"] = components.Count,
            ["standalone_parameter_count"] = standaloneParams.Count,
            ["group_count"] = groups.Count,
            ["components"] = components,
            ["standalone_parameters"] = standaloneParams,
            ["groups"] = groups,
            ["visibility"] = GrasshopperVisibilityState(doc)
        };
    }

    private static string ValidateGrasshopperFilePath(string requestedPath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("Grasshopper file path cannot be empty.");
        }
        string path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(requestedPath));
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".gh", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ghx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Grasshopper file path must end in .gh or .ghx.");
        }
        if (mustExist && !File.Exists(path))
        {
            throw new FileNotFoundException("Grasshopper file was not found.", path);
        }
        return path;
    }
}
