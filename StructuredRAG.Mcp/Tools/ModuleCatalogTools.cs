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

    [McpServerTool(Name = "fetch", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/widgetAccessible", true)] // the plan-builder widget loads module details through this
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
    [McpMeta("openai/widgetAccessible", true)]
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
        {
            var validated = ValidateSemester(semester);
            candidates = candidates.Where(m => MatchesSemester(m, validated));
        }
        if (!string.IsNullOrWhiteSpace(level))
            candidates = candidates.Where(m => m.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(moduleType))
            candidates = candidates.Where(m => moduleType.Equals(m.ModuleType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(studyProgram))
            candidates = candidates.Where(m => m.StudyPrograms.Any(p => p.Contains(studyProgram, StringComparison.OrdinalIgnoreCase)));
        if (minEcts.HasValue) candidates = candidates.Where(m => m.Ects >= minEcts.Value);
        if (maxEcts.HasValue) candidates = candidates.Where(m => m.Ects <= maxEcts.Value);
        if (!string.IsNullOrWhiteSpace(language))
            candidates = candidates.Where(m => MatchesLanguage(m, language, semester));

        var filtered = candidates.ToList();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // Rank within the filtered set — ranking the whole catalog first and
            // intersecting would drop filtered matches outscored by unfiltered ones.
            filtered = store.Search(query, limit: filtered.Count, within: filtered)
                .Select(x => x.Module)
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

    // UseStructuredContent: the ChatGPT widget is hydrated from structuredContent
    // (window.openai.toolOutput); the SDK still emits the JSON text block alongside.
    [McpServerTool(Name = "plan_semester", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/semester-planner.html")]
    [McpMeta("openai/toolInvocation/invoking", "Collecting eligible modules…")]
    [McpMeta("openai/toolInvocation/invoked", "Semester planning data ready")]
    [Description("Get semester planning data for a student: which modules they are eligible for (structured prerequisites met, offered in the given semester) and which are blocked and why. Results include free-text prerequisite notes and weekdays — combine everything with the student's interests, ECTS target and schedule to propose a plan. In ChatGPT this also renders an interactive plan-builder widget.")]
    public static SemesterPlanData PlanSemester(
        CatalogStore store,
        [Description("Semester to plan: type 'HS'/'FS' or concrete id like '26HS'")] string semester,
        [Description("Module codes the student has already completed")] string[]? completedModules = null,
        [Description("Optional tags (German or English) describing the student's interests, used to annotate results")] string[]? interestTags = null,
        [Description("The student's target ECTS for the semester; echoed into the result and used to initialize the plan-builder widget (default 30)")] int? ectsTarget = null)
    {
        semester = ValidateSemester(semester);
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
            EctsTarget: ectsTarget is > 0 ? ectsTarget.Value : 30,
            Note: "Structured prerequisites are LLM-extracted from the official requirement texts and validated against " +
                  "this catalog; per-module 'prerequisiteNotes' may contain additional requirements that could not be " +
                  "resolved to module codes — take them into account when planning.");
    }

    [McpServerTool(Name = "compare_modules", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/module-comparer.html")]
    [McpMeta("openai/widgetAccessible", true)] // the comparer widget re-calls this to add/swap a column
    [McpMeta("openai/toolInvocation/invoking", "Comparing modules…")]
    [McpMeta("openai/toolInvocation/invoked", "Module comparison ready")]
    [Description("Compare 2-4 modules side by side: ECTS, level, module type, semesters, languages, weekdays, tags, prerequisites and summaries. In ChatGPT this renders an interactive comparison-table widget.")]
    public static ModuleComparisonData CompareModules(
        CatalogStore store,
        [Description("2-4 module codes to compare, e.g. from search or search_modules")] string[] codes)
    {
        var distinct = (codes ?? [])
            .Select(c => c?.Trim())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count is < 2 or > 4)
            throw new McpException($"compare_modules needs 2-4 distinct module codes, got {distinct.Count}.");

        var modules = new List<ModuleSummary>();
        var notFound = new List<string>();
        foreach (var code in distinct)
        {
            var m = store.GetModule(code);
            if (m is null) notFound.Add(code);
            else modules.Add(ModuleSummary.From(m));
        }
        if (modules.Count < 2)
            throw new McpException(
                $"Fewer than two of the given codes exist in the catalog (unknown: {string.Join(", ", notFound)}). " +
                "Use search or search_modules to find valid codes.");

        return new ModuleComparisonData(modules, notFound);
    }

    /// <summary>Rejects malformed semester inputs early — silently returning an empty
    /// result for a typo like "Herbst" would mislead the client model.</summary>
    private static string ValidateSemester(string semester)
    {
        var s = semester.Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(s, @"^(HS|FS|\d{2}(HS|FS))$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            throw new McpException(
                $"Invalid semester '{semester}'. Use the type 'HS' (autumn) / 'FS' (spring) or a concrete id like '26HS'.");
        }
        return s;
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

    /// <summary>Language filtering: when a concrete semester is given and that offering
    /// carries its own language list, use it — languages can differ between semesters.</summary>
    private static bool MatchesLanguage(CompiledModule m, string language, string? semester)
    {
        if (semester is { Length: 4 })
        {
            var offering = m.Offerings.FirstOrDefault(o =>
                o.SemesterId.Equals(semester, StringComparison.OrdinalIgnoreCase));
            if (offering is { Languages.Count: > 0 })
                return offering.Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
        }
        return m.Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
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
    int EctsTarget,
    string Note);

public record ModuleComparisonData(
    IReadOnlyList<ModuleSummary> Modules,
    IReadOnlyList<string> NotFound);
