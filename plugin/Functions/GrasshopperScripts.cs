using System;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;

namespace RhinoMCPPlugin.Functions;

public partial class RhinoMCPFunctions
{
    private const string RhinoCodeScriptInterface = "RhinoCodePlatform.GH.IScriptComponent";

    [McpCommand("gh_get_script_source", ReadOnly = true)]
    public JObject GhGetScriptSource(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        var obj = FindGhObject(doc, parameters);
        var script = GetScriptComponentAccess(obj);
        string source = script.Text.GetValue(obj)?.ToString() ?? "";

        return new JObject
        {
            ["instance_id"] = obj.InstanceGuid.ToString(),
            ["name"] = obj.Name,
            ["nickname"] = obj.NickName,
            ["language"] = GetScriptLanguage(obj, script.Interface),
            ["source"] = source,
            ["source_length"] = source.Length
        };
    }

    [McpCommand("gh_set_script_source")]
    public JObject GhSetScriptSource(JObject parameters)
    {
        var doc = GetActiveGrasshopperDocument();
        string source = parameters["source"]?.ToString()
            ?? throw new ArgumentException("source is required.");
        var obj = FindGhObject(doc, parameters);
        var script = GetScriptComponentAccess(obj);
        string language = GetScriptLanguage(obj, script.Interface);
        string expectedLanguage = OptionalString(parameters, "expected_language");
        if (!string.IsNullOrWhiteSpace(expectedLanguage) &&
            !ScriptLanguageMatches(language, expectedLanguage))
        {
            throw new InvalidOperationException(
                $"Script component language is '{language}', not expected language '{expectedLanguage}'.");
        }

        script.Text.SetValue(obj, source);
        doc.Modified();
        obj.ExpireSolution(true);
        bool recompute = OptionalBool(parameters, "recompute", true);
        if (recompute)
        {
            RunGrasshopperSolution(doc, false);
        }
        RedrawGrasshopperCanvas();

        return new JObject
        {
            ["instance_id"] = obj.InstanceGuid.ToString(),
            ["name"] = obj.Name,
            ["nickname"] = obj.NickName,
            ["language"] = language,
            ["source_length"] = source.Length,
            ["recomputed"] = recompute,
            ["runtime_messages"] = obj is IGH_ActiveObject active
                ? RuntimeMessagesToJson(active)
                : new JArray(),
            ["message"] = $"Updated source for script component '{obj.NickName}'"
        };
    }

    private static (Type Interface, PropertyInfo Text) GetScriptComponentAccess(IGH_DocumentObject obj)
    {
        var scriptInterface = obj.GetType().GetInterfaces()
            .FirstOrDefault(candidate => candidate.FullName == RhinoCodeScriptInterface);
        var textProperty = scriptInterface?.GetProperty("Text");
        if (scriptInterface == null || textProperty == null || !textProperty.CanRead || !textProperty.CanWrite)
        {
            throw new InvalidOperationException(
                $"Grasshopper object '{obj.NickName}' ({obj.GetType().FullName}) is not a supported Rhino 8 C# or Python script component.");
        }
        return (scriptInterface, textProperty);
    }

    private static string GetScriptLanguage(IGH_DocumentObject obj, Type scriptInterface)
    {
        object language = scriptInterface.GetProperty("LanguageSpec")?.GetValue(obj);
        if (language == null)
        {
            return obj.Name ?? obj.GetType().Name;
        }
        var languageType = language.GetType();
        foreach (string propertyName in new[] { "Name", "Language", "Id" })
        {
            object value = languageType.GetProperty(propertyName)?.GetValue(language);
            if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
            {
                return value.ToString();
            }
        }
        return language.ToString() ?? obj.Name ?? obj.GetType().Name;
    }

    private static bool ScriptLanguageMatches(string actual, string expected)
    {
        static string Normalize(string value)
        {
            return new string((value ?? "")
                .Replace("#", "sharp", StringComparison.OrdinalIgnoreCase)
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        }

        string normalizedActual = Normalize(actual);
        string normalizedExpected = Normalize(expected);
        return normalizedExpected.Length > 0 && normalizedActual.Contains(normalizedExpected);
    }
}
