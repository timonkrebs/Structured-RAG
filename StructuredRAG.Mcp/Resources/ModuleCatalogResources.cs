using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace StructuredRAG.Mcp.Resources;

/// <summary>
/// MCP resources over the precompiled catalog. Clients that support resources
/// (Claude, IDEs, ...) can attach these directly to the conversation context so the
/// model can reason over the whole catalog without any tool round-trips.
/// </summary>
[McpServerResourceType]
public static class ModuleCatalogResources
{
    [McpServerResource(UriTemplate = "catalog://index", Name = "Module catalog index", MimeType = "text/markdown")]
    [Description("Compact overview of all modules: code, title, ECTS, level, offered semesters and tags. Small enough to load fully into context.")]
    public static string Index(CatalogStore store) => store.IndexMarkdown();

    [McpServerResource(UriTemplate = "catalog://taxonomy", Name = "Tag taxonomy", MimeType = "text/markdown")]
    [Description("The closed tag taxonomy with a description of what each tag covers. Read this to map student interests onto tags for search_modules.")]
    public static string Taxonomy(CatalogStore store) => store.TaxonomyMarkdown();

    [McpServerResource(UriTemplate = "catalog://module/{code}", Name = "Module details", MimeType = "application/json")]
    [Description("Full compiled record of a single module by code.")]
    public static string Module(CatalogStore store, string code)
    {
        var module = store.GetModule(code)
            ?? throw new McpException($"No module with code '{code}'");
        return System.Text.Json.JsonSerializer.Serialize(module,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
