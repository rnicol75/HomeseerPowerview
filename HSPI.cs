using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Devices.Controls;
using HomeSeer.PluginSdk.Devices.Identification;
using HomeSeer.PluginSdk.Logging;
using HomeSeer.Jui.Views;

namespace HSPI_PowerView
{
    /// <summary>
    /// Hunter Douglas PowerView Plugin for HomeSeer 4
    /// </summary>
    public class HSPI : AbstractPlugin
    {
        private PowerViewClient _powerViewClient;
        private System.Threading.Timer _pollTimer;
        private const int POLL_INTERVAL_SECONDS = 30;
        private const string SETTING_HUB_IP = "HubIP";

        public override string Id { get; } = "PowerView";
        public override string Name { get; } = "PowerView";
        protected override string SettingsFileName { get; } = "PowerView.ini";

        protected override void Initialize()
        {
            HomeSeerSystem.WriteLog(ELogType.Info, "Initializing PowerView Plugin...", Name);

            // Log assembly version and build timestamp to confirm correct binary is loaded
            try
            {
                var asm = typeof(HSPI).Assembly;
                var ver = asm.GetName().Version?.ToString() ?? "unknown";
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
                var fileVer = fvi?.FileVersion ?? "unknown";
                var ts = System.IO.File.GetLastWriteTime(asm.Location);
                HomeSeerSystem.WriteLog(ELogType.Info, $"PowerView Plugin version {ver} (file {fileVer}) built {ts:yyyy-MM-dd HH:mm:ss}", Name);
            }
            catch { /* best-effort version logging */ }

            // Load settings
            var hubIp = GetSetting(SETTING_HUB_IP);
            if (!string.IsNullOrEmpty(hubIp))
            {
                _powerViewClient = new PowerViewClient(hubIp);
                HomeSeerSystem.WriteLog(ELogType.Info, $"PowerView Hub configured at {hubIp}", Name);

                // Test connection to hub
                Task.Run(async () => 
                {
                    var connected = await _powerViewClient.TestConnectionAsync();
                    if (connected)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, "Successfully connected to PowerView Hub", Name);
                        // Start polling for shade status updates
                        StartPolling();
                        // Initial discovery of shades
                        await DiscoverShadesAsync();
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, "Failed to connect to PowerView Hub", Name);
                    }
                });
            }
            else
            {
                HomeSeerSystem.WriteLog(ELogType.Warning, "PowerView Hub IP not configured. Please configure in settings.", Name);
            }

            Status = PluginStatus.Ok();
            HomeSeerSystem.WriteLog(ELogType.Info, "PowerView Plugin initialized successfully.", Name);
        }

        protected override void BeforeReturnStatus()
        {
            // Called before returning status
        }

        public override void SetIOMulti(List<ControlEvent> colSend)
        {
            foreach (var controlEvent in colSend)
            {
                Task.Run(async () => await HandleControlEventAsync(controlEvent));
            }
        }

        private async Task HandleControlEventAsync(ControlEvent controlEvent)
        {
            try
            {
                var device = HomeSeerSystem.GetDeviceByRef(controlEvent.TargetRef);
                if (device == null)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Device not found for ref {controlEvent.TargetRef}", Name);
                    return;
                }

                var plugExtraData = device.PlugExtraData;
                if (!plugExtraData.ContainsNamed("ShadeId"))
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Device {device.Name} does not have ShadeId", Name);
                    return;
                }

                var shadeId = int.Parse(plugExtraData["ShadeId"].ToString());
                var controlValue = controlEvent.ControlValue;

                HomeSeerSystem.WriteLog(ELogType.Info, $"Setting shade {shadeId} to position {controlValue}", Name);

                // Convert from percentage (0-100) to PowerView position (0-65535)
                var position = (int)((controlValue / 100.0) * 65535);
                var success = await _powerViewClient.SetShadePositionAsync(shadeId, position);

                if (success)
                {
                    HomeSeerSystem.UpdatePropertyByRef(controlEvent.TargetRef, EProperty.Value, controlValue);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shadeId} position updated successfully", Name);
                }
                else
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to update shade {shadeId} position", Name);
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error handling control event: {ex.Message}", Name);
            }
        }

        private async Task DiscoverShadesAsync()
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, "Discovering PowerView shades...", Name);

                var shades = await _powerViewClient.GetShadesAsync();
                HomeSeerSystem.WriteLog(ELogType.Info, $"Found {shades.Count} shades", Name);

                foreach (var shade in shades)
                {
                    var shadeName = PowerViewClient.DecodeName(shade.Name);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Processing shade: {shadeName} (ID: {shade.Id})", Name);

                    // Check if device already exists
                    var existingDevice = FindDeviceByShadeId(shade.Id);
                    if (existingDevice == null)
                    {
                        CreateShadeDevice(shade);
                    }
                    else
                    {
                        UpdateShadeDevice(existingDevice, shade);
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error discovering shades: {ex.Message}", Name);
            }
        }

        private HsDevice FindDeviceByShadeId(int shadeId)
        {
            try
            {
                // Search all devices by ref, starting from 1
                for (int ref_num = 1; ref_num < 10000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null)
                        continue;

                    if (device.Interface == Id && device.PlugExtraData.ContainsNamed("ShadeId"))
                    {
                        var storedShadeId = int.Parse(device.PlugExtraData["ShadeId"].ToString());
                        if (storedShadeId == shadeId)
                        {
                            return device;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error finding device by shade ID: {ex.Message}", Name);
            }
            return null;
        }

        private void CreateShadeDevice(PowerViewShade shade)
        {
            try
            {
                var shadeName = PowerViewClient.DecodeName(shade.Name);
                HomeSeerSystem.WriteLog(ELogType.Info, $"Creating device for shade: {shadeName}", Name);

                // Use DeviceFactory to create the device
                var df = DeviceFactory.CreateDevice(Id);
                df = df.WithName(shadeName)
                       .WithLocation("PowerView")
                       .WithLocation2("Shades");

                // Create a generic dimmable control feature (0-100%)
                var ff = FeatureFactory.CreateGenericBinaryControl(Id, "Shade Control", "Open", "Close", 100, 0)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");
                df.WithFeature(ff);

                // Get the NewDeviceData
                var deviceData = df.PrepareForHs();

                // Create the device and get its reference
                var devRef = HomeSeerSystem.CreateDevice(deviceData);
                
                // After creation, update with extra data
                var extraData = HomeSeerSystem.GetPropertyByRef(devRef, EProperty.PlugExtraData);
                if (extraData == null)
                {
                    extraData = new PlugExtraData();
                }
                var extraDataObj = extraData as PlugExtraData;
                if (extraDataObj != null)
                {
                    extraDataObj.AddNamed("ShadeId", shade.Id.ToString());
                    HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.PlugExtraData, extraDataObj);
                }
                
                // Set initial position
                if (shade.Positions?.Position1 != null)
                {
                    var percentage = (shade.Positions.Position1.Value / 65535.0) * 100;
                    HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.Value, percentage);
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Created device {shadeName} with ref {devRef}", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error creating shade device: {ex.Message}", Name);
            }
        }

        private void UpdateShadeDevice(HsDevice device, PowerViewShade shade)
        {
            try
            {
                if (shade.Positions?.Position1 != null)
                {
                    var percentage = (shade.Positions.Position1.Value / 65535.0) * 100;
                    HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.Value, percentage);
                }

                // Update battery status if available
                if (shade.BatteryStrength > 0)
                {
                    device.PlugExtraData.AddNamed("BatteryLevel", shade.BatteryStrength.ToString());
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error updating shade device: {ex.Message}", Name);
            }
        }

        private void StartPolling()
        {
            if (_powerViewClient != null && _pollTimer == null)
            {
                _pollTimer = new System.Threading.Timer(
                    async _ => await PollShadesAsync(),
                    null,
                    TimeSpan.FromSeconds(POLL_INTERVAL_SECONDS),
                    TimeSpan.FromSeconds(POLL_INTERVAL_SECONDS)
                );
                HomeSeerSystem.WriteLog(ELogType.Info, $"Started polling every {POLL_INTERVAL_SECONDS} seconds", Name);
            }
        }

        private void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
                HomeSeerSystem.WriteLog(ELogType.Info, "Stopped polling", Name);
            }
        }

        private async Task PollShadesAsync()
        {
            try
            {
                var shades = await _powerViewClient.GetShadesAsync();
                foreach (var shade in shades)
                {
                    var device = FindDeviceByShadeId(shade.Id);
                    if (device != null)
                    {
                        UpdateShadeDevice(device, shade);
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error polling shades: {ex.Message}", Name);
            }
        }

        private string GetSetting(string key)
        {
            try
            {
                return HomeSeerSystem.GetINISetting("Settings", key, string.Empty, Id + ".ini");
            }
            catch
            {
                return string.Empty;
            }
        }

        private void SaveSetting(string key, string value)
        {
            try
            {
                HomeSeerSystem.SaveINISetting("Settings", key, value, Id + ".ini");
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error saving setting {key}: {ex.Message}", Name);
            }
        }

        protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView)
        {
            try
            {
                // Handle PowerView Hub IP configuration change
                if (pageId == "settings")
                {
                    // Settings have been updated, reload configuration
                    var hubIp = GetSetting(SETTING_HUB_IP);
                    
                    if (!string.IsNullOrEmpty(hubIp))
                    {
                        // Reinitialize client with hub IP (may have changed)
                        _powerViewClient = new PowerViewClient(hubIp);
                        HomeSeerSystem.WriteLog(ELogType.Info, $"PowerView Hub configuration: {hubIp}", Name);
                        
                        // Test connection and discover shades
                        Task.Run(async () =>
                        {
                            var connected = await _powerViewClient.TestConnectionAsync();
                            if (connected)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Info, "Connected to PowerView Hub", Name);
                                StartPolling();
                                await DiscoverShadesAsync();
                            }
                            else
                            {
                                HomeSeerSystem.WriteLog(ELogType.Error, "Failed to connect to PowerView Hub at " + hubIp, Name);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in OnSettingChange: {ex.Message}", Name);
                return false;
            }
            
            return true;
        }
    }
}
