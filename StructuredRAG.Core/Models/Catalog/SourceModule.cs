namespace StructuredRAG.Core.Models.Catalog;

/// <summary>
/// Raw module description as ingested from a module catalog (e.g. university course directory).
/// This is the input to the knowledge compilation step.
/// </summary>
public class SourceModule
{
    /// <summary>Unique, stable module code (e.g. "algd").</summary>
    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Full catalog description of the module.</summary>
    public string Description { get; set; } = string.Empty;

    public int Ects { get; set; }

    /// <summary>e.g. "Bachelor", "Master".</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Semesters in which the module is offered, e.g. ["HS"], ["FS"], ["HS", "FS"].</summary>
    public List<string> OfferedIn { get; set; } = new();

    /// <summary>ISO language codes of instruction, e.g. ["de", "en"].</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Module codes that must be completed before enrolling.</summary>
    public List<string> Prerequisites { get; set; } = new();

    /// <summary>Module codes that are recommended but not required.</summary>
    public List<string> Recommended { get; set; } = new();

    /// <summary>e.g. "written exam", "project work".</summary>
    public string Assessment { get; set; } = string.Empty;

    /// <summary>Link to the official module page, if any.</summary>
    public string? Url { get; set; }
}
