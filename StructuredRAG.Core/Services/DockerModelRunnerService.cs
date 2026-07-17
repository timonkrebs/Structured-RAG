using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

namespace StructuredRAG.Core.Services;

/// <summary>
/// Client for any OpenAI-compatible chat-completions endpoint: Docker Model Runner,
/// llama.cpp, OpenAI, Azure OpenAI, OpenRouter, ... Configure via
/// DockerModelRunner:Endpoint, :SimpleModel and (for hosted APIs) :ApiKey.
/// </summary>
public class DockerModelRunnerService : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DockerModelRunnerService> _logger;
    private readonly string _modelEndpoint;
    private readonly string _modelName;
    private readonly JsonSerializerOptions _jsonOptions;

    public DockerModelRunnerService(HttpClient httpClient, IConfiguration configuration, ILogger<DockerModelRunnerService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _modelEndpoint = (configuration["DockerModelRunner:Endpoint"] ?? "http://localhost:12434/engines/llama.cpp/v1").TrimEnd('/');
        _modelName = configuration["DockerModelRunner:SimpleModel"] ?? "ai/granite-4.0-nano:latest";

        var apiKey = configuration["DockerModelRunner:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
    }

    // A compile run makes one call per changed module and only writes artifacts at the
    // end, so a single transient failure (rate limit, gateway hiccup) must not abort
    // the whole run. 429/5xx and network/timeout errors are retried with backoff.
    private const int MaxAttempts = 4;

    /// <summary>
    /// Sends a prompt to the LLM and returns the response
    /// </summary>
    public async Task<string> GenerateAsync(string prompt, CancellationToken cancellationToken = default, string? system = null)
    {
        var request = new
        {
            model = _modelName,
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = system ?? @"You are an expert content classifier and taxonomy system. Your goal is to analyze the provided text and generate precise, hierarchical tags. Format: [""tag1"", ""tag2""]"
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync(_modelEndpoint + "/chat/completions", request, _jsonOptions, cancellationToken);
                if (attempt < MaxAttempts && IsTransient(response.StatusCode))
                {
                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 5);
                    _logger.LogWarning("LLM endpoint returned {StatusCode}; retrying in {Delay:0}s (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, delay.TotalSeconds, attempt, MaxAttempts);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken);
                return result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            }
            catch (Exception ex) when (attempt < MaxAttempts
                && ex is HttpRequestException or TaskCanceledException
                && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("LLM call failed ({Message}); retrying in {Delay}s (attempt {Attempt}/{Max})",
                    ex.Message, attempt * 5, attempt, MaxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(attempt * 5), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating response from LLM endpoint {Endpoint}", _modelEndpoint);
                throw;
            }
        }

        static bool IsTransient(HttpStatusCode status) =>
            status == HttpStatusCode.TooManyRequests || (int)status >= 500;
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
