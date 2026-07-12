using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json.Nodes;

namespace StructuredRAG.Mcp.Resources;

/// <summary>
/// Interactive widget templates, served for two host conventions from the same
/// embedded HTML (the widgets detect the host at runtime):
///
///  - OpenAI Apps SDK (ChatGPT): mime <c>text/html+skybridge</c>, referenced from
///    tools via <c>_meta["openai/outputTemplate"]</c>; the widget talks to the host
///    through <c>window.openai</c>.
///  - MCP Apps extension (Claude, VS Code, ...; SEP-1865, 2026-01-26): mime
///    <c>text/html;profile=mcp-app</c>, referenced via <c>_meta.ui.resourceUri</c>;
///    the widget talks JSON-RPC over postMessage.
///
/// Each is a self-contained HTML/JS document rendered in a sandboxed iframe. All
/// widget interaction is deterministic client-side JS — the zero-inference principle
/// of the serving layer extends to the UI. Hosts without widget support ignore the
/// meta keys and use the structured JSON results.
/// </summary>
[McpServerResourceType]
public static class WidgetResources
{
    private const string SkybridgeMimeType = "text/html+skybridge";
    private const string McpAppMimeType = "text/html;profile=mcp-app";

    private const string PlannerDescription =
        "Interactive semester plan builder: pick eligible modules, track ECTS against a target, " +
        "see same-weekday hints and why blocked modules are blocked.";

    private const string ComparerDescription =
        "Side-by-side comparison table for 2-4 modules: ECTS, semesters, languages, weekdays, " +
        "shared tags, prerequisites and summaries.";

    private const string PathDescription =
        "Fastest-path timeline to a target module: missing prerequisites scheduled into the " +
        "earliest possible semesters, with waiting semesters, earliest completion and total ECTS.";

    // ---- OpenAI Apps SDK (ChatGPT) ----

    [McpServerResource(UriTemplate = "ui://widget/semester-planner.html", Name = "Semester plan builder widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for plan_semester results.")]
    [McpMeta("openai/widgetDescription", PlannerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    public static TextResourceContents SemesterPlannerSkybridge() =>
        WidgetContents("ui://widget/semester-planner.html", SkybridgeMimeType, "semester-planner.html", PlannerDescription);

    [McpServerResource(UriTemplate = "ui://widget/module-comparer.html", Name = "Module comparer widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for compare_modules results.")]
    [McpMeta("openai/widgetDescription", ComparerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    public static TextResourceContents ModuleComparerSkybridge() =>
        WidgetContents("ui://widget/module-comparer.html", SkybridgeMimeType, "module-comparer.html", ComparerDescription);

    [McpServerResource(UriTemplate = "ui://widget/path-planner.html", Name = "Path planner widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for plan_path results.")]
    [McpMeta("openai/widgetDescription", PathDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    public static TextResourceContents PathPlannerSkybridge() =>
        WidgetContents("ui://widget/path-planner.html", SkybridgeMimeType, "path-planner.html", PathDescription);

    // ---- MCP Apps extension (Claude, VS Code, ...) ----

    [McpServerResource(UriTemplate = "ui://module-catalog/semester-planner", Name = "Semester plan builder widget (MCP Apps)", MimeType = McpAppMimeType)]
    [Description("MCP Apps view template rendered for plan_semester results.")]
    [McpMeta("ui", JsonValue = """{"csp":{"connectDomains":[],"resourceDomains":[]}}""")]
    public static TextResourceContents SemesterPlannerApp() =>
        WidgetContents("ui://module-catalog/semester-planner", McpAppMimeType, "semester-planner.html", PlannerDescription);

    [McpServerResource(UriTemplate = "ui://module-catalog/module-comparer", Name = "Module comparer widget (MCP Apps)", MimeType = McpAppMimeType)]
    [Description("MCP Apps view template rendered for compare_modules results.")]
    [McpMeta("ui", JsonValue = """{"csp":{"connectDomains":[],"resourceDomains":[]}}""")]
    public static TextResourceContents ModuleComparerApp() =>
        WidgetContents("ui://module-catalog/module-comparer", McpAppMimeType, "module-comparer.html", ComparerDescription);

    [McpServerResource(UriTemplate = "ui://module-catalog/path-planner", Name = "Path planner widget (MCP Apps)", MimeType = McpAppMimeType)]
    [Description("MCP Apps view template rendered for plan_path results.")]
    [McpMeta("ui", JsonValue = """{"csp":{"connectDomains":[],"resourceDomains":[]}}""")]
    public static TextResourceContents PathPlannerApp() =>
        WidgetContents("ui://module-catalog/path-planner", McpAppMimeType, "path-planner.html", PathDescription);

    // The [McpMeta] attributes cover resources/list; resources/read only carries
    // contents-level meta when the method returns TextResourceContents itself,
    // so the equivalent keys are set here explicitly. Built fresh per call:
    // JsonNode trees are single-parent and must not be shared across responses.
    private static TextResourceContents WidgetContents(string uri, string mimeType, string fileName, string description) => new()
    {
        Uri = uri,
        MimeType = mimeType,
        Text = LoadWidgetHtml(fileName),
        Meta = mimeType == SkybridgeMimeType
            ? new JsonObject
            {
                ["openai/widgetDescription"] = description,
                ["openai/widgetPrefersBorder"] = true,
                ["openai/widgetCSP"] = new JsonObject
                {
                    ["connect_domains"] = new JsonArray(),
                    ["resource_domains"] = new JsonArray(),
                },
            }
            : new JsonObject
            {
                ["ui"] = new JsonObject
                {
                    ["csp"] = new JsonObject
                    {
                        ["connectDomains"] = new JsonArray(),
                        ["resourceDomains"] = new JsonArray(),
                    },
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
