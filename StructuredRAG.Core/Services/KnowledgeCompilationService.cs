using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using System.Text;
using System.Text.Json;

namespace StructuredRAG.Core.Services;

/// <summary>
/// Compiles a raw module catalog into query-time-optimized artifacts.
///
/// The compilation is deliberately LLM-heavy because it runs offline (daily/weekly):
///   Phase 1 — design a closed tag taxonomy over the whole catalog in one pass.
///   Phase 2 — enrich each module against that closed vocabulary.
///
/// The output contains everything a query-time client model needs to reason on its
/// own (tag descriptions, summaries, typical questions), so the serving layer does
/// zero inference.
/// </summary>
public class KnowledgeCompilationService
{
    private readonly DockerModelRunnerService _llmService;
    private readonly ILogger<KnowledgeCompilationService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public KnowledgeCompilationService(
        DockerModelRunnerService llmService,
        ILogger<KnowledgeCompilationService> logger)
    {
        _llmService = llmService;
        _logger = logger;
    }

    public async Task<CompiledCatalog> CompileAsync(
        IReadOnlyList<SourceModule> modules,
        string sourceName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Compiling catalog with {Count} modules", modules.Count);

        var taxonomy = await DesignTaxonomyAsync(modules, cancellationToken);
        _logger.LogInformation("Taxonomy designed: {Count} tags", taxonomy.Count);

        var compiled = new List<CompiledModule>(modules.Count);
        foreach (var module in modules)
        {
            compiled.Add(await CompileModuleAsync(module, taxonomy, cancellationToken));
            _logger.LogInformation("Compiled module {Code}", module.Code);
        }

        foreach (var tag in taxonomy)
        {
            tag.ModuleCount = compiled.Count(m =>
                m.Tags.Contains(tag.Name, StringComparer.OrdinalIgnoreCase));
        }

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
    /// Requires a model with enough context for the full module list; module descriptions
    /// are truncated since titles carry most of the signal for taxonomy design.
    /// </summary>
    private async Task<List<TagDefinition>> DesignTaxonomyAsync(
        IReadOnlyList<SourceModule> modules,
        CancellationToken cancellationToken)
    {
        var catalogOverview = new StringBuilder();
        foreach (var m in modules)
        {
            catalogOverview.AppendLine($"- {m.Code}: {m.Title} — {Truncate(m.Description, 200)}");
        }

        var prompt = $@"You are designing the tag taxonomy for a university module catalog.
Students will use an AI assistant to find modules and plan their semester. The assistant
selects tags based ONLY on your tag names and descriptions, so descriptions must state
clearly what kinds of student interests and questions each tag covers.

### Rules
- Between 10 and 40 tags for the whole catalog.
- Each tag: a short name (max 3 words) and a one-sentence description.
- Cover both subject areas (e.g. ""Machine Learning"") and student intents (e.g. ""Career Foundations"").
- No near-duplicates, no overly generic tags like ""Education"".
- Output ONLY a JSON array: [{{""name"": ""..."", ""description"": ""...""}}]

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

        return tags;
    }

    /// <summary>
    /// Phase 2: enrich a single module against the closed taxonomy. This is a
    /// constrained classification + summarization task, suitable for a small model.
    /// </summary>
    private async Task<CompiledModule> CompileModuleAsync(
        SourceModule module,
        List<TagDefinition> taxonomy,
        CancellationToken cancellationToken)
    {
        var tagList = string.Join("\n", taxonomy.Select(t => $"- {t.Name}: {t.Description}"));

        var prompt = $@"Enrich this university module for an AI study advisor. Students ask the
advisor things like ""which modules fit my interest in X?"" or ""help me plan my semester"".

### Module
Code: {module.Code}
Title: {module.Title}
ECTS: {module.Ects}, Level: {module.Level}, Offered in: {string.Join("/", module.OfferedIn)}
Description: {module.Description}

### Closed tag vocabulary (assign ONLY from this list)
{tagList}

### Output
A single JSON object:
{{
  ""summary"": ""2-3 sentences describing what the module teaches and what students can do afterwards"",
  ""audience"": ""1-2 sentences on who should take it (interests, goals, prior strengths)"",
  ""tags"": [""3 to 8 tags copied verbatim from the vocabulary above""],
  ""typicalQuestions"": [""3 to 5 questions a student might ask for which this module is a good answer""]
}}

Output JSON:";

        var system = "You are an expert study advisor compiling structured module metadata. Output only valid JSON.";
        var response = await _llmService.GenerateAsync(prompt, cancellationToken, system);

        var enrichment = ExtractJson<ModuleEnrichment>(response);
        if (enrichment == null)
        {
            _logger.LogWarning("Enrichment parse failed for {Code}; falling back to raw description", module.Code);
        }

        var validTagNames = new HashSet<string>(taxonomy.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var assignedTags = (enrichment?.Tags ?? new List<string>())
            .Where(t => validTagNames.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CompiledModule
        {
            Code = module.Code,
            Title = module.Title,
            Description = module.Description,
            Ects = module.Ects,
            Level = module.Level,
            OfferedIn = module.OfferedIn,
            Languages = module.Languages,
            Prerequisites = module.Prerequisites,
            Recommended = module.Recommended,
            Assessment = module.Assessment,
            Url = module.Url,
            Summary = enrichment?.Summary ?? Truncate(module.Description, 300),
            Audience = enrichment?.Audience ?? string.Empty,
            Tags = assignedTags,
            TypicalQuestions = enrichment?.TypicalQuestions ?? new List<string>()
        };
    }

    private class ModuleEnrichment
    {
        public string? Summary { get; set; }
        public string? Audience { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? TypicalQuestions { get; set; }
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
