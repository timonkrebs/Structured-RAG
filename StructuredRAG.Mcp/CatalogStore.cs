using StructuredRAG.Core.Models.Catalog;
using System.Text;
using System.Text.Json;

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
    private DateTime _loadedManifestWriteTime = DateTime.MinValue;

    public CatalogStore(IConfiguration configuration, ILogger<CatalogStore> logger)
    {
        _compiledPath = configuration["Catalog:CompiledPath"] ?? "../compiled-sample";
        _logger = logger;
        Reload();
    }

    public CatalogManifest Manifest { get { EnsureFresh(); return _manifest; } }
    public IReadOnlyList<TagDefinition> Taxonomy { get { EnsureFresh(); return _taxonomy; } }
    public IReadOnlyList<CompiledModule> Modules { get { EnsureFresh(); return _modules; } }

    public CompiledModule? GetModule(string code)
    {
        EnsureFresh();
        return _byCode.GetValueOrDefault(code.Trim());
    }

    /// <summary>
    /// Lexical relevance search over compiled fields. Deliberately simple: semantic
    /// interpretation of the query is the client model's job (it has the taxonomy);
    /// this only has to reward literal overlap with the compiled text.
    /// </summary>
    public IReadOnlyList<(CompiledModule Module, int Score)> Search(string query, int limit = 10)
    {
        EnsureFresh();
        var terms = Tokenize(query);
        if (terms.Count == 0) return Array.Empty<(CompiledModule, int)>();

        return _modules
            .Select(m => (Module: m, Score: ScoreModule(m, terms)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Module.Code)
            .Take(limit)
            .ToList();
    }

    private static int ScoreModule(CompiledModule m, IReadOnlyCollection<string> terms)
    {
        var title = $"{m.Title} {m.TitleEn}".ToLowerInvariant();
        var tags = string.Join(' ', m.Tags).ToLowerInvariant();
        var body = ($"{m.Summary} {m.SummaryEn} {m.Audience} {m.AudienceEn} " +
                    $"{string.Join(' ', m.TypicalQuestions)} {string.Join(' ', m.TypicalQuestionsEn)} " +
                    $"{m.PrerequisiteNotes} {m.Description} {m.DescriptionEn}")
            .ToLowerInvariant();

        var score = 0;
        foreach (var term in terms)
        {
            if (m.Code.Equals(term, StringComparison.OrdinalIgnoreCase)) score += 10;
            if (title.Contains(term)) score += 5;
            if (tags.Contains(term)) score += 3;
            if (body.Contains(term)) score += 1;
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

    private static List<string> Tokenize(string query) =>
        query.ToLowerInvariant()
            .Split(new[] { ' ', ',', ';', '?', '!', '.', '/', '(', ')', '"', '\'' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 2)
            .Distinct()
            .ToList();

    /// <summary>Compact catalog overview, suitable to load fully into a client's context.</summary>
    public string IndexMarkdown()
    {
        EnsureFresh();
        var sb = new StringBuilder();
        sb.AppendLine($"# Module catalog index ({_manifest.ModuleCount} modules, compiled {_manifest.CompiledAt:yyyy-MM-dd})");
        sb.AppendLine();
        sb.AppendLine("| Code | Title | ECTS | Level | Type | Offered | Lang | Tags |");
        sb.AppendLine("|------|-------|------|-------|------|---------|------|------|");
        foreach (var m in _modules.OrderBy(m => m.Title, StringComparer.OrdinalIgnoreCase))
        {
            var title = string.IsNullOrWhiteSpace(m.TitleEn) || m.TitleEn == m.Title
                ? m.Title
                : $"{m.Title} / {m.TitleEn}";
            var offered = m.Offerings.Count > 0
                ? string.Join("/", m.Offerings.Select(o => o.SemesterId))
                : string.Join("/", m.OfferedIn);
            sb.AppendLine($"| {m.Code} | {title} | {m.Ects} | {m.Level} | {m.ModuleType} | {offered} | {string.Join(",", m.Languages)} | {string.Join(", ", m.Tags)} |");
        }
        return sb.ToString();
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

            _manifest = Read<CatalogManifest>(manifestFile) ?? new CatalogManifest();
            _taxonomy = Read<List<TagDefinition>>(Path.Combine(_compiledPath, "taxonomy.json")) ?? new();
            _modules = Read<List<CompiledModule>>(Path.Combine(_compiledPath, "modules.json")) ?? new();
            _byCode = _modules.ToDictionary(m => m.Code, StringComparer.OrdinalIgnoreCase);
            _loadedManifestWriteTime = writeTime;

            _logger.LogInformation(
                "Loaded compiled catalog: {Modules} modules, {Tags} tags (compiled {At:u})",
                _modules.Count, _taxonomy.Count, _manifest.CompiledAt);
        }
    }

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions) : null;
}
