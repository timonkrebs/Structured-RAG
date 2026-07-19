namespace StructuredRAG.Core.Models.Catalog;

/// <summary>
/// A module enriched by the compilation step. All LLM-derived fields are produced
/// offline so that query-time consumers (MCP clients) need no inference on the server.
/// </summary>
public class CompiledModule
{
    public string Code { get; set; } = string.Empty;
    public string? ModuleId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? TitleEn { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? DescriptionEn { get; set; }
    public int Ects { get; set; }
    public string Level { get; set; } = string.Empty;
    public List<string> OfferedIn { get; set; } = new();
    public List<ModuleOffering> Offerings { get; set; } = new();
    public List<string> Languages { get; set; } = new();
    public List<string> Weekdays { get; set; } = new();

    /// <summary>Structured prerequisites: module codes of this catalog. Either provided by the
    /// source or extracted by the compiler from the free-text requirements (validated).</summary>
    public List<string> Prerequisites { get; set; } = new();

    /// <summary>Prerequisites as evaluation groups: outer list = AND, inner list = interchangeable
    /// module codes (OR) — e.g. the German and English variant of the same course. Derived
    /// deterministically from <see cref="Prerequisites"/> (see PrerequisiteGrouping); the
    /// authoritative shape for eligibility checks. Empty on catalogs compiled before this
    /// field existed — consumers fall back via <see cref="EffectivePrerequisiteGroups"/>.</summary>
    public List<List<string>> PrerequisiteGroups { get; set; } = new();

    /// <summary>Groups when present, else one singleton group per flat prerequisite.
    /// A method (not a property) so serialization never emits the derived view.</summary>
    public IEnumerable<List<string>> EffectivePrerequisiteGroups() =>
        PrerequisiteGroups.Count > 0 ? PrerequisiteGroups : Prerequisites.Select(p => new List<string> { p });

    /// <summary>Original free-text requirements that could not be (fully) resolved to module codes.
    /// Query-time clients should reason over these when planning.</summary>
    public string? PrerequisiteNotes { get; set; }

    public List<string> Recommended { get; set; } = new();
    public List<string> StudyPrograms { get; set; } = new();
    public string? ModuleType { get; set; }
    public List<string> Locations { get; set; } = new();
    public string? ResponsibleName { get; set; }
    public string Assessment { get; set; } = string.Empty;
    public string? Url { get; set; }

    // --- LLM-compiled enrichments ---

    /// <summary>Two to three sentences written for an AI assistant that advises students (German).</summary>
    public string Summary { get; set; } = string.Empty;
    public string? SummaryEn { get; set; }

    /// <summary>Who should take this module and why (interests, career goals, strengths).</summary>
    public string Audience { get; set; } = string.Empty;
    public string? AudienceEn { get; set; }

    /// <summary>Tags assigned from the closed taxonomy of this catalog version (canonical names).</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Questions a student might ask that this module is a good answer to.</summary>
    public List<string> TypicalQuestions { get; set; } = new();
    public List<string> TypicalQuestionsEn { get; set; } = new();

    /// <summary>Hash of the source module this record was compiled from; enables incremental compilation.</summary>
    public string? SourceHash { get; set; }
}
