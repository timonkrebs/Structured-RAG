using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Fhnw;
using System.Text.Json;

namespace StructuredRAG.Compiler.Commands;

/// <summary>
/// Ingests module data from the FHNW Modulbeschreibungen API into a SourceModule JSON
/// file. Enumerates per (semester × study program) facet slice — the search endpoint
/// caps results at 1000 — and keeps a raw-response disk cache so repeated runs only
/// fetch what is new.
/// </summary>
public class IngestCommand
{
    private readonly BariApiClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<IngestCommand> _logger;

    public IngestCommand(BariApiClient client, IConfiguration configuration, ILogger<IngestCommand> logger)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> RunAsync(JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        var semesters = SplitList(_configuration["Ingest:Semesters"] ?? "26HS;27FS");
        var programs = SplitList(_configuration["Ingest:StudyPrograms"] ?? "BSc in Wirtschaftsinformatik");
        var rawDir = _configuration["Ingest:RawCachePath"] ?? "data/raw";
        var outputPath = _configuration["Ingest:OutputPath"] ?? "data/modules.ingested.json";
        var refreshRaw = _configuration.GetValue("Ingest:RefreshRaw", false);

        var latest = await _client.GetLatestPlanSemesterAsync(ct);
        _logger.LogInformation("Latest plan semester according to API: {Semester}", latest?.Value ?? "unknown");

        // Facet filters must echo the complete facet items (display values included),
        // so resolve the configured names against the live facet list first.
        var facets = await _client.GetFacetsAsync(ct);
        var semesterItems = ResolveFacetValues(facets, "translated_SemesterId", semesters);
        var programItems = ResolveFacetValues(facets, "translated_StudyPrograms", programs);

        var planIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // id -> title
        foreach (var semester in semesterItems)
        {
            foreach (var program in programItems)
            {
                var count = await EnumerateSliceAsync(semester, program, planIds, ct);
                _logger.LogInformation("Slice {Semester} × {Program}: {Count} modules",
                    semester.Value, program.Value, count);
            }
        }

        _logger.LogInformation("Enumerated {Count} distinct module offerings; fetching details...", planIds.Count);

        Directory.CreateDirectory(rawDir);
        var mapped = new List<SourceModule>();
        var fetched = 0; var fromCache = 0; var missing = 0;

        foreach (var planId in planIds.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var rawPath = Path.Combine(rawDir, planId + ".json");
            string? raw;
            if (!refreshRaw && File.Exists(rawPath))
            {
                raw = await File.ReadAllTextAsync(rawPath, ct);
                fromCache++;
            }
            else
            {
                raw = await _client.GetModuleDetailRawAsync(planId, ct);
                if (raw != null)
                {
                    await File.WriteAllTextAsync(rawPath, raw, ct);
                    fetched++;
                }
            }

            if (raw == null)
            {
                _logger.LogWarning("Module {PlanId} not found (removed from catalog?)", planId);
                missing++;
                continue;
            }

            var detail = BariApiClient.ParseModuleDetail(raw);
            if (detail == null || string.IsNullOrEmpty(detail.PlanSemesterModulId))
            {
                _logger.LogWarning("Could not parse module detail for {PlanId}", planId);
                continue;
            }

            mapped.Add(SourceModuleMapper.Map(detail));
        }

        // One module can be offered in several semesters — merge offerings by stable identity.
        var merged = mapped
            .GroupBy(m => m.ModuleId ?? m.Code, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Aggregate(SourceModuleMapper.Merge))
            .OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(merged, jsonOptions), ct);

        var emptyDescriptions = merged.Count(m => string.IsNullOrWhiteSpace(m.Description));
        _logger.LogInformation(
            "Ingest complete: {Modules} modules ({Offerings} offerings) written to {Output}. " +
            "Details: {Fetched} fetched, {Cached} from raw cache, {Missing} missing. Empty descriptions: {Empty}",
            merged.Count, merged.Sum(m => m.Offerings.Count), Path.GetFullPath(outputPath),
            fetched, fromCache, missing, emptyDescriptions);

        return 0;
    }

    private async Task<int> EnumerateSliceAsync(
        FacetValueDto semester, FacetValueDto program,
        Dictionary<string, string> planIds, CancellationToken ct)
    {
        const int pageSize = 100;
        var query = new SearchQueryDto
        {
            FacetQuery = new List<FacetQueryItemDto>
            {
                new() { Name = "translated_SemesterId", Values = new List<FacetValueDto> { semester } },
                new() { Name = "translated_StudyPrograms", Values = new List<FacetValueDto> { program } }
            }
        };

        var sliceCount = 0;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await _client.SearchAsync(query, skip, pageSize, ct);
            if (skip == 0 && page.ResultsCount >= 1000)
            {
                _logger.LogWarning(
                    "Slice {Semester} × {Program} reports {Count} results — the API caps at 1000; " +
                    "results may be truncated. Narrow the slice (e.g. filter additionally by module type).",
                    semester.Value, program.Value, page.ResultsCount);
            }

            if (page.CurrentPageSearchResults.Count == 0) break;

            foreach (var item in page.CurrentPageSearchResults)
            {
                if (string.IsNullOrEmpty(item.PlanSemesterModulId)) continue;
                planIds.TryAdd(item.PlanSemesterModulId, item.Title ?? string.Empty);
                sliceCount++;
            }

            if (skip + pageSize >= page.ResultsCount) break;
        }

        return sliceCount;
    }

    private List<FacetValueDto> ResolveFacetValues(
        List<FacetResultDto> facets, string facetName, List<string> requestedValues)
    {
        var facet = facets.FirstOrDefault(f => f.Name.Equals(facetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Facet '{facetName}' not found in API response");

        var resolved = new List<FacetValueDto>();
        foreach (var requested in requestedValues)
        {
            var match = facet.Values.FirstOrDefault(v =>
                v.Value.Equals(requested, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var available = string.Join(" | ", facet.Values.Select(v => v.Value).OrderBy(v => v).Take(50));
                throw new InvalidOperationException(
                    $"Value '{requested}' not found in facet '{facetName}'. Available values include: {available}");
            }
            resolved.Add(match);
        }
        return resolved;
    }

    // Semicolon-separated (program names may legitimately contain commas).
    private static List<string> SplitList(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
