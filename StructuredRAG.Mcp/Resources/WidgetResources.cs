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

    // Unique origin identifying this app's widgets (ChatGPT sandboxes them under a
    // domain derived from it; required for app-store submission). The deployment's
    // public origin — update if the service moves.
    //
    // About the "CSP off" badge ChatGPT shows under these widgets: that is HOST
    // behavior for apps running in developer mode. ChatGPT only enforces (and
    // reflects) the declared widget CSP once an app is published/verified; until
    // then the badge stays "CSP off" no matter what is declared (see OpenAI
    // community threads 1371258 and 1372222 — resolved only by publishing). The
    // CSP here is already declared in both the standard (_meta.ui.csp) and legacy
    // (openai/widgetCSP) shapes, at the resources/list AND resources/read level,
    // with empty allowlists (the widgets are fully self-contained) — there is
    // nothing more the server can do to remove the badge.
    private const string WidgetDomain = "https://structured-rag-69g2.onrender.com";

    private const string PlannerDescription =
        "Interactive semester plan builder: pick eligible modules, track ECTS against a target, " +
        "see same-weekday hints and why blocked modules are blocked.";

    private const string ComparerDescription =
        "Side-by-side comparison table for 2-4 modules: ECTS, semesters, languages, weekdays, " +
        "shared tags, prerequisites and summaries.";

    private const string PathDescription =
        "Fastest-path timeline to a target module: missing prerequisites scheduled into the " +
        "earliest possible semesters, with waiting semesters, earliest completion and total ECTS.";

    private const string StartDescription =
        "Getting-started view: catalog snapshot of the study program, a one-time picker for " +
        "already-completed modules and an ECTS target, plus buttons that start a planning flow " +
        "as a chat message.";

    // ---- OpenAI Apps SDK (ChatGPT) ----

    [McpServerResource(UriTemplate = "ui://widget/semester-planner.html", Name = "Semester plan builder widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for plan_semester results.")]
    [McpMeta("openai/widgetDescription", PlannerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetDomain", WidgetDomain)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    [McpMeta("ui", JsonValue = "{\"domain\":\"" + WidgetDomain + "\",\"csp\":{\"connectDomains\":[],\"resourceDomains\":[]}}")]
    public static TextResourceContents SemesterPlannerSkybridge() =>
        WidgetContents("ui://widget/semester-planner.html", SkybridgeMimeType, "semester-planner.html", PlannerDescription);

    [McpServerResource(UriTemplate = "ui://widget/module-comparer.html", Name = "Module comparer widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for compare_modules results.")]
    [McpMeta("openai/widgetDescription", ComparerDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetDomain", WidgetDomain)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    [McpMeta("ui", JsonValue = "{\"domain\":\"" + WidgetDomain + "\",\"csp\":{\"connectDomains\":[],\"resourceDomains\":[]}}")]
    public static TextResourceContents ModuleComparerSkybridge() =>
        WidgetContents("ui://widget/module-comparer.html", SkybridgeMimeType, "module-comparer.html", ComparerDescription);

    [McpServerResource(UriTemplate = "ui://widget/path-planner.html", Name = "Path planner widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for plan_path results.")]
    [McpMeta("openai/widgetDescription", PathDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetDomain", WidgetDomain)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    [McpMeta("ui", JsonValue = "{\"domain\":\"" + WidgetDomain + "\",\"csp\":{\"connectDomains\":[],\"resourceDomains\":[]}}")]
    public static TextResourceContents PathPlannerSkybridge() =>
        WidgetContents("ui://widget/path-planner.html", SkybridgeMimeType, "path-planner.html", PathDescription);

    [McpServerResource(UriTemplate = "ui://widget/start.html", Name = "Getting-started widget (ChatGPT)", MimeType = SkybridgeMimeType)]
    [Description("ChatGPT widget template rendered for get_started results.")]
    [McpMeta("openai/widgetDescription", StartDescription)]
    [McpMeta("openai/widgetPrefersBorder", true)]
    [McpMeta("openai/widgetDomain", WidgetDomain)]
    [McpMeta("openai/widgetCSP", JsonValue = """{"connect_domains":[],"resource_domains":[]}""")]
    [McpMeta("ui", JsonValue = "{\"domain\":\"" + WidgetDomain + "\",\"csp\":{\"connectDomains\":[],\"resourceDomains\":[]}}")]
    public static TextResourceContents StartSkybridge() =>
        WidgetContents("ui://widget/start.html", SkybridgeMimeType, "start.html", StartDescription);

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

    [McpServerResource(UriTemplate = "ui://module-catalog/start", Name = "Getting-started widget (MCP Apps)", MimeType = McpAppMimeType)]
    [Description("MCP Apps view template rendered for get_started results.")]
    [McpMeta("ui", JsonValue = """{"csp":{"connectDomains":[],"resourceDomains":[]}}""")]
    public static TextResourceContents StartApp() =>
        WidgetContents("ui://module-catalog/start", McpAppMimeType, "start.html", StartDescription);

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
                ["openai/widgetDomain"] = WidgetDomain,
                ["openai/widgetCSP"] = new JsonObject
                {
                    ["connect_domains"] = new JsonArray(),
                    ["resource_domains"] = new JsonArray(),
                },
                // Standard MCP Apps equivalents of the openai/* keys above — ChatGPT
                // prefers these going forward; empty lists = fully self-contained.
                ["ui"] = new JsonObject
                {
                    ["domain"] = WidgetDomain,
                    ["csp"] = new JsonObject
                    {
                        ["connectDomains"] = new JsonArray(),
                        ["resourceDomains"] = new JsonArray(),
                    },
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

    /// <summary>
    /// A line whose only content is <c>{{include:name}}</c> inside a line comment —
    /// <c>/* {{include:_tokens.css}} */</c> in CSS, <c>// {{include:_host.js}}</c> in JS.
    /// The whole line is replaced by the named resource, so the shared design tokens
    /// and the host bridge live in one file each instead of being copied into all four
    /// widgets. Character classes are <c>[ \t]</c> rather than <c>\s</c> on purpose: a
    /// newline-crossing match would swallow the surrounding structure.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex IncludeMarker = new(
        @"^[ \t]*(?://|/\*)[ \t]*\{\{include:([\w.\-]+)\}\}[ \t]*(?:\*/)?[ \t]*$",
        System.Text.RegularExpressions.RegexOptions.Multiline |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // Widgets are served on every resources/read, so the composed HTML is cached.
    private static string LoadWidgetHtml(string fileName) => HtmlCache.GetOrAdd(fileName, static f =>
    {
        var html = IncludeMarker.Replace(ReadWidgetResource(f),
            m => ReadWidgetResource(m.Groups[1].Value).TrimEnd('\r', '\n'));
        // Substitution is a single pass, so a marker inside an included file would
        // ship verbatim to the host. Fail loudly instead of serving a broken widget.
        if (IncludeMarker.IsMatch(html))
            throw new InvalidOperationException($"Unresolved include marker in widget '{f}'.");
        return html;
    });

    private static string ReadWidgetResource(string fileName)
    {
        var assembly = typeof(WidgetResources).Assembly;
        var resourceName = $"StructuredRAG.Mcp.Widgets.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded widget resource '{resourceName}' not found. Available: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
