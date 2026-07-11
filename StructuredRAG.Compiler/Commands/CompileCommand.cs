using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Core.Services;
using System.Text.Json;

namespace StructuredRAG.Compiler.Commands;

/// <summary>
/// Runs the LLM knowledge compilation: source modules → taxonomy + enriched modules.
/// Loads the previous artifacts (if any) from the output directory so the taxonomy
/// stays stable across runs and unchanged modules are not re-enriched.
/// </summary>
public class CompileCommand
{
    private readonly KnowledgeCompilationService _compiler;
    private readonly IConfiguration _configuration;
    private readonly ILogger<CompileCommand> _logger;

    public CompileCommand(
        KnowledgeCompilationService compiler,
        IConfiguration configuration,
        ILogger<CompileCommand> logger)
    {
        _compiler = compiler;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> RunAsync(JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        var sourcePath = _configuration["Compiler:SourcePath"]
            ?? throw new InvalidOperationException("Compiler:SourcePath is not configured");
        var outputPath = _configuration["Compiler:OutputPath"] ?? "compiled";
        var sourceName = _configuration["Compiler:SourceName"] ?? Path.GetFileName(sourcePath);
        var force = _configuration.GetValue("Compiler:Force", false);

        _logger.LogInformation("Reading source modules from {Path}", sourcePath);
        var modules = JsonSerializer.Deserialize<List<SourceModule>>(
                await File.ReadAllTextAsync(sourcePath, ct), jsonOptions)
            ?? throw new InvalidOperationException($"No modules found in {sourcePath}");

        var duplicateCodes = modules.GroupBy(m => m.Code, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicateCodes.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate module codes in source: {string.Join(", ", duplicateCodes)}");
        }

        var previous = LoadPreviousCatalog(outputPath, jsonOptions);
        if (previous != null)
        {
            _logger.LogInformation(
                "Previous catalog found ({Modules} modules, {Tags} tags, compiled {At:u}) — " +
                "taxonomy will be evolved and unchanged modules reused{Force}",
                previous.Modules.Count, previous.Taxonomy.Count, previous.Manifest.CompiledAt,
                force ? " (disabled by Compiler:Force)" : "");
        }

        var catalog = await _compiler.CompileAsync(modules, sourceName, force ? null : previous, ct);

        Directory.CreateDirectory(outputPath);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "taxonomy.json"),
            JsonSerializer.Serialize(catalog.Taxonomy, jsonOptions), ct);
        await File.WriteAllTextAsync(Path.Combine(outputPath, "modules.json"),
            JsonSerializer.Serialize(catalog.Modules, jsonOptions), ct);
        // Manifest last: its mtime signals consumers that a complete new version is present.
        await File.WriteAllTextAsync(Path.Combine(outputPath, "manifest.json"),
            JsonSerializer.Serialize(catalog.Manifest, jsonOptions), ct);

        _logger.LogInformation(
            "Compiled {Modules} modules with {Tags} tags into {Output}",
            catalog.Manifest.ModuleCount, catalog.Manifest.TagCount, Path.GetFullPath(outputPath));

        return 0;
    }

    private CompiledCatalog? LoadPreviousCatalog(string outputPath, JsonSerializerOptions jsonOptions)
    {
        try
        {
            var manifestPath = Path.Combine(outputPath, "manifest.json");
            if (!File.Exists(manifestPath)) return null;

            return new CompiledCatalog
            {
                Manifest = Read<CatalogManifest>(manifestPath) ?? new CatalogManifest(),
                Taxonomy = Read<List<TagDefinition>>(Path.Combine(outputPath, "taxonomy.json")) ?? new(),
                Modules = Read<List<CompiledModule>>(Path.Combine(outputPath, "modules.json")) ?? new()
            };

            T? Read<T>(string path) where T : class =>
                File.Exists(path)
                    ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), jsonOptions)
                    : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load previous catalog from {Path}; compiling from scratch", outputPath);
            return null;
        }
    }
}
