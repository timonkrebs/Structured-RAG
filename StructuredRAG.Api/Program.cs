using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StructuredRAG.Api.Data;
using StructuredRAG.Api.Models;
using StructuredRAG.Api.Services;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddEnvironmentVariables();

// Configure database
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(connectionString));

// Configure HTTP client for Gemma LLM
builder.Services.AddHttpClient<GemmaLlmService>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

// Register services
builder.Services.AddScoped<TagGenerationService>();
builder.Services.AddScoped<RagQueryService>();

var host = builder.Build();

// Ensure database is created and apply migrations
using (var scope = host.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var logger = services.GetRequiredService<ILogger<Program>>();
    
    try
    {
        var dbContext = services.GetRequiredService<ApplicationDbContext>();
        logger.LogInformation("Ensuring database is created...");
        await dbContext.Database.EnsureCreatedAsync();
        logger.LogInformation("Database is ready");

        // Seed sample data if database is empty
        if (!await dbContext.Entities.AnyAsync())
        {
            logger.LogInformation("Seeding sample data...");
            await SeedSampleDataAsync(dbContext, logger);
        }

        // Generate tags for all entities
        var tagService = services.GetRequiredService<TagGenerationService>();
        logger.LogInformation("Generating tags for entities...");
        await tagService.GenerateTagsForAllEntitiesAsync();
        
        // Demo RAG query
        var ragService = services.GetRequiredService<RagQueryService>();
        logger.LogInformation("Running demo RAG query...");
        
        var result = await ragService.ProcessQueryAsync(
            "What programming languages are mentioned?");
        
        logger.LogInformation("\n=== RAG Query Demo ===");
        logger.LogInformation("Query: {Query}", result.Query);
        logger.LogInformation("Selected Tags: {Tags}", string.Join(", ", result.SelectedTags));
        logger.LogInformation("Found {Count} entities", result.FilteredEntities.Count);
        logger.LogInformation("Response: {Response}", result.Response);
        logger.LogInformation("===================\n");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "An error occurred during startup");
        throw;
    }
}

var appLogger = host.Services.GetRequiredService<ILogger<Program>>();
appLogger.LogInformation("Application completed successfully");

static async Task SeedSampleDataAsync(ApplicationDbContext dbContext, ILogger logger)
{
    var entities = new[]
    {
        new Entity
        {
            Name = "Introduction to C# Programming",
            Content = "C# is a modern, object-oriented programming language developed by Microsoft. It is widely used for building Windows applications, web services, and games using Unity.",
            CreatedAt = DateTime.UtcNow
        },
        new Entity
        {
            Name = "Python for Data Science",
            Content = "Python is a versatile programming language popular in data science, machine learning, and artificial intelligence. Libraries like NumPy, Pandas, and Scikit-learn make it powerful for data analysis.",
            CreatedAt = DateTime.UtcNow
        },
        new Entity
        {
            Name = "JavaScript Web Development",
            Content = "JavaScript is the language of the web, enabling interactive web pages and modern web applications. Frameworks like React, Vue, and Angular make frontend development efficient.",
            CreatedAt = DateTime.UtcNow
        },
        new Entity
        {
            Name = "Database Design with SQL Server",
            Content = "SQL Server is a relational database management system by Microsoft. It provides robust data storage, querying capabilities, and is commonly used in enterprise applications.",
            CreatedAt = DateTime.UtcNow
        },
        new Entity
        {
            Name = "Machine Learning Fundamentals",
            Content = "Machine learning is a branch of artificial intelligence that enables systems to learn from data. Common algorithms include decision trees, neural networks, and support vector machines.",
            CreatedAt = DateTime.UtcNow
        }
    };

    dbContext.Entities.AddRange(entities);
    await dbContext.SaveChangesAsync();
    logger.LogInformation("Seeded {Count} sample entities", entities.Length);
}
