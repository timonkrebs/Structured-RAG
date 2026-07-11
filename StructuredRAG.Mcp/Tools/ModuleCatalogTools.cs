using ModelContextProtocol;
using ModelContextProtocol.Server;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Mcp.Services;
using System.ComponentModel;

namespace StructuredRAG.Mcp.Tools;

/// <summary>
/// Query-time MCP tools. Every tool is a deterministic lookup over the precompiled
/// catalog — no LLM calls. The connected client model (ChatGPT, Claude, ...) does the
/// semantic work: it reads the taxonomy, picks tags/filters, and reasons over results.
///
/// `search` and `fetch` follow the shape ChatGPT connectors require, so this server
/// can be registered directly in the ChatGPT web interface. `fetch` passes through to
/// the official FHNW catalog API for always-current details (deterministic HTTP GET —
/// still no inference).
/// </summary>
[McpServerToolType]
public static class ModuleCatalogTools
{
    [McpServerTool(Name = "search", ReadOnly = true)]
    [Description("Search study modules by free-text query (German or English). Returns matching modules with id, title and summary. For precise filtering by tags, ECTS, semester or level use search_modules instead.")]
    public static SearchResults Search(
        CatalogStore store,
        [Description("Free-text search query, e.g. 'machine learning mit python'")] string query)
    {
        var results = store.Search(query, limit: 10)
            .Select(x => new SearchResultItem(
                Id: x.Module.Code,
                Title: $"{x.Module.Title} ({x.Module.Ects} ECTS, {OfferedText(x.Module)})",
                Text: x.Module.Summary,
                Url: x.Module.Url))
            .ToList();

        return new SearchResults(results);
    }

    [McpServerTool(Name = "fetch", ReadOnly = true)]
    [Description("Fetch the full record of one module by its id/code: the current official catalog description (fetched live from the FHNW module directory) plus the compiled enrichments (summary, audience, tags, prerequisites, typical questions).")]
    public static async Task<FetchResult> Fetch(
        CatalogStore store,
        LiveModuleFetcher liveFetcher,
        [Description("The module code returned by search, e.g. '9212177'")] string id,
        CancellationToken cancellationToken = default)
    {
        var m = store.GetModule(id)
            ?? throw new McpException($"No module with code '{id}'. Use search or search_modules to find valid codes.");

        return await liveFetcher.FetchAsync(m, cancellationToken);
    }

    [McpServerTool(Name = "search_modules", ReadOnly = true)]
    [Description("Filter modules by structured criteria (tags from the taxonomy, semester, level, module type, study program, ECTS range, language) plus optional free text. Read the catalog://taxonomy resource, call list_tags or get_catalog_overview first to see valid tags.")]
    public static IReadOnlyList<ModuleSummary> SearchModules(
        CatalogStore store,
        [Description("Tags to match (module must have at least one). Canonical German names or English aliases from the taxonomy")] string[]? tags = null,
        [Description("Semester the module must be offered in: type 'HS'/'FS' or concrete id like '26HS'")] string? semester = null,
        [Description("Study level, e.g. 'Bachelor' or 'Master'")] string? level = null,
        [Description("Module type, e.g. 'Pflichtmodul', 'Wahlpflichtmodul', 'Wahlmodul'")] string? moduleType = null,
        [Description("Study program the module belongs to (substring match), e.g. 'Wirtschaftsinformatik'")] string? studyProgram = null,
        [Description("Minimum ECTS credits")] int? minEcts = null,
        [Description("Maximum ECTS credits")] int? maxEcts = null,
        [Description("Language of instruction, e.g. 'de' or 'en'")] string? language = null,
        [Description("Optional free-text query applied on top of the filters")] string? query = null)
    {
        IEnumerable<CompiledModule> candidates = store.Modules;

        if (tags is { Length: > 0 })
        {
            var canonical = tags.Select(store.ResolveTagName).Where(t => t != null).Select(t => t!).ToList();
            if (canonical.Count == 0)
                throw new McpException($"None of the given tags exist in the taxonomy: {string.Join(", ", tags)}. Call list_tags for valid tags.");
            candidates = candidates.Where(m => m.Tags.Intersect(canonical, StringComparer.OrdinalIgnoreCase).Any());
        }
        if (!string.IsNullOrWhiteSpace(semester))
            candidates = candidates.Where(m => MatchesSemester(m, semester));
        if (!string.IsNullOrWhiteSpace(level))
            candidates = candidates.Where(m => m.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(moduleType))
            candidates = candidates.Where(m => moduleType.Equals(m.ModuleType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(studyProgram))
            candidates = candidates.Where(m => m.StudyPrograms.Any(p => p.Contains(studyProgram, StringComparison.OrdinalIgnoreCase)));
        if (minEcts.HasValue) candidates = candidates.Where(m => m.Ects >= minEcts.Value);
        if (maxEcts.HasValue) candidates = candidates.Where(m => m.Ects <= maxEcts.Value);
        if (!string.IsNullOrWhiteSpace(language))
            candidates = candidates.Where(m => m.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));

        var filtered = candidates.ToList();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var codes = store.Search(query, limit: filtered.Count).Select(x => x.Module.Code).ToList();
            filtered = filtered
                .Where(m => codes.Contains(m.Code, StringComparer.OrdinalIgnoreCase))
                .OrderBy(m => codes.FindIndex(c => c.Equals(m.Code, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return filtered.Select(ModuleSummary.From).ToList();
    }

    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("List the closed tag taxonomy of the catalog: canonical German tag names, English aliases, what each tag covers, and how many modules carry it. Use these tags with search_modules.")]
    public static IReadOnlyList<TagDefinition> ListTags(CatalogStore store) =>
        store.Taxonomy.OrderByDescending(t => t.ModuleCount).ToList();

    [McpServerTool(Name = "get_catalog_overview", ReadOnly = true)]
    [Description("Compact markdown overview of the whole catalog (all modules with code, title, ECTS, type, semesters, tags) plus the tag taxonomy. Ideal first call to load the full catalog into context, especially for semester planning.")]
    public static string GetCatalogOverview(CatalogStore store) =>
        store.TaxonomyMarkdown() + "\n\n" + store.IndexMarkdown();

    [McpServerTool(Name = "plan_semester", ReadOnly = true)]
    [Description("Get semester planning data for a student: which modules they are eligible for (structured prerequisites met, offered in the given semester) and which are blocked and why. Results include free-text prerequisite notes and weekdays — combine everything with the student's interests, ECTS target and schedule to propose a plan.")]
    public static SemesterPlanData PlanSemester(
        CatalogStore store,
        [Description("Semester to plan: type 'HS'/'FS' or concrete id like '26HS'")] string semester,
        [Description("Module codes the student has already completed")] string[]? completedModules = null,
        [Description("Optional tags (German or English) describing the student's interests, used to annotate results")] string[]? interestTags = null)
    {
        var completed = new HashSet<string>(completedModules ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var interests = (interestTags ?? Array.Empty<string>())
            .Select(store.ResolveTagName).Where(t => t != null).Select(t => t!).ToList();

        var eligible = new List<PlannableModule>();
        var blocked = new List<BlockedModule>();

        foreach (var m in store.Modules)
        {
            if (completed.Contains(m.Code)) continue;
            if (!MatchesSemester(m, semester)) continue;

            var missing = m.Prerequisites.Where(p => !completed.Contains(p)).ToList();
            var interestMatches = m.Tags.Intersect(interests, StringComparer.OrdinalIgnoreCase).ToList();

            if (missing.Count == 0)
            {
                var recommendedMissing = m.Recommended.Where(r => !completed.Contains(r)).ToList();
                eligible.Add(new PlannableModule(ModuleSummary.From(m), interestMatches, recommendedMissing));
            }
            else
            {
                blocked.Add(new BlockedModule(m.Code, m.Title, m.Ects, missing));
            }
        }

        return new SemesterPlanData(
            Semester: semester,
            Eligible: eligible
                .OrderByDescending(e => e.InterestMatches.Count)
                .ThenBy(e => e.Module.Code)
                .ToList(),
            Blocked: blocked.OrderBy(b => b.Code).ToList(),
            TotalEligibleEcts: eligible.Sum(e => e.Module.Ects),
            Note: "Structured prerequisites are LLM-extracted from the official requirement texts and validated against " +
                  "this catalog; per-module 'prerequisiteNotes' may contain additional requirements that could not be " +
                  "resolved to module codes — take them into account when planning.");
    }

    /// <summary>Semester matching: "HS"/"FS" match the offering type; "26HS" matches a concrete offering.
    /// Falls back to OfferedIn for catalogs without concrete offerings (e.g. sample data).</summary>
    private static bool MatchesSemester(CompiledModule m, string semester)
    {
        var isConcrete = semester.Length == 4;
        if (isConcrete && m.Offerings.Count > 0)
            return m.Offerings.Any(o => o.SemesterId.Equals(semester, StringComparison.OrdinalIgnoreCase));

        var type = isConcrete ? semester[2..] : semester;
        return m.OfferedIn.Contains(type, StringComparer.OrdinalIgnoreCase)
               || m.Offerings.Any(o => o.SemesterId.EndsWith(type, StringComparison.OrdinalIgnoreCase));
    }

    private static string OfferedText(CompiledModule m) =>
        m.Offerings.Count > 0
            ? string.Join("/", m.Offerings.Select(o => o.SemesterId))
            : string.Join("/", m.OfferedIn);
}

public record SearchResults(IReadOnlyList<SearchResultItem> Results);

public record SearchResultItem(string Id, string Title, string Text, string? Url);

public record FetchResult(string Id, string Title, string Text, string? Url, Dictionary<string, object?> Metadata);

public record ModuleSummary(
    string Code, string Title, string? TitleEn, int Ects, string Level, string? ModuleType,
    IReadOnlyList<string> OfferedIn, IReadOnlyList<ModuleOffering> Offerings,
    IReadOnlyList<string> Languages, IReadOnlyList<string> Weekdays,
    IReadOnlyList<string> Tags, string Summary, string? SummaryEn, string Audience,
    IReadOnlyList<string> Prerequisites, string? PrerequisiteNotes, string? Url)
{
    public static ModuleSummary From(CompiledModule m) => new(
        m.Code, m.Title, m.TitleEn, m.Ects, m.Level, m.ModuleType,
        m.OfferedIn, m.Offerings, m.Languages, m.Weekdays,
        m.Tags, m.Summary, m.SummaryEn, m.Audience,
        m.Prerequisites, m.PrerequisiteNotes, m.Url);
}

public record PlannableModule(
    ModuleSummary Module,
    IReadOnlyList<string> InterestMatches,
    IReadOnlyList<string> MissingRecommended);

public record BlockedModule(string Code, string Title, int Ects, IReadOnlyList<string> MissingPrerequisites);

public record SemesterPlanData(
    string Semester,
    IReadOnlyList<PlannableModule> Eligible,
    IReadOnlyList<BlockedModule> Blocked,
    int TotalEligibleEcts,
    string Note);
