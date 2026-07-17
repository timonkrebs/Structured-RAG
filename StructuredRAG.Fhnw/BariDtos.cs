using System.Text.Json;

namespace StructuredRAG.Fhnw;

// DTOs for the FHNW Modulbeschreibungen backend ("bariapi").
// Response JSON is camelCase; deserialized with PropertyNameCaseInsensitive.

public class FacetValueDto
{
    public string? DisplayValueEnglish { get; set; }
    public string? DisplayValueGerman { get; set; }
    public string Value { get; set; } = string.Empty;
}

public class FacetResultDto
{
    public string Name { get; set; } = string.Empty;
    public List<FacetValueDto> Values { get; set; } = new();
}

public class FacetsResponseDto
{
    public List<FacetResultDto> FacetResults { get; set; } = new();
}

/// <summary>
/// Facet filter for search requests. The backend matches on the complete facet
/// item (display values included), so values must be echoed exactly as returned
/// by the facets endpoint.
/// </summary>
public class FacetQueryItemDto
{
    public string Name { get; set; } = string.Empty;
    public List<FacetValueDto> Values { get; set; } = new();
}

public class SearchQueryDto
{
    public string SearchText { get; set; } = string.Empty;
    public List<FacetQueryItemDto> FacetQuery { get; set; } = new();
}

public class PagingQueryDto
{
    public int Skip { get; set; }
    public int Take { get; set; } = 100;
}

public class SearchRequestDto
{
    public SearchQueryDto SearchQuery { get; set; } = new();
    public PagingQueryDto PagingQuery { get; set; } = new();
}

/// <summary>Search results are shallow: only title and planSemesterModulId are populated.</summary>
public class SearchResultItemDto
{
    public string? Title { get; set; }
    public string? PlanSemesterModulId { get; set; }
}

public class SearchResponseDto
{
    public int ResultsCount { get; set; }
    public List<SearchResultItemDto> CurrentPageSearchResults { get; set; } = new();
}

public class SemesterDto
{
    public string? DisplayValueEnglish { get; set; }
    public string? DisplayValueGerman { get; set; }
    public string Value { get; set; } = string.Empty;
}

public class ModuleResponsibleDto
{
    public string? Name { get; set; }
    public string? Email { get; set; }
}

/// <summary>One concrete course instance (class) of a module offering. Lecturer and
/// capacity fields are deliberately not declared — they carry personal data (emails)
/// or noise and must not reach compiled artifacts.</summary>
public class ModuleInstanceDto
{
    public string? Number { get; set; }
    public string? Day { get; set; }

    /// <summary>Lesson start with a dummy date part (e.g. "1899-12-30T08:15:00") — only the clock time is meaningful.</summary>
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }

    /// <summary>Rhythm in days; 7 = weekly.</summary>
    public int? Periodicity { get; set; }

    public string? Language { get; set; }
    public string? Location { get; set; }
}

/// <summary>
/// Full module record from GET /api/PlanSemesterModul/{planSemesterModulId}.
/// Text fields contain HTML fragments; *EN properties are the English variants.
/// </summary>
public class ModuleDetailDto
{
    public long ModuleId { get; set; }
    public string PlanSemesterModulId { get; set; } = string.Empty;
    public string? SemesterId { get; set; }

    public string? Title { get; set; }
    public string? TitleEN { get; set; }
    public string? KeyIdea { get; set; }
    public string? KeyIdeaEN { get; set; }
    public string? CourseContent { get; set; }
    public string? CourseContentEN { get; set; }
    public string? Requirements { get; set; }
    public string? RequirementsEN { get; set; }

    public int Ects { get; set; }
    public string? StudyLevel { get; set; }
    public string? Language { get; set; }
    public List<string>? Locations { get; set; }
    public List<string>? StudyPrograms { get; set; }
    public List<string>? ModuleTypes { get; set; }
    public List<ModuleResponsibleDto>? ModuleResponsibles { get; set; }
    public List<ModuleInstanceDto>? ModuleInstances { get; set; }
    public List<int>? Weekdays { get; set; }

    /// <summary>Array of assessment records with German PascalCase keys (e.g. LeistungsnachweisArt).
    /// Delivered by the API as a JSON string containing the array.</summary>
    public JsonElement? PerformanceRecords { get; set; }
}
