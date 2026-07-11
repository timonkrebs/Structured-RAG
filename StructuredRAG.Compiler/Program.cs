using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Models.Catalog;
using StructuredRAG.Core.Services;
using System.Text.Json;

// Offline knowledge compilation. Run this daily/weekly (cron, GitHub Actions, ...):
//
//   dotnet run --project StructuredRAG.Compiler -- \
//     --Compiler:SourcePath=data/modules.sample.json \
//     --Compiler:OutputPath=compiled
//
// It reads raw module data, uses an LLM to compile a closed taxonomy and enriched
// module records, and writes static JSON artifacts consumed by StructuredRAG.Mcp.
// All configuration can be overridden via environment variables or command line.

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true));
services.AddSingleton<IConfiguration>(configuration);
services.AddHttpClient<DockerModelRunnerService>(client =>
{
    var timeoutSeconds = configuration.GetValue("DockerModelRunner:TimeoutSeconds", 300);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
services.AddSingleton<KnowledgeCompilationService>();

await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILogger<Program>>();

var sourcePath = configuration["Compiler:SourcePath"]
    ?? throw new InvalidOperationException("Compiler:SourcePath is not configured");
var outputPath = configuration["Compiler:OutputPath"] ?? "compiled";
var sourceName = configuration["Compiler:SourceName"] ?? Path.GetFileName(sourcePath);

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

logger.LogInformation("Reading source modules from {Path}", sourcePath);
var modules = JsonSerializer.Deserialize<List<SourceModule>>(
        await File.ReadAllTextAsync(sourcePath), jsonOptions)
    ?? throw new InvalidOperationException($"No modules found in {sourcePath}");

var duplicateCodes = modules.GroupBy(m => m.Code, StringComparer.OrdinalIgnoreCase)
    .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
if (duplicateCodes.Count > 0)
{
    throw new InvalidOperationException($"Duplicate module codes in source: {string.Join(", ", duplicateCodes)}");
}

var compiler = provider.GetRequiredService<KnowledgeCompilationService>();
var catalog = await compiler.CompileAsync(modules, sourceName);

Directory.CreateDirectory(outputPath);
await File.WriteAllTextAsync(Path.Combine(outputPath, "taxonomy.json"),
    JsonSerializer.Serialize(catalog.Taxonomy, jsonOptions));
await File.WriteAllTextAsync(Path.Combine(outputPath, "modules.json"),
    JsonSerializer.Serialize(catalog.Modules, jsonOptions));
// Manifest last: its mtime signals consumers that a complete new version is present.
await File.WriteAllTextAsync(Path.Combine(outputPath, "manifest.json"),
    JsonSerializer.Serialize(catalog.Manifest, jsonOptions));

logger.LogInformation(
    "Compiled {Modules} modules with {Tags} tags into {Output}",
    catalog.Manifest.ModuleCount, catalog.Manifest.TagCount, Path.GetFullPath(outputPath));
