using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    [McpCommand("get_operation_status", ReadOnly = true)]
    public JObject GetOperationStatus(JObject parameters)
    {
        string operationId = OptionalString(parameters, "operation_id");
        bool includeResult = OptionalBool(parameters, "include_result", true);
        return BridgeOperationRegistry.GetStatus(operationId, includeResult);
    }

    [McpCommand("cancel_operation", ReadOnly = true)]
    public JObject CancelOperation(JObject parameters)
    {
        return BridgeOperationRegistry.Cancel(OptionalString(parameters, "operation_id"));
    }
}
