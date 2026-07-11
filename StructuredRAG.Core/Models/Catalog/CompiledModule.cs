namespace StructuredRAG.Core.Models.Catalog;

/// <summary>
/// A module enriched by the compilation step. All LLM-derived fields are produced
/// offline so that query-time consumers (MCP clients) need no inference on the server.
/// </summary>
public class CompiledModule
{
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int Ects { get; set; }
    public string Level { get; set; } = string.Empty;
    public List<string> OfferedIn { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> Prerequisites { get; set; } = new();
    public List<string> Recommended { get; set; } = new();
    public string Assessment { get; set; } = string.Empty;
    public string? Url { get; set; }

    // --- LLM-compiled enrichments ---

    /// <summary>Two to three sentences written for an AI assistant that advises students.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Who should take this module and why (interests, career goals, strengths).</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Tags assigned from the closed taxonomy of this catalog version.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Questions a student might ask that this module is a good answer to.</summary>
    public List<string> TypicalQuestions { get; set; } = new();
}
