using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace StructuredRAG.Api.Services;

/// <summary>
/// Service for interacting with Docker Model Runner
/// </summary>
public class DockerModelRunnerService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DockerModelRunnerService> _logger;
    private readonly string _modelEndpoint;
    private readonly string _modelName;

    public DockerModelRunnerService(HttpClient httpClient, IConfiguration configuration, ILogger<DockerModelRunnerService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _modelEndpoint = configuration["DockerModelRunner:Endpoint"] ?? "http://localhost:12434/engines/llama.cpp/v1/chat/completions";
        _modelName = configuration["DockerModelRunner:Model"] ?? "ai/gemma3";
    }

    /// <summary>
    /// Sends a prompt to the LLM and returns the response
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                model = _modelName,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "You are a helpful assistant.",
                        timestamp = DateTime.UtcNow
                    },
                    new
                    {
                        role = "user",
                        content = prompt,
                        timestamp = DateTime.UtcNow
                    }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(_modelEndpoint, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken);
            return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response from Docker Model Runner");
            throw;
        }
    }

    // Response models matching OpenAI Chat Completion format
    private class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice> Choices { get; set; } = new();
    }

    private class Choice
    {
        [JsonPropertyName("message")]
        public Message Message { get; set; } = new();
    }

    private class Message
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}
