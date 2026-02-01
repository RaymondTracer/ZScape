using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ZScape.Services;

/// <summary>
/// Service for looking up country codes from IP addresses using ip-api.com.
/// </summary>
public class Ip2CountryService
{
    private readonly HttpClient _httpClient;
    private readonly LoggingService _logger;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _rateLimiter = new(1, 1);
    private DateTime _lastRequest = DateTime.MinValue;
    
    // ip-api.com has a limit of 45 requests per minute for free tier
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(1500);

    public Ip2CountryService(LoggingService logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Looks up the country code for an IP address.
    /// Returns ISO 3166-1 alpha-2 code (e.g., "US", "DE") or null if lookup fails.
    /// </summary>
    public async Task<string?> LookupCountryAsync(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
            return null;

        // Check cache first
        if (_cache.TryGetValue(ipAddress, out var cached))
            return cached;

        await _rateLimiter.WaitAsync();
        try
        {
            // Check cache again after acquiring lock
            if (_cache.TryGetValue(ipAddress, out cached))
                return cached;

            // Rate limiting
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed);
            }

            _lastRequest = DateTime.UtcNow;

            // Use ip-api.com (free, no API key required, returns ISO alpha-2 codes)
            var url = $"http://ip-api.com/json/{ipAddress}?fields=status,countryCode";
            var response = await _httpClient.GetFromJsonAsync<IpApiResponse>(url);

            if (response?.Status == "success" && !string.IsNullOrEmpty(response.CountryCode))
            {
                var countryCode = response.CountryCode.ToUpperInvariant();
                _cache[ipAddress] = countryCode;
                _logger.Verbose($"IP2C: {ipAddress} -> {countryCode}");
                return countryCode;
            }

            _logger.Verbose($"IP2C lookup failed for {ipAddress}: {response?.Status}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Warning($"IP2C lookup error for {ipAddress}: {ex.Message}");
            return null;
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    /// <summary>
    /// Batch lookup for multiple IP addresses. More efficient than individual lookups.
    /// </summary>
    public async Task<Dictionary<string, string>> LookupCountriesAsync(IEnumerable<string> ipAddresses)
    {
        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var toLookup = new List<string>();

        // Check cache first
        foreach (var ip in ipAddresses.Distinct())
        {
            if (_cache.TryGetValue(ip, out var cached))
            {
                results[ip] = cached;
            }
            else
            {
                toLookup.Add(ip);
            }
        }

        if (toLookup.Count == 0)
            return results;

        // ip-api.com supports batch requests (up to 100 IPs)
        await _rateLimiter.WaitAsync();
        try
        {
            // Rate limiting
            var elapsed = DateTime.UtcNow - _lastRequest;
            if (elapsed < MinRequestInterval)
            {
                await Task.Delay(MinRequestInterval - elapsed);
            }

            _lastRequest = DateTime.UtcNow;

            // Batch API (POST to /batch with JSON array)
            var batchSize = 100;
            for (int i = 0; i < toLookup.Count; i += batchSize)
            {
                var batch = toLookup.Skip(i).Take(batchSize).ToList();
                var requestBody = batch.Select(ip => new { query = ip, fields = "status,countryCode,query" }).ToList();

                try
                {
                    var response = await _httpClient.PostAsJsonAsync("http://ip-api.com/batch", requestBody);
                    if (response.IsSuccessStatusCode)
                    {
                        var batchResults = await response.Content.ReadFromJsonAsync<List<IpApiBatchResponse>>();
                        if (batchResults != null)
                        {
                            foreach (var result in batchResults)
                            {
                                if (result.Status == "success" && !string.IsNullOrEmpty(result.CountryCode) && !string.IsNullOrEmpty(result.Query))
                                {
                                    var countryCode = result.CountryCode.ToUpperInvariant();
                                    _cache[result.Query] = countryCode;
                                    results[result.Query] = countryCode;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"IP2C batch lookup error: {ex.Message}");
                }

                // Rate limit between batches
                if (i + batchSize < toLookup.Count)
                {
                    await Task.Delay(MinRequestInterval);
                }
            }

            _logger.Info($"IP2C: Looked up {toLookup.Count} IPs, resolved {results.Count - (ipAddresses.Count() - toLookup.Count)} new");
        }
        finally
        {
            _rateLimiter.Release();
        }

        return results;
    }

    /// <summary>
    /// Clears the IP-to-country cache.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
    }

    private class IpApiResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("countryCode")]
        public string? CountryCode { get; set; }
    }

    private class IpApiBatchResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("countryCode")]
        public string? CountryCode { get; set; }

        [JsonPropertyName("query")]
        public string? Query { get; set; }
    }
}
