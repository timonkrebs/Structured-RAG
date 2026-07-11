using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Fhnw;
using StructuredRAG.Mcp.Tools;
using System.Collections.Concurrent;
using System.Text;

namespace StructuredRAG.Mcp.Services;

/// <summary>
/// Builds fetch results with the compiled catalog as index and the official FHNW API
/// as source of truth: current description fetched live (plain HTTP, no inference),
/// compiled enrichments (summary, tags, prerequisites) attached. A short TTL cache
/// keeps latency low and load on the university service polite; when the live API is
/// unreachable or the module has no offering id, the compiled record is served instead.
/// </summary>
public class LiveModuleFetcher
{
    private readonly BariApiClient _client;
    private readonly ILogger<LiveModuleFetcher> _logger;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, (DateTime FetchedAt, ModuleDetailDto Detail)> _cache = new();

    public LiveModuleFetcher(BariApiClient client, IConfiguration configuration, ILogger<LiveModuleFetcher> logger)
    {
        _client = client;
        _logger = logger;
        _ttl = TimeSpan.FromMinutes(configuration.GetValue("BariApi:FetchCacheTtlMinutes", 60));
    }

    public async Task<FetchResult> FetchAsync(CompiledModule m, CancellationToken ct = default)
    {
        var planId = m.Offerings.FirstOrDefault()?.PlanSemesterModulId;
        if (planId != null)
        {
            var detail = await GetLiveDetailAsync(planId, ct);
            if (detail != null)
            {
                return BuildResult(m, SourceModuleMapper.Map(detail), source: "live");
            }
        }

        return BuildResult(m, live: null, source: "compiled");
    }

    private async Task<ModuleDetailDto?> GetLiveDetailAsync(string planId, CancellationToken ct)
    {
        if (_cache.TryGetValue(planId, out var cached) && DateTime.UtcNow - cached.FetchedAt < _ttl)
        {
            return cached.Detail;
        }

        try
        {
            var detail = await _client.GetModuleDetailAsync(planId, ct);
            if (detail != null)
            {
                _cache[planId] = (DateTime.UtcNow, detail);
            }
            return detail;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client aborted the request — propagate instead of serving a fallback.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live fetch for {PlanId} failed; falling back to compiled record", planId);
            return null;
        }
    }

    private static FetchResult BuildResult(CompiledModule compiled, SourceModule? live, string source)
    {
        // Everything the live record carries wins over the compiled snapshot — official
        // changes (ECTS, level, schedule, ...) must show up even before the next compile.
        // Only the LLM enrichments (summary, audience, tags) and the offering list
        // (the live record covers a single semester) come from the compiled catalog.
        var title = live?.Title ?? compiled.Title;
        var description = live?.Description ?? compiled.Description;
        var requirements = live?.RequirementsText ?? compiled.PrerequisiteNotes;
        var assessment = live?.Assessment ?? compiled.Assessment;
        var url = live?.Url ?? compiled.Url;
        var ects = live?.Ects ?? compiled.Ects;
        var level = live?.Level ?? compiled.Level;
        var moduleType = live?.ModuleType ?? compiled.ModuleType;
        var languages = live is { Languages.Count: > 0 } ? live.Languages : compiled.Languages;
        var weekdays = live is { Weekdays.Count: > 0 } ? live.Weekdays : compiled.Weekdays;

        var sb = new StringBuilder();
        sb.AppendLine($"# {title} ({compiled.Code})");
        sb.AppendLine();
        sb.AppendLine(compiled.Summary);
        if (!string.IsNullOrWhiteSpace(compiled.SummaryEn)) sb.AppendLine($"\n*EN:* {compiled.SummaryEn}");
        sb.AppendLine();
        sb.AppendLine($"**Who should take it:** {compiled.Audience}");
        sb.AppendLine();
        sb.AppendLine($"**Details:** {ects} ECTS · {level} · {moduleType} · " +
                      $"offered in {(compiled.Offerings.Count > 0 ? string.Join("/", compiled.Offerings.Select(o => o.SemesterId)) : string.Join("/", compiled.OfferedIn))} · " +
                      $"languages: {string.Join(", ", languages)}" +
                      (weekdays.Count > 0 ? $" · weekdays: {string.Join(", ", weekdays)}" : ""));
        sb.AppendLine($"**Assessment:** {assessment}");
        sb.AppendLine($"**Tags:** {string.Join(", ", compiled.Tags)}");
        sb.AppendLine($"**Prerequisites (module codes):** {(compiled.Prerequisites.Count > 0 ? string.Join(", ", compiled.Prerequisites) : "none")}");
        if (!string.IsNullOrWhiteSpace(requirements))
            sb.AppendLine($"**Requirements (official text):** {requirements}");
        sb.AppendLine();
        sb.AppendLine($"## Official catalog description ({(source == "live" ? "current" : "as of last compile")})");
        sb.AppendLine(description);
        if (compiled.TypicalQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Typical student questions this module answers");
            foreach (var q in compiled.TypicalQuestions) sb.AppendLine($"- {q}");
        }

        return new FetchResult(
            Id: compiled.Code,
            Title: title,
            Text: sb.ToString(),
            Url: url,
            Metadata: new Dictionary<string, object?>
            {
                ["source"] = source,
                ["ects"] = ects,
                ["level"] = level,
                ["moduleType"] = moduleType,
                ["offerings"] = compiled.Offerings.Select(o => o.SemesterId).ToList(),
                ["tags"] = compiled.Tags,
                ["prerequisites"] = compiled.Prerequisites,
                ["studyPrograms"] = live is { StudyPrograms.Count: > 0 } ? live.StudyPrograms : compiled.StudyPrograms
            });
    }
}
