using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HSPI_PowerView
{
    /// <summary>
    /// Hunter Douglas cloud API client for shade control
    /// Used as fallback when local HTTP API doesn't support write operations (Gen3 hubs)
    /// </summary>
    public class HunterDouglasCloudClient
    {
        private readonly string _email;
        private readonly string _password;
        private readonly HttpClient _httpClient;
        private string _accessToken;
        private string _homeId;
        private string _activeCloudBase;
        private Action<string> _logger;

        private const string CloudApiBase = "https://app.powerview.cloud";
        private const string CloudApiBaseFallback = "https://api.hunterdouglascloud.com";
        private const string AuthEndpoint = "/v1/authentication/login";
        private const string ShadesEndpoint = "/v1/homes/{homeId}/shades";

        public HunterDouglasCloudClient(string email, string password, Action<string> logger = null)
        {
            _email = email;
            _password = password;
            _logger = logger;
            
            // Enable TLS 1.2 for cloud API (TLS 1.3 not available in .NET Framework 4.7.2)
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
            
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true // Accept all certificates
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Authenticate with Hunter Douglas cloud service
        /// </summary>
        public async Task<bool> AuthenticateAsync()
        {
            try
            {
                _logger?.Invoke("Attempting Hunter Douglas cloud authentication...");

                var authBody = new
                {
                    email = _email,
                    password = _password
                };

                var json = JsonConvert.SerializeObject(authBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                
                // Try primary endpoint first, then fallback
                var endpoints = new[] { CloudApiBase, CloudApiBaseFallback };
                bool authenticated = false;
                
                foreach (var baseUrl in endpoints)
                {
                    _logger?.Invoke($"Trying cloud authentication with endpoint: {baseUrl}");
                    
                    try
                    {
                        var response = await _httpClient.PostAsync($"{baseUrl}{AuthEndpoint}", content);

                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Invoke($"Authentication failed at {baseUrl}: {response.StatusCode}");
                            continue;
                        }

                        var responseContent = await response.Content.ReadAsStringAsync();
                        var authResponse = JsonConvert.DeserializeObject<dynamic>(responseContent);

                        _accessToken = authResponse?["accessToken"]?.ToString();
                        _homeId = authResponse?["homes"]?[0]?["id"]?.ToString();

                        if (string.IsNullOrEmpty(_accessToken) || string.IsNullOrEmpty(_homeId))
                        {
                            _logger?.Invoke("Authentication succeeded but token or home ID missing");
                            continue;
                        }

                        _logger?.Invoke($"Successfully authenticated with {baseUrl} (Home ID: {_homeId})");
                        _activeCloudBase = baseUrl;
                        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
                        authenticated = true;
                        break;
                    }
                    catch (HttpRequestException httpEx)
                    {
                        var innerMsg = httpEx.InnerException?.Message ?? "No inner exception";
                        _logger?.Invoke($"Endpoint {baseUrl} unreachable: {httpEx.Message}. Inner: {innerMsg}");
                        continue;
                    }
                    catch (Exception ex)
                    {
                        var innerMsg = ex.InnerException?.Message ?? "No inner exception";
                        _logger?.Invoke($"Error trying endpoint {baseUrl}: {ex.Message}. Inner: {innerMsg}");
                        continue;
                    }
                }
                
                if (!authenticated)
                {
                    _logger?.Invoke("Cloud API endpoints unreachable. This is expected if the cloud service is unavailable. Local shade control will still work.");
                    return false;
                }
                
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Cloud authentication error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Set shade position via cloud API
        /// </summary>
        public async Task<bool> SetShadePositionAsync(int shadeId, decimal positionDecimal)
        {
            try
            {
                if (string.IsNullOrEmpty(_accessToken))
                {
                    _logger?.Invoke("Cloud API not authenticated");
                    return false;
                }

                // Convert decimal 0-1 to cloud API format (typically 0-100 percentage)
                int position = (int)(positionDecimal * 100);

                var url = $"{_activeCloudBase}/v1/homes/{_homeId}/shades/{shadeId}/position";
                var positionBody = new { position = position };
                var json = JsonConvert.SerializeObject(positionBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.PutAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.Invoke($"Successfully set shade {shadeId} to position {position}% via cloud API");
                    return true;
                }
                else
                {
                    _logger?.Invoke($"Cloud API failed to set shade {shadeId}: {response.StatusCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error setting shade position via cloud API: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get all shades from cloud API for reference
        /// </summary>
        public async Task<List<dynamic>> GetShadesAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(_accessToken))
                {
                    _logger?.Invoke("Cloud API not authenticated");
                    return new List<dynamic>();
                }

                var url = $"{_activeCloudBase}{ShadesEndpoint.Replace("{homeId}", _homeId)}";
                var response = await _httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.Invoke($"Cloud API failed to get shades: {response.StatusCode}");
                    return new List<dynamic>();
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                var shades = JsonConvert.DeserializeObject<List<dynamic>>(responseContent);
                return shades ?? new List<dynamic>();
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error getting shades from cloud API: {ex.Message}");
                return new List<dynamic>();
            }
        }

        /// <summary>
        /// Check if cloud client is authenticated
        /// </summary>
        public bool IsAuthenticated => !string.IsNullOrEmpty(_accessToken);
    }
}
