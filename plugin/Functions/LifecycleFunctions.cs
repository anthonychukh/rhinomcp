using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using Rhino;
using Rhino.FileIO;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("get_bridge_health", ReadOnly = true)]
    public JObject GetBridgeHealth(JObject parameters)
    {
        // RhinoMCPServer handles this command outside the UI thread. This
        // fallback keeps the reflection registry and protocol surface complete.
        return RhinoShutdownLifecycle.BasicStatus();
    }

    [McpCommand("shutdown_rhino", Undoable = false)]
    public JObject ShutdownRhino(JObject parameters)
    {
        return RhinoShutdownLifecycle.Prepare(parameters);
    }
}

internal static class RhinoShutdownLifecycle
{
    private static int shutdownState;
    private static int exitScheduled;
    private static int forceAfterMilliseconds = 15000;

    public static bool IsShutdownInProgress => Volatile.Read(ref shutdownState) != 0;

    public static string State => Volatile.Read(ref shutdownState) switch
    {
        0 => "not_requested",
        1 => "shutdown_accepted",
        2 => "exit_invoked",
        _ => "shutdown_in_progress"
    };

    public static JObject BasicStatus()
    {
        return new JObject
        {
            ["shutdown_in_progress"] = IsShutdownInProgress,
            ["shutdown_state"] = State,
            ["process_id"] = Process.GetCurrentProcess().Id
        };
    }

    public static JObject Prepare(JObject parameters)
    {
        if (IsShutdownInProgress)
        {
            return new JObject
            {
                ["shutdown_accepted"] = true,
                ["shutdown_state"] = State,
                ["already_in_progress"] = true,
                ["expected_disconnect"] = true,
                ["process_id"] = Process.GetCurrentProcess().Id
            };
        }

        string policy = (parameters?["save_changes"]?.ToString() ?? "refuse")
            .Trim()
            .ToLowerInvariant();
        if (policy is not ("refuse" or "save" or "discard"))
        {
            throw new ArgumentException("save_changes must be 'refuse', 'save', or 'discard'.");
        }

        int requestedForceAfter = parameters?["force_after_ms"]?.ToObject<int>() ?? 15000;
        if (requestedForceAfter != 0 && (requestedForceAfter < 1000 || requestedForceAfter > 60000))
        {
            throw new ArgumentOutOfRangeException(
                "force_after_ms",
                "force_after_ms must be 0 (disabled) or between 1000 and 60000.");
        }

        var rhinoDocuments = RhinoDoc.OpenDocuments(includeHeadless: false)
            .Where(document => document != null)
            .ToArray();
        var grasshopperServer = Instances.DocumentServer;
        var grasshopperDocuments = Enumerable.Range(0, grasshopperServer.DocumentCount)
            .Select(index => grasshopperServer[index])
            .Where(document => document != null)
            .ToArray();

        string[] modifiedRhino = rhinoDocuments
            .Where(document => document.Modified)
            .Select(DocumentDisplayName)
            .ToArray();
        string[] modifiedGrasshopper = grasshopperDocuments
            .Where(document => document.IsModified)
            .Select(GrasshopperDocumentDisplayName)
            .ToArray();

        if (policy == "refuse" && (modifiedRhino.Length > 0 || modifiedGrasshopper.Length > 0))
        {
            throw new InvalidOperationException(
                "Shutdown refused because documents have unsaved changes. " +
                $"Rhino: [{string.Join(", ", modifiedRhino)}]; " +
                $"Grasshopper: [{string.Join(", ", modifiedGrasshopper)}]. " +
                "Retry with save_changes='save' or save_changes='discard'.");
        }

        if (policy == "save")
        {
            string[] unsavedRhino = modifiedRhino
                .Where(name => name == "(unsaved)")
                .ToArray();
            string[] unsavedGrasshopper = modifiedGrasshopper
                .Where(name => name == "(unsaved)")
                .ToArray();
            if (unsavedRhino.Length > 0 || unsavedGrasshopper.Length > 0)
            {
                throw new InvalidOperationException(
                    "Shutdown cannot save documents that do not have a file path. " +
                    "Save them explicitly first or use save_changes='discard'.");
            }
        }

        int savedRhinoCount = 0;
        int savedGrasshopperCount = 0;
        if (policy == "save")
        {
            foreach (RhinoDoc document in rhinoDocuments.Where(document => document.Modified))
            {
                if (!document.WriteFile(document.Path, new FileWriteOptions()))
                {
                    throw new InvalidOperationException($"Rhino could not save '{document.Path}'.");
                }
                document.Modified = false;
                savedRhinoCount++;
            }

            foreach (GH_Document document in grasshopperDocuments.Where(document => document.IsModified))
            {
                if (!new GH_DocumentIO(document).Save())
                {
                    throw new InvalidOperationException($"Grasshopper could not save '{document.FilePath}'.");
                }
                document.IsModified = false;
                savedGrasshopperCount++;
            }
        }
        else if (policy == "discard")
        {
            foreach (RhinoDoc document in rhinoDocuments) document.Modified = false;
            foreach (GH_Document document in grasshopperDocuments) document.IsModified = false;
        }

        int closedGrasshopperCount = 0;
        foreach (GH_Document document in grasshopperDocuments)
        {
            document.IsModified = false;
            if (!grasshopperServer.SafeRemoveDocument(document))
            {
                throw new InvalidOperationException(
                    $"Grasshopper refused to close '{GrasshopperDocumentDisplayName(document)}'.");
            }
            closedGrasshopperCount++;
        }
        if (Instances.ActiveCanvas != null)
        {
            Instances.ActiveCanvas.Document = grasshopperServer.NextAvailableDocument();
        }

        forceAfterMilliseconds = requestedForceAfter;
        Interlocked.Exchange(ref shutdownState, 1);
        return new JObject
        {
            ["shutdown_accepted"] = true,
            ["shutdown_state"] = "shutdown_accepted",
            ["already_in_progress"] = false,
            ["expected_disconnect"] = true,
            ["process_id"] = Process.GetCurrentProcess().Id,
            ["save_changes"] = policy,
            ["rhino_document_count"] = rhinoDocuments.Length,
            ["grasshopper_document_count"] = grasshopperDocuments.Length,
            ["saved_rhino_document_count"] = savedRhinoCount,
            ["saved_grasshopper_document_count"] = savedGrasshopperCount,
            ["closed_grasshopper_document_count"] = closedGrasshopperCount,
            ["force_after_ms"] = requestedForceAfter,
            ["message"] = "Shutdown accepted; the Rhino connection is expected to disconnect."
        };
    }

    public static void ScheduleExit()
    {
        if (!IsShutdownInProgress || Interlocked.Exchange(ref exitScheduled, 1) != 0) return;

        if (forceAfterMilliseconds > 0)
        {
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(forceAfterMilliseconds);
                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "RhinoMCP shutdown watchdog"
            };
            watchdog.Start();
        }

        var exitThread = new Thread(() =>
        {
            // Leave enough time for the completed operation state to become
            // observable before Rhino begins tearing down the listener.
            Thread.Sleep(250);
            try
            {
                RhinoApp.InvokeOnUiThread(new Action(() =>
                {
                    Interlocked.Exchange(ref shutdownState, 2);
                    RhinoApp.Exit();
                }));
            }
            catch
            {
                // The watchdog is the final fallback when graceful exit cannot
                // be invoked or Rhino becomes stuck during plug-in unloading.
            }
        })
        {
            IsBackground = true,
            Name = "RhinoMCP graceful shutdown"
        };
        exitThread.Start();
    }

    private static string DocumentDisplayName(RhinoDoc document)
    {
        return string.IsNullOrWhiteSpace(document.Path) ? "(unsaved)" : document.Path;
    }

    private static string GrasshopperDocumentDisplayName(GH_Document document)
    {
        return string.IsNullOrWhiteSpace(document.FilePath) ? "(unsaved)" : document.FilePath;
    }
}
