using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using NLI.Infrastructure.Utils;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace NLI.Infrastructure.PrimoService;

public class PrimoService : IPrimoService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ApiClient _apiClient;
    private readonly ILogger<PrimoService> _logger;
    private readonly IMemoryCache _memoryCache;

    public PrimoService(HttpClient httpClient, IConfiguration configuration, ApiClient apiClient, IMemoryCache memoryCache, ILogger<PrimoService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _apiClient = apiClient;
        _logger = logger;
        _memoryCache = memoryCache;

        var baseUrl = _configuration["Primo:PrimoNewUi"];
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogCritical("BaseAddress is missing from configuration.");
            throw new Exception("BaseAddress is missing from configuration.");
        }

        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<string> GetPrimoJWT()
    {
        _logger.LogDebug("Attempting to retrieve Primo JWT from cache.");

        if (!_memoryCache.TryGetValue("PrimoJWT", out string jwt) || string.IsNullOrEmpty(jwt))
        {
            _logger.LogInformation("Primo JWT not found in cache. Requesting new token.");

            jwt = await GetPrimoAuthToken();

            if (string.IsNullOrEmpty(jwt))
            {
                _logger.LogWarning("Failed to retrieve Primo JWT.");
                return null;
            }

            try
            {
                var jwtToken = new JwtSecurityTokenHandler().ReadToken(jwt) as JwtSecurityToken;
                var expiration = jwtToken.ValidTo;

                _memoryCache.Set("PrimoJWT", jwt, new MemoryCacheEntryOptions
                {
                    AbsoluteExpiration = expiration
                });

                _logger.LogDebug("Primo JWT cached until {Expiration}.", expiration);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse JWT expiration.");
            }
        }

        return jwt;
    }

    private async Task<string> GetPrimoAuthToken()
    {
        try
        {
            var url = string.Format(
                _configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:authRestUrl"],
                _configuration["PrimoUi:authInst"],
                _configuration["PrimoUi:authVid"]
            );

            _logger.LogDebug("Requesting Primo auth token from URL: {Url}", url);

            return await _apiClient.SendAsync<string>(
                HttpMethod.Get,
                url,
                deserializeResponse: true,
                isXmlResponse: false
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred while getting Primo auth token.");
            return null;
        }
    }

    public async Task<string> GetSearchResultsFromPrimo(string query)
    {
        try
        {
            _logger.LogInformation("Searching Primo with query: {Query}", query);
            query = query.Replace("[", "%5B").Replace("]", "%5D");

            var url = _configuration["PrimoUi:briefSearch"] + "?" + query;
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            _logger.LogDebug("Search successful.");
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetSearchResultsFromPrimo.");
            return null;
        }
    }

    public string GetPrimoQuery(string docId, string almaScope = "", string vid = "")
    {
        if (string.IsNullOrEmpty(docId))
        {
            _logger.LogWarning("GetPrimoQuery was called with null or empty docId.");
            throw new ArgumentException("Document ID cannot be null or empty", nameof(docId));
        }

        try
        {
            string url;
            var serverUrl = _configuration["Primo:PrimoNewUi"];
            bool isDigitalItem = vid == "MANUSCRIPTS" && docId.Contains("-");

            if (!string.IsNullOrEmpty(almaScope))
            {
                url = vid != "KTIV"
                    ? string.Format(serverUrl + (isDigitalItem ? _configuration["PrimoUi:briefSearchDigitalItem"] : _configuration["PrimoUi:briefSearchAlma"]), docId, almaScope, vid)
                    : string.Format(serverUrl + _configuration["PrimoUi:ktivReDB"], docId.Replace("PNX_MANUSCRIPTS", ""), almaScope, vid);
            }
            else
            {
                string pnxRestUrl = _configuration["PrimoUi:PnxRestUrl"];
                if (!pnxRestUrl.Contains("{0}"))
                    throw new InvalidOperationException("PrimoUi.PnxRestUrl must contain '{0}' for docId.");

                url = string.Format(serverUrl + pnxRestUrl, docId);
            }

            _logger.LogDebug("Generated Primo URL: {Url}", url);
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build Primo query URL.");
            return string.Empty;
        }
    }

    public async Task<JToken> GetCachedPNXAsync(string docId, string vid, string lang)
    {
        if (string.IsNullOrEmpty(docId))
        {
            _logger.LogWarning("GetCachedPNXAsync called with empty docId.");
            return null;
        }

        var cacheKey = $"{docId}_{vid}_{lang}";

        if (!_memoryCache.TryGetValue(cacheKey, out JToken cachedPNX))
        {
            _logger.LogInformation("PNX not found in cache for key: {CacheKey}", cacheKey);

            var responseContent = await GetPNX(docId, "", vid);

            if (!string.IsNullOrEmpty(responseContent))
            {
                try
                {
                    cachedPNX = JToken.Parse(responseContent);
                    _memoryCache.Set(cacheKey, cachedPNX, TimeSpan.FromDays(1));
                    _logger.LogDebug("Cached new PNX for {CacheKey}", cacheKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to parse PNX response.");
                    return null;
                }
            }
        }

        return cachedPNX;
    }

    public async Task<string> GetPNX(string docId, string almaScope = "", string vid = "")
    {
        if (string.IsNullOrEmpty(docId))
        {
            _logger.LogWarning("GetPNX called with empty docId.");
            throw new ArgumentException("Document ID cannot be null or empty", nameof(docId));
        }

        try
        {
            var url = GetPrimoQuery(docId, almaScope, vid);
            var token = await GetPrimoJWT();

            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Unable to get JWT token for PNX request.");
                return null;
            }

            var headers = new Dictionary<string, string> { { "authorization", $"Bearer {token}" } };

            var response = await _apiClient.SendAsync<string>(
                HttpMethod.Get,
                url,
                headers: headers,
                deserializeResponse: false,
                isXmlResponse: false
            );

            if (string.IsNullOrEmpty(response))
            {
                _logger.LogWarning("Empty PNX response for docId: {DocId}", docId);
                return null;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving PNX for docId: {DocId}", docId);
            throw;
        }
    }

    public JToken GetConfiguration(string vid)
    {
        try
        {
            var configurationUrl = _configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:viewRestConfigUrl"] + vid;

            _logger.LogDebug("Fetching configuration from: {Url}", configurationUrl);

            var response = _apiClient.SendAsync<string>(
                HttpMethod.Get,
                configurationUrl,
                deserializeResponse: true,
                isXmlResponse: false
            );

            return JToken.Parse(response.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve configuration for vid: {Vid}", vid);
            return null;
        }
    }

    public async Task<JToken> GetTranslation(string vid, string lang)
    {
        try
        {
            var translationUrl = string.Format(
                _configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:viewRestTranslateUrl"],
                vid,
                GetPrimoLang(lang)
            );

            _logger.LogDebug("Fetching translations from: {Url}", translationUrl);

            var response = await _apiClient.SendAsync<string>(
                HttpMethod.Get,
                translationUrl,
                deserializeResponse: false,
                isXmlResponse: false
            );

            return JToken.Parse(response.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get translation for vid: {Vid}, lang: {Lang}", vid, lang);
            return null;
        }
    }

    private string GetPrimoLang(string lang)
    {
        return lang switch
        {
            "en" => "en_US",
            "ar" => "ar_EG",
            _ => "iw_IL"
        };
    }
}
