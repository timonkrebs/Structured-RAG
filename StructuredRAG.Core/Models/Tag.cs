namespace StructuredRAG.Core.Models;

/// <summary>
/// Represents a tag optimized for RAG retrieval
/// </summary>
public class Tag
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Navigation property
    public int EntityId { get; set; }
    public Entity Entity { get; set; } = null!;
}
