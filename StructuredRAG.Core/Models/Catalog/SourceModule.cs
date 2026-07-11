namespace StructuredRAG.Core.Models.Catalog;

/// <summary>
/// Raw module description as ingested from a module catalog (e.g. university course directory).
/// This is the input to the knowledge compilation step. Only Code, Title and Description are
/// required; all other fields enrich filtering and planning when the source provides them.
/// </summary>
public class SourceModule
{
    /// <summary>Unique, stable module code (e.g. "algd" or a numeric catalog id).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Semester-independent source identity, when the catalog distinguishes it from Code.</summary>
    public string? ModuleId { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? TitleEn { get; set; }

    /// <summary>Full catalog description of the module (plain text; HTML stripped at ingestion).</summary>
    public string Description { get; set; } = string.Empty;
    public string? DescriptionEn { get; set; }

    /// <summary>
    /// Free-text enrollment requirements as published by the catalog. The compiler
    /// extracts structured prerequisite links from this where possible.
    /// </summary>
    public string? RequirementsText { get; set; }
    public string? RequirementsTextEn { get; set; }

    public int Ects { get; set; }

    /// <summary>e.g. "Bachelor", "Master".</summary>
    public string Level { get; set; } = string.Empty;

    /// <summary>Semester types in which the module is offered, e.g. ["HS"], ["FS"], ["HS", "FS"].</summary>
    public List<string> OfferedIn { get; set; } = new();

    /// <summary>Concrete offerings with their per-semester catalog ids (e.g. 26HS → 26HS_9521316).</summary>
    public List<ModuleOffering> Offerings { get; set; } = new();

    /// <summary>ISO language codes of instruction, e.g. ["de", "en"].</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Weekdays on which classes take place (English day names) — enables schedule-aware planning.</summary>
    public List<string> Weekdays { get; set; } = new();

    /// <summary>Module codes that must be completed before enrolling (structured, if the source has them).</summary>
    public List<string> Prerequisites { get; set; } = new();

    /// <summary>Module codes that are recommended but not required.</summary>
    public List<string> Recommended { get; set; } = new();

    /// <summary>Study programs this module belongs to.</summary>
    public List<string> StudyPrograms { get; set; } = new();

    /// <summary>e.g. "Pflichtmodul", "Wahlpflichtmodul", "Wahlmodul".</summary>
    public string? ModuleType { get; set; }

    public List<string> Locations { get; set; } = new();

    /// <summary>Name of the responsible lecturer (no contact details — kept out of compiled artifacts).</summary>
    public string? ResponsibleName { get; set; }

    /// <summary>e.g. "written exam", "project work".</summary>
    public string Assessment { get; set; } = string.Empty;

    /// <summary>Link to the official module page, if any.</summary>
    public string? Url { get; set; }
}

public class ModuleOffering
{
    /// <summary>Concrete semester id, e.g. "26HS".</summary>
    public string SemesterId { get; set; } = string.Empty;

    /// <summary>Per-semester catalog id, e.g. "26HS_9521316".</summary>
    public string PlanSemesterModulId { get; set; } = string.Empty;
}
