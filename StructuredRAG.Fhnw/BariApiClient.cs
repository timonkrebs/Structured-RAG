using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace StructuredRAG.Fhnw;

/// <summary>
/// Typed client for the FHNW Modulbeschreibungen backend (public, no auth).
/// Requests are throttled and retried; be polite — this is a shared university service.
/// </summary>
public class BariApiClient
{
    public const string DefaultBaseUrl = "https://bariapi.fhnw.ch/cit_modulbeschreibungen/prod";
    public const string ModuleDetailPageUrlTemplate = "https://modulbeschreibungen.webapps.fhnw.ch/detail/{0}";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<BariApiClient> _logger;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _gate;
    private readonly int _delayMs;

    public BariApiClient(HttpClient httpClient, IConfiguration configuration, ILogger<BariApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["BariApi:BaseUrl"]?.TrimEnd('/') ?? DefaultBaseUrl;
        _gate = new SemaphoreSlim(configuration.GetValue("BariApi:MaxConcurrency", 4));
        _delayMs = configuration.GetValue("BariApi:DelayMs", 100);
    }

    public static string GetModuleDetailPageUrl(string planSemesterModulId, string uiLanguage = "de") =>
        string.Format(ModuleDetailPageUrlTemplate, Uri.EscapeDataString(planSemesterModulId)) + $"?uiLanguage={uiLanguage}";

    public Task<SemesterDto?> GetLatestPlanSemesterAsync(CancellationToken ct = default) =>
        SendWithRetryAsync<SemesterDto>(() =>
            new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/PlanSemester/LatestPlanSemester"), ct);

    public async Task<List<FacetResultDto>> GetFacetsAsync(CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync<FacetsResponseDto>(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/search/facets")
            {
                Content = JsonContent.Create(new SearchQueryDto(), options: JsonOptions)
            }, ct);
        return response?.FacetResults ?? new List<FacetResultDto>();
    }

    public async Task<SearchResponseDto> SearchAsync(
        SearchQueryDto query, int skip, int take, CancellationToken ct = default)
    {
        var request = new SearchRequestDto
        {
            SearchQuery = query,
            PagingQuery = new PagingQueryDto { Skip = skip, Take = take }
        };
        var response = await SendWithRetryAsync<SearchResponseDto>(() =>
            new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/search")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            }, ct);
        return response ?? new SearchResponseDto();
    }

    /// <summary>Returns null when the module does not exist (404) — e.g. removed since the last compile.</summary>
    public Task<ModuleDetailDto?> GetModuleDetailAsync(string planSemesterModulId, CancellationToken ct = default) =>
        SendWithRetryAsync<ModuleDetailDto>(() =>
            new HttpRequestMessage(HttpMethod.Get,
                $"{_baseUrl}/api/PlanSemesterModul/{Uri.EscapeDataString(planSemesterModulId)}"), ct);

    /// <summary>Fetches the raw JSON of a module detail (used for the ingestion raw cache).</summary>
    public async Task<string?> GetModuleDetailRawAsync(string planSemesterModulId, CancellationToken ct = default)
    {
        var url = $"{_baseUrl}/api/PlanSemesterModul/{Uri.EscapeDataString(planSemesterModulId)}";
        return await ExecuteThrottledAsync(async () =>
        {
            using var response = await _httpClient.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(ct);
        }, () => url, ct);
    }

    public static ModuleDetailDto? ParseModuleDetail(string json) =>
        JsonSerializer.Deserialize<ModuleDetailDto>(json, JsonOptions);

    private async Task<T?> SendWithRetryAsync<T>(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct) where T : class
    {
        string? urlForLog = null;
        return await ExecuteThrottledAsync(async () =>
        {
            using var request = requestFactory();
            urlForLog ??= request.RequestUri?.ToString();
            using var response = await _httpClient.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        }, () => urlForLog ?? "?", ct);
    }

    private async Task<T?> ExecuteThrottledAsync<T>(
        Func<Task<T?>> action, Func<string> urlForLog, CancellationToken ct)
    {
        const int maxAttempts = 3;
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var result = await action();
                    if (_delayMs > 0) await Task.Delay(_delayMs, ct);
                    return result;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           && attempt < maxAttempts && !ct.IsCancellationRequested)
                {
                    var backoff = TimeSpan.FromSeconds(attempt * 2);
                    _logger.LogWarning("Request to {Url} failed (attempt {Attempt}/{Max}): {Message}. Retrying in {Backoff}s",
                        urlForLog(), attempt, maxAttempts, ex.Message, backoff.TotalSeconds);
                    await Task.Delay(backoff, ct);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
