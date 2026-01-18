using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Devices.Controls;
using HomeSeer.PluginSdk.Logging;
using HomeSeer.PluginSdk.Features;
using HomeSeer.PluginSdk.Devices.Identification;
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

        public override string Id => "PowerView";
        public override string Name => "PowerView";

        protected override void Initialize()
        {
            WriteLog(ELogType.Info, "Initializing PowerView Plugin...");

            // Load settings
            var hubIp = GetSetting(SETTING_HUB_IP);
            if (!string.IsNullOrEmpty(hubIp))
            {
                _powerViewClient = new PowerViewClient(hubIp);
                WriteLog(ELogType.Info, $"PowerView Hub configured at {hubIp}");

                // Start polling for shade status updates
                StartPolling();

                // Initial discovery of shades
                Task.Run(async () => await DiscoverShadesAsync());
            }
            else
            {
                WriteLog(ELogType.Warning, "PowerView Hub IP not configured. Please configure in settings.");
            }

            Status = PluginStatus.Ok();
            WriteLog(ELogType.Info, "PowerView Plugin initialized successfully.");
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
                    WriteLog(ELogType.Warning, $"Device not found for ref {controlEvent.TargetRef}");
                    return;
                }

                var plugExtraData = device.PlugExtraData;
                if (!plugExtraData.ContainsNamed("ShadeId"))
                {
                    WriteLog(ELogType.Warning, $"Device {device.Name} does not have ShadeId");
                    return;
                }

                var shadeId = int.Parse(plugExtraData["ShadeId"].ToString());
                var controlValue = controlEvent.ControlValue;

                WriteLog(ELogType.Info, $"Setting shade {shadeId} to position {controlValue}");

                // Convert from percentage (0-100) to PowerView position (0-65535)
                var position = (int)((controlValue / 100.0) * 65535);
                var success = await _powerViewClient.SetShadePositionAsync(shadeId, position);

                if (success)
                {
                    HomeSeerSystem.UpdatePropertyByRef(controlEvent.TargetRef, EProperty.Value, controlValue);
                    WriteLog(ELogType.Info, $"Shade {shadeId} position updated successfully");
                }
                else
                {
                    WriteLog(ELogType.Error, $"Failed to update shade {shadeId} position");
                }
            }
            catch (Exception ex)
            {
                WriteLog(ELogType.Error, $"Error handling control event: {ex.Message}");
            }
        }

        private async Task DiscoverShadesAsync()
        {
            try
            {
                WriteLog(ELogType.Info, "Discovering PowerView shades...");

                var shades = await _powerViewClient.GetShadesAsync();
                WriteLog(ELogType.Info, $"Found {shades.Count} shades");

                foreach (var shade in shades)
                {
                    var shadeName = PowerViewClient.DecodeName(shade.Name);
                    WriteLog(ELogType.Info, $"Processing shade: {shadeName} (ID: {shade.Id})");

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
                WriteLog(ELogType.Error, $"Error discovering shades: {ex.Message}");
            }
        }

        private HsDevice FindDeviceByShadeId(int shadeId)
        {
            var devices = HomeSeerSystem.GetDevicesByInterface(Id);
            foreach (var deviceRef in devices)
            {
                var device = HomeSeerSystem.GetDeviceByRef(deviceRef);
                if (device != null && device.PlugExtraData.ContainsNamed("ShadeId"))
                {
                    var storedShadeId = int.Parse(device.PlugExtraData["ShadeId"].ToString());
                    if (storedShadeId == shadeId)
                    {
                        return device;
                    }
                }
            }
            return null;
        }

        private void CreateShadeDevice(PowerViewShade shade)
        {
            try
            {
                var shadeName = PowerViewClient.DecodeName(shade.Name);
                WriteLog(ELogType.Info, $"Creating device for shade: {shadeName}");

                var deviceData = DeviceFactory.CreateDevice(Id);
                deviceData.Name = shadeName;
                deviceData.Location = "PowerView";
                deviceData.Location2 = "Shades";
                deviceData.Device = DeviceTypeEnum.Generic;

                // Store shade ID in PlugExtraData
                deviceData.PlugExtraData.AddNamed("ShadeId", shade.Id.ToString());

                // Add status graphics
                deviceData.StatusGraphics.Add(new StatusGraphic("/images/HomeSeer/status/off.gif", 0));
                deviceData.StatusGraphics.Add(new StatusGraphic("/images/HomeSeer/status/on.gif", 100));

                // Add status controls for shade position
                var statusControl = new StatusControl(EControlType.TextBoxNumber)
                {
                    Label = "Position",
                    ControlUse = EControlUse.OnAlternate,
                    TargetRange = new ValueRange(0, 100)
                };
                deviceData.StatusControls.Add(statusControl);

                // Add control for opening
                var openControl = new StatusControl(EControlType.Button)
                {
                    Label = "Open",
                    ControlUse = EControlUse.On,
                    TargetValue = 100
                };
                deviceData.StatusControls.Add(openControl);

                // Add control for closing
                var closeControl = new StatusControl(EControlType.Button)
                {
                    Label = "Close",
                    ControlUse = EControlUse.Off,
                    TargetValue = 0
                };
                deviceData.StatusControls.Add(closeControl);

                var devRef = HomeSeerSystem.CreateDevice(deviceData);
                
                // Set initial position
                if (shade.Positions?.Position1 != null)
                {
                    var percentage = (shade.Positions.Position1.Value / 65535.0) * 100;
                    HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.Value, percentage);
                }

                WriteLog(ELogType.Info, $"Created device {shadeName} with ref {devRef}");
            }
            catch (Exception ex)
            {
                WriteLog(ELogType.Error, $"Error creating shade device: {ex.Message}");
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
                WriteLog(ELogType.Error, $"Error updating shade device: {ex.Message}");
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
                WriteLog(ELogType.Info, $"Started polling every {POLL_INTERVAL_SECONDS} seconds");
            }
        }

        private void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Dispose();
                _pollTimer = null;
                WriteLog(ELogType.Info, "Stopped polling");
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
                WriteLog(ELogType.Error, $"Error polling shades: {ex.Message}");
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
                WriteLog(ELogType.Error, $"Error saving setting {key}: {ex.Message}");
            }
        }

        protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView)
        {
            // Settings change handler
            return true;
        }
    }
}
