using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

namespace StructuredRAG.Api.Services;

/// <summary>
/// Service for interacting with Gemma 3 LLM via Docker model runner
/// </summary>
public class GemmaLlmService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GemmaLlmService> _logger;
    private readonly string _modelEndpoint;

    public GemmaLlmService(HttpClient httpClient, IConfiguration configuration, ILogger<GemmaLlmService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _modelEndpoint = configuration["Gemma:Endpoint"] ?? "http://gemma:11434/api/generate";
    }

    /// <summary>
    /// Sends a prompt to the Gemma LLM and returns the response
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new
            {
                model = "gemma2:3b",
                prompt = prompt,
                stream = false,
                options = new
                {
                    temperature = 0.7,
                    top_p = 0.9
                }
            };

            var response = await _httpClient.PostAsJsonAsync(_modelEndpoint, request, cancellationToken);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GemmaResponse>(cancellationToken);
            return result?.Response ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response from Gemma LLM");
            throw;
        }
    }

    private class GemmaResponse
    {
        public string Response { get; set; } = string.Empty;
    }
}
