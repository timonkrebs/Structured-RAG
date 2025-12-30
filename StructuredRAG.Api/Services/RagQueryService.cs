using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructuredRAG.Api.Data;
using System.Text.Json;

namespace StructuredRAG.Api.Services;

/// <summary>
/// Service for RAG query processing with tag-based filtering
/// </summary>
public class RagQueryService
{
    private const int MaxEntitiesPerQuery = 10;

    private readonly ApplicationDbContext _dbContext;
    private readonly DockerModelRunnerService _llmService;
    private readonly ILogger<RagQueryService> _logger;

    public RagQueryService(
        ApplicationDbContext dbContext,
        DockerModelRunnerService llmService,
        ILogger<RagQueryService> logger)
    {
        _dbContext = dbContext;
        _llmService = llmService;
        _logger = logger;
    }

    /// <summary>
    /// Processes a user query using RAG with tag-based filtering
    /// </summary>
    public async Task<RagQueryResult> ProcessQueryAsync(string userQuery, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Processing RAG query: {Query}", userQuery);

        // Step 1: Get all available tags
        var allTags = await _dbContext.Tags
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (!allTags.Any())
        {
            _logger.LogWarning("No tags available in the system");
            return new RagQueryResult
            {
                Query = userQuery,
                SelectedTags = new List<string>(),
                FilteredEntities = new List<EntityResult>(),
                Response = "No data available in the system yet."
            };
        }

        // Step 2: Use LLM to select relevant tags
        var relevantTags = await SelectRelevantTagsAsync(userQuery, allTags, cancellationToken);

        _logger.LogInformation("Selected {Count} relevant tags: {Tags}",
            relevantTags.Count, string.Join(", ", relevantTags));

        // Step 3: Filter entities by selected tags
        var filteredEntities = await FilterEntitiesByTagsAsync(relevantTags, cancellationToken);

        _logger.LogInformation("Found {Count} entities matching the tags", filteredEntities.Count);

        // Step 4: Generate response using filtered entities
        var response = await GenerateResponseAsync(userQuery, filteredEntities, cancellationToken);

        return new RagQueryResult
        {
            Query = userQuery,
            SelectedTags = relevantTags,
            FilteredEntities = filteredEntities.Select(e => new EntityResult
            {
                Id = e.Id,
                Name = e.Name,
                Content = e.Content
            }).ToList(),
            Response = response
        };
    }

    private async Task<List<string>> SelectRelevantTagsAsync(
        string userQuery,
        List<string> availableTags,
        CancellationToken cancellationToken)
    {
        var prompt = $@"Select 1-5 most relevant tags (JSON array) for this query from the list.

Query: '{userQuery}'

Tags: [""{string.Join(", ", availableTags)}""]

Output Format: [""tag1"", ""tag2""] or []";

        var response = await _llmService.GenerateAsync(prompt, cancellationToken);
        return ParseTagsFromResponse(response);
    }

    private async Task<List<Models.Entity>> FilterEntitiesByTagsAsync(
        List<string> tags,
        CancellationToken cancellationToken)
    {
        if (!tags.Any())
        {
            // If no tags selected, return all entities (or implement default filtering)
            return await _dbContext.Entities
                .Include(e => e.Tags)
                .Take(MaxEntitiesPerQuery)
                .ToListAsync(cancellationToken);
        }

        // Get entities that have at least one of the selected tags
        var entities = await _dbContext.Entities
            .Include(e => e.Tags)
            .Where(e => e.Tags.Any(t => tags.Contains(t.Name)))
            .OrderByDescending(e => e.Tags.Count(t => tags.Contains(t.Name)))
            .Take(MaxEntitiesPerQuery)
            .ToListAsync(cancellationToken);

        return entities;
    }

    private async Task<string> GenerateResponseAsync(
        string userQuery,
        List<Models.Entity> filteredEntities,
        CancellationToken cancellationToken)
    {
        if (!filteredEntities.Any())
        {
            return "I couldn't find any relevant information to answer your query.";
        }

        var contextText = string.Join("\n\n", filteredEntities.Select(e =>
            $"[{e.Name}]\n{e.Content}"));

        var prompt = $@"Answer this query using ONLY the provided context. Be concise.

Query: {userQuery}

Context:
{contextText}";

        return await _llmService.GenerateAsync(prompt, cancellationToken);
    }

    private List<string> ParseTagsFromResponse(string response)
    {
        try
        {
            var jsonStart = response.IndexOf('[');
            var jsonEnd = response.LastIndexOf(']');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var tags = JsonSerializer.Deserialize<List<string>>(jsonContent);
                return tags ?? new List<string>();
            }

            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing tags from response: {Response}", response);
            return new List<string>();
        }
    }
}

public class RagQueryResult
{
    public string Query { get; set; } = string.Empty;
    public List<string> SelectedTags { get; set; } = new();
    public List<EntityResult> FilteredEntities { get; set; } = new();
    public string Response { get; set; } = string.Empty;
}

public class EntityResult
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
