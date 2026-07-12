namespace StructuredRAG.Core.Services;

/// <summary>
/// Minimal text-generation abstraction used by the knowledge compiler, so the
/// transport (OpenAI-compatible HTTP endpoint, Codex CLI, ...) is swappable.
/// </summary>
public interface ILlmClient
{
    /// <summary>Sends a prompt (and optional system instruction) and returns the model's text response.</summary>
    Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default, string? system = null);
}
