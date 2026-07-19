using ModelContextProtocol;
using ModelContextProtocol.Server;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Fhnw;
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
    [McpServerTool(Name = "search", ReadOnly = true, UseStructuredContent = true)]
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

    [McpServerTool(Name = "search_modules", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/widgetAccessible", true)]
    [Description("Filter modules by boolean tag criteria (allOfTags/anyOfTags/noneOfTags) plus semester, level, module type, study program, ECTS range, language and optional free text. Returns the total match count, the modules in the requested format and — with includeFacets — per-tag counts of the result set to pick the next filter from. Search WIDE: tags are compiled and approximate, so prefer anyOfTags with many tags over stacking allOfTags — missing a relevant module is worse than reviewing extras, and the compact format keeps wide results cheap. The tag vocabulary is in the server instructions; list_tags has the tag descriptions.")]
    public static ModuleSearchResults SearchModules(
        CatalogStore store,
        [Description("Modules must carry ALL of these tags (AND). Use sparingly — every added tag silently drops relevant modules whose compiled tags are incomplete; prefer anyOfTags plus your own shortlisting")] string[]? allOfTags = null,
        [Description("Modules must carry AT LEAST ONE of these tags (OR). Cast a wide net: include every plausibly relevant tag. Canonical German names or English aliases from the taxonomy")] string[]? anyOfTags = null,
        [Description("Modules must carry NONE of these tags (exclusion)")] string[]? noneOfTags = null,
        [Description("Semester the module must be offered in: type 'HS'/'FS' or concrete id like '26HS'")] string? semester = null,
        [Description("Study level, e.g. 'Bachelor' or 'Master'")] string? level = null,
        [Description("Module type, e.g. 'Pflichtmodul', 'Wahlpflichtmodul', 'Wahlmodul'")] string? moduleType = null,
        [Description("Study program the module belongs to (substring match), e.g. 'Wirtschaftsinformatik'")] string? studyProgram = null,
        [Description("Minimum ECTS credits")] int? minEcts = null,
        [Description("Maximum ECTS credits")] int? maxEcts = null,
        [Description("Language of instruction, e.g. 'de' or 'en'")] string? language = null,
        [Description("Optional free-text query applied on top of the filters (results are relevance-ranked)")] string? query = null,
        [Description("Response shape: 'compact' (default) core facts per module; 'full' complete summaries incl. offerings, lesson slots and prerequisites; 'codes' module codes only")] string? format = null,
        [Description("Maximum number of modules to return; omit for all. 'total' in the result is counted before this cut")] int? limit = null,
        [Description("Set true to also return facets: how many matching modules carry each tag — pick the next narrowing filter from these counts instead of guessing")] bool includeFacets = false)
    {
        IEnumerable<CompiledModule> candidates = store.Modules;

        // Validate/normalize once and reuse — the language filter and the result
        // narrowing must see the same semester value the semester filter used.
        var validatedSemester = string.IsNullOrWhiteSpace(semester) ? null : ValidateSemester(semester);

        var resultFormat = (format ?? "compact").Trim().ToLowerInvariant();
        if (resultFormat is not ("compact" or "full" or "codes"))
            throw new McpException($"Invalid format '{format}'. Use 'compact', 'full' or 'codes'.");
        if (limit is < 1)
            throw new McpException($"Invalid limit {limit} — it must be at least 1 (omit it to get all matches).");

        var allOf = ResolveTags(store, allOfTags, "allOfTags");
        var anyOf = ResolveTags(store, anyOfTags, "anyOfTags");
        var noneOf = ResolveTags(store, noneOfTags, "noneOfTags");
        if (allOf.Count > 0)
            candidates = candidates.Where(m => allOf.All(t => m.Tags.Contains(t, StringComparer.OrdinalIgnoreCase)));
        if (anyOf.Count > 0)
            candidates = candidates.Where(m => m.Tags.Intersect(anyOf, StringComparer.OrdinalIgnoreCase).Any());
        if (noneOf.Count > 0)
            candidates = candidates.Where(m => !m.Tags.Intersect(noneOf, StringComparer.OrdinalIgnoreCase).Any());
        if (validatedSemester != null)
            candidates = candidates.Where(m => MatchesSemester(m, validatedSemester));
        if (!string.IsNullOrWhiteSpace(level))
            candidates = candidates.Where(m => m.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(moduleType))
            candidates = candidates.Where(m => moduleType.Equals(m.ModuleType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(studyProgram))
            candidates = candidates.Where(m => m.StudyPrograms.Any(p => p.Contains(studyProgram, StringComparison.OrdinalIgnoreCase)));
        if (minEcts.HasValue) candidates = candidates.Where(m => m.Ects >= minEcts.Value);
        if (maxEcts.HasValue) candidates = candidates.Where(m => m.Ects <= maxEcts.Value);
        if (!string.IsNullOrWhiteSpace(language))
            candidates = candidates.Where(m => MatchesLanguage(m, language, validatedSemester));

        var filtered = candidates.ToList();

        if (!string.IsNullOrWhiteSpace(query))
        {
            // Rank within the filtered set — ranking the whole catalog first and
            // intersecting would drop filtered matches outscored by unfiltered ones.
            filtered = store.Search(query, limit: filtered.Count, within: filtered)
                .Select(x => x.Module)
                .ToList();
        }

        // Facets are computed over the full filtered set (before the limit cut) —
        // they describe the match set, not the returned page.
        IReadOnlyList<TagFacet>? facets = null;
        if (includeFacets)
        {
            facets = filtered
                .SelectMany(m => m.Tags)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .Select(g => new TagFacet(g.Key, g.Count()))
                .OrderByDescending(f => f.Count)
                .ThenBy(f => f.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var returned = limit.HasValue && limit.Value < filtered.Count
            ? filtered.Take(limit.Value).ToList()
            : filtered;
        IReadOnlyList<object> modules = resultFormat switch
        {
            "codes" => returned.Select(m => (object)m.Code).ToList(),
            "compact" => returned.Select(m => (object)CompactModule.From(m, validatedSemester)).ToList(),
            _ => returned.Select(m => (object)ModuleSummary.From(m, validatedSemester)).ToList()
        };

        return new ModuleSearchResults(filtered.Count, resultFormat, modules, facets);
    }

    /// <summary>Resolves tag inputs (German canonical or English alias) strictly: any
    /// unknown tag is an error — silently dropping one would widen an allOf filter or
    /// narrow an anyOf filter without the client noticing.</summary>
    private static List<string> ResolveTags(CatalogStore store, string[]? tags, string parameterName)
    {
        if (tags is not { Length: > 0 }) return new List<string>();

        var resolved = new List<string>();
        var unknown = new List<string>();
        foreach (var tag in tags)
        {
            var canonical = store.ResolveTagName(tag);
            if (canonical is null) unknown.Add(tag);
            else if (!resolved.Contains(canonical, StringComparer.OrdinalIgnoreCase)) resolved.Add(canonical);
        }
        if (unknown.Count > 0)
            throw new McpException(
                $"Unknown tags in {parameterName}: {string.Join(", ", unknown)}. Call list_tags for the valid vocabulary.");
        return resolved;
    }

    [McpServerTool(Name = "list_tags", ReadOnly = true, UseStructuredContent = true)]
    [Description("List the closed tag taxonomy of the catalog: canonical German tag names, English aliases, what each tag covers, and how many modules carry it. Use these tags with search_modules.")]
    public static IReadOnlyList<TagDefinition> ListTags(CatalogStore store) =>
        store.Taxonomy.OrderByDescending(t => t.ModuleCount).ToList();

    [McpServerTool(Name = "get_started", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/start.html")] // OpenAI Apps SDK (ChatGPT)
    [McpMeta("ui", JsonValue = """{"resourceUri":"ui://module-catalog/start"}""")] // MCP Apps (Claude, ...)
    [McpMeta("openai/toolInvocation/invoking", "Preparing the module advisor…")]
    [McpMeta("openai/toolInvocation/invoked", "Module advisor ready")]
    [Description("Onboarding entry point. Use this when the conversation starts, the student greets, the app was just added to the chat, or the request is vague ('hilf mir bei meinem Studium') — call it IMMEDIATELY, before any research and INSTEAD of asking clarifying questions or running a questionnaire/user-input flow of your own. The widget it renders shows a snapshot of the study program and interactively collects exactly what you would otherwise have to ask for (modules already completed, ECTS target), then starts a flow (plan the next semester, path to a target module, explore the catalog) as a chat message. Takes no arguments — there is nothing to prepare or ask beforehand. Skip it only when the student already made a specific request you can act on directly.")]
    public static StartData GetStarted(CatalogStore store)
    {
        var program = store.Modules
            .SelectMany(m => m.StudyPrograms)
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault() ?? "FHNW module catalog";
        var semesters = store.Modules
            .SelectMany(m => m.Offerings.Select(o => o.SemesterId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new StartData(
            Program: program,
            ModuleCount: store.Modules.Count,
            TagCount: store.Taxonomy.Count,
            CompiledAt: store.Manifest.CompiledAt.ToString("yyyy-MM-dd"),
            Semesters: semesters,
            Modules: store.Modules
                .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
                .Select(m => new StartModule(m.Code, m.Title, m.TitleEn, m.Ects))
                .ToList());
    }

    [McpServerTool(Name = "get_catalog_overview", ReadOnly = true, UseStructuredContent = true)]
    [Description("Compact markdown overview of the whole catalog (all modules with code, title, ECTS, type, semesters, schedule, tags) plus the tag taxonomy. Schedule semantics: ' + ' joins slots of the same class (the student attends all of them), ' or ' separates parallel classes (the student attends exactly one — no clash if one alternative is free); semester-prefixed entries apply to that semester only. Ideal first call to load the full catalog into context, especially before proposing a semester plan.")]
    public static string GetCatalogOverview(CatalogStore store) =>
        store.TaxonomyMarkdown() + "\n\n" + store.IndexMarkdown();

    // UseStructuredContent: the ChatGPT widget is hydrated from structuredContent
    // (window.openai.toolOutput); the SDK still emits the JSON text block alongside.
    [McpServerTool(Name = "plan_semester", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/semester-planner.html")] // OpenAI Apps SDK (ChatGPT)
    [McpMeta("ui", JsonValue = """{"resourceUri":"ui://module-catalog/semester-planner"}""")] // MCP Apps (Claude, ...)
    [McpMeta("openai/toolInvocation/invoking", "Collecting eligible modules…")]
    [McpMeta("openai/toolInvocation/invoked", "Semester planning data ready")]
    [Description("Get semester planning data for a student: which modules they are eligible for (structured prerequisites met, offered in the given semester) and which are blocked and why. Prerequisites are evaluated as GROUPS of interchangeable alternatives (language variants of the same course, e.g. 'Statistik 1' de/en) — completing any ONE member satisfies a group; blocked modules report missingPrerequisiteGroups in that shape. Results include free-text prerequisite notes, weekdays and lesson time slots (day, start-end; one entry per weekly slot, slots sharing a class number form one parallel class) where published. When the student wants a concrete plan or suggestion, work one out YOURSELF first — get_catalog_overview lists every module with ECTS, semesters, schedule and tags in one call — and pass it as proposedModules so the plan-builder widget opens with your proposal preselected. Call plan_semester exactly ONCE per planning request, with the proposal already included: every call renders its own plan-builder widget, so calling it again in the same turn (e.g. first without and then with proposedModules) shows the student two duplicate widgets. Prepare first, then make the one call; re-call only when the request itself changes (different semester, newly completed modules). You are advising the one student you are chatting with; if their interests or level are unknown, do not interrogate them — propose a sensible plan (mandatory/foundational modules first), state your assumptions briefly, and let them adjust in the widget. If you do not even know which modules they completed and the request is vague, call get_started first: its widget collects completed modules and the ECTS target without chat questions. In ChatGPT this renders an interactive plan-builder widget.")]
    public static SemesterPlanData PlanSemester(
        CatalogStore store,
        [Description("Semester to plan: type 'HS'/'FS' or concrete id like '26HS'")] string semester,
        [Description("Module codes the student has already completed. A completed module also counts for its equivalent language variants — the sibling edition is treated as completed too")] string[]? completedModules = null,
        [Description("Optional tags (German or English) describing the student's interests, used to annotate results")] string[]? interestTags = null,
        [Description("The student's target ECTS for the semester; echoed into the result and used to initialize the plan-builder widget (default 30)")] int? ectsTarget = null,
        [Description("Your concrete plan proposal: module codes to preselect in the plan-builder widget. Assemble it from get_catalog_overview — eligible modules matching the student's interests and ECTS target without overlapping lesson times. Codes that turn out not eligible are dropped and reported in proposedDropped.")] string[]? proposedModules = null)
    {
        semester = ValidateSemester(semester);
        // Expanded with equivalent language variants: a completed variant both satisfies
        // prerequisite groups and takes its sibling edition out of the eligible list —
        // otherwise the plan proposes retaking the same course in the other language.
        var completed = store.ExpandWithVariants(completedModules ?? Array.Empty<string>());
        var interests = (interestTags ?? Array.Empty<string>())
            .Select(store.ResolveTagName).Where(t => t != null).Select(t => t!).ToList();

        var eligible = new List<PlannableModule>();
        var blocked = new List<BlockedModule>();
        // Coverage counters: the widget explains WHY the eligible list is short
        // ("N not offered this semester, M already completed") instead of leaving
        // students to wonder where the rest of the catalog went.
        var notOffered = 0;
        var completedCount = 0;

        foreach (var m in store.Modules)
        {
            if (completed.Contains(m.Code)) { completedCount++; continue; }
            if (!MatchesSemester(m, semester)) { notOffered++; continue; }

            // Group semantics: ANY completed member satisfies a group — its members are
            // interchangeable language variants of the same course (issue #21).
            var missing = m.EffectivePrerequisiteGroups()
                .Where(g => !g.Any(completed.Contains))
                .Select(g => (IReadOnlyList<string>)g)
                .ToList();
            var interestMatches = m.Tags.Intersect(interests, StringComparer.OrdinalIgnoreCase).ToList();

            if (missing.Count == 0)
            {
                var recommendedMissing = m.Recommended.Where(r => !completed.Contains(r)).ToList();
                eligible.Add(new PlannableModule(ModuleSummary.From(m, semester), interestMatches, recommendedMissing));
            }
            else
            {
                blocked.Add(new BlockedModule(m.Code, m.Title, m.Ects, missing));
            }
        }

        // The client model's proposal is only forwarded where it can actually be
        // selected — everything else is echoed back so the model can correct itself.
        // Accepted codes are returned in the catalog's canonical casing: the widget
        // preselects by exact code match, so an accepted "MLDM" must become "mldm".
        var canonicalEligible = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in eligible) canonicalEligible[e.Module.Code] = e.Module.Code;
        var proposedInput = (proposedModules ?? Array.Empty<string>())
            .Select(c => c?.Trim()).Where(c => !string.IsNullOrEmpty(c)).Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var proposed = proposedInput
            .Where(canonicalEligible.ContainsKey)
            .Select(c => canonicalEligible[c])
            .ToList();
        var proposedDropped = proposedInput.Where(c => !canonicalEligible.ContainsKey(c)).ToList();

        return new SemesterPlanData(
            Semester: semester,
            Eligible: eligible
                .OrderByDescending(e => e.InterestMatches.Count)
                .ThenBy(e => e.Module.Code)
                .ToList(),
            Blocked: blocked.OrderBy(b => b.Code).ToList(),
            TotalEligibleEcts: eligible.Sum(e => e.Module.Ects),
            EctsTarget: ectsTarget is > 0 ? ectsTarget.Value : 30,
            NotOfferedCount: notOffered,
            CompletedCount: completedCount,
            Proposed: proposed,
            ProposedDropped: proposedDropped,
            Note: "Structured prerequisites are LLM-extracted from the official requirement texts and validated against " +
                  "this catalog; per-module 'prerequisiteNotes' may contain additional requirements that could not be " +
                  "resolved to module codes — take them into account when planning. 'missingPrerequisiteGroups' lists " +
                  "GROUPS of interchangeable codes (language variants of the same course): completing any ONE member " +
                  "unlocks that group.");
    }

    [McpServerTool(Name = "compare_modules", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/module-comparer.html")] // OpenAI Apps SDK (ChatGPT)
    [McpMeta("ui", JsonValue = """{"resourceUri":"ui://module-catalog/module-comparer"}""")] // MCP Apps (Claude, ...)
    [McpMeta("openai/widgetAccessible", true)] // the comparer widget re-calls this to add/swap a column
    [McpMeta("openai/toolInvocation/invoking", "Comparing modules…")]
    [McpMeta("openai/toolInvocation/invoked", "Module comparison ready")]
    [Description("Compare 2-4 modules side by side: ECTS, level, module type, semesters, languages, weekdays, lesson times, tags, prerequisites and summaries. In ChatGPT this renders an interactive comparison-table widget.")]
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

    [McpServerTool(Name = "plan_path", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("openai/outputTemplate", "ui://widget/path-planner.html")] // OpenAI Apps SDK (ChatGPT)
    [McpMeta("ui", JsonValue = """{"resourceUri":"ui://module-catalog/path-planner"}""")] // MCP Apps (Claude, ...)
    [McpMeta("openai/widgetAccessible", true)] // the path widget re-plans when the student marks modules as completed
    [McpMeta("openai/toolInvocation/invoking", "Computing the fastest path…")]
    [McpMeta("openai/toolInvocation/invoked", "Path to target module ready")]
    [Description("Compute the fastest way to reach ONE specific target module: all transitive prerequisites the student is still missing, scheduled into the earliest possible semesters (prerequisite order + HS/FS offering rhythm), and the earliest semester the target itself can be taken. Interchangeable language variants of the same course count once — the path schedules one variant and notes the alternatives. Call this only when the user explicitly asks how or when they can reach a particular module (e.g. 'When can I take X at the earliest?'); for planning a whole semester use plan_semester alone — do NOT call both for the same request unless the user asked for a path. Deterministic graph scheduling — no inference. In ChatGPT/Claude this renders an interactive path-timeline widget.")]
    public static PathPlanData PlanPath(
        CatalogStore store,
        [Description("Target module code the student wants to reach, e.g. 'mldm'")] string targetModule,
        [Description("Module codes the student has already completed. A completed module also counts for its equivalent language variants")] string[]? completedModules = null,
        [Description("First semester available for planning: concrete id like '26HS' (real semester labels) or type 'HS'/'FS' (generic labels). Default 'HS'.")] string? startSemester = null)
    {
        var target = store.GetModule(targetModule.Trim())
            ?? throw new McpException($"No module with code '{targetModule}'. Use search or search_modules to find valid codes.");
        var literalCompleted = new HashSet<string>(completedModules ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (literalCompleted.Contains(target.Code))
            throw new McpException($"'{target.Code}' is already in the completed modules — there is no path to plan.");
        // Expanded with equivalent language variants: a completed variant satisfies
        // prerequisites exactly like the course itself — including the target: planning
        // (and counting ECTS for) a course the student already passed in the other
        // language would contradict that contract.
        var completed = store.ExpandWithVariants(literalCompleted);
        if (completed.Contains(target.Code))
        {
            var variant = literalCompleted.FirstOrDefault(c =>
                store.ExpandWithVariants(new[] { c }).Contains(target.Code)) ?? "a language variant";
            throw new McpException(
                $"'{variant}' is already completed and is an equivalent language variant of '{target.Code}' — " +
                "equivalent variants count as completed, so there is no path to plan. If the student wants to take " +
                $"'{target.Code}' anyway, treat it as a regular eligible module (plan_semester).");
        }

        var start = ValidateSemester(startSemester ?? "HS");
        var concrete = start.Length == 4;
        var anchorIsHs = (concrete ? start[2..] : start).Equals("HS", StringComparison.OrdinalIgnoreCase);
        string SlotType(int i) => (i % 2 == 0) == anchorIsHs ? "HS" : "FS";

        var notes = new List<string>();
        var alreadyCompleted = new List<string>();

        // First slot >= the given one that matches the module's offering rhythm
        // (modules without offering info are unconstrained; the scheduling loop
        // reports those separately).
        int FitSlot(CompiledModule m, int slot)
        {
            var types = m.OfferedIn.Count > 0
                ? m.OfferedIn
                : m.Offerings.Select(o => SourceModuleMapper.SemesterTypeOf(o.SemesterId))
                    .Where(t => t != null).Select(t => t!).Distinct().ToList();
            if (types.Count > 0)
            {
                while (!types.Contains(SlotType(slot), StringComparer.OrdinalIgnoreCase)) slot++;
            }
            return slot;
        }

        // Earliest slot each module could be completed in (memoized). A group is
        // available as soon as its FASTEST member is: alternatives can differ in
        // offering rhythm (HS/FS) and in their own prerequisite chains, so picking
        // a fixed group member could miss the fastest path.
        var earliest = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var computing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int Earliest(CompiledModule m)
        {
            if (earliest.TryGetValue(m.Code, out var known)) return known;
            if (!computing.Add(m.Code))
                throw new McpException($"Prerequisite cycle detected around '{m.Code}'. " +
                                       "The catalog data needs fixing before a path can be planned.");
            var slot = 0;
            foreach (var group in m.EffectivePrerequisiteGroups())
            {
                if (group.Any(completed.Contains)) continue;
                var members = group.Select(store.GetModule).Where(x => x != null).Select(x => x!).ToList();
                if (members.Count == 0) continue; // dangling codes are reported during Visit
                slot = Math.Max(slot, members.Min(Earliest) + 1);
            }
            slot = FitSlot(m, slot);
            computing.Remove(m.Code);
            earliest[m.Code] = slot;
            return slot;
        }

        // Missing-prerequisite closure of the target, in topological order (prereqs first).
        // Completed modules cut the recursion — their own prerequisites are irrelevant.
        var state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // false = in progress, true = done
        var path = new List<string>();
        var topo = new List<CompiledModule>();
        void Visit(CompiledModule m)
        {
            state[m.Code] = false;
            path.Add(m.Code);
            foreach (var group in m.EffectivePrerequisiteGroups())
            {
                // ANY completed member satisfies the group (interchangeable language
                // variants). Report the code the student actually completed, not the
                // expanded sibling variant.
                var completedMember = group.FirstOrDefault(literalCompleted.Contains)
                    ?? group.FirstOrDefault(completed.Contains);
                if (completedMember != null)
                {
                    if (!alreadyCompleted.Contains(completedMember, StringComparer.OrdinalIgnoreCase))
                        alreadyCompleted.Add(completedMember);
                    continue;
                }
                // Schedule exactly ONE representative per group: the fastest-completing
                // alternative wins; ties prefer a variant another module already pulled
                // onto the path (no duplicate variants across dependents), then the
                // order the requirement referenced them in.
                var candidates = group.Select(store.GetModule).Where(x => x != null).Select(x => x!).ToList();
                if (candidates.Count == 0)
                {
                    notes.Add($"{m.Code}: prerequisite '{group[0]}' is not in this catalog — verify it manually.");
                    continue;
                }
                var best = candidates.Min(Earliest);
                var pm = candidates.FirstOrDefault(c => Earliest(c) == best && state.ContainsKey(c.Code))
                    ?? candidates.First(c => Earliest(c) == best);
                if (group.Count > 1)
                    notes.Add($"{m.Code}: '{string.Join("' / '", group)}' are equivalent variants — " +
                              $"the path schedules '{pm.Code}'; any one of them satisfies the requirement.");
                if (state.TryGetValue(pm.Code, out var done))
                {
                    if (!done)
                        throw new McpException("Prerequisite cycle detected: " +
                            string.Join(" -> ", path.SkipWhile(c => !c.Equals(pm.Code, StringComparison.OrdinalIgnoreCase))) +
                            $" -> {pm.Code}. The catalog data needs fixing before a path can be planned.");
                    continue;
                }
                Visit(pm);
            }
            path.RemoveAt(path.Count - 1);
            state[m.Code] = true;
            topo.Add(m);
        }
        Visit(target);

        // Earliest-slot scheduling: a module goes into the first semester after all its
        // missing prerequisites whose HS/FS type matches one of its offering types.
        // Types alternate per slot, so the while loop advances at most one slot.
        var slotOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in topo)
        {
            // Per group only the scheduled representative appears in slotOf; a group whose
            // requirement is met by a completed variant contributes no constraint.
            var slot = m.EffectivePrerequisiteGroups()
                .SelectMany(g => g.Where(slotOf.ContainsKey))
                .Select(p => slotOf[p] + 1)
                .DefaultIfEmpty(0)
                .Max();
            var types = m.OfferedIn.Count > 0
                ? m.OfferedIn
                : m.Offerings.Select(o => SourceModuleMapper.SemesterTypeOf(o.SemesterId))
                    .Where(t => t != null).Select(t => t!).Distinct().ToList();
            if (types.Count > 0)
            {
                while (!types.Contains(SlotType(slot), StringComparer.OrdinalIgnoreCase)) slot++;
            }
            else
            {
                notes.Add($"{m.Code}: no offering semester known — scheduled without the HS/FS constraint.");
            }
            slotOf[m.Code] = slot;

            if (!string.IsNullOrWhiteSpace(m.PrerequisiteNotes))
                notes.Add($"{m.Code}: {m.PrerequisiteNotes}");
        }

        var labels = new string[slotOf.Values.Max() + 1];
        for (var i = 0; i < labels.Length; i++)
            labels[i] = concrete ? NthSemesterId(start, i) : $"Semester {i + 1} ({SlotType(i)})";

        var steps = slotOf
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var modules = g
                    .Select(kv => store.GetModule(kv.Key)!)
                    .OrderBy(m => m.Code, StringComparer.OrdinalIgnoreCase)
                    .Select(m => concrete ? ModuleSummary.From(m, labels[g.Key]) : ModuleSummary.From(m))
                    .ToList();
                return new PathStep(labels[g.Key], g.Key, modules, modules.Sum(m => m.Ects));
            })
            .ToList();

        notes.Add("Modules are placed at their earliest possible semester; they can be moved later as long as the order is kept.");

        return new PathPlanData(
            TargetCode: target.Code,
            TargetTitle: target.Title,
            TargetTitleEn: target.TitleEn,
            StartSemester: start,
            EarliestSemester: labels[slotOf[target.Code]],
            SemesterCount: slotOf[target.Code] + 1,
            TotalEcts: topo.Sum(m => m.Ects),
            CompletedModules: completed.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            AlreadyCompleted: alreadyCompleted.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList(),
            Steps: steps,
            Notes: notes);
    }

    /// <summary>The i-th semester id starting at a concrete anchor: FS→HS is the same
    /// year (spring precedes autumn), HS→FS rolls into the next year.</summary>
    private static string NthSemesterId(string start, int i)
    {
        var year = int.Parse(start[..2]);
        var isHs = start[2..].Equals("HS", StringComparison.OrdinalIgnoreCase);
        for (var k = 0; k < i; k++)
        {
            if (isHs) { year++; isHs = false; }
            else isHs = true;
        }
        return $"{year:00}{(isHs ? "HS" : "FS")}";
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

    /// <summary>Offerings matching a validated semester: a type ("HS"/"FS") matches by
    /// suffix, a concrete id ("26HS") matches exactly.</summary>
    internal static List<ModuleOffering> MatchingOfferings(CompiledModule m, string semester)
    {
        var isConcrete = semester.Length == 4;
        return m.Offerings
            .Where(o => isConcrete
                ? o.SemesterId.Equals(semester, StringComparison.OrdinalIgnoreCase)
                : o.SemesterId.EndsWith(semester, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>Semester matching: "HS"/"FS" match the offering type; "26HS" matches a concrete offering.
    /// Falls back to OfferedIn for catalogs without concrete offerings (e.g. sample data).</summary>
    private static bool MatchesSemester(CompiledModule m, string semester)
    {
        if (semester.Length == 4 && m.Offerings.Count > 0)
            return MatchingOfferings(m, semester).Count > 0;

        var type = semester.Length == 4 ? semester[2..] : semester;
        return m.OfferedIn.Contains(type, StringComparer.OrdinalIgnoreCase)
               || MatchingOfferings(m, type).Count > 0;
    }

    /// <summary>Language filtering: prefer the language lists of the offerings matching the
    /// requested semester (type or concrete id) — languages can differ between semesters.
    /// Falls back to the module-level union when no matched offering carries languages.</summary>
    private static bool MatchesLanguage(CompiledModule m, string language, string? semester)
    {
        if (!string.IsNullOrWhiteSpace(semester))
        {
            var withLanguages = MatchingOfferings(m, semester).Where(o => o.Languages.Count > 0).ToList();
            if (withLanguages.Count > 0)
                return withLanguages.Any(o => o.Languages.Contains(language, StringComparer.OrdinalIgnoreCase));
        }
        return m.Languages.Contains(language, StringComparer.OrdinalIgnoreCase);
    }

    internal static string OfferedText(CompiledModule m) =>
        m.Offerings.Count > 0
            ? string.Join("/", m.Offerings.Select(o => o.SemesterId))
            : string.Join("/", m.OfferedIn);
}

public record SearchResults(IReadOnlyList<SearchResultItem> Results);

/// <summary>Result of search_modules: Total counts all matches (before any limit cut),
/// Modules holds the matches shaped per the requested format, Facets the per-tag counts
/// of the full match set when requested.</summary>
public record ModuleSearchResults(
    int Total,
    string Format,
    IReadOnlyList<object> Modules,
    IReadOnlyList<TagFacet>? Facets);

public record TagFacet(string Tag, int Count);

/// <summary>Index-row-sized module facts for the 'compact' search format: enough to
/// shortlist and count, cheap enough to return in bulk. Details come from the 'full'
/// format or fetch.</summary>
public record CompactModule(
    string Code, string Title, string? TitleEn, int Ects, string Level, string? ModuleType,
    string Offered, IReadOnlyList<string> Languages, IReadOnlyList<string> Tags)
{
    public static CompactModule From(CompiledModule m) => new(
        m.Code, m.Title, m.TitleEn, m.Ects, m.Level, m.ModuleType,
        ModuleCatalogTools.OfferedText(m), m.Languages, m.Tags);

    /// <summary>Narrowed to a semester: languages can differ between HS and FS, so a
    /// semester-filtered compact row must advertise the matched offerings' languages
    /// (clients shortlist from these rows) — not the module-level union. Falls back to
    /// the union when the semester has no offering or none carry languages.</summary>
    public static CompactModule From(CompiledModule m, string? semester)
    {
        var matched = string.IsNullOrWhiteSpace(semester)
            ? new List<ModuleOffering>()
            : ModuleCatalogTools.MatchingOfferings(m, semester);
        var languages = matched.SelectMany(o => o.Languages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return languages.Count > 0 ? From(m) with { Languages = languages } : From(m);
    }
}

public record SearchResultItem(string Id, string Title, string Text, string? Url);

public record FetchResult(string Id, string Title, string Text, string? Url, Dictionary<string, object?> Metadata);

public record ModuleSummary(
    string Code, string Title, string? TitleEn, int Ects, string Level, string? ModuleType,
    IReadOnlyList<string> OfferedIn, IReadOnlyList<ModuleOffering> Offerings,
    IReadOnlyList<string> Languages, IReadOnlyList<string> Weekdays,
    IReadOnlyList<Lesson> Lessons,
    IReadOnlyList<string> Tags, string Summary, string? SummaryEn, string Audience,
    IReadOnlyList<string> Prerequisites, IReadOnlyList<IReadOnlyList<string>> PrerequisiteGroups,
    IReadOnlyList<string> Recommended,
    string? PrerequisiteNotes, string? Url)
{
    public static ModuleSummary From(CompiledModule m) => new(
        m.Code, m.Title, m.TitleEn, m.Ects, m.Level, m.ModuleType,
        m.OfferedIn, m.Offerings, m.Languages, m.Weekdays,
        NewestLessons(m.Offerings),
        m.Tags, m.Summary, m.SummaryEn, m.Audience,
        m.Prerequisites,
        m.EffectivePrerequisiteGroups().Select(g => (IReadOnlyList<string>)g).ToList(),
        m.Recommended, m.PrerequisiteNotes, m.Url);

    /// <summary>Summary narrowed to the offerings matching the given semester: a module can
    /// meet on different weekdays in HS vs FS, and the module-level union would produce wrong
    /// clash hints in the planner widget. Weekdays and lesson slots come strictly from the
    /// matched offerings (empty is accurate — the widget hides the chips); languages fall
    /// back to the module union when the matched offerings carry none. Without a semester,
    /// or for catalogs without concrete offerings, the module-level fields are used unchanged.</summary>
    public static ModuleSummary From(CompiledModule m, string? semester)
    {
        var matched = string.IsNullOrWhiteSpace(semester)
            ? new List<ModuleOffering>()
            : ModuleCatalogTools.MatchingOfferings(m, semester);
        if (matched.Count == 0) return From(m);

        var languages = matched.SelectMany(o => o.Languages).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var weekdays = matched.SelectMany(o => o.Weekdays).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return From(m) with
        {
            Languages = languages.Count > 0 ? languages : m.Languages,
            Weekdays = weekdays,
            Lessons = NewestLessons(matched),
        };
    }

    /// <summary>Offerings are ordered newest-first; the newest one with published lesson
    /// slots represents the schedule — a cross-semester union would mix HS and FS times
    /// (or the same semester type of different years) into one bogus timetable.</summary>
    private static IReadOnlyList<Lesson> NewestLessons(IReadOnlyList<ModuleOffering> offerings) =>
        offerings.FirstOrDefault(o => o.Lessons.Count > 0)?.Lessons ?? (IReadOnlyList<Lesson>)Array.Empty<Lesson>();
}

public record PlannableModule(
    ModuleSummary Module,
    IReadOnlyList<string> InterestMatches,
    IReadOnlyList<string> MissingRecommended);

/// <summary>Missing prerequisites as groups: the student must complete ONE module from each
/// group — members of a group are interchangeable (language variants of the same course).</summary>
public record BlockedModule(string Code, string Title, int Ects, IReadOnlyList<IReadOnlyList<string>> MissingPrerequisiteGroups);

public record SemesterPlanData(
    string Semester,
    IReadOnlyList<PlannableModule> Eligible,
    IReadOnlyList<BlockedModule> Blocked,
    int TotalEligibleEcts,
    int EctsTarget,
    int NotOfferedCount,
    int CompletedCount,
    IReadOnlyList<string> Proposed,
    IReadOnlyList<string> ProposedDropped,
    string Note);

public record ModuleComparisonData(
    IReadOnlyList<ModuleSummary> Modules,
    IReadOnlyList<string> NotFound);

/// <summary>Minimal module row for the start widget's completed-modules and target
/// pickers — enough for client-side autocomplete without further tool calls.</summary>
public record StartModule(string Code, string Title, string? TitleEn, int Ects);

public record StartData(
    string Program,
    int ModuleCount,
    int TagCount,
    string CompiledAt,
    IReadOnlyList<string> Semesters,
    IReadOnlyList<StartModule> Modules);

public record PathStep(string Semester, int Slot, IReadOnlyList<ModuleSummary> Modules, int Ects);

public record PathPlanData(
    string TargetCode,
    string TargetTitle,
    string? TargetTitleEn,
    string StartSemester,
    string EarliestSemester,
    int SemesterCount,
    int TotalEcts,
    IReadOnlyList<string> CompletedModules,
    IReadOnlyList<string> AlreadyCompleted,
    IReadOnlyList<PathStep> Steps,
    IReadOnlyList<string> Notes);
