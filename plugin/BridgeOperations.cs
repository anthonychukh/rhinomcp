using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin;

internal sealed class BridgeOperation
{
    private readonly object sync = new();
    private readonly ManualResetEventSlim terminalSignal = new(false);
    private readonly JToken requestPayload;
    private string state = "queued";
    private string message = "Queued for Rhino's UI thread";
    private JObject result;
    private string error;
    private DateTime? startedAtUtc;
    private DateTime? completedAtUtc;

    public BridgeOperation(string command, string requestId, JToken requestPayload)
    {
        OperationId = Guid.NewGuid().ToString();
        Command = command;
        RequestId = requestId;
        this.requestPayload = requestPayload?.DeepClone() ?? new JObject();
        CreatedAtUtc = DateTime.UtcNow;
    }

    public string OperationId { get; }
    public string Command { get; }
    public string RequestId { get; }
    public DateTime CreatedAtUtc { get; }

    public bool MatchesRequest(string command, JToken payload)
    {
        return string.Equals(Command, command, StringComparison.Ordinal) &&
            JToken.DeepEquals(requestPayload, payload ?? new JObject());
    }

    public bool TryStart()
    {
        lock (sync)
        {
            if (state == "cancelled") return false;
            state = "running";
            message = "Executing on Rhino's UI thread";
            startedAtUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void Complete(JObject value)
    {
        lock (sync)
        {
            result = value == null ? new JObject() : (JObject)value.DeepClone();
            state = "completed";
            message = "Operation completed";
            completedAtUtc = DateTime.UtcNow;
            terminalSignal.Set();
        }
    }

    public void Fail(string detail)
    {
        lock (sync)
        {
            error = detail ?? "Unknown operation error";
            state = "failed";
            message = "Operation failed";
            completedAtUtc = DateTime.UtcNow;
            terminalSignal.Set();
        }
    }

    public string RequestCancel()
    {
        lock (sync)
        {
            if (state == "queued")
            {
                state = "cancelled";
                message = "Cancelled before execution";
                completedAtUtc = DateTime.UtcNow;
                terminalSignal.Set();
            }
            else if (state == "running")
            {
                state = "cancel_requested";
                message = "Cancellation requested; waiting for a safe Grasshopper cancellation point";
            }
            return state;
        }
    }

    public bool IsTerminal
    {
        get
        {
            lock (sync)
            {
                return state is "completed" or "failed" or "cancelled";
            }
        }
    }

    public DateTime? CompletedAtUtc
    {
        get
        {
            lock (sync) return completedAtUtc;
        }
    }

    public bool WaitForTerminal(int timeoutMilliseconds)
    {
        return terminalSignal.Wait(Math.Max(0, timeoutMilliseconds));
    }

    public JObject ToCommandResponse()
    {
        lock (sync)
        {
            if (state == "completed")
            {
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = result?.DeepClone() ?? new JObject()
                };
            }
            if (state == "failed")
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = error ?? "Unknown operation error"
                };
            }
            if (state == "cancelled")
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Operation was cancelled before execution"
                };
            }
            return new JObject
            {
                ["status"] = "success",
                ["result"] = ToJsonLocked(includeResult: true)
            };
        }
    }

    public JObject ToJson(bool includeResult)
    {
        lock (sync)
        {
            return ToJsonLocked(includeResult);
        }
    }

    private JObject ToJsonLocked(bool includeResult)
    {
            DateTime end = completedAtUtc ?? DateTime.UtcNow;
            DateTime start = startedAtUtc ?? CreatedAtUtc;
            string visibleState = state;
            JObject modal = null;
            if (state is "queued" or "running" or "cancel_requested")
            {
                modal = RhinoModalDetector.TryGetModalWindow();
                if (modal != null) visibleState = "waiting_for_user";
            }

            var json = new JObject
            {
                ["operation_id"] = OperationId,
                ["request_id"] = RequestId,
                ["process_id"] = Process.GetCurrentProcess().Id,
                ["command"] = Command,
                ["state"] = visibleState,
                ["execution_state"] = state,
                ["terminal"] = state is "completed" or "failed" or "cancelled",
                ["created_at_utc"] = CreatedAtUtc.ToString("O"),
                ["started_at_utc"] = startedAtUtc?.ToString("O"),
                ["completed_at_utc"] = completedAtUtc?.ToString("O"),
                ["elapsed_ms"] = Math.Max(0, (long)(end - start).TotalMilliseconds),
                ["message"] = modal == null ? message : "Rhino is showing a modal window that may require user attention",
                ["modal_detected"] = modal != null,
                ["modal"] = modal
            };

            if (!string.IsNullOrWhiteSpace(error)) json["error"] = error;
            if (includeResult && result != null) json["result"] = result.DeepClone();
            return json;
    }
}

internal static class BridgeOperationRegistry
{
    private static readonly object RegistrationLock = new();
    private static readonly ConcurrentDictionary<string, BridgeOperation> Operations = new();
    private static readonly ConcurrentDictionary<string, string> RequestIds = new();
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(15);

    public static (BridgeOperation Operation, bool Created) GetOrCreate(
        string command,
        string requestId,
        JToken requestPayload)
    {
        lock (RegistrationLock)
        {
            Cleanup();
            if (string.IsNullOrWhiteSpace(requestId))
            {
                requestId = Guid.NewGuid().ToString();
            }
            else if (Guid.TryParse(requestId, out Guid parsedRequestId))
            {
                requestId = parsedRequestId.ToString();
            }
            else
            {
                throw new ArgumentException("execution.request_id must be a GUID.");
            }
            if (RequestIds.TryGetValue(requestId, out string existingId) &&
                Operations.TryGetValue(existingId, out BridgeOperation existing))
            {
                if (!existing.MatchesRequest(command, requestPayload))
                {
                    throw new InvalidOperationException(
                        $"execution.request_id '{requestId}' is already associated with a different request payload.");
                }
                return (existing, false);
            }

            var operation = new BridgeOperation(command, requestId, requestPayload);
            Operations[operation.OperationId] = operation;
            RequestIds[requestId] = operation.OperationId;
            return (operation, true);
        }
    }

    public static BridgeOperation Get(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId) ||
            !Operations.TryGetValue(operationId, out BridgeOperation operation))
        {
            throw new InvalidOperationException($"Unknown operation_id: {operationId}");
        }
        return operation;
    }

    public static JObject GetStatus(string operationId, bool includeResult = true)
    {
        return Get(operationId).ToJson(includeResult);
    }

    public static JObject Cancel(string operationId)
    {
        BridgeOperation operation = Get(operationId);
        string state = operation.RequestCancel();

        // Grasshopper cancellation is cooperative. RequestAbortSolution sets the
        // document's abort flag; the currently executing component may still have
        // to return before Grasshopper can honor it.
        if (state == "cancel_requested")
        {
            try
            {
                Grasshopper.Instances.ActiveCanvas?.Document?.RequestAbortSolution();
            }
            catch
            {
                // Status still records the request even when no active GH document
                // is available or the current operation is document IO.
            }
        }
        return operation.ToJson(includeResult: false);
    }

    private static void Cleanup()
    {
        DateTime cutoff = DateTime.UtcNow - Retention;
        foreach (KeyValuePair<string, BridgeOperation> pair in Operations)
        {
            DateTime? completed = pair.Value.CompletedAtUtc;
            if (!pair.Value.IsTerminal || !completed.HasValue || completed.Value >= cutoff) continue;
            if (Operations.TryRemove(pair.Key, out BridgeOperation removed))
            {
                RequestIds.TryRemove(removed.RequestId, out _);
            }
        }
    }
}

internal static class RhinoModalDetector
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private const uint GwOwner = 4;

    public static JObject TryGetModalWindow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        uint currentProcessId = (uint)Process.GetCurrentProcess().Id;
        JObject found = null;
        try
        {
            EnumWindows((hWnd, _) =>
            {
                bool visible = IsWindowVisible(hWnd);
                GetWindowThreadProcessId(hWnd, out uint processId);
                IntPtr owner = GetWindow(hWnd, GwOwner);
                if (processId != currentProcessId || owner == IntPtr.Zero) return true;

                var title = new StringBuilder(512);
                var className = new StringBuilder(256);
                GetWindowText(hWnd, title, title.Capacity);
                GetClassName(hWnd, className, className.Capacity);
                if (title.Length == 0) return true;

                // Rhino and Grasshopper can have ordinary owned floating windows.
                // Only report standard dialog windows or windows whose owner has
                // been disabled by modal activation.
                bool standardDialog = string.Equals(className.ToString(), "#32770", StringComparison.Ordinal);
                bool ownerDisabled = !IsWindowEnabled(owner);
                if (!visible && !ownerDisabled) return true;
                if (!standardDialog && !ownerDisabled) return true;

                found = new JObject
                {
                    ["title"] = title.ToString(),
                    ["window_class"] = className.ToString()
                };
                return false;
            }, IntPtr.Zero);
        }
        catch
        {
            return null;
        }
        return found;
    }
}
