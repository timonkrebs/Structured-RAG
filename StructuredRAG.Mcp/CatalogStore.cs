using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Core.Services;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StructuredRAG.Mcp;

/// <summary>
/// In-memory view over the precompiled catalog artifacts. Pure data access —
/// no inference happens here; all reasoning is done by the connected MCP client.
/// Reloads automatically when the compiler writes a new manifest.json.
/// </summary>
public class CatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _compiledPath;
    private readonly ILogger<CatalogStore> _logger;
    private readonly object _reloadLock = new();

    private CatalogManifest _manifest = new();
    private List<TagDefinition> _taxonomy = new();
    private List<CompiledModule> _modules = new();
    private Dictionary<string, CompiledModule> _byCode = new();
    private Dictionary<string, List<string>> _variantClasses = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedManifestWriteTime = DateTime.MinValue;

    public CatalogStore(IConfiguration configuration, ILogger<CatalogStore> logger)
    {
        _compiledPath = ResolveCompiledPath(configuration["Catalog:CompiledPath"]);
        _logger = logger;
        Reload();
    }

    /// <summary>
    /// Relative paths resolve against the process working directory, which differs
    /// between `dotnet run --project StructuredRAG.Mcp` (repo root) and running from
    /// the project directory — so the default probes both locations for the sample.
    /// </summary>
    private static string ResolveCompiledPath(string? configured)
    {
        var candidates = configured != null
            ? new[] { configured }
            : new[] { "compiled-sample", Path.Combine("..", "compiled-sample") };
        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "manifest.json")))
               ?? candidates[0];
    }

    public CatalogManifest Manifest { get { EnsureFresh(); return _manifest; } }
    public IReadOnlyList<TagDefinition> Taxonomy { get { EnsureFresh(); return _taxonomy; } }
    public IReadOnlyList<CompiledModule> Modules { get { EnsureFresh(); return _modules; } }

    /// <summary>The given codes plus every equivalent language variant of each: a
    /// completed variant counts as the course itself, so eligibility checks must not
    /// treat the sibling edition as still open (nobody should be offered a retake of
    /// Statistics 1 in the other language).</summary>
    public HashSet<string> ExpandWithVariants(IEnumerable<string> codes)
    {
        EnsureFresh();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var c = code.Trim();
            if (c.Length == 0) continue;
            set.Add(c);
            if (_variantClasses.TryGetValue(c, out var cls)) set.UnionWith(cls);
        }
        return set;
    }

    public CompiledModule? GetModule(string code)
    {
        EnsureFresh();
        return _byCode.GetValueOrDefault(code.Trim());
    }

    /// <summary>
    /// Lexical relevance search over compiled fields. Deliberately simple: semantic
    /// interpretation of the query is the client model's job (it has the taxonomy);
    /// this only has to reward literal overlap with the compiled text.
    /// Pass <paramref name="within"/> to rank only a pre-filtered subset.
    /// </summary>
    public IReadOnlyList<(CompiledModule Module, int Score)> Search(
        string query, int limit = 10, IReadOnlyCollection<CompiledModule>? within = null)
    {
        EnsureFresh();
        var matchers = Tokenize(query).Select(TermMatcher.For).ToList();
        if (matchers.Count == 0) return Array.Empty<(CompiledModule, int)>();

        var pool = within ?? (IReadOnlyCollection<CompiledModule>)_modules;
        return pool
            .Select(m => (Module: m, Score: ScoreModule(m, matchers)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Module.Code)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// Matches one query term against lowercased text. Short terms (acronyms like
    /// "AI"/"KI"/"BI" or the language "R") match on word boundaries only — plain
    /// substring matching would hit them inside almost every word.
    /// </summary>
    private sealed class TermMatcher
    {
        private readonly string _term;
        private readonly Regex? _wordBoundary;

        private TermMatcher(string term, Regex? wordBoundary)
        {
            _term = term;
            _wordBoundary = wordBoundary;
        }

        public static TermMatcher For(string term) => new(
            term,
            term.Length <= 3
                ? new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                : null);

        public string Term => _term;

        public bool Matches(string lowercasedText) =>
            _wordBoundary?.IsMatch(lowercasedText) ?? lowercasedText.Contains(_term);
    }

    private static int ScoreModule(CompiledModule m, IReadOnlyCollection<TermMatcher> matchers)
    {
        var title = $"{m.Title} {m.TitleEn}".ToLowerInvariant();
        var tags = string.Join(' ', m.Tags).ToLowerInvariant();
        var body = ($"{m.Summary} {m.SummaryEn} {m.Audience} {m.AudienceEn} " +
                    $"{string.Join(' ', m.TypicalQuestions)} {string.Join(' ', m.TypicalQuestionsEn)} " +
                    $"{m.PrerequisiteNotes} {m.Description} {m.DescriptionEn}")
            .ToLowerInvariant();

        var score = 0;
        foreach (var matcher in matchers)
        {
            if (m.Code.Equals(matcher.Term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (matcher.Matches(title)) score += 5;
            if (matcher.Matches(tags)) score += 3;
            if (matcher.Matches(body)) score += 1;
        }
        return score;
    }

    /// <summary>Resolves a tag given by canonical (German) name or English alias to its canonical name.</summary>
    public string? ResolveTagName(string tag)
    {
        EnsureFresh();
        var match = _taxonomy.FirstOrDefault(t =>
            t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)
            || (t.NameEn?.Equals(tag, StringComparison.OrdinalIgnoreCase) ?? false));
        return match?.Name;
    }

    // One-character tokens are kept only when the user capitalized them ("R", "C" —
    // deliberate course/language terms); lowercase singles ("i", "a") are noise.
    private static List<string> Tokenize(string query) =>
        query
            .Split(new[] { ' ', ',', ';', '?', '!', '.', '/', '(', ')', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1 || (t.Length == 1 && char.IsUpper(t[0])))
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .ToList();

    /// <summary>Compact catalog overview, suitable to load fully into a client's context.</summary>
    public string IndexMarkdown()
    {
        EnsureFresh();
        var sb = new StringBuilder();
        sb.AppendLine($"# Module catalog index ({_manifest.ModuleCount} modules, compiled {_manifest.CompiledAt:yyyy-MM-dd})");
        sb.AppendLine();
        sb.AppendLine("| Code | Title | ECTS | Level | Type | Offered | Schedule | Lang | Tags |");
        sb.AppendLine("|------|-------|------|-------|------|---------|----------|------|------|");
        foreach (var m in _modules.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            var title = string.IsNullOrWhiteSpace(m.TitleEn) || m.TitleEn == m.Title
                ? m.Title
                : $"{m.Title} / {m.TitleEn}";
            var offered = m.Offerings.Count > 0
                ? string.Join("/", m.Offerings.Select(o => o.SemesterId))
                : string.Join("/", m.OfferedIn);
            sb.AppendLine($"| {m.Code} | {title} | {m.Ects} | {m.Level} | {m.ModuleType} | {offered} | {ScheduleText(m)} | {string.Join(",", m.Languages)} | {string.Join(", ", m.Tags)} |");
        }
        return sb.ToString();
    }

    /// <summary>Compact schedule for the index as "Ddd HH:MM-HH:MM" slots (deduped;
    /// "irregular" when no weekday is published), falling back to plain weekdays.
    /// Schedules can differ between a module's offerings (pmgt meets Fri in 26HS but
    /// Mon in 27FS), so when they do — or when only some offerings publish lessons —
    /// each slot list is prefixed with its semester id. This is what lets a client
    /// model assemble a clash-free proposal for a SPECIFIC semester directly from the
    /// overview, without per-module calls.</summary>
    private static string ScheduleText(CompiledModule m)
    {
        static string SlotLabel(Lesson l)
        {
            var day = string.IsNullOrEmpty(l.Day) ? "irregular" : l.Day[..Math.Min(3, l.Day.Length)];
            return string.IsNullOrEmpty(l.Start) ? day : $"{day} {l.Start}-{l.End}";
        }

        // Slots sharing a class number belong to ONE class (the student attends all of
        // them, joined with " + "); different numbers are parallel classes — the student
        // attends exactly one, so alternatives join with " or ". Flattening them into a
        // single list would make a parallel-class module look like it occupies every
        // slot at once, and a model would flag valid proposals as clashes. Parallel
        // alternatives carry their class number in [brackets]: that is the id a client
        // passes to plan_semester's proposedClasses to pin the exact class its
        // proposal reasoned about.
        static string SlotText(IReadOnlyList<Lesson> lessons)
        {
            var groups = new List<List<Lesson>>();
            var byNum = new Dictionary<string, List<Lesson>>();
            foreach (var l in lessons)
            {
                var key = string.IsNullOrEmpty(l.Number) ? $"~{groups.Count}" : l.Number;
                if (!byNum.TryGetValue(key, out var g)) { g = new List<Lesson>(); byNum[key] = g; groups.Add(g); }
                g.Add(l);
            }
            return string.Join(" or ", groups
                .Select(g =>
                {
                    var label = string.Join(" + ", g.Select(SlotLabel).Distinct());
                    return groups.Count > 1 && !string.IsNullOrEmpty(g[0].Number)
                        ? $"{label} [{g[0].Number}]"
                        : label;
                })
                .Distinct());
        }

        var parts = m.Offerings
            .Where(o => o.Lessons.Count > 0)
            .Select(o => (o.SemesterId, Text: SlotText(o.Lessons)))
            .ToList();
        if (parts.Count == 0) return string.Join(",", m.Weekdays);
        if (m.Offerings.Count == 1
            || (parts.Count == m.Offerings.Count && parts.Select(p => p.Text).Distinct().Count() == 1))
        {
            return parts[0].Text;
        }
        return string.Join("; ", parts.Select(p => $"{p.SemesterId}: {p.Text}"));
    }

    /// <summary>Compact catalog snapshot for the MCP initialize instructions: size,
    /// compile date and the tag vocabulary with per-tag module counts. Names only —
    /// clients get an immediate vocabulary to filter with, without a first round-trip;
    /// tag descriptions stay one call away in list_tags / catalog://taxonomy.</summary>
    public string InstructionsSnapshot()
    {
        EnsureFresh();
        if (_modules.Count == 0)
            return "No compiled catalog is loaded on this server yet.";

        var vocabulary = string.Join(", ", _taxonomy
            .OrderByDescending(t => t.ModuleCount)
            .Select(t =>
            {
                var alias = string.IsNullOrWhiteSpace(t.NameEn) || t.NameEn == t.Name ? "" : $" / {t.NameEn}";
                return $"{t.Name}{alias} ({t.ModuleCount})";
            }));
        return $"Catalog snapshot: {_modules.Count} modules — {_manifest.Source}, compiled {_manifest.CompiledAt:yyyy-MM-dd}. " +
               $"Tag vocabulary as \"German canonical / English alias (module count)\": {vocabulary}.";
    }

    public string TaxonomyMarkdown()
    {
        EnsureFresh();
        var sb = new StringBuilder();
        sb.AppendLine("# Tag taxonomy");
        sb.AppendLine();
        sb.AppendLine("Use these tags (German canonical name or English alias) to filter modules with the search_modules tool.");
        sb.AppendLine();
        foreach (var t in _taxonomy.OrderByDescending(t => t.ModuleCount))
        {
            var alias = string.IsNullOrWhiteSpace(t.NameEn) || t.NameEn == t.Name ? "" : $" / {t.NameEn}";
            var descriptionEn = string.IsNullOrWhiteSpace(t.DescriptionEn) ? "" : $" — {t.DescriptionEn}";
            sb.AppendLine($"- **{t.Name}**{alias} ({t.ModuleCount} modules): {t.Description}{descriptionEn}");
        }
        return sb.ToString();
    }

    private void EnsureFresh()
    {
        var manifestFile = Path.Combine(_compiledPath, "manifest.json");
        if (File.Exists(manifestFile) && File.GetLastWriteTimeUtc(manifestFile) > _loadedManifestWriteTime)
        {
            Reload();
        }
    }

    private void Reload()
    {
        lock (_reloadLock)
        {
            var manifestFile = Path.Combine(_compiledPath, "manifest.json");
            if (!File.Exists(manifestFile))
            {
                _logger.LogWarning(
                    "No compiled catalog found at {Path}. Run StructuredRAG.Compiler first or point Catalog:CompiledPath at the artifacts.",
                    Path.GetFullPath(_compiledPath));
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(manifestFile);
            if (writeTime <= _loadedManifestWriteTime) return; // another request already reloaded

            try
            {
                // Load into locals first: if any file is truncated or mid-write, keep the
                // previous snapshot and retry on a later request (mtime not advanced).
                var manifest = Read<CatalogManifest>(manifestFile) ?? new CatalogManifest();
                var taxonomy = Read<List<TagDefinition>>(Path.Combine(_compiledPath, "taxonomy.json")) ?? new();
                var modules = Read<List<CompiledModule>>(Path.Combine(_compiledPath, "modules.json")) ?? new();

                // Catalogs compiled before prerequisiteGroups existed carry only the flat
                // AND-list — derive the OR-groups here so evaluation is uniformly
                // group-based. No-op for modules that already have groups.
                PrerequisiteGrouping.EnsureGroups(modules);

                _manifest = manifest;
                _taxonomy = taxonomy;
                _modules = modules;
                _byCode = modules.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
                _variantClasses = PrerequisiteGrouping.BuildEquivalenceClasses(modules);
                _loadedManifestWriteTime = writeTime;
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                _logger.LogWarning(ex,
                    "Compiled catalog at {Path} could not be (re)loaded — keeping the previous snapshot and retrying later",
                    Path.GetFullPath(_compiledPath));
                return;
            }

            _logger.LogInformation(
                "Loaded compiled catalog: {Modules} modules, {Tags} tags (compiled {At:u})",
                _modules.Count, _taxonomy.Count, _manifest.CompiledAt);
        }
    }

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
}
