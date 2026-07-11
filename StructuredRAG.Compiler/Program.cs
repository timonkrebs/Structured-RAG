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

// Only the first token can be the subcommand; everything after it goes to the
// configuration provider intact — AddCommandLine supports both "--Key=value"
// and the space-separated "--Key value" form, so values must not be filtered out.
string command;
string[] configArgs;
if (args.Length > 0 && !args[0].StartsWith('-'))
{
    command = args[0].ToLowerInvariant();
    configArgs = args.Skip(1).ToArray();
}
else
{
    command = "compile";
    configArgs = args;
}

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
            await provider.GetRequiredService<IngestCommand>().RunAsync(jsonOptions);
            return 0;

        case "compile":
            return await provider.GetRequiredService<CompileCommand>().RunAsync(jsonOptions);

        case "all":
            // Compile exactly what this run ingested — otherwise a non-default
            // Ingest:OutputPath would silently compile a stale Compiler:SourcePath.
            var ingestedPath = await provider.GetRequiredService<IngestCommand>().RunAsync(jsonOptions);
            return await provider.GetRequiredService<CompileCommand>().RunAsync(jsonOptions, ingestedPath);

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
