using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace HSPI_PowerView
{
    /// <summary>
    /// Client for communicating with Hunter Douglas PowerView Hub REST API
    /// Falls back to cloud API for shade control on Gen3 hubs
    /// </summary>
    public class PowerViewClient
    {
        private readonly string _hubIpAddress;
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logger;
        private readonly HunterDouglasCloudClient _cloudClient;
        private const int API_TIMEOUT_SECONDS = 10;

        public string HubIp => _hubIpAddress;
        public string BaseUrl => $"http://{_hubIpAddress}";
        public HunterDouglasCloudClient CloudClient => _cloudClient;

        public PowerViewClient(string hubIpAddress, Action<string> logger = null, HunterDouglasCloudClient cloudClient = null)
        {
            _hubIpAddress = hubIpAddress;
            _logger = logger;
            _cloudClient = cloudClient;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(API_TIMEOUT_SECONDS)
            };
        }

        private string GetBaseUrl()
        {
            return BaseUrl;
        }

        /// <summary>
        /// Get hub user data (serial number, firmware, etc.)
        /// </summary>
        public async Task<PowerViewUserData> GetUserDataAsync()
        {
            var url = $"{GetBaseUrl()}/api/userdata";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewUserDataResponse>(response);
            return result?.UserData;
        }

        /// <summary>
        /// Get all shades from the hub (from /home endpoint in modern API)
        /// Automatically deduplicates shades by ID to prevent duplicate devices
        /// </summary>
        public async Task<List<PowerViewShade>> GetShadesAsync()
        {
            try
            {
                var url = $"{GetBaseUrl()}/home";
                _logger?.Invoke($"PowerView: Calling GET {url}");
                
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // Check if this is a secondary hub in multi-gateway setup
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        if (errorContent.Contains("Multi-Gateway environment") || errorContent.Contains("not the primary gateway"))
                        {
                            _logger?.Invoke($"This hub ({_hubIpAddress}) is a secondary hub in a multi-gateway setup. Shade discovery is only available on the primary hub. Skipping shade discovery.");
                            return new List<PowerViewShade>();
                        }
                    }
                    
                    _logger?.Invoke($"Failed to get shades: {response.StatusCode}");
                    return new List<PowerViewShade>();
                }
                
                var content = await response.Content.ReadAsStringAsync();
                var homeData = JsonConvert.DeserializeObject<dynamic>(content);
                
                var shades = new List<PowerViewShade>();
                var shadeIdsSeen = new HashSet<int>(); // Track seen shade IDs to prevent duplicates
                
                if (homeData?["gateways"] != null)
                {
                    foreach (var gateway in homeData["gateways"])
                    {
                        // Treat gateway as JObject for robust extraction
                        var gatewayObj = gateway as Newtonsoft.Json.Linq.JObject;
                        // Parse shade IDs from gateway's shd_Ids - can be string or array
                        var shadeIdsObj = gatewayObj?["shd_Ids"] ?? gateway["shd_Ids"];
                        List<string> ids = new List<string>();
                        
                        if (shadeIdsObj is string shadeIdsStr)
                        {
                            // Space-separated string format
                            if (!string.IsNullOrWhiteSpace(shadeIdsStr))
                            {
                                ids.AddRange(shadeIdsStr.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                            }
                        }
                        else if (shadeIdsObj is Newtonsoft.Json.Linq.JArray jarray)
                        {
                            // Array format
                            foreach (var item in jarray)
                            {
                                ids.Add(item.ToString());
                            }
                        }
                        
                        // Fetch details for each shade
                        foreach (var id in ids)
                        {
                            if (int.TryParse(id, out int shadeId))
                            {
                                // Skip if we've already seen this shade ID
                                if (shadeIdsSeen.Contains(shadeId))
                                {
                                    _logger?.Invoke($"Skipping duplicate shade {shadeId} (already discovered from another gateway)");
                                    continue;
                                }
                                
                                try
                                {
                                    var shadeDetail = await GetShadeDetailsAsync(shadeId);
                                    if (shadeDetail != null)
                                    {
                                        // Assign owning gateway IP
                                        string gatewayIp = gatewayObj?.Value<string>("ip") ?? _hubIpAddress;
                                        shadeDetail.GatewayIp = gatewayIp;
                                        shades.Add(shadeDetail);
                                        shadeIdsSeen.Add(shadeId);
                                        // Removed verbose logging: Added shade X from gateway Y
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.Invoke($"Failed to fetch shade {shadeId}: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                
                if (shadeIdsSeen.Count > 0)
                {
                    _logger?.Invoke($"GetShadesAsync returning {shades.Count} unique shades (deduplicated)");
                }
                
                return shades;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error getting shades: {ex.Message}");
                return new List<PowerViewShade>();
            }
        }

        /// <summary>
        /// Get details for a specific shade including position
        /// </summary>
        private async Task<PowerViewShade> GetShadeDetailsAsync(int shadeId)
        {
            var url = $"{GetBaseUrl()}/home/shades/{shadeId}";
            var response = await _httpClient.GetStringAsync(url);
            var data = JsonConvert.DeserializeObject<dynamic>(response);
            
            // Handle cases where data might be wrapped in a "shade" property
            dynamic shadeData = data["shade"] ?? data;
            
            int batteryStatus = 0;
            int batteryStrength = 0;
            try
            {
                // Gen3 API returns batteryStatus (1-4 code), convert to percentage estimate
                // batteryStatus: 1=Low, 2=Medium, 3=High, 4=Plugged In
                if (shadeData["batteryStatus"] != null)
                {
                    batteryStatus = (int)shadeData["batteryStatus"];
                    if (batteryStatus == 4) batteryStrength = 100; // Plugged in
                    else if (batteryStatus == 3) batteryStrength = 75; // High
                    else if (batteryStatus == 2) batteryStrength = 50; // Medium
                    else if (batteryStatus == 1) batteryStrength = 25; // Low
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Shade {shadeId}: Error parsing battery status: {ex.Message}");
                batteryStrength = 0;
            }
            
            // Removed verbose logging: batteryStatus, batteryStrength
            
            // Convert signal strength from dBm (-100 to -30) to percentage (0-100)
            int signalStrengthRaw = -100;
            int signalStrengthPercent = 0;
            try
            {
                if (shadeData["signalStrength"] != null)
                {
                    signalStrengthRaw = (int)shadeData["signalStrength"];
                    signalStrengthPercent = Math.Max(0, Math.Min(100, (signalStrengthRaw + 100) * 100 / 70));
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Shade {shadeId}: Error parsing signal strength: {ex.Message}");
                signalStrengthPercent = 0;
            }
            
            // Removed verbose logging: signalStrength
            
            var shade = new PowerViewShade
            {
                Id = shadeId,
                Name = shadeData["ptName"] ?? shadeData["name"] ?? $"Shade {shadeId}",
                Type = shadeData["type"] != null ? (int)shadeData["type"] : 0,
                BatteryStatus = batteryStatus,
                BatteryStrength = batteryStrength,
                SignalStrength = signalStrengthPercent
            };
            
            // Parse position - try multiple possible locations in the response
            try
            {
                int? positionValue = null;
                
                // Try primary position (Gen3 API format: 0.0-1.0 decimal)
                if (shadeData["positions"]?["primary"] != null)
                {
                    decimal posDecimal = shadeData["positions"]["primary"];
                    positionValue = (int)(posDecimal * 65535);
                    // Removed verbose logging: position parsing
                }
                // Try position1 directly (alternative format)
                else if (shadeData["position1"] != null)
                {
                    positionValue = (int)shadeData["position1"];
                }
                // Try position directly (another alternative)
                else if (shadeData["position"] != null)
                {
                    positionValue = (int)shadeData["position"];
                }
                else
                {
                    _logger?.Invoke($"Shade {shadeId}: No position data found in response");
                }
                
                if (positionValue.HasValue)
                {
                    shade.Positions = new PowerViewPosition
                    {
                        Position1 = positionValue.Value
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Shade {shadeId}: Error parsing position: {ex.Message}");
            }
            
            return shade;
        }

        /// <summary>
        /// Get a specific shade by ID
        /// </summary>
        public async Task<PowerViewShade> GetShadeAsync(int shadeId)
        {
            var url = $"{GetBaseUrl()}/api/shades/{shadeId}";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewShadeResponse>(response);
            return result?.Shade;
        }

        /// <summary>
        /// Set shade position (0-65535, where 0=closed, 65535=open)
        /// Uses motion commands (up/down) as per PowerView API documentation
        /// Falls back to cloud API for Gen3 hubs
        /// </summary>
        public async Task<bool> SetShadePositionAsync(int shadeId, int position)
        {
            try
            {
                _logger?.Invoke($"SetShadePositionAsync: Setting shade {shadeId} to position {position} on hub {_hubIpAddress}");
                
                // Determine motion command based on position
                // 0 = fully closed (down), 65535 = fully open (up)
                string motion;
                if (position == 0)
                {
                    motion = "down";
                }
                else if (position == 65535)
                {
                    motion = "up";
                }
                else
                {
                    // For intermediate positions, use "jog" to move incrementally
                    // This is a limitation - the API doesn't support direct position setting
                    motion = "jog";
                    _logger?.Invoke($"Position {position} is intermediate - using 'jog' command. For precise control, use fully open (100%) or fully closed (0%).");
                }

                // Try motion command API (standard for Gen2 hubs)
                var url = $"{GetBaseUrl()}/api/shades/{shadeId}";
                var body = new
                {
                    shade = new
                    {
                        motion = motion
                    }
                };

                var json = JsonConvert.SerializeObject(body);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                try
                {
                    _logger?.Invoke($"Trying motion command API: PUT {url} with motion='{motion}'");
                    var response = await _httpClient.PutAsync(url, content);
                    _logger?.Invoke($"Motion command API response: {response.StatusCode}");
                    
                    if (response.IsSuccessStatusCode)
                    {
                        _logger?.Invoke($"Successfully sent '{motion}' command to shade {shadeId}");
                        return true;
                    }
                    
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.Invoke($"Motion command failed. Response body: {responseBody}");
                }
                catch (HttpRequestException ex)
                {
                    _logger?.Invoke($"Motion command API HTTP error: {ex.Message}");
                }
                catch (TaskCanceledException ex)
                {
                    _logger?.Invoke($"Motion command API timeout: {ex.Message}");
                }
                catch (Exception ex)
                {
                    _logger?.Invoke($"Motion command API failed: {ex.GetType().Name} - {ex.Message}");
                }

                // Fall back to cloud API if available
                if (_cloudClient != null && _cloudClient.IsAuthenticated)
                {
                    _logger?.Invoke($"Local motion API failed, attempting cloud API for shade {shadeId}...");
                    // Convert position to decimal for cloud API (0.0-1.0)
                    decimal posDecimal = position / 65535.0m;
                    return await _cloudClient.SetShadePositionAsync(shadeId, posDecimal);
                }

                _logger?.Invoke($"Failed to set shade {shadeId}: Local APIs unavailable and cloud API not configured. Configure Hunter Douglas cloud credentials for Gen3 hub control.");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error setting shade {shadeId} position: {ex.GetType().Name} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Open shade completely
        /// </summary>
        public async Task<bool> OpenShadeAsync(int shadeId)
        {
            return await SetShadePositionAsync(shadeId, 65535);
        }

        /// <summary>
        /// Close shade completely
        /// </summary>
        public async Task<bool> CloseShadeAsync(int shadeId)
        {
            return await SetShadePositionAsync(shadeId, 0);
        }

        /// <summary>
        /// Get all scenes from the hub
        /// </summary>
        public async Task<List<PowerViewScene>> GetScenesAsync()
        {
            try
            {
                var url = $"{GetBaseUrl()}/home/scenes";
                _logger?.Invoke($"Fetching scenes from {url}");
                
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    // Check if this is a secondary hub in multi-gateway setup
                    if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        if (errorContent.Contains("Multi-Gateway environment") || errorContent.Contains("not the primary gateway"))
                        {
                            _logger?.Invoke($"This hub ({_hubIpAddress}) is a secondary hub in a multi-gateway setup. Scene discovery is only available on the primary hub. Skipping scene sync.");
                            return new List<PowerViewScene>();
                        }
                    }
                    
                    _logger?.Invoke($"Failed to get scenes: {response.StatusCode}");
                    return new List<PowerViewScene>();
                }
                
                var content = await response.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<dynamic>(content);
                
                var scenes = new List<PowerViewScene>();
                
                // Handle both array response and object with "value" property
                var sceneArray = data as Newtonsoft.Json.Linq.JArray;
                if (sceneArray == null && data?["value"] != null)
                {
                    sceneArray = data["value"] as Newtonsoft.Json.Linq.JArray;
                }
                
                if (sceneArray != null)
                {
                    foreach (var sceneData in sceneArray)
                    {
                        var scene = new PowerViewScene
                        {
                            Id = (int)sceneData["id"],
                            Name = sceneData["name"]?.ToString(),
                            PtName = sceneData["ptName"]?.ToString(),
                            NetworkNumber = sceneData["networkNumber"] != null ? (int)sceneData["networkNumber"] : 0,
                            RoomIds = sceneData["roomIds"]?.ToObject<List<int>>(),
                            ShadeIds = sceneData["shadeIds"]?.ToObject<List<int>>()
                        };
                        scenes.Add(scene);
                    }
                }
                
                _logger?.Invoke($"Retrieved {scenes.Count} scenes from hub");
                return scenes;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error getting scenes: {ex.Message}");
                return new List<PowerViewScene>();
            }
        }

        /// <summary>
        /// Activate a scene by ID (Gen3 endpoint)
        /// </summary>
        public async Task<bool> ActivateSceneAsync(int sceneId)
        {
            try
            {
                // Try a few variants because Gen3 firmware revisions differ on verb/body requirements.
                var attempts = new List<(string name, Func<Task<HttpResponseMessage>> call)>
                {
                    ($"PUT /home/scenes/{sceneId}/activate (empty)", () =>
                        _httpClient.PutAsync($"{GetBaseUrl()}/home/scenes/{sceneId}/activate", null)),
                    ($"PUT /home/scenes/{sceneId}/activate (json body)", () =>
                        _httpClient.PutAsync($"{GetBaseUrl()}/home/scenes/{sceneId}/activate", new StringContent("{}", Encoding.UTF8, "application/json"))),
                    ($"POST /home/scenes/{sceneId}/activate (json body)", () =>
                        _httpClient.PostAsync($"{GetBaseUrl()}/home/scenes/{sceneId}/activate", new StringContent("{}", Encoding.UTF8, "application/json"))),
                    ($"PUT /api/scenes/{sceneId}/activate (json body)", () =>
                        _httpClient.PutAsync($"{GetBaseUrl()}/api/scenes/{sceneId}/activate", new StringContent("{}", Encoding.UTF8, "application/json")))
                };

                foreach (var attempt in attempts)
                {
                    try
                    {
                        _logger?.Invoke($"Activating scene {sceneId} via {attempt.name}");
                        var response = await attempt.call();
                        var content = await response.Content.ReadAsStringAsync();

                        if (response.IsSuccessStatusCode)
                        {
                            _logger?.Invoke($"Scene {sceneId} activated successfully via {attempt.name}: {content}");
                            return true;
                        }

                        _logger?.Invoke($"Scene {sceneId} activation failed via {attempt.name}: {response.StatusCode} Body: {content}");
                    }
                    catch (Exception exAttempt)
                    {
                        _logger?.Invoke($"Scene {sceneId} activation attempt '{attempt.name}' errored: {exAttempt.GetType().Name} - {exAttempt.Message}");
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"Error activating scene {sceneId}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get all rooms from the hub
        /// </summary>
        public async Task<List<PowerViewRoom>> GetRoomsAsync()
        {
            var url = $"{GetBaseUrl()}/api/rooms";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewRoomsResponse>(response);
            return result?.RoomData ?? new List<PowerViewRoom>();
        }

        /// <summary>
        /// <summary>
        /// Test connection to hub
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                // Try to get shades to test connection - simpler endpoint than userdata
                var shades = await GetShadesAsync();
                return true; // If we got here without exception, connection works
            }
            catch (Exception ex)
            {
                _logger?.Invoke($"PowerView connection test failed for {_hubIpAddress}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Decode base64 name from PowerView API
        /// </summary>
        public static string DecodeName(string base64Name)
        {
            if (string.IsNullOrEmpty(base64Name))
                return string.Empty;

            try
            {
                // Only attempt Base64 decode if it looks like Base64
                // Heuristics: length multiple of 4, no spaces, valid chars
                var trimmed = base64Name.Trim();
                if (trimmed.Length % 4 == 0 && !trimmed.Contains(" "))
                {
                    foreach (var ch in trimmed)
                    {
                        if (!(char.IsLetterOrDigit(ch) || ch == '+' || ch == '/' || ch == '='))
                        {
                            return base64Name; // Not Base64-like
                        }
                    }
                    var bytes = Convert.FromBase64String(trimmed);
                    var decoded = Encoding.UTF8.GetString(bytes);
                    // If decoded contains non-printable characters, fallback
                    for (int i = 0; i < decoded.Length; i++)
                    {
                        var c = decoded[i];
                        if (char.IsControl(c) && c != '\n' && c != '\r' && c != '\t')
                        {
                            return base64Name;
                        }
                    }
                    return decoded;
                }
                return base64Name;
            }
            catch
            {
                return base64Name;
            }
        }

        /// <summary>
        /// Encode name to base64 for PowerView API
        /// </summary>
        public static string EncodeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(name);
            return Convert.ToBase64String(bytes);
        }
    }
}
