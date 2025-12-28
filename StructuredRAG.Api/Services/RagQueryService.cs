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
    private readonly ApplicationDbContext _dbContext;
    private readonly GemmaLlmService _llmService;
    private readonly ILogger<RagQueryService> _logger;

    public RagQueryService(
        ApplicationDbContext dbContext,
        GemmaLlmService llmService,
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
        var prompt = $@"You are a tag selection system for a RAG (Retrieval-Augmented Generation) pipeline.

Given a user query and a list of available tags, select the most relevant tags that would help retrieve the right information.

User Query: {userQuery}

Available Tags:
{string.Join(", ", availableTags)}

Instructions:
1. Analyze the user query
2. Select 1-5 most relevant tags from the available tags
3. Return ONLY the selected tags as a JSON array of strings
4. If no tags are relevant, return an empty array []

Selected Tags:";

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
                .Take(10)
                .ToListAsync(cancellationToken);
        }

        // Get entities that have at least one of the selected tags
        var entities = await _dbContext.Entities
            .Include(e => e.Tags)
            .Where(e => e.Tags.Any(t => tags.Contains(t.Name)))
            .OrderByDescending(e => e.Tags.Count(t => tags.Contains(t.Name)))
            .Take(10)
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

        var prompt = $@"You are a helpful assistant answering user questions based on provided context.

User Query: {userQuery}

Context:
{contextText}

Instructions:
1. Answer the user's query based ONLY on the provided context
2. Be concise and specific
3. If the context doesn't contain enough information, say so
4. Reference the relevant entities when appropriate

Answer:";

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
