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

        // Read the setting
        var baseUrl = _configuration["Primo:PrimoNewUi"];
        if (string.IsNullOrEmpty(baseUrl))
        {
            throw new Exception("BaseAddress is missing from configuration.");
        }
        _httpClient.BaseAddress = new Uri(baseUrl);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    }

    public async Task<string> GetPrimoJWT()
    {
        if (!_memoryCache.TryGetValue("PrimoJWT", out string jwt) || string.IsNullOrEmpty(jwt))
        {
            jwt = await GetPrimoAuthToken();

            var tokenHandler = new JwtSecurityTokenHandler();
            var jwtToken = tokenHandler.ReadToken(jwt) as JwtSecurityToken;
            DateTimeOffset expiration = jwtToken.ValidTo;

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(expiration);

            _memoryCache.Set("PrimoJWT", jwt, cacheOptions);
        }
        return jwt;
    }

    private async Task<string> GetPrimoAuthToken()
    {
        try
        {
            var url = string.Format(_configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:authRestUrl"], _configuration["PrimoUi:authInst"], _configuration["PrimoUi:authVid"]);

            var response = await _apiClient.SendAsync<string>(
               HttpMethod.Get,
               url,
               deserializeResponse: true,
               isXmlResponse: false // Assuming JSON response, change if needed
           );

            return response;
        }
        catch (Exception e)
        {
            _logger.LogError("Error in GetPrimoAuthToken " + e.Message);
        }
        return null;
    }


    public async Task<string> GetSearchResultsFromPrimo(string query)
    {
        try
        {
            query = query.Replace("[", "%5B").Replace("]", "%5D");

            HttpResponseMessage response = await _httpClient.GetAsync(_configuration["PrimoUi:briefSearch"] + "?" + query);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError("error in GetSearchResultsFromPrimo " + ex.Message + ex.StackTrace);
            return null;
        }
    }

    public string GetPrimoQuery(string docId, string AlmaScope = "", string vid = "")
    {
        if (string.IsNullOrEmpty(docId))
        {
            throw new ArgumentException("Document ID cannot be null or empty", nameof(docId));
        }
        try
        {
            string url;
            bool isManuscriptDigitalItem = vid == "MANUSCRIPTS" && docId.Contains("-");
            var serverUrl = _configuration["Primo:PrimoNewUi"];

            if (!string.IsNullOrEmpty(AlmaScope))
            {
                if (vid != "KTIV")
                    url = string.Format(
                        serverUrl + (!isManuscriptDigitalItem ? _configuration["PrimoUi:briefSearchAlma"] : _configuration["PrimoUi:briefSearchDigitalItem"]),
                        docId, AlmaScope, vid
                    );
                else
                    url = string.Format(
                        serverUrl + _configuration["PrimoUi:ktivReDB"], docId.Replace("PNX_MANUSCRIPTS", ""),
                        AlmaScope, vid
                    );
            }
            else
            {
                string pnxRestUrl = _configuration["PrimoUi:PnxRestUrl"];
                if (!pnxRestUrl.Contains("{0}"))
                {
                    throw new InvalidOperationException("PrimoUi.PnxRestUrl is not properly formatted. It must contain '{0}' for docId.");
                }

                url = string.Format(serverUrl + pnxRestUrl, docId);
            }
            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError("Error in GetPrimoQuery " + ex.Message + ex.StackTrace);
            return "";
        }
    }

    public async Task<JToken> GetCachedPNXAsync(string docId, string vid, string lang)
    {
        if (string.IsNullOrEmpty(docId))
            return null;

        var cacheKey = $"{docId}_{vid}_{lang}";

        if (!_memoryCache.TryGetValue(cacheKey, out JToken cachedPNX))
        {
            // Await the GetPNX call
            string responseContent = await GetPNX(docId);

            if (!string.IsNullOrEmpty(responseContent))
            {
                try
                {
                    cachedPNX = JToken.Parse(responseContent);//.SelectToken("pnx");
                    _memoryCache.Set(cacheKey, cachedPNX, TimeSpan.FromDays(1));
                }
                catch (Exception ex)
                {
                    _logger.LogError($"JSON Parsing Error for docId {docId}: {ex.Message}");
                    return null;
                }
            }
        }

        return cachedPNX;
    }


    public async Task<string> GetPNX(string docId, string AlmaScope = "", string vid = "")
    {
        if (string.IsNullOrEmpty(docId))
        {
            throw new ArgumentException("Document ID cannot be null or empty", nameof(docId));
        }

        try
        {
            string url = GetPrimoQuery(docId, AlmaScope, vid);

            string token = await GetPrimoJWT();  // Await async token retrieval
                                                 // Make the API request
            var headers = new Dictionary<string, string>
                {
                    { "authorization", "Bearer " + token }
                };

            var response = await _apiClient.SendAsync<string>(
                HttpMethod.Get,
                url,
                headers: headers, // Passing headers
                deserializeResponse: false,
                isXmlResponse: false // Assuming JSON response, change if needed
            );


            if (string.IsNullOrEmpty(response))
            {
                _logger.LogWarning($"Empty response from PNX API for docId: {docId}");
                return null;
            }

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in GetPNX for docId {docId}: {ex.Message}");
            throw;
        }
    }
    a
    public JToken GetConfiguration(string vid)
    {
        JToken configuration = null;
        try
        {
            var configurationUrl = _configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:viewRestConfigUrl"] + vid;

            var response = _apiClient.SendAsync<string>(
                HttpMethod.Get,
                configurationUrl,
                deserializeResponse: true,
                isXmlResponse: false // Assuming JSON response, change if needed
            );
            configuration = JToken.Parse(response.ToString());
        }
        catch { }
        return configuration;
    }

    public async Task<JToken> GetTranslation(string vid, string lang)
    {
        JToken translations = null;
        try
        {
            var translationUrl = string.Format(_configuration["Primo:PrimoNewUi"] + _configuration["PrimoUi:viewRestTranslateUrl"], vid, GetPrimoLang(lang));

            var response = await _apiClient.SendAsync<string>(
                HttpMethod.Get,
                translationUrl,
                deserializeResponse: false,
                isXmlResponse: false // Assuming JSON response, change if needed
            );

            translations = JToken.Parse(response.ToString());
        }
        catch { }
        return translations;
    }

    private string GetPrimoLang(string lang)
    {
        switch (lang)
        {
            case "en":
                return "en_US";
            case "ar":
                return "ar_EG";
            default:
                return "iw_IL";
        }
    }
}
