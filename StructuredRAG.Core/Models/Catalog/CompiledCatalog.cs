namespace StructuredRAG.Core.Models.Catalog;

/// <summary>
/// The complete output of one compilation run: manifest, closed tag taxonomy and
/// enriched modules. Serialized to static JSON files served by the MCP server.
/// </summary>
public class CompiledCatalog
{
    public CatalogManifest Manifest { get; set; } = new();
    public List<TagDefinition> Taxonomy { get; set; } = new();
    public List<CompiledModule> Modules { get; set; } = new();
}

public class CatalogManifest
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime CompiledAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public int ModuleCount { get; set; }
    public int TagCount { get; set; }
}

/// <summary>
/// One tag of the closed taxonomy. The description is essential: query-time clients
/// use it to map free-form student questions onto tags without server-side inference.
/// </summary>
public class TagDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int ModuleCount { get; set; }
}
