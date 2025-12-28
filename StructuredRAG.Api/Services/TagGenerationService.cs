using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructuredRAG.Api.Data;
using StructuredRAG.Api.Models;
using System.Text.Json;

namespace StructuredRAG.Api.Services;

/// <summary>
/// Service for generating and managing tags for entities using LLM
/// </summary>
public class TagGenerationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly GemmaLlmService _llmService;
    private readonly ILogger<TagGenerationService> _logger;

    public TagGenerationService(
        ApplicationDbContext dbContext,
        GemmaLlmService llmService,
        ILogger<TagGenerationService> logger)
    {
        _dbContext = dbContext;
        _llmService = llmService;
        _logger = logger;
    }

    /// <summary>
    /// Generates optimized tags for an entity considering existing tags
    /// </summary>
    public async Task GenerateTagsForEntityAsync(int entityId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.Entities
            .Include(e => e.Tags)
            .FirstOrDefaultAsync(e => e.Id == entityId, cancellationToken);

        if (entity == null)
        {
            _logger.LogWarning("Entity with ID {EntityId} not found", entityId);
            return;
        }

        // Get all existing tags from database
        var existingTags = await _dbContext.Tags
            .Select(t => t.Name)
            .Distinct()
            .ToListAsync(cancellationToken);

        var prompt = BuildTagGenerationPrompt(entity, existingTags);
        var response = await _llmService.GenerateAsync(prompt, cancellationToken);

        var newTags = ParseTagsFromResponse(response);

        foreach (var tagName in newTags)
        {
            if (!entity.Tags.Any(t => t.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase)))
            {
                entity.Tags.Add(new Tag
                {
                    Name = tagName,
                    Description = $"Auto-generated tag for RAG optimization on {DateTime.UtcNow:yyyy-MM-dd}",
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        entity.LastTagGeneratedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Generated {Count} new tags for entity {EntityId}", newTags.Count, entityId);
    }

    /// <summary>
    /// Generates tags for all entities that haven't been tagged yet
    /// </summary>
    public async Task GenerateTagsForAllEntitiesAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.Entities
            .Where(e => e.LastTagGeneratedAt == null)
            .ToListAsync(cancellationToken);

        _logger.LogInformation("Generating tags for {Count} entities", entities.Count);

        foreach (var entity in entities)
        {
            await GenerateTagsForEntityAsync(entity.Id, cancellationToken);
        }
    }

    private string BuildTagGenerationPrompt(Entity entity, List<string> existingTags)
    {
        var existingTagsText = existingTags.Any()
            ? $"\n\nExisting tags in the system that you should consider and reuse when appropriate:\n{string.Join(", ", existingTags)}"
            : "";

        return $@"You are a tagging system that generates optimized tags for Retrieval-Augmented Generation (RAG).

Your task is to generate 3-7 relevant tags for the following entity. These tags should:
1. Be descriptive and specific
2. Help with semantic search and retrieval
3. Cover different aspects (topic, domain, type, key concepts)
4. Reuse existing tags from the system when appropriate to maintain consistency
5. Be concise (1-3 words per tag)

Entity Name: {entity.Name}
Entity Content: {entity.Content}{existingTagsText}

Generate the tags as a JSON array of strings, like this: [""tag1"", ""tag2"", ""tag3""]

Tags:";
    }

    private List<string> ParseTagsFromResponse(string response)
    {
        try
        {
            // Try to extract JSON array from response
            var jsonStart = response.IndexOf('[');
            var jsonEnd = response.LastIndexOf(']');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonContent = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var parsedTags = JsonSerializer.Deserialize<List<string>>(jsonContent);
                return parsedTags ?? new List<string>();
            }

            // Fallback: split by common delimiters
            var fallbackTags = response
                .Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim().Trim('"', '\'', '[', ']', ' '))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Take(7)
                .ToList();

            return fallbackTags;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing tags from response: {Response}", response);
            return new List<string>();
        }
    }
}
