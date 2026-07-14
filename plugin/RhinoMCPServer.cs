using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino.DocObjects;
using rhinomcp.Serializers;
using JsonException = Newtonsoft.Json.JsonException;
using Eto.Forms;
using RhinoMCPPlugin.Functions;

namespace RhinoMCPPlugin
{
    public class RhinoMCPServer
    {
        // Wire framing: a 4-byte big-endian length header followed by UTF-8
        // JSON, in both directions. Legacy (pre-framing) clients send bare
        // JSON instead; the first byte of a connection decides which protocol
        // that connection speaks. The size cap bounds memory per frame and
        // keeps the header's first byte below any byte a legacy client could
        // open with ('{' or whitespace), so the sniff is unambiguous.
        private const int FrameHeaderSize = 4;
        private const int MaxFrameSize = 64 * 1024 * 1024;
        private const int UiBusyThresholdMilliseconds = 2000;
        private static readonly int SyncWaitMilliseconds = ReadBoundedEnvironmentInteger(
            "RHINO_MCP_SYNC_WAIT_MS", 5000, 250, 14000);

        private enum ClientProtocol
        {
            Undecided,
            Framed,
            Legacy
        }

        private string host;
        private int port;
        private bool running;
        private TcpListener listener;
        private Thread serverThread;
        private Thread operationThread;
        private readonly ConcurrentQueue<QueuedBridgeOperation> operationQueue = new();
        private readonly AutoResetEvent operationSignal = new(false);
        private readonly object lockObject = new object();
        private readonly object healthLock = new object();
        private RhinoMCPFunctions handler;
        private BridgeOperation currentOperation;
        private DateTime listenerStartedAtUtc;
        private DateTime? lastUiHeartbeatUtc;
        private bool grasshopperReady;

        private sealed class QueuedBridgeOperation
        {
            public BridgeOperation Operation { get; init; }
            public JObject Command { get; init; }
        }

        public RhinoMCPServer(string host = "127.0.0.1", int port = 1999)
        {
            this.host = host;
            this.port = port;
            this.running = false;
            this.listener = null;
            this.serverThread = null;
            this.handler = new RhinoMCPFunctions();
        }


        public void Start()
        {
            lock (lockObject)
            {
                if (running)
                {
                    RhinoApp.WriteLine("Server is already running");
                    return;
                }

                running = true;
                listenerStartedAtUtc = DateTime.UtcNow;
                lastUiHeartbeatUtc = null;
                grasshopperReady = false;
            }

            try
            {
                // Create TCP listener
                IPAddress ipAddress = IPAddress.Parse(host);
                listener = new TcpListener(ipAddress, port);
                listener.Start();

                // Start server thread
                serverThread = new Thread(ServerLoop);
                serverThread.IsBackground = true;
                serverThread.Start();

                // Potentially long commands are acknowledged immediately and
                // executed one-at-a-time by this worker. The worker still invokes
                // the actual handler on Rhino's UI thread; it only decouples socket
                // waiting from UI execution and never runs Grasshopper in parallel.
                operationThread = new Thread(OperationLoop);
                operationThread.IsBackground = true;
                operationThread.Start();

                RhinoApp.Idle += OnRhinoIdle;

                RhinoApp.WriteLine($"RhinoMCP server started on {host}:{port}");
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"Failed to start server: {e.Message}");
                Stop();
            }
        }

        public void Stop()
        {
            lock (lockObject)
            {
                running = false;
            }

            RhinoApp.Idle -= OnRhinoIdle;

            // Close listener
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                    // Ignore errors on closing
                }
                listener = null;
            }

            // Wait for thread to finish
            if (serverThread != null && serverThread.IsAlive)
            {
                try
                {
                    serverThread.Join(1000); // Wait up to 1 second
                }
                catch
                {
                    // Ignore errors on join
                }
                serverThread = null;
            }

            operationSignal.Set();
            if (operationThread != null && operationThread.IsAlive)
            {
                try
                {
                    operationThread.Join(1000);
                }
                catch
                {
                    // A modal may still own the UI thread. The operation thread is
                    // background-only and will not keep Rhino alive.
                }
                operationThread = null;
            }

            RhinoApp.WriteLine("RhinoMCP server stopped");
        }

        private void ServerLoop()
        {
            RhinoApp.WriteLine("Server thread started");

            while (IsRunning())
            {
                try
                {
                    // Blocking accept; Stop() calls listener.Stop() which throws
                    // ObjectDisposedException or a SocketException(Interrupted),
                    // unblocking us cleanly instead of polling+sleeping.
                    TcpClient client = listener.AcceptTcpClient();
                    RhinoApp.WriteLine($"Connected to client: {client.Client.RemoteEndPoint}");

                    Thread clientThread = new Thread(() => HandleClient(client));
                    clientThread.IsBackground = true;
                    clientThread.Start();
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException ex) when (!IsRunning() || ex.SocketErrorCode == SocketError.Interrupted)
                {
                    break;
                }
                catch (Exception e)
                {
                    RhinoApp.WriteLine($"Error in server loop: {e.Message}");
                    if (!IsRunning()) break;
                    Thread.Sleep(500);
                }
            }

            RhinoApp.WriteLine("Server thread stopped");
        }

        public bool IsRunning()
        {
            lock (lockObject)
            {
                return running;
            }
        }

        private void HandleClient(TcpClient client)
        {
            RhinoApp.WriteLine("Client handler started");

            byte[] buffer = new byte[8192];
            var pending = new List<byte>();
            ClientProtocol protocol = ClientProtocol.Undecided;

            try
            {
                NetworkStream stream = client.GetStream();

                while (IsRunning())
                {
                    try
                    {
                        // Check if there's data available to read
                        if (client.Available > 0 || stream.DataAvailable)
                        {
                            int bytesRead = stream.Read(buffer, 0, buffer.Length);
                            if (bytesRead == 0)
                            {
                                RhinoApp.WriteLine("Client disconnected");
                                break;
                            }

                            for (int i = 0; i < bytesRead; i++)
                            {
                                pending.Add(buffer[i]);
                            }

                            if (protocol == ClientProtocol.Undecided)
                            {
                                protocol = SniffProtocol(pending[0]);
                            }

                            if (protocol == ClientProtocol.Framed)
                            {
                                // Drain every complete frame in the buffer so
                                // pipelined commands all execute in order
                                // instead of wedging the connection.
                                while (TryExtractFrame(pending, out string framedJson))
                                {
                                    JObject framedCommand;
                                    try
                                    {
                                        framedCommand = JObject.Parse(framedJson);
                                    }
                                    catch (JsonException ex)
                                    {
                                        // The frame was well-formed (its length
                                        // matched) but the payload isn't valid
                                        // JSON. Framing already located the next
                                        // frame, so answer this one with an error
                                        // and keep the connection instead of
                                        // dropping every command queued behind it.
                                        // Routed through the UI thread like every
                                        // other write so responses stay
                                        // single-writer and in send order.
                                        string detail = ex.Message;
                                        RhinoApp.InvokeOnUiThread(new Action(() =>
                                        {
                                            try
                                            {
                                                WriteMessage(stream, new JObject
                                                {
                                                    ["status"] = "error",
                                                    ["message"] = $"Invalid JSON in framed message: {detail}"
                                                }.ToString(), framed: true);
                                            }
                                            catch
                                            {
                                                RhinoApp.WriteLine("Failed to send error response - client disconnected");
                                            }
                                        }));
                                        continue;
                                    }
                                    DispatchCommand(framedCommand, stream, framed: true);
                                }
                            }
                            else
                            {
                                // Legacy client: bare JSON, no framing. Keep
                                // the original semantics: try to parse the
                                // whole accumulation, wait for more on failure.
                                string incompleteData = Encoding.UTF8.GetString(pending.ToArray());
                                try
                                {
                                    JObject command = JObject.Parse(incompleteData);
                                    pending.Clear();
                                    DispatchCommand(command, stream, framed: false);
                                }
                                catch (JsonException)
                                {
                                    // Incomplete JSON data, wait for more
                                }
                            }
                        }
                        else
                        {
                            // No data available, sleep a bit to prevent CPU overuse
                            Thread.Sleep(50);
                        }
                    }
                    catch (Exception e)
                    {
                        RhinoApp.WriteLine($"Error receiving data: {e.Message}");
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"Error in client handler: {e.Message}");
            }
            finally
            {
                try
                {
                    client.Close();
                }
                catch
                {
                    // Ignore errors on close
                }
                RhinoApp.WriteLine("Client handler stopped");
            }
        }

        private static ClientProtocol SniffProtocol(byte firstByte)
        {
            // Legacy clients open with bare JSON: '{', possibly preceded by
            // whitespace. A frame header's first byte is the high byte of the
            // message length, which MaxFrameSize caps at 0x04 — below '{'
            // (0x7B) and every whitespace byte (0x09, 0x0A, 0x0D, 0x20).
            if (firstByte == (byte)'{' || firstByte == (byte)' ' ||
                firstByte == (byte)'\t' || firstByte == (byte)'\r' ||
                firstByte == (byte)'\n')
            {
                return ClientProtocol.Legacy;
            }
            return ClientProtocol.Framed;
        }

        private static bool TryExtractFrame(List<byte> pending, out string payloadJson)
        {
            // Frame de-chunking only: pulls the bytes of one complete frame off
            // the buffer and returns them as a string. JSON parsing happens in
            // the caller, on purpose — a well-framed message whose payload is
            // bad JSON should be a per-message error, not a dropped connection,
            // and that's only recoverable once the frame bytes are consumed.
            payloadJson = null;
            if (pending.Count < FrameHeaderSize) return false;

            int frameLength = (pending[0] << 24) | (pending[1] << 16) |
                              (pending[2] << 8) | pending[3];
            if (frameLength <= 0 || frameLength > MaxFrameSize)
            {
                // A bad length means framing sync is lost: we can't tell where
                // the next frame starts, so this one stays fatal. The caller
                // logs and drops the connection.
                throw new InvalidOperationException(
                    $"Invalid frame length {frameLength} (limit {MaxFrameSize} bytes)");
            }

            if (pending.Count < FrameHeaderSize + frameLength) return false;

            payloadJson = Encoding.UTF8.GetString(
                pending.GetRange(FrameHeaderSize, frameLength).ToArray());
            pending.RemoveRange(0, FrameHeaderSize + frameLength);
            return true;
        }

        private void DispatchCommand(JObject command, NetworkStream stream, bool framed)
        {
            string cmdType = command["type"]?.ToString();

            // These control-plane reads intentionally bypass Rhino's UI thread so
            // they remain available while Grasshopper is solving or showing a modal.
            if (cmdType is "get_operation_status" or "cancel_operation" or "get_bridge_health")
            {
                try
                {
                    JObject response = ExecuteOperationControlCommand(command);
                    WriteMessage(stream, JsonConvert.SerializeObject(response), framed);
                }
                catch (Exception e)
                {
                    WriteMessage(stream, new JObject
                    {
                        ["status"] = "error",
                        ["message"] = e.Message
                    }.ToString(), framed);
                }
                return;
            }

            // Capability discovery is reflection-only. Keeping it off the UI queue
            // lets clients distinguish a live bridge from a busy Rhino UI thread.
            if (cmdType == "describe_capabilities")
            {
                try
                {
                    WriteMessage(stream, new JObject
                    {
                        ["status"] = "success",
                        ["result"] = handler.DescribeCapabilities(new JObject())
                    }.ToString(), framed);
                }
                catch (Exception e)
                {
                    WriteMessage(stream, new JObject
                    {
                        ["status"] = "error",
                        ["message"] = e.Message
                    }.ToString(), framed);
                }
                return;
            }

            if (RhinoShutdownLifecycle.IsShutdownInProgress && cmdType != "shutdown_rhino")
            {
                WriteMessage(stream, new JObject
                {
                    ["status"] = "error",
                    ["message"] = "Rhino shutdown is already in progress. Only bridge health and operation status are available."
                }.ToString(), framed);
                return;
            }

            JObject execution = command["execution"] as JObject;
            bool asynchronous = string.Equals(
                execution?["mode"]?.ToString(),
                "async",
                StringComparison.OrdinalIgnoreCase);
            string requestId = execution?["request_id"]?.ToString();
            var registration = BridgeOperationRegistry.GetOrCreate(
                cmdType,
                requestId,
                command);

            if (asynchronous)
            {
                JObject accepted = new JObject
                {
                    ["status"] = "success",
                    ["result"] = registration.Operation.ToJson(includeResult: true)
                };

                // Acknowledge before touching the UI thread. This is the key
                // guarantee that prevents a modal or long solution from consuming
                // the socket timeout.
                WriteMessage(stream, JsonConvert.SerializeObject(accepted), framed);
                if (registration.Created)
                {
                    EnqueueOperation(registration.Operation, command);
                }
                return;
            }

            // Synchronous callers retain the fast-path response shape. If Rhino's
            // UI does not pick the request up promptly, return the operation record
            // before the client's socket timeout instead of reporting an ambiguous
            // failure while leaving an untracked delegate behind.
            if (registration.Created)
            {
                EnqueueOperation(registration.Operation, command);
            }

            registration.Operation.WaitForTerminal(SyncWaitMilliseconds);
            JObject syncResponse = registration.Operation.ToCommandResponse();
            if (!registration.Operation.IsTerminal && syncResponse["result"] is JObject pending)
            {
                pending["poll_after_ms"] = 500;
                if (!(pending["modal_detected"]?.ToObject<bool>() ?? false))
                {
                    pending["message"] =
                        "Rhino accepted the request but its UI thread is still busy. Poll get_operation_status; do not retry a mutating command.";
                }
            }
            WriteMessage(stream, JsonConvert.SerializeObject(syncResponse), framed);
        }

        private void EnqueueOperation(BridgeOperation operation, JObject command)
        {
            operationQueue.Enqueue(new QueuedBridgeOperation
            {
                Operation = operation,
                Command = (JObject)command.DeepClone()
            });
            operationSignal.Set();
        }

        private JObject ExecuteOperationControlCommand(JObject command)
        {
            string cmdType = command["type"]?.ToString();
            JObject parameters = command["params"] as JObject ?? new JObject();
            if (cmdType == "get_bridge_health")
            {
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = GetBridgeHealth()
                };
            }
            string operationId = parameters["operation_id"]?.ToString();
            JObject result = cmdType == "cancel_operation"
                ? BridgeOperationRegistry.Cancel(operationId)
                : BridgeOperationRegistry.GetStatus(
                    operationId,
                    parameters["include_result"]?.ToObject<bool>() ?? true);
            return new JObject
            {
                ["status"] = "success",
                ["result"] = result
            };
        }

        private void OperationLoop()
        {
            while (IsRunning() || !operationQueue.IsEmpty)
            {
                if (!operationQueue.TryDequeue(out QueuedBridgeOperation queued))
                {
                    operationSignal.WaitOne(250);
                    continue;
                }

                SetCurrentOperation(queued.Operation);
                try
                {
                    RhinoApp.InvokeOnUiThread(new Action(() =>
                    {
                        // Stay genuinely queued until the UI delegate begins. A
                        // cancellation received while Rhino is busy can therefore
                        // prevent the mutation from ever executing.
                        if (!queued.Operation.TryStart()) return;
                        JObject response = ExecuteCommand(queued.Command);
                        if (response?["status"]?.ToString() == "success")
                        {
                            queued.Operation.Complete(response["result"] as JObject ?? new JObject());
                            if (queued.Operation.Command == "shutdown_rhino")
                            {
                                RhinoShutdownLifecycle.ScheduleExit();
                            }
                        }
                        else
                        {
                            queued.Operation.Fail(response?["message"]?.ToString() ?? "Unknown Rhino operation error");
                        }
                    }));
                }
                catch (Exception e)
                {
                    queued.Operation.Fail(e.Message);
                }
                finally
                {
                    SetCurrentOperation(null);
                }
            }
        }

        private void OnRhinoIdle(object sender, EventArgs e)
        {
            lock (healthLock)
            {
                lastUiHeartbeatUtc = DateTime.UtcNow;
                try
                {
                    grasshopperReady = Grasshopper.Instances.ActiveCanvas != null;
                }
                catch
                {
                    grasshopperReady = false;
                }
            }
        }

        private void SetCurrentOperation(BridgeOperation operation)
        {
            lock (healthLock)
            {
                currentOperation = operation;
            }
        }

        private JObject GetBridgeHealth()
        {
            DateTime now = DateTime.UtcNow;
            DateTime? heartbeat;
            bool ghReady;
            BridgeOperation operation;
            lock (healthLock)
            {
                heartbeat = lastUiHeartbeatUtc;
                ghReady = grasshopperReady;
                operation = currentOperation;
            }

            long? heartbeatAge = heartbeat.HasValue
                ? Math.Max(0, (long)(now - heartbeat.Value).TotalMilliseconds)
                : null;
            JObject modal = RhinoModalDetector.TryGetModalWindow();
            bool uiResponsive = operation == null &&
                heartbeatAge.HasValue &&
                heartbeatAge.Value <= UiBusyThresholdMilliseconds;

            string readinessState;
            if (RhinoShutdownLifecycle.IsShutdownInProgress)
            {
                readinessState = "shutdown_in_progress";
            }
            else if (modal != null)
            {
                readinessState = "blocked_by_modal";
            }
            else if (!ghReady && !uiResponsive)
            {
                readinessState = "grasshopper_loading";
            }
            else if (!uiResponsive)
            {
                readinessState = "rhino_ui_busy";
            }
            else if (ghReady)
            {
                readinessState = "grasshopper_ready";
            }
            else
            {
                readinessState = "listener_started";
            }

            return new JObject
            {
                ["state"] = readinessState,
                ["listener_started"] = IsRunning(),
                ["listener_started_at_utc"] = listenerStartedAtUtc.ToString("O"),
                ["process_id"] = Process.GetCurrentProcess().Id,
                ["rhino_ui_responsive"] = uiResponsive,
                ["last_ui_heartbeat_utc"] = heartbeat?.ToString("O"),
                ["ui_heartbeat_age_ms"] = heartbeatAge.HasValue
                    ? JToken.FromObject(heartbeatAge.Value)
                    : JValue.CreateNull(),
                ["grasshopper_ready"] = ghReady,
                ["shutdown_in_progress"] = RhinoShutdownLifecycle.IsShutdownInProgress,
                ["shutdown_state"] = RhinoShutdownLifecycle.State,
                ["queued_operation_count"] = operationQueue.Count,
                ["current_operation"] = operation?.ToJson(includeResult: false),
                ["modal_detected"] = modal != null,
                ["modal"] = modal
            };
        }

        private static int ReadBoundedEnvironmentInteger(
            string name,
            int defaultValue,
            int minimum,
            int maximum)
        {
            string value = System.Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out int parsed)
                ? Math.Max(minimum, Math.Min(maximum, parsed))
                : defaultValue;
        }

        private static void WriteMessage(NetworkStream stream, string json, bool framed)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            if (!framed)
            {
                stream.Write(payload, 0, payload.Length);
                return;
            }

            // One write for header + payload keeps the frame contiguous.
            byte[] message = new byte[FrameHeaderSize + payload.Length];
            message[0] = (byte)((payload.Length >> 24) & 0xFF);
            message[1] = (byte)((payload.Length >> 16) & 0xFF);
            message[2] = (byte)((payload.Length >> 8) & 0xFF);
            message[3] = (byte)(payload.Length & 0xFF);
            Buffer.BlockCopy(payload, 0, message, FrameHeaderSize, payload.Length);
            stream.Write(message, 0, message.Length);
        }

        private JObject ExecuteCommand(JObject command)
        {
            try
            {
                string cmdType = command["type"]?.ToString();
                JObject parameters = command["params"] as JObject ?? new JObject();
                // Opt-in perception flag, carried on the envelope (not in params)
                // so it never collides with a command's own parameters. Defaults
                // off, so behavior is unchanged unless a client asks for it.
                bool includeDelta = command["include_delta"]?.ToObject<bool>() ?? false;
                bool includeHealth = command["include_health"]?.ToObject<bool>() ?? false;

                RhinoApp.WriteLine($"Executing command: {cmdType}");

                JObject result = ExecuteCommandInternal(cmdType, parameters, includeDelta, includeHealth);

                RhinoApp.WriteLine("Command execution complete");
                return result;
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"Error executing command: {e.Message}");
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = e.Message
                };
            }
        }

        private JObject ExecuteCommandInternal(string cmdType, JObject parameters, bool includeDelta, bool includeHealth)
        {
            // Reflection-discovered dispatch table — see Functions/_Registry.cs.
            // Adding a new command means adding a [McpCommand("name")] method on
            // RhinoMCPFunctions; no edits to this file are required.
            var dispatch = this.handler.GetDispatchTable();

            if (!dispatch.TryGetValue(cmdType, out var entry))
            {
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = $"Unknown command type: {cmdType}"
                };
            }

            var doc = RhinoDoc.ActiveDoc;
            bool needsUndo = !entry.ReadOnly && entry.Undoable;

            // A change-delta or health report only makes sense for a mutating
            // command, and only when the client asked for it. Snapshot the
            // document's object ids just before the handler runs so we can diff
            // against the post-state. This is the one place every mutator funnels
            // through, so it covers them all (including multi-effect ones like
            // run_command and booleans with delete_sources) with no per-handler
            // changes.
            bool wantDelta = includeDelta && needsUndo && doc != null;
            bool wantHealth = includeHealth && needsUndo && doc != null;
            HashSet<Guid> idsBefore = (wantDelta || wantHealth)
                ? this.handler.SnapshotObjectIds(doc) : null;

            uint record = 0;
            if (needsUndo)
            {
                record = doc.BeginUndoRecord($"MCP: {cmdType}");
            }

            try
            {
                JObject result = entry.Handler(parameters);
                if ((wantDelta || wantHealth) && result != null)
                {
                    var idsAfter = this.handler.SnapshotObjectIds(doc);
                    if (wantDelta) result["_delta"] = this.handler.BuildDelta(idsBefore, idsAfter);
                    // Health walks RhinoCommon geometry, so keep it from ever
                    // turning a successful mutation into a failure: a hiccup
                    // degrades to no _health rather than throwing out the result.
                    if (wantHealth)
                    {
                        try { result["_health"] = this.handler.BuildHealth(doc, idsBefore, idsAfter); }
                        catch (Exception he) { RhinoApp.WriteLine($"health check skipped: {he.Message}"); }
                    }
                }
                return new JObject
                {
                    ["status"] = "success",
                    ["result"] = result
                };
            }
            catch (Exception e)
            {
                RhinoApp.WriteLine($"Error in handler: {e.Message}");
                return new JObject
                {
                    ["status"] = "error",
                    ["message"] = e.Message
                };
            }
            finally
            {
                if (needsUndo)
                {
                    doc.EndUndoRecord(record);
                }
            }
        }
    }
}
