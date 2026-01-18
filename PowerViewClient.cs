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
    /// </summary>
    public class PowerViewClient
    {
        private readonly string _hubIpAddress;
        private readonly HttpClient _httpClient;
        private const int API_TIMEOUT_SECONDS = 10;

        public PowerViewClient(string hubIpAddress)
        {
            _hubIpAddress = hubIpAddress;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(API_TIMEOUT_SECONDS)
            };
        }

        private string GetBaseUrl()
        {
            return $"http://{_hubIpAddress}/api";
        }

        /// <summary>
        /// Get hub user data (serial number, firmware, etc.)
        /// </summary>
        public async Task<PowerViewUserData> GetUserDataAsync()
        {
            var url = $"{GetBaseUrl()}/userdata";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewUserDataResponse>(response);
            return result?.UserData;
        }

        /// <summary>
        /// Get all shades from the hub
        /// </summary>
        public async Task<List<PowerViewShade>> GetShadesAsync()
        {
            var url = $"{GetBaseUrl()}/shades";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewShadesResponse>(response);
            return result?.ShadeData ?? new List<PowerViewShade>();
        }

        /// <summary>
        /// Get a specific shade by ID
        /// </summary>
        public async Task<PowerViewShade> GetShadeAsync(int shadeId)
        {
            var url = $"{GetBaseUrl()}/shades/{shadeId}";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewShadeResponse>(response);
            return result?.Shade;
        }

        /// <summary>
        /// Set shade position (0-65535, where 0=closed, 65535=open)
        /// </summary>
        public async Task<bool> SetShadePositionAsync(int shadeId, int position)
        {
            try
            {
                var url = $"{GetBaseUrl()}/shades/{shadeId}";
                var positionData = new
                {
                    shade = new
                    {
                        positions = new
                        {
                            position1 = position,
                            posKind1 = 1
                        }
                    }
                };

                var json = JsonConvert.SerializeObject(positionData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
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
            var url = $"{GetBaseUrl()}/scenes";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewScenesResponse>(response);
            return result?.SceneData ?? new List<PowerViewScene>();
        }

        /// <summary>
        /// Activate a scene
        /// </summary>
        public async Task<bool> ActivateSceneAsync(int sceneId)
        {
            try
            {
                var url = $"{GetBaseUrl()}/scenes";
                var sceneData = new
                {
                    sceneId = sceneId
                };

                var json = JsonConvert.SerializeObject(sceneData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PutAsync(url, content);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Get all rooms from the hub
        /// </summary>
        public async Task<List<PowerViewRoom>> GetRoomsAsync()
        {
            var url = $"{GetBaseUrl()}/rooms";
            var response = await _httpClient.GetStringAsync(url);
            var result = JsonConvert.DeserializeObject<PowerViewRoomsResponse>(response);
            return result?.RoomData ?? new List<PowerViewRoom>();
        }

        /// <summary>
        /// Test connection to hub
        /// </summary>
        public async Task<bool> TestConnectionAsync()
        {
            try
            {
                var userData = await GetUserDataAsync();
                return userData != null;
            }
            catch
            {
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
                var bytes = Convert.FromBase64String(base64Name);
                return Encoding.UTF8.GetString(bytes);
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
