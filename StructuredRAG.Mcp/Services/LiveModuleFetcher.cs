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
        // Offerings are ordered newest-first; the newest can 404 (expired/withdrawn)
        // while an older one is still published — try each id before giving up on live.
        foreach (var offering in m.Offerings)
        {
            ModuleDetailDto? detail;
            try
            {
                detail = await GetLiveDetailAsync(offering.PlanSemesterModulId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client aborted the request — propagate instead of serving a fallback.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live fetch for {PlanId} failed; falling back to compiled record",
                    offering.PlanSemesterModulId);
                break; // transport trouble — further ids would just retry against the same host
            }

            if (detail != null)
            {
                return BuildResult(m, SourceModuleMapper.Map(detail), source: "live");
            }
            // null means 404 for this offering — try the next one.
        }

        return BuildResult(m, live: null, source: "compiled");
    }

    private async Task<ModuleDetailDto?> GetLiveDetailAsync(string planId, CancellationToken ct)
    {
        if (_cache.TryGetValue(planId, out var cached))
        {
            if (DateTime.UtcNow - cached.FetchedAt < _ttl)
                return cached.Detail;
            _cache.TryRemove(planId, out _);
        }

        var detail = await _client.GetModuleDetailAsync(planId, ct);
        if (detail != null)
        {
            // Keys come from compiled offerings, so the cache is bounded by catalog size —
            // but offerings change across recompiles over a long-lived process, so drop
            // expired entries before adding. O(n) is fine next to the network fetch above.
            PruneExpired();
            _cache[planId] = (DateTime.UtcNow, detail);
        }
        return detail;
    }

    private void PruneExpired()
    {
        var cutoff = DateTime.UtcNow - _ttl;
        foreach (var entry in _cache)
        {
            if (entry.Value.FetchedAt < cutoff) _cache.TryRemove(entry.Key, out _);
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
        // The live record covers one semester (one offering); compiled offerings are
        // newest-first, and the newest one with published slots represents the schedule.
        var lessons = live?.Offerings.FirstOrDefault()?.Lessons is { Count: > 0 } liveLessons
            ? liveLessons
            : compiled.Offerings.FirstOrDefault(o => o.Lessons.Count > 0)?.Lessons ?? new List<Lesson>();

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
        if (lessons.Count > 0)
            sb.AppendLine($"**Lessons (one per parallel class):** {string.Join("; ", lessons.Select(FormatLesson))}");
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
                ["lessons"] = lessons,
                ["tags"] = compiled.Tags,
                ["prerequisites"] = compiled.Prerequisites,
                ["studyPrograms"] = live is { StudyPrograms.Count: > 0 } ? live.StudyPrograms : compiled.StudyPrograms
            });
    }

    private static string FormatLesson(Lesson l)
    {
        var time = string.IsNullOrEmpty(l.Start) ? "" : $" {l.Start}–{l.End}";
        var location = string.IsNullOrEmpty(l.Location) ? "" : $" ({l.Location})";
        return $"{l.Day ?? "day n/a"}{time}{location}";
    }
}
