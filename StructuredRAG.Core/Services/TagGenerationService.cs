using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using StructuredRAG.Core.Data;
using StructuredRAG.Core.Models;
using System.Text.Json;

namespace StructuredRAG.Core.Services;

/// <summary>
/// Service for generating and managing tags for entities using LLM
/// </summary>
public class TagGenerationService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly DockerModelRunnerService _llmService;
    private readonly ILogger<TagGenerationService> _logger;

    public TagGenerationService(
        ApplicationDbContext dbContext,
        DockerModelRunnerService llmService,
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

    /// <summary>
    /// Generates an initial set of tags from a representative subset of entities (10%)
    /// to ensure tag consistency.
    /// </summary>
    public async Task GenerateInitialTagSetAsync(CancellationToken cancellationToken = default)
    {
        var untaggedEntities = await _dbContext.Entities
            .Where(e => e.LastTagGeneratedAt == null)
            .ToListAsync(cancellationToken);

        if (untaggedEntities.Count == 0)
        {
            _logger.LogInformation("No untagged entities found to generate initial tag set.");
            return;
        }

        var sampleSize = (int)Math.Max(1, untaggedEntities.Count * 0.1);
        var random = new Random();
        var sampleEntities = untaggedEntities.OrderBy(e => random.Next()).Take(sampleSize).ToList();

        _logger.LogInformation("Generating initial tag set from a sample of {SampleSize} entities.", sampleEntities.Count);

        foreach (var entity in sampleEntities)
        {
            await GenerateTagsForEntityAsync(entity.Id, cancellationToken);
        }

        _logger.LogInformation("Finished generating initial tag set.");
    }

    private string BuildTagGenerationPrompt(Entity entity, List<string> existingTags)
    {
        var existingTagsText = existingTags.Any()
            ? $@"\n Use these existing tags if suitable to maintain consistency: [{string.Join(", ", existingTags)}]. 
            Add new tags if relevant but not found in the existing ones."
            : "";

        return $@"### Instructions
1. **Analyze:** Read the Title, Content, and any Existing Tags to understand the core subject.
2. **Hierarchy:** Generate tags ranging from the general domain to specific topics.
3. **Quantity:** Output exactly between 3 to 10 tags.
4. **Formatting Rules:**
    * Output a raw JSON array of strings (e.g., [""tag1"", ""tag2""]).
    * Maximum 3 words per tag.
    * **Atomic Concepts:** Split distinct concepts (e.g., convert ""Nature and Technology"" -> ""Nature"", ""Technology"").
    * **Compound Nouns:** Keep standard phrases together (e.g., keep ""Team Analysis"" or ""Machine Learning"" as single tags).
    * Do not use commas within a tag.

### Examples
Input: ""The aesthetics of space and immersion in design.""
Bad Output: [""aesthetics, space"", ""immersion design""]
Good Output: [""Design"", ""Aesthetics"", ""Space"", ""Immersion"", ""Spatial Theory""]

Input: ""How to conduct a team analysis using Python.""
Bad Output: [""team"", ""analysis"", ""python code""]
Good Output: [""Data Science"", ""Team Analysis"", ""Python"", ""Management""]

### Data to Process
Title: '{entity.Name}'
Content: '{entity.Content}'{existingTagsText}

Output JSON:";
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
