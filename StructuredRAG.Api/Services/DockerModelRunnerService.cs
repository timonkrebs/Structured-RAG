using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

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
                messages = new[] { new 
                {
                    role =  "user",
                    content = prompt,
                    timestamp = new DateTime()
                } }
            };

            var response = await _httpClient.PostAsJsonAsync(_modelEndpoint, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ModelRunnerResponse>(cancellationToken);
            return result?.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response from Docker Model Runner");
            throw;
        }
    }

    private class ModelRunnerResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}
