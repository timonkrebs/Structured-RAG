using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace StructuredRAG.Mcp.Resources;

/// <summary>
/// ChatGPT Apps-SDK widget templates. Each is a self-contained HTML/JS document
/// (embedded in the assembly) served with the <c>text/html+skybridge</c> mime type;
/// ChatGPT loads it via resources/read and renders it in a sandboxed iframe whenever
/// a tool carrying the matching <c>openai/outputTemplate</c> meta returns. All widget
/// interaction is deterministic client-side JS — the zero-inference principle of the
/// serving layer extends to the UI. Other MCP clients simply ignore these resources.
/// </summary>
[McpServerResourceType]
public static class WidgetResources
{
    private const string SkybridgeMimeType = "text/html+skybridge";

    private const string PlannerDescription =
        "Interactive semester plan builder: pick eligible modules, track ECTS against a target, " +
        "see same-weekday hints and why blocked modules are blocked.";

    private const string ComparerDescription =
        "Side-by-side comparison table for 2-4 modules: ECTS, semesters, languages, weekdays, " +
        "shared tags, prerequisites and summaries.";

    [McpServerResource(UriTemplate = "ui://widget/semester-planner.html", Name = "Semester plan builder widget", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for plan_semester results.")]
    [McpMeta("openai/widgetDescription", PlannerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    public static TextResourceContents SemesterPlanner() =>
        WidgetContents("ui://widget/semester-planner.html", "semester-planner.html", PlannerDescription);

    [McpServerResource(UriTemplate = "ui://widget/module-comparer.html", Name = "Module comparer widget", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for compare_modules results.")]
    [McpMeta("openai/widgetDescription", ComparerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    public static TextResourceContents ModuleComparer() =>
        WidgetContents("ui://widget/module-comparer.html", "module-comparer.html", ComparerDescription);

    // The [McpMeta] attributes cover resources/list; resources/read only carries
    // contents-level meta when the method returns TextResourceContents itself,
    // so the same keys are set here explicitly. Built fresh per call: JsonNode
    // trees are single-parent and must not be shared across responses.
    private static TextResourceContents WidgetContents(string uri, string fileName, string description) => new()
    {
        Uri = uri,
        MimeType = SkybridgeMimeType,
        Text = LoadWidgetHtml(fileName),
        Meta = new JsonObject
        {
            ["openai/widgetDescription"] = description,
            ["openai/widgetPrefersBorder"] = true,
            ["openai/widgetCSP"] = new JsonObject
            {
                ["connect_domains"] = new JsonArray(),
                ["resource_domains"] = new JsonArray(),
            },
        },
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> HtmlCache = new();

    private static string LoadWidgetHtml(string fileName) => HtmlCache.GetOrAdd(fileName, static f =>
    {
        var assembly = typeof(WidgetResources).Assembly;
        var resourceName = $"StructuredRAG.Mcp.Widgets.{f}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded widget template '{resourceName}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
