using System;

namespace RhinoMCPPlugin;

/// <summary>
/// Marks a method on RhinoMCPFunctions as the handler for a JSON command type.
/// The reflection-based registry in RhinoMCPFunctions.GetDispatchTable() uses
/// these attributes to build the dispatch table at startup. To add a new command,
/// add a new method with this attribute — no other wiring needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpCommandAttribute : Attribute
{
    /// <summary>The JSON command type, e.g. "create_object". Conventionally snake_case.</summary>
    public string Name { get; }

    /// <summary>
    /// If true, the command is introspective and the dispatcher does not create
    /// a Rhino undo record.
    /// Settable so call sites can use named-argument syntax: [McpCommand("foo", ReadOnly = true)].
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// If false, a mutating command is not wrapped in a Rhino document undo record.
    /// Lifecycle operations such as process shutdown are mutating but cannot be undone.
    /// </summary>
    public bool Undoable { get; set; } = true;

    public McpCommandAttribute(string name)
    {
        Name = name;
    }
}
