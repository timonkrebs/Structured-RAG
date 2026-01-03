namespace StructuredRAG.Core.Models;

/// <summary>
/// Represents an entity that can have tags generated for RAG optimization
/// </summary>
public class Entity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastTagGeneratedAt { get; set; }

    // Navigation property
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
}
