using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StructuredRAG.Compiler.Commands;
using StructuredRAG.Core.Services;
using StructuredRAG.Fhnw;
using System.Text.Json;

// Offline knowledge pipeline. Run daily/weekly (cron, GitHub Actions, ...):
//
//   dotnet run --project StructuredRAG.Compiler -- ingest    # FHNW API -> data/modules.*.json
//   dotnet run --project StructuredRAG.Compiler -- compile   # source JSON -> compiled artifacts
//   dotnet run --project StructuredRAG.Compiler -- all       # both
//
// All settings can be overridden via appsettings.json, environment variables or
// --Section:Key=value arguments (e.g. --Compiler:SourcePath=data/modules.ingested.json).

var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? "compile";
var configArgs = args.Where(a => a.StartsWith('-')).ToArray();

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(configArgs)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddSimpleConsole(o => o.SingleLine = true));
services.AddSingleton<IConfiguration>(configuration);
services.AddHttpClient<DockerModelRunnerService>(client =>
{
    var timeoutSeconds = configuration.GetValue("DockerModelRunner:TimeoutSeconds", 300);
    client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
});
services.AddHttpClient<BariApiClient>(client => client.Timeout = TimeSpan.FromSeconds(60));
services.AddSingleton<KnowledgeCompilationService>();
services.AddSingleton<IngestCommand>();
services.AddSingleton<CompileCommand>();

await using var provider = services.BuildServiceProvider();
var logger = provider.GetRequiredService<ILogger<Program>>();

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

try
{
    switch (command)
    {
        case "ingest":
            return await provider.GetRequiredService<IngestCommand>().RunAsync(jsonOptions);

        case "compile":
            return await provider.GetRequiredService<CompileCommand>().RunAsync(jsonOptions);

        case "all":
            var ingestResult = await provider.GetRequiredService<IngestCommand>().RunAsync(jsonOptions);
            if (ingestResult != 0) return ingestResult;
            return await provider.GetRequiredService<CompileCommand>().RunAsync(jsonOptions);

        default:
            logger.LogError("Unknown command '{Command}'. Use: ingest | compile | all", command);
            return 1;
    }
}
catch (Exception ex)
{
    logger.LogError(ex, "Command '{Command}' failed", command);
    return 1;
}
