using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Core.Services;
using System.Text.Json;

namespace StructuredRAG.Compiler.Commands;

/// <summary>
/// Re-derives the prerequisite OR-groups over an already-compiled catalog — no LLM calls.
/// Migrates catalogs compiled before prerequisiteGroups existed and repairs groups after
/// hand edits, without re-enriching any module.
/// </summary>
public class RegroupCommand
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<RegroupCommand> _logger;

    public RegroupCommand(IConfiguration configuration, ILogger<RegroupCommand> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<int> RunAsync(JsonSerializerOptions jsonOptions, CancellationToken ct = default)
    {
        var outputPath = _configuration["Compiler:OutputPath"] ?? "compiled";
        var modulesPath = Path.Combine(outputPath, "modules.json");
        if (!File.Exists(modulesPath))
        {
            _logger.LogError("No compiled catalog at {Path} — run compile first.", Path.GetFullPath(modulesPath));
            return 1;
        }

        var modules = JsonSerializer.Deserialize<List<CompiledModule>>(
                await File.ReadAllTextAsync(modulesPath, ct), jsonOptions)
            ?? throw new InvalidOperationException($"No modules found in {modulesPath}");

        PrerequisiteGrouping.EnsureGroups(modules, force: true);

        await CompileCommand.WriteAtomicAsync(modulesPath, JsonSerializer.Serialize(modules, jsonOptions), ct);

        // Rewrite the manifest unchanged: its mtime is the reload signal for watching servers.
        var manifestPath = Path.Combine(outputPath, "manifest.json");
        if (File.Exists(manifestPath))
            await CompileCommand.WriteAtomicAsync(manifestPath, await File.ReadAllTextAsync(manifestPath, ct), ct);

        var grouped = modules.Where(m => m.PrerequisiteGroups.Any(g => g.Count > 1)).ToList();
        foreach (var module in grouped)
        {
            _logger.LogInformation("Module {Code}: prerequisite alternatives grouped: {Groups}",
                module.Code, string.Join("; ", module.PrerequisiteGroups
                    .Where(g => g.Count > 1).Select(g => string.Join(" | ", g))));
        }
        _logger.LogInformation(
            "Regrouped prerequisites of {Total} modules in {Output}; {Grouped} have interchangeable variants",
            modules.Count, Path.GetFullPath(outputPath), grouped.Count);

        return 0;
    }
}
