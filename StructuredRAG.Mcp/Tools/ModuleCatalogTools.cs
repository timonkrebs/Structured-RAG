using ModelContextProtocol;
using ModelContextProtocol.Server;
using StructuredRAG.Core.Models.Catalog;
using System.ComponentModel;

namespace StructuredRAG.Mcp.Tools;

/// <summary>
/// Query-time MCP tools. Every tool is a deterministic lookup over the precompiled
/// catalog — no LLM calls. The connected client model (ChatGPT, Claude, ...) does the
/// semantic work: it reads the taxonomy, picks tags/filters, and reasons over results.
///
/// `search` and `fetch` follow the shape ChatGPT connectors require, so this server
/// can be registered directly in the ChatGPT web interface.
/// </summary>
[McpServerToolType]
public static class ModuleCatalogTools
{
    [McpServerTool(Name = "search", ReadOnly = true)]
    [Description("Search study modules by free-text query. Returns matching modules with id, title and summary. For precise filtering by tags, ECTS, semester or level use search_modules instead.")]
    public static SearchResults Search(
        CatalogStore store,
        [Description("Free-text search query, e.g. 'machine learning with python'")] string query)
    {
        var results = store.Search(query, limit: 10)
            .Select(x => new SearchResultItem(
                Id: x.Module.Code,
                Title: $"{x.Module.Title} ({x.Module.Ects} ECTS, {string.Join("/", x.Module.OfferedIn)})",
                Text: x.Module.Summary,
                Url: x.Module.Url))
            .ToList();

        return new SearchResults(results);
    }

    [McpServerTool(Name = "fetch", ReadOnly = true)]
    [Description("Fetch the full compiled record of one module by its id/code, including description, audience, tags, prerequisites and typical questions.")]
    public static FetchResult Fetch(
        CatalogStore store,
        [Description("The module code returned by search, e.g. 'algd'")] string id)
    {
        var m = store.GetModule(id)
            ?? throw new McpException($"No module with code '{id}'. Use search or search_modules to find valid codes.");

        var text = $"""
            # {m.Title} ({m.Code})

            {m.Summary}

            **Who should take it:** {m.Audience}

            **Details:** {m.Ects} ECTS · {m.Level} · offered in {string.Join("/", m.OfferedIn)} · languages: {string.Join(", ", m.Languages)} · assessment: {m.Assessment}
            **Tags:** {string.Join(", ", m.Tags)}
            **Prerequisites:** {(m.Prerequisites.Count > 0 ? string.Join(", ", m.Prerequisites) : "none")}
            **Recommended before:** {(m.Recommended.Count > 0 ? string.Join(", ", m.Recommended) : "none")}

            ## Catalog description
            {m.Description}

            ## Typical student questions this module answers
            {string.Join("\n", m.TypicalQuestions.Select(q => $"- {q}"))}
            """;

        return new FetchResult(
            Id: m.Code,
            Title: m.Title,
            Text: text,
            Url: m.Url,
            Metadata: new Dictionary<string, object?>
            {
                ["ects"] = m.Ects,
                ["level"] = m.Level,
                ["offeredIn"] = m.OfferedIn,
                ["tags"] = m.Tags,
                ["prerequisites"] = m.Prerequisites
            });
    }

    [McpServerTool(Name = "search_modules", ReadOnly = true)]
    [Description("Filter modules by structured criteria (tags from the taxonomy, semester, level, ECTS range, language) plus optional free text. Read the catalog://taxonomy resource or call list_tags first to see valid tags.")]
    public static IReadOnlyList<ModuleSummary> SearchModules(
        CatalogStore store,
        [Description("Tags to match (module must have at least one), exactly as defined in the taxonomy")] string[]? tags = null,
        [Description("Semester the module must be offered in: 'HS' (autumn) or 'FS' (spring)")] string? semester = null,
        [Description("Study level, e.g. 'Bachelor' or 'Master'")] string? level = null,
        [Description("Minimum ECTS credits")] int? minEcts = null,
        [Description("Maximum ECTS credits")] int? maxEcts = null,
        [Description("Language of instruction, e.g. 'de' or 'en'")] string? language = null,
        [Description("Optional free-text query applied on top of the filters")] string? query = null)
    {
        IEnumerable<CompiledModule> candidates = store.Modules;

        if (tags is { Length: > 0 })
            candidates = candidates.Where(m => m.Tags.Intersect(tags, StringComparer.OrdinalIgnoreCase).Any());
        if (!string.IsNullOrWhiteSpace(semester))
            candidates = candidates.Where(m => m.OfferedIn.Contains(semester, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(level))
            candidates = candidates.Where(m => m.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
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
    [Description("List the closed tag taxonomy of the catalog: tag names, what each tag covers, and how many modules carry it. Use these tags with search_modules.")]
    public static IReadOnlyList<TagDefinition> ListTags(CatalogStore store) =>
        store.Taxonomy.OrderByDescending(t => t.ModuleCount).ToList();

    [McpServerTool(Name = "plan_semester", ReadOnly = true)]
    [Description("Get semester planning data for a student: which modules they are eligible for (prerequisites met, offered in the given semester) and which are blocked and why. The tool only computes constraints — combine the results with the student's interests and ECTS target to propose a plan.")]
    public static SemesterPlanData PlanSemester(
        CatalogStore store,
        [Description("Semester to plan: 'HS' (autumn) or 'FS' (spring)")] string semester,
        [Description("Module codes the student has already completed")] string[]? completedModules = null,
        [Description("Optional tags describing the student's interests, used to annotate results")] string[]? interestTags = null)
    {
        var completed = new HashSet<string>(completedModules ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var interests = interestTags ?? Array.Empty<string>();

        var eligible = new List<PlannableModule>();
        var blocked = new List<BlockedModule>();

        foreach (var m in store.Modules)
        {
            if (completed.Contains(m.Code)) continue;
            if (!m.OfferedIn.Contains(semester, StringComparer.OrdinalIgnoreCase)) continue;

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
            TotalEligibleEcts: eligible.Sum(e => e.Module.Ects));
    }
}

public record SearchResults(IReadOnlyList<SearchResultItem> Results);

public record SearchResultItem(string Id, string Title, string Text, string? Url);

public record FetchResult(string Id, string Title, string Text, string? Url, Dictionary<string, object?> Metadata);

public record ModuleSummary(
    string Code, string Title, int Ects, string Level,
    IReadOnlyList<string> OfferedIn, IReadOnlyList<string> Languages,
    IReadOnlyList<string> Tags, string Summary, string Audience,
    IReadOnlyList<string> Prerequisites)
{
    public static ModuleSummary From(CompiledModule m) => new(
        m.Code, m.Title, m.Ects, m.Level, m.OfferedIn, m.Languages,
        m.Tags, m.Summary, m.Audience, m.Prerequisites);
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
    int TotalEligibleEcts);
