using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StructuredRAG.Core.Services;

/// <summary>
/// Compiles a raw module catalog into query-time-optimized artifacts.
///
/// The compilation is deliberately LLM-heavy because it runs offline (daily/weekly):
///   Phase 1 — design (or evolve) a closed, bilingual tag taxonomy over the whole catalog.
///   Phase 2 — enrich each module against that closed vocabulary: bilingual summaries,
///             audience, typical student questions, and prerequisite links extracted
///             from the free-text requirements and validated against the catalog.
///
/// The output contains everything a query-time client model needs to reason on its
/// own, so the serving layer does zero inference. Incremental: modules whose source
/// is unchanged (SourceHash) are reused from the previous catalog.
/// </summary>
public class KnowledgeCompilationService
{
    private readonly ILlmClient _llmService;
    private readonly ILogger<KnowledgeCompilationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions HashOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public KnowledgeCompilationService(
        ILlmClient llmService,
        ILogger<KnowledgeCompilationService> logger)
    {
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<CompiledCatalog> CompileAsync(
        IReadOnlyList<SourceModule> modules,
        string sourceName,
        CompiledCatalog? previous = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Compiling catalog with {Count} modules", modules.Count);

        var taxonomy = await DesignTaxonomyAsync(modules, previous?.Taxonomy, cancellationToken);
        _logger.LogInformation("Taxonomy ready: {Count} tags", taxonomy.Count);

        var validTagNames = new HashSet<string>(taxonomy.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var previousByCode = (previous?.Modules ?? new List<CompiledModule>())
            .Where(m => m.SourceHash != null)
            .ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);

        var compiled = new List<CompiledModule>(modules.Count);
        var reused = 0;
        foreach (var module in modules)
        {
            var hash = ComputeSourceHash(module);

            // Incremental: reuse the previous record's LLM enrichments when the prompt
            // inputs are unchanged and its tags are still part of the (possibly evolved)
            // taxonomy. Pass-through data is refreshed from the current source — schedule
            // or location changes must reach the catalog without an LLM call.
            if (previousByCode.TryGetValue(module.Code, out var prev)
                && prev.SourceHash == hash
                && prev.Tags.All(validTagNames.Contains))
            {
                compiled.Add(RefreshPassThrough(prev, module, hash));
                reused++;
                continue;
            }

            compiled.Add(await CompileModuleAsync(module, taxonomy, modules, hash, cancellationToken));
            _logger.LogInformation("Compiled module {Code} ({Title})", module.Code, module.Title);
        }

        if (reused > 0)
        {
            _logger.LogInformation("Reused {Count} unchanged modules from previous catalog", reused);
        }

        ValidateCatalog(compiled, taxonomy);

        return new CompiledCatalog
        {
            Manifest = new CatalogManifest
            {
                CompiledAt = DateTime.UtcNow,
                Source = sourceName,
                ModuleCount = compiled.Count,
                TagCount = taxonomy.Count
            },
            Taxonomy = taxonomy,
            Modules = compiled
        };
    }

    /// <summary>
    /// Phase 1: one pass over the whole catalog to design a closed, described taxonomy.
    /// When a previous taxonomy exists it is passed as the base vocabulary so tag names
    /// stay stable across runs.
    /// </summary>
    private async Task<List<TagDefinition>> DesignTaxonomyAsync(
        IReadOnlyList<SourceModule> modules,
        List<TagDefinition>? previousTaxonomy,
        CancellationToken cancellationToken)
    {
        var catalogOverview = new StringBuilder();
        foreach (var m in modules)
        {
            var titleEn = string.IsNullOrWhiteSpace(m.TitleEn) ? "" : $" | {m.TitleEn}";
            catalogOverview.AppendLine($"- {m.Code}: {m.Title}{titleEn} — {Truncate(m.Description.Replace('\n', ' '), 180)}");
        }

        var existingSection = "";
        if (previousTaxonomy is { Count: > 0 })
        {
            var existing = string.Join("\n", previousTaxonomy.Select(t =>
                $"- {t.Name} ({t.NameEn}): {t.Description}"));
            existingSection = $@"

### Existing taxonomy (from the previous compilation run)
Keep these tag names STABLE — clients and links depend on them. Only add new tags for
uncovered topics, and only drop a tag if it clearly no longer fits any module.
{existing}";
        }

        var prompt = $@"You are designing the tag taxonomy for a university module catalog.
Students will use an AI assistant to find modules and plan their semester. The assistant
selects tags based ONLY on your tag names and descriptions, so descriptions must state
clearly what kinds of student interests and questions each tag covers.

### Rules
- Between 10 and 40 tags for the whole catalog.
- Bilingual: 'name' is the canonical GERMAN tag name (max 3 words), 'nameEn' the English
  equivalent; 'description' one German sentence, 'descriptionEn' one English sentence.
- Cover subject areas (e.g. ""Maschinelles Lernen"") and student intents (e.g. ""Karriere-Grundlagen"").
- No near-duplicates, no overly generic tags like ""Bildung"".
- Output ONLY a JSON array:
  [{{""name"": ""..."", ""nameEn"": ""..."", ""description"": ""..."", ""descriptionEn"": ""...""}}]{existingSection}

### Catalog
{catalogOverview}

Output JSON:";

        var system = "You are an expert curriculum librarian designing a retrieval taxonomy. Output only valid JSON.";
        var response = await _llmService.GenerateAsync(prompt, cancellationToken, system);

        var tags = ExtractJson<List<TagDefinition>>(response);
        if (tags == null || tags.Count == 0)
        {
            throw new InvalidOperationException(
                $"Taxonomy design failed: could not parse tags from model response: {Truncate(response, 500)}");
        }

        return tags
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Phase 2: enrich a single module against the closed taxonomy — bilingual summaries
    /// plus prerequisite extraction constrained to the codes of this catalog.
    /// </summary>
    private async Task<CompiledModule> CompileModuleAsync(
        SourceModule module,
        List<TagDefinition> taxonomy,
        IReadOnlyList<SourceModule> allModules,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        var tagList = string.Join("\n", taxonomy.Select(t => $"- {t.Name} ({t.NameEn}): {t.Description}"));
        var moduleList = string.Join("\n", allModules
            .Where(m => m.Code != module.Code)
            .Select(m => $"- {m.Code}: {m.Title}"));
        var requirements = module.RequirementsText ?? module.RequirementsTextEn;

        var prompt = $@"Enrich this university module for an AI study advisor. Students ask the
advisor things like ""welche Module passen zu meinem Interesse an X?"" or ""hilf mir, mein
Semester zu planen"". Content is German-first; produce German AND English enrichments.

### Module
Code: {module.Code}
Title: {module.Title}{(string.IsNullOrWhiteSpace(module.TitleEn) ? "" : $" | EN: {module.TitleEn}")}
ECTS: {module.Ects}, Level: {module.Level}, Type: {module.ModuleType}, Offered in: {string.Join("/", module.OfferedIn)}
Study programs: {string.Join(", ", module.StudyPrograms)}
Description:
{Truncate(module.Description, 2500)}

Enrollment requirements (free text): {(string.IsNullOrWhiteSpace(requirements) ? "none stated" : Truncate(requirements, 800))}

### Closed tag vocabulary (assign ONLY canonical German names from this list, verbatim)
{tagList}

### Modules available in this catalog (for prerequisite resolution)
{moduleList}

### Output
A single JSON object:
{{
  ""summary"": ""2-3 German sentences: what the module teaches and what students can do afterwards"",
  ""summaryEn"": ""the same in English"",
  ""audience"": ""1-2 German sentences: who should take it (interests, goals, prior strengths)"",
  ""audienceEn"": ""the same in English"",
  ""tags"": [""3 to 8 canonical German tag names copied verbatim from the vocabulary""],
  ""typicalQuestions"": [""3 to 5 German questions a student might ask for which this module is a good answer""],
  ""typicalQuestionsEn"": [""the same questions in English""],
  ""prerequisites"": [""codes from the module list above that the enrollment requirements clearly refer to; empty if none""],
  ""prerequisiteNotes"": ""requirement aspects that could NOT be mapped to module codes (German, verbatim-ish); empty string if none""
}}

Output JSON:";

        var system = "You are an expert bilingual (German/English) study advisor compiling structured module metadata. Output only valid JSON.";
        var response = await _llmService.GenerateAsync(prompt, cancellationToken, system);

        var enrichment = ExtractJson<ModuleEnrichment>(response);
        if (enrichment == null)
        {
            _logger.LogWarning("Enrichment parse failed for {Code}; falling back to raw description", module.Code);
        }

        var validTagNames = new HashSet<string>(taxonomy.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var assignedTags = (enrichment?.Tags ?? new List<string>())
            .Where(validTagNames.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Prerequisites: source-provided ones win; otherwise use the extracted ones,
        // kept only when they resolve to real catalog codes (no self-references).
        var validCodes = new HashSet<string>(allModules.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);
        var extractedPrerequisites = (enrichment?.Prerequisites ?? new List<string>())
            .Where(p => validCodes.Contains(p) && !p.Equals(module.Code, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var droppedPrereqs = (enrichment?.Prerequisites ?? new List<string>()).Except(extractedPrerequisites).ToList();
        if (droppedPrereqs.Count > 0)
        {
            _logger.LogWarning("Module {Code}: dropped unresolvable extracted prerequisites: {Dropped}",
                module.Code, string.Join(", ", droppedPrereqs));
        }

        var prerequisiteNotes = enrichment?.PrerequisiteNotes;
        if (string.IsNullOrWhiteSpace(prerequisiteNotes) && extractedPrerequisites.Count == 0)
        {
            // Nothing extracted at all — keep the raw requirement text so clients can still reason over it.
            prerequisiteNotes = requirements;
        }

        return new CompiledModule
        {
            Code = module.Code,
            ModuleId = module.ModuleId,
            Title = module.Title,
            TitleEn = module.TitleEn,
            Description = module.Description,
            DescriptionEn = module.DescriptionEn,
            Ects = module.Ects,
            Level = module.Level,
            OfferedIn = module.OfferedIn,
            Offerings = module.Offerings,
            Languages = module.Languages,
            Weekdays = module.Weekdays,
            Prerequisites = module.Prerequisites.Count > 0 ? module.Prerequisites : extractedPrerequisites,
            PrerequisiteNotes = string.IsNullOrWhiteSpace(prerequisiteNotes) ? null : prerequisiteNotes.Trim(),
            Recommended = module.Recommended,
            StudyPrograms = module.StudyPrograms,
            ModuleType = module.ModuleType,
            Locations = module.Locations,
            ResponsibleName = module.ResponsibleName,
            Assessment = module.Assessment,
            Url = module.Url,
            Summary = enrichment?.Summary ?? Truncate(module.Description, 300),
            SummaryEn = enrichment?.SummaryEn,
            Audience = enrichment?.Audience ?? string.Empty,
            AudienceEn = enrichment?.AudienceEn,
            Tags = assignedTags,
            TypicalQuestions = enrichment?.TypicalQuestions ?? new List<string>(),
            TypicalQuestionsEn = enrichment?.TypicalQuestionsEn ?? new List<string>(),
            SourceHash = sourceHash
        };
    }

    /// <summary>
    /// Referential-integrity pass. Prerequisites pointing outside the catalog are removed
    /// (with a warning) — a dangling link would make plan_semester block modules forever.
    /// </summary>
    private void ValidateCatalog(List<CompiledModule> modules, List<TagDefinition> taxonomy)
    {
        var codes = new HashSet<string>(modules.Select(m => m.Code), StringComparer.OrdinalIgnoreCase);

        foreach (var module in modules)
        {
            var dangling = module.Prerequisites.Where(p => !codes.Contains(p)).ToList();
            if (dangling.Count > 0)
            {
                _logger.LogWarning("Module {Code}: removing dangling prerequisites: {Dangling}",
                    module.Code, string.Join(", ", dangling));
                module.Prerequisites = module.Prerequisites.Where(codes.Contains).ToList();
            }
        }

        foreach (var tag in taxonomy)
        {
            tag.ModuleCount = modules.Count(m => m.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
        }

        var untagged = modules.Where(m => m.Tags.Count == 0).Select(m => m.Code).ToList();
        if (untagged.Count > 0)
        {
            _logger.LogWarning("{Count} modules have no tags: {Codes}", untagged.Count, string.Join(", ", untagged));
        }

        var unusedTags = taxonomy.Where(t => t.ModuleCount == 0).Select(t => t.Name).ToList();
        if (unusedTags.Count > 0)
        {
            _logger.LogWarning("{Count} tags are not used by any module: {Tags}", unusedTags.Count, string.Join(", ", unusedTags));
        }
    }

    /// <summary>
    /// Hash over exactly the fields that feed the enrichment prompt in
    /// <see cref="CompileModuleAsync"/>. Deterministic pass-through data (offerings,
    /// lessons, locations, assessment, ...) is deliberately excluded — it changes
    /// between catalog publications without affecting what the LLM would produce,
    /// and is refreshed on reuse instead.
    /// </summary>
    public static string ComputeSourceHash(SourceModule module)
    {
        var llmInputs = new
        {
            module.Code,
            module.Title,
            module.TitleEn,
            module.Description,
            module.Ects,
            module.Level,
            module.ModuleType,
            module.OfferedIn,
            module.StudyPrograms,
            module.RequirementsText,
            module.RequirementsTextEn
        };
        var json = JsonSerializer.Serialize(llmInputs, HashOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>
    /// Rebuilds a reused module from the current source's deterministic fields plus the
    /// previous record's LLM outputs — mirrors the assignments in
    /// <see cref="CompileModuleAsync"/> so a reused record never carries stale
    /// pass-through data (e.g. an outdated lesson schedule).
    /// </summary>
    private static CompiledModule RefreshPassThrough(CompiledModule prev, SourceModule module, string sourceHash) => new()
    {
        Code = module.Code,
        ModuleId = module.ModuleId,
        Title = module.Title,
        TitleEn = module.TitleEn,
        Description = module.Description,
        DescriptionEn = module.DescriptionEn,
        Ects = module.Ects,
        Level = module.Level,
        OfferedIn = module.OfferedIn,
        Offerings = module.Offerings,
        Languages = module.Languages,
        Weekdays = module.Weekdays,
        // Same rule as a fresh compile: structured source prerequisites win; otherwise
        // keep the previously extracted (and validated) ones.
        Prerequisites = module.Prerequisites.Count > 0 ? module.Prerequisites : prev.Prerequisites,
        PrerequisiteNotes = prev.PrerequisiteNotes,
        Recommended = module.Recommended,
        StudyPrograms = module.StudyPrograms,
        ModuleType = module.ModuleType,
        Locations = module.Locations,
        ResponsibleName = module.ResponsibleName,
        Assessment = module.Assessment,
        Url = module.Url,
        Summary = prev.Summary,
        SummaryEn = prev.SummaryEn,
        Audience = prev.Audience,
        AudienceEn = prev.AudienceEn,
        Tags = prev.Tags,
        TypicalQuestions = prev.TypicalQuestions,
        TypicalQuestionsEn = prev.TypicalQuestionsEn,
        SourceHash = sourceHash
    };

    private class ModuleEnrichment
    {
        public string? Summary { get; set; }
        public string? SummaryEn { get; set; }
        public string? Audience { get; set; }
        public string? AudienceEn { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? TypicalQuestions { get; set; }
        public List<string>? TypicalQuestionsEn { get; set; }
        public List<string>? Prerequisites { get; set; }
        public string? PrerequisiteNotes { get; set; }
    }

    /// <summary>Extracts the first JSON object or array embedded in a model response.</summary>
    private T? ExtractJson<T>(string response) where T : class
    {
        foreach (var (open, close) in new[] { ('[', ']'), ('{', '}') })
        {
            var start = response.IndexOf(open);
            var end = response.LastIndexOf(close);
            if (start < 0 || end <= start) continue;

            try
            {
                var json = response.Substring(start, end - start + 1);
                var parsed = JsonSerializer.Deserialize<T>(json, JsonOptions);
                if (parsed != null) return parsed;
            }
            catch (JsonException)
            {
                // try the next bracket pair
            }
        }

        return null;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
