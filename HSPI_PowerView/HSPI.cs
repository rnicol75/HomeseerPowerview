using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HomeSeer.PluginSdk;
using HomeSeer.PluginSdk.Devices;
using HomeSeer.PluginSdk.Devices.Controls;
using HomeSeer.PluginSdk.Devices.Identification;
using HomeSeer.PluginSdk.Logging;
using HomeSeer.Jui.Views;
using HomeSeer.Jui.Types;
using Page = HomeSeer.Jui.Views.Page;

namespace HSPI_PowerView
{
    /// <summary>
    /// Hunter Douglas PowerView Plugin for HomeSeer 4
    /// </summary>
    public class HSPI : AbstractPlugin
    {
        private readonly List<PowerViewClient> _clients = new List<PowerViewClient>();
        private readonly Dictionary<string, System.Threading.Timer> _pollTimers = new Dictionary<string, System.Threading.Timer>();
        private readonly Dictionary<string, DateTime> _lastSceneSync = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private HunterDouglasCloudClient _cloudClient;
        private const int POLL_INTERVAL_SECONDS = 30;
        private const int SCENE_SYNC_INTERVAL_SECONDS = 300; // 5 minutes
        private const string SETTING_HUB_IP = "HubIP";
        private const string SETTING_HUB_IPS = "HubIPs";
        private bool _verboseLogging = false;
        private PowerViewClient _primaryClient;
        private string _primaryHubIp;
        private bool _initialDiscoveryDone = false;
        private bool _discoveryInProgress = false;

        public override string Id { get; } = "PowerView";
        public override string Name { get; } = "PowerView";
        protected override string SettingsFileName { get; } = "PowerView.ini";
        public override bool HasSettings => true;
        public override bool SupportsConfigDevice => false;

        protected override void BeforeReturnStatus()
        {
            // No pre-status work required
        }

        protected override void Initialize()
        {
            HomeSeerSystem.WriteLog(ELogType.Info, "PowerView Plugin v1.0.1 Initializing", Name);

            // Optional verbose logging flag
            var verboseFlag = HomeSeerSystem.GetINISetting("Settings", "VerboseLogging", "false", SettingsFileName);
            _verboseLogging = verboseFlag.Equals("true", StringComparison.OrdinalIgnoreCase) || verboseFlag == "1";
            if (_verboseLogging)
            {
                HomeSeerSystem.WriteLog(ELogType.Info, "Verbose logging is ENABLED", Name);
            }

            // Cloud login disabled (not supported in current environment)
            _cloudClient = null;

            var hubIps = GetHubIps();
            if (hubIps.Count == 0)
            {
                HomeSeerSystem.WriteLog(ELogType.Warning, "PowerView Hub IP not configured. Please configure in settings (HubIPs or HubIP).", Name);
            }
            else
            {
                // Create all clients first
                foreach (var hubIp in hubIps)
                {
                    try
                    {
                        var client = new PowerViewClient(hubIp, msg => HomeSeerSystem.WriteLog(ELogType.Info, msg, Name), _cloudClient);
                        _clients.Add(client);
                        HomeSeerSystem.WriteLog(ELogType.Info, $"PowerView Hub configured at {hubIp}", Name);
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error configuring hub {hubIp}: {ex.Message}", Name);
                    }
                }

                // Set primary references
                _primaryClient = _clients.FirstOrDefault();
                _primaryHubIp = hubIps.FirstOrDefault();

                // Run initial cleanup and discovery on first startup ONLY
                // Do NOT re-run scene sync on subsequent HomeSeer restarts to preserve scene refIDs
                if (_primaryClient != null && !_initialDiscoveryDone)
                {
                    // Use immediate execution with logging instead of Task.Run to ensure it completes
                    HomeSeerSystem.WriteLog(ELogType.Info, "Queueing initial discovery for all hubs...", Name);
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100); // Brief delay to let plugin fully initialize
                        _discoveryInProgress = true;
                        
                        try
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, "Starting initial discovery for all hubs", Name);
                            
                            if (_clients.Count == 0)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Error, "CRITICAL: No clients configured, cannot proceed with discovery", Name);
                                return;
                            }
                            
                            // IMPORTANT: Only discover shades from primary hub - secondary hub doesn't return complete information
                            if (_primaryClient != null)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Discovering shades from primary hub {_primaryHubIp}", Name);
                                
                                try
                                {
                                    // Discover shades first (creates status devices)
                                    await DiscoverShadesAsync(_primaryClient, _primaryHubIp);
                                    HomeSeerSystem.WriteLog(ELogType.Info, $"Shades discovered from primary hub {_primaryHubIp}", Name);
                                    
                                    // Clean up any duplicate shade devices immediately
                                    await Task.Delay(500); // Brief delay
                                    GlobalCleanupStatusDevices();
                                }
                                catch (Exception shadeEx)
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Error, $"Shade discovery failed for primary hub: {shadeEx.Message}", Name);
                                }
                            }
                            
                            // Sync scenes from PRIMARY HUB ONLY - secondary hub doesn't have complete information
                            if (_primaryClient != null)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Syncing scenes from primary hub {_primaryHubIp}", Name);
                                
                                try
                                {
                                    // Delete ALL scene devices before re-syncing to prevent duplicates
                                    DeleteAllSceneDevicesForHub(_primaryHubIp);
                                    
                                    await SyncScenesAsync(_primaryClient, _primaryHubIp);
                                    _lastSceneSync[_primaryHubIp] = DateTime.UtcNow;
                                    HomeSeerSystem.WriteLog(ELogType.Info, $"Scenes synced from primary hub {_primaryHubIp}", Name);
                                    
                                    // Re-link scenes to existing shades
                                    await RelinkScenesToExistingShades(_primaryHubIp);
                                }
                                catch (Exception sceneEx)
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Error, $"Scene sync failed for primary hub: {sceneEx.Message}", Name);
                                }
                            }
                            
                            // Clean up duplicates AFTER discovery creates devices
                            await Task.Delay(2000); // Give devices time to be created
                            GlobalCleanupStatusDevices();
                            
                            _initialDiscoveryDone = true;
                            _discoveryInProgress = false;
                            HomeSeerSystem.WriteLog(ELogType.Info, "Initial discovery complete", Name);
                        }
                        catch (Exception ex)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Error, $"CRITICAL: Error during initial discovery: {ex.Message}", Name);
                            HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
                            _discoveryInProgress = false;
                        }
                    }); // Remove ConfigureAwait to keep Task alive

                    // Start polling AFTER initial discovery completes
                    // This prevents polling from interfering with scene linking and causing task cancellations
                    _ = Task.Run(async () =>
                    {
                        // Wait for discovery to complete
                        int maxWaitSeconds = 600; // 10 minute max wait
                        int elapsedSeconds = 0;
                        while (_discoveryInProgress && elapsedSeconds < maxWaitSeconds)
                        {
                            await Task.Delay(1000);
                            elapsedSeconds++;
                        }
                        
                        if (_discoveryInProgress)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Warning, "Discovery did not complete within 10 minutes, starting polling anyway", Name);
                            _discoveryInProgress = false;
                        }
                        
                        // Now start polling
                        foreach (var client in _clients)
                        {
                            StartPollingForHub(client.HubIp, client);
                        }
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Started polling for all {_clients.Count} hub(s). Use Settings page to re-discover devices if needed.", Name);
                    });
                }
            }
        }

        protected override void OnSettingsLoad()
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, "OnSettingsLoad called - building settings page", Name);
                
                // Remove existing settings page if it exists
                var existingPage = Settings.Pages.FirstOrDefault(p => p.Id == "settings");
                if (existingPage != null)
                {
                    Settings.Pages.Remove(existingPage);
                }
                
                // Build settings page with discover button
                var page = HomeSeer.Jui.Views.PageFactory.CreateGenericPage("settings", "PowerView Settings");
                
                // Status info
                var statusMsg = _initialDiscoveryDone 
                    ? "Initial discovery completed. Click button below to re-scan if needed."
                    : "Initial discovery in progress...";
                var status = new LabelView("status", "Status", statusMsg);
                page = page.WithView(status);
                
                // Instructions
                var instructions = new LabelView("instructions", "Instructions", 
                    "Click the Discover Devices button to scan the PowerView hub and create/update all shade, scene, and status devices. " +
                    "This will REMOVE ALL existing PowerView devices first to eliminate duplicates.");
                page = page.WithView(instructions);
                
                // Discover button - use ToggleView 
                var discoverToggle = new ToggleView("discover_button", "Discover Devices");
                page = page.WithView(discoverToggle);
                
                Settings.Add(page.Page);
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Settings page created with {page.Page.Views.Count} views", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error creating settings page: {ex.Message}", Name);
            }
        }

        public override string GetJuiDeviceConfigPage(int deviceOrFeatureRef)
        {
            // No device-specific configuration needed
            return string.Empty;
        }

        protected override bool OnSettingChange(string pageId, AbstractView currentView, AbstractView changedView)
        {
            HomeSeerSystem.WriteLog(ELogType.Info, $"OnSettingChange called: pageId={pageId}, changedView.Id={changedView?.Id}, changedView.Type={changedView?.GetType().Name}", Name);
            
            // Handle discover button toggle
            if (changedView != null && changedView.Id == "discover_button")
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Discover button clicked!", Name);
                
                // Check if discovery is already running
                if (_discoveryInProgress)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, "Discovery already in progress, ignoring manual discovery request", Name);
                    return false;
                }
                
                // Trigger manual discovery
                Task.Run(async () =>
                {
                    try
                    {
                        _discoveryInProgress = true;
                        HomeSeerSystem.WriteLog(ELogType.Info, "Manual device discovery started for all hubs...", Name);
                        
                        // Stop all polling during manual discovery to avoid interference
                        HomeSeerSystem.WriteLog(ELogType.Info, "Stopping polling timers...", Name);
                        StopPolling();
                        await Task.Delay(1000); // Give polling a moment to stop
                        
                        // DELETE ALL existing PowerView devices first to eliminate duplicates and start with a clean slate
                        DeleteAllPowerViewDevices();
                        
                        // Wait a moment for deletions to complete
                        await Task.Delay(1000);
                        
                        // IMPORTANT: Only discover shades from primary hub - secondary hub doesn't return complete information
                        // But we still need to sync scenes from all hubs
                        if (_primaryClient != null)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Discovering shades from PRIMARY hub {_primaryHubIp}...", Name);
                            
                            try
                            {
                                HomeSeerSystem.WriteLog(ELogType.Info, "Discovering shades from primary hub...", Name);
                                await DiscoverShadesAsync(_primaryClient, _primaryHubIp);
                                HomeSeerSystem.WriteLog(ELogType.Info, "Shade discovery complete from primary hub", Name);
                            }
                            catch (Exception ex)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Error, $"Error discovering shades from primary hub: {ex.Message}", Name);
                                HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
                            }
                        }
                        
                        // Sync scenes from PRIMARY HUB ONLY - secondary hub doesn't have complete information
                        if (_primaryClient != null)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Syncing scenes from PRIMARY hub {_primaryHubIp}...", Name);
                            
                            try
                            {
                                // Delete ALL scene devices before re-syncing to prevent duplicates
                                DeleteAllSceneDevicesForHub(_primaryHubIp);
                                
                                HomeSeerSystem.WriteLog(ELogType.Info, "About to sync scenes for primary hub " + _primaryHubIp, Name);
                                await SyncScenesAsync(_primaryClient, _primaryHubIp);
                                HomeSeerSystem.WriteLog(ELogType.Info, "Completed scene sync for primary hub " + _primaryHubIp, Name);
                                _lastSceneSync[_primaryHubIp] = DateTime.UtcNow;
                            }
                            catch (Exception ex)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Error, $"Error syncing scenes from primary hub: {ex.Message}", Name);
                                HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
                            }
                        }
                        
                        HomeSeerSystem.WriteLog(ELogType.Info, "Manual device discovery complete.", Name);
                        _discoveryInProgress = false;
                        
                        // Restart polling after discovery is complete (only primary hub)
                        HomeSeerSystem.WriteLog(ELogType.Info, "Restarting polling timers...", Name);
                        if (_primaryClient != null)
                        {
                            StartPollingForHub(_primaryHubIp, _primaryClient);
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error during manual discovery: {ex.Message}", Name);
                        _discoveryInProgress = false;
                        
                        // Ensure polling is restarted even if discovery fails
                        try
                        {
                            foreach (var client in _clients)
                            {
                                StartPollingForHub(client.HubIp, client);
                            }
                        }
                        catch (Exception ex2)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Error, $"Error restarting polling after discovery failure: {ex2.Message}", Name);
                        }
                    }
                });
                
                // Return true to keep dialog open while discovery runs
                return true;
            }
            
            // For any other change (like Cancel button), return false to close the dialog
            HomeSeerSystem.WriteLog(ELogType.Info, $"Closing settings page (Cancel button)", Name);
            return false;
        }

        public override void SetIOMulti(List<ControlEvent> colSend)
        {
            HomeSeerSystem.WriteLog(ELogType.Info, $"SetIOMulti called with {colSend?.Count ?? 0} control events", Name);
            foreach (var controlEvent in colSend)
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Processing control event: TargetRef={controlEvent.TargetRef}, ControlValue={controlEvent.ControlValue}, ControlString={controlEvent.ControlString}", Name);
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

                // Ignore controls sent to read-only status devices (Position/Battery siblings)
                try
                {
                    var devPed = HomeSeerSystem.GetPropertyByRef(device.Ref, EProperty.PlugExtraData) as PlugExtraData;
                    if (devPed != null && devPed.ContainsNamed("StatusType"))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Ignoring control for read-only status device {device.Name} (ref {device.Ref})", Name);
                        return;
                    }
                }
                catch { }

                // Resolve parent device (features carry controls; parent carries metadata)
                HsDevice parentDevice = device;
                if (device.Relationship == ERelationship.Feature)
                {
                    var parentRef = device.AssociatedDevices.FirstOrDefault();
                    parentDevice = HomeSeerSystem.GetDeviceByRef(parentRef);
                    if (parentDevice == null)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Parent device not found for feature {device.Name} (ref {controlEvent.TargetRef}), assoc {parentRef}", Name);
                        return;
                    }
                }

                // Reload PlugExtraData from HS to ensure we see recent backfills
                var plugExtraData = HomeSeerSystem.GetPropertyByRef(parentDevice.Ref, EProperty.PlugExtraData) as PlugExtraData;
                var featureExtra = HomeSeerSystem.GetPropertyByRef(device.Ref, EProperty.PlugExtraData) as PlugExtraData;

                string DumpExtra(PlugExtraData ed)
                {
                    if (ed == null) return "<null>";
                    var entries = new List<string>();
                    if (ed.ContainsNamed("ShadeId")) entries.Add($"ShadeId={ed["ShadeId"]}");
                    if (ed.ContainsNamed("HubIp")) entries.Add($"HubIp={ed["HubIp"]}");
                    return entries.Count == 0 ? "<empty>" : string.Join(", ", entries);
                }

                LogVerbose($"Control event refs device={device.Ref} ({device.Name}) rel={device.Relationship}; parent={parentDevice.Ref} ({parentDevice.Name}); featureExtra=[{DumpExtra(featureExtra)}]; parentExtra=[{DumpExtra(plugExtraData)}]");

                // If this is a Scene device (SceneId present), activate the scene directly
                var scenePed = plugExtraData ?? new PlugExtraData();

                // If this looks like a scene device (Location2 "Scenes") but is missing SceneId,
                // attempt to resolve from INI, alias, or hub scene list before giving up
                if (!scenePed.ContainsNamed("SceneId") && (featureExtra == null || !featureExtra.ContainsNamed("SceneId")))
                {
                    if (string.Equals(parentDevice.Location2, "Scenes", StringComparison.OrdinalIgnoreCase) || string.Equals(device.Location2, "Scenes", StringComparison.OrdinalIgnoreCase))
                    {
                        bool resolved = false;

                        // 1) Try INI reverse mapping
                        if (TryPopulateSceneFromIni(parentDevice.Ref, scenePed, out var recoveredSceneId, out var recoveredHubIp))
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Recovered SceneId={recoveredSceneId}, HubIp={recoveredHubIp} from INI for scene device {parentDevice.Name}", Name);
                            var featurePed = featureExtra ?? new PlugExtraData();
                            featurePed.AddNamed("SceneId", recoveredSceneId.ToString());
                            if (!string.IsNullOrEmpty(recoveredHubIp)) featurePed.AddNamed("HubIp", recoveredHubIp);
                            HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, scenePed);
                            HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                            featureExtra = featurePed;
                            resolved = true;
                        }

                        // 2) Try alias mapping
                        if (!resolved && TryResolveSceneAliasFromIni(parentDevice.Name, out var aliasSceneId, out var aliasHubIp))
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Resolved scene alias for '{parentDevice.Name}' -> {aliasHubIp}:{aliasSceneId}", Name);
                            scenePed.AddNamed("SceneId", aliasSceneId.ToString());
                            if (!string.IsNullOrEmpty(aliasHubIp)) scenePed.AddNamed("HubIp", aliasHubIp);
                            var featurePed = featureExtra ?? new PlugExtraData();
                            featurePed.AddNamed("SceneId", aliasSceneId.ToString());
                            if (!string.IsNullOrEmpty(aliasHubIp)) featurePed.AddNamed("HubIp", aliasHubIp);
                            HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, scenePed);
                            HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                            featureExtra = featurePed;
                            if (!string.IsNullOrEmpty(aliasHubIp))
                                HomeSeerSystem.SaveINISetting("Scenes", $"{aliasHubIp}:{aliasSceneId}", parentDevice.Ref.ToString(), Id + ".ini");
                            resolved = true;
                        }

                        // 3) Try hub scene list name match
                        if (!resolved)
                        {
                            try
                            {
                                var ips = GetHubIps();
                                var hubForScene = ips.FirstOrDefault();
                                var sceneClientForLookup = !string.IsNullOrEmpty(hubForScene) ? GetClientByHubIp(hubForScene) : null;
                                if (sceneClientForLookup != null)
                                {
                                    var scenes = await sceneClientForLookup.GetScenesAsync();
                                    var matched = scenes.FirstOrDefault(s => string.Equals(s.PtName ?? s.Name, parentDevice.Name, StringComparison.OrdinalIgnoreCase));
                                    if (matched != null)
                                    {
                                        scenePed.AddNamed("SceneId", matched.Id.ToString());
                                        scenePed.AddNamed("HubIp", hubForScene);
                                        var featurePed = featureExtra ?? new PlugExtraData();
                                        featurePed.AddNamed("SceneId", matched.Id.ToString());
                                        featurePed.AddNamed("HubIp", hubForScene);
                                        HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, scenePed);
                                        HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                                        featureExtra = featurePed;
                                        HomeSeerSystem.SaveINISetting("Scenes", $"{hubForScene}:{matched.Id}", parentDevice.Ref.ToString(), Id + ".ini");
                                        HomeSeerSystem.WriteLog(ELogType.Info, $"Resolved scene '{parentDevice.Name}' by name to ID {matched.Id}", Name);
                                        resolved = true;
                                    }
                                }
                            }
                            catch (Exception resolveEx)
                            {
                                HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene resolution failed for {parentDevice.Name}: {resolveEx.Message}", Name);
                            }
                        }

                        if (!resolved)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene control requested but SceneId missing and not found in INI, alias, or hub list for {parentDevice.Name}", Name);
                            return;
                        }
                    }
                }

                if ((scenePed.ContainsNamed("SceneId") || (featureExtra != null && featureExtra.ContainsNamed("SceneId"))))
                {
                    int sceneIdVal = 0;
                    string hubForScene = null;

                    if (featureExtra != null && featureExtra.ContainsNamed("SceneId"))
                    {
                        int.TryParse(featureExtra["SceneId"].ToString(), out sceneIdVal);
                        if (featureExtra.ContainsNamed("HubIp")) hubForScene = featureExtra["HubIp"].ToString();
                    }
                    if (sceneIdVal == 0 && scenePed.ContainsNamed("SceneId"))
                    {
                        int.TryParse(scenePed["SceneId"].ToString(), out sceneIdVal);
                        if (scenePed.ContainsNamed("HubIp")) hubForScene = scenePed["HubIp"].ToString();
                    }

                    if (sceneIdVal == 0)
                    {
                        // Try to resolve by name from the hub scenes list
                        try
                        {
                            var ips = GetHubIps();
                            hubForScene = hubForScene ?? ips.FirstOrDefault();
                            // First, try alias mapping from INI
                            if (TryResolveSceneAliasFromIni(parentDevice.Name, out var aliasSceneId, out var aliasHubIp))
                            {
                                sceneIdVal = aliasSceneId;
                                hubForScene = aliasHubIp ?? hubForScene;
                                scenePed.AddNamed("SceneId", sceneIdVal.ToString());
                                if (!string.IsNullOrEmpty(hubForScene)) scenePed.AddNamed("HubIp", hubForScene);
                                HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, scenePed);

                                var featurePed = featureExtra ?? new PlugExtraData();
                                featurePed.AddNamed("SceneId", sceneIdVal.ToString());
                                if (!string.IsNullOrEmpty(hubForScene)) featurePed.AddNamed("HubIp", hubForScene);
                                HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                                featureExtra = featurePed;

                                if (!string.IsNullOrEmpty(hubForScene))
                                {
                                    HomeSeerSystem.SaveINISetting("Scenes", $"{hubForScene}:{sceneIdVal}", parentDevice.Ref.ToString(), Id + ".ini");
                                }
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Resolved scene '{parentDevice.Name}' via alias to ID {sceneIdVal} on hub {hubForScene}", Name);
                            }
                            else
                            {
                            if (hubForScene != null)
                            {
                                var sceneClientForLookup = GetClientByHubIp(hubForScene);
                                if (sceneClientForLookup == null)
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene name resolution failed for {parentDevice.Name}: no client for hub {hubForScene}", Name);
                                }
                                else
                                {
                                    var scenes = await sceneClientForLookup.GetScenesAsync();
                                    var matched = scenes.FirstOrDefault(s => string.Equals(s.PtName ?? s.Name, parentDevice.Name, StringComparison.OrdinalIgnoreCase));
                                    if (matched != null)
                                    {
                                        sceneIdVal = matched.Id;
                                        scenePed.AddNamed("SceneId", sceneIdVal.ToString());
                                        scenePed.AddNamed("HubIp", hubForScene);
                                        HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, scenePed);

                                        var featurePed = featureExtra ?? new PlugExtraData();
                                        featurePed.AddNamed("SceneId", sceneIdVal.ToString());
                                        featurePed.AddNamed("HubIp", hubForScene);
                                        HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                                        featureExtra = featurePed;

                                        HomeSeerSystem.SaveINISetting("Scenes", $"{hubForScene}:{sceneIdVal}", parentDevice.Ref.ToString(), Id + ".ini");
                                        HomeSeerSystem.WriteLog(ELogType.Info, $"Resolved scene '{parentDevice.Name}' by name to ID {sceneIdVal} and cached in INI", Name);
                                    }
                                    else
                                    {
                                        var sceneNames = string.Join(", ", scenes.Select(s => s.PtName ?? s.Name));
                                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene name resolution could not find '{parentDevice.Name}' on hub {hubForScene}. Available: {sceneNames}", Name);
                                    }
                                }
                            }
                            }
                        }
                        catch (Exception resolveEx)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene name resolution failed for {parentDevice.Name}: {resolveEx.Message}", Name);
                        }
                    }

                    if (sceneIdVal == 0)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Scene control requested but SceneId missing and not found in INI or hub list for {parentDevice.Name}", Name);
                        return;
                    }
                    if (string.IsNullOrEmpty(hubForScene))
                    {
                        var ips = GetHubIps();
                        hubForScene = ips.FirstOrDefault();
                    }
                    var sceneClient = GetClientByHubIp(hubForScene);
                    if (sceneClient == null)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"No client for hub {hubForScene} to activate scene {sceneIdVal}", Name);
                        return;
                    }

                    HomeSeerSystem.WriteLog(ELogType.Info, $"Activating scene {sceneIdVal} on hub {hubForScene} for device {parentDevice.Name}", Name);
                    var ok = await sceneClient.ActivateSceneAsync(sceneIdVal);
                    if (ok)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Scene {sceneIdVal} activated successfully for {parentDevice.Name}", Name);
                        HomeSeerSystem.UpdatePropertyByRef(controlEvent.TargetRef, EProperty.Value, 100);
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to activate scene {sceneIdVal} for {parentDevice.Name}", Name);
                    }
                    return;
                }

                // If parent missing ShadeId but feature has it, copy over
                if ((plugExtraData == null || !plugExtraData.ContainsNamed("ShadeId")) && featureExtra != null && featureExtra.ContainsNamed("ShadeId"))
                {
                    if (plugExtraData == null) plugExtraData = new PlugExtraData();
                    plugExtraData.AddNamed("ShadeId", featureExtra["ShadeId"].ToString());
                    if (featureExtra.ContainsNamed("HubIp"))
                    {
                        plugExtraData.AddNamed("HubIp", featureExtra["HubIp"].ToString());
                    }
                    HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, plugExtraData);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Copied ShadeId/HubIp from feature to parent for {parentDevice.Name}", Name);
                }

                // Refresh after potential copy
                plugExtraData = HomeSeerSystem.GetPropertyByRef(parentDevice.Ref, EProperty.PlugExtraData) as PlugExtraData;
                if (plugExtraData == null)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Device {parentDevice.Name} has no PlugExtraData after refresh", Name);
                    return;
                }
                if (!plugExtraData.ContainsNamed("ShadeId"))
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"ShadeId missing from parent device {parentDevice.Name} (ref {parentDevice.Ref}), attempting INI recovery...", Name);
                    // Try to recover from INI (reverse lookup by device ref)
                    if (TryPopulateShadeFromIni(parentDevice.Ref, plugExtraData, out var recoveredShadeId, out var recoveredHubIp))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Recovered ShadeId={recoveredShadeId}, HubIp={recoveredHubIp} from INI for device {parentDevice.Name}", Name);
                        HomeSeerSystem.UpdatePropertyByRef(parentDevice.Ref, EProperty.PlugExtraData, plugExtraData);
                        
                        // Also copy to feature
                        var featurePed = HomeSeerSystem.GetPropertyByRef(device.Ref, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                        featurePed.AddNamed("ShadeId", recoveredShadeId.ToString());
                        featurePed.AddNamed("HubIp", recoveredHubIp);
                        HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, featurePed);
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Populated feature {device.Name} (ref {device.Ref}) with ShadeId={recoveredShadeId}, HubIp={recoveredHubIp}", Name);
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"INI recovery failed for parent device ref {parentDevice.Ref}", Name);
                    }
                }
                if (!plugExtraData.ContainsNamed("ShadeId"))
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Device {parentDevice.Name} does not have ShadeId after refresh", Name);
                    return;
                }

                string hubIpForDevice = null;
                if (plugExtraData.ContainsNamed("HubIp"))
                {
                    hubIpForDevice = plugExtraData["HubIp"]?.ToString();
                }
                else if (_clients.Count == 1)
                {
                    var ips = GetHubIps();
                    hubIpForDevice = ips.Count == 1 ? ips[0] : null;
                }
                if (string.IsNullOrEmpty(hubIpForDevice))
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Device {parentDevice.Name} missing HubIp; cannot route command to correct hub", Name);
                    return;
                }

                var shadeId = int.Parse(plugExtraData["ShadeId"].ToString());
                var controlValue = controlEvent.ControlValue;

                HomeSeerSystem.WriteLog(ELogType.Info, $"Setting shade {shadeId} to position {controlValue} using HubIp={hubIpForDevice}", Name);

                var client = GetClientByHubIp(hubIpForDevice);
                if (client == null)
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"No client found for hub {hubIpForDevice}. Available clients: {string.Join(", ", _clients.Select(c => c.HubIp))}", Name);
                    return;
                }

                // Gen3 hubs require scene-based control (no direct position API)
                bool useScenes = plugExtraData.ContainsNamed("SceneOpenId") || plugExtraData.ContainsNamed("SceneCloseId");
                bool success = false;

                if (useScenes)
                {
                    // Determine which scene to activate based on control value
                    int? sceneId = null;
                    string sceneName = null;
                    
                    if (controlValue >= 90) // Open (90-100%)
                    {
                        if (plugExtraData.ContainsNamed("SceneOpenId"))
                        {
                            sceneId = int.Parse(plugExtraData["SceneOpenId"].ToString());
                            sceneName = "Open";
                        }
                    }
                    else if (controlValue >= 40 && controlValue < 90) // Privacy (40-89%)
                    {
                        if (plugExtraData.ContainsNamed("ScenePrivacyId"))
                        {
                            sceneId = int.Parse(plugExtraData["ScenePrivacyId"].ToString());
                            sceneName = "Privacy";
                        }
                        else if (plugExtraData.ContainsNamed("SceneOpenId")) // Fallback to Open if no Privacy scene
                        {
                            sceneId = int.Parse(plugExtraData["SceneOpenId"].ToString());
                            sceneName = "Open (no privacy)";
                        }
                    }
                    else // Close (0-39%)
                    {
                        if (plugExtraData.ContainsNamed("SceneCloseId"))
                        {
                            sceneId = int.Parse(plugExtraData["SceneCloseId"].ToString());
                            sceneName = "Close";
                        }
                    }
                    
                    if (sceneId.HasValue)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Activating {sceneName} scene {sceneId.Value} for shade {shadeId}", Name);
                        success = await client.ActivateSceneAsync(sceneId.Value);
                        
                        if (success)
                        {
                            HomeSeerSystem.UpdatePropertyByRef(controlEvent.TargetRef, EProperty.Value, controlValue);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Scene {sceneId.Value} ({sceneName}) activated successfully for shade {shadeId}", Name);
                        }
                        else
                        {
                            HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to activate scene {sceneId.Value} for shade {shadeId}", Name);
                        }
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"No appropriate scene found for shade {shadeId} at position {controlValue}%", Name);
                    }
                }
                else
                {
                    // Fallback to direct position control for Gen1/Gen2 hubs
                    var position = (int)((controlValue / 100.0) * 65535);
                    success = await client.SetShadePositionAsync(shadeId, position);

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
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error handling control event: {ex.Message}", Name);
            }
        }

        private bool TryPopulateSceneFromIni(int deviceRef, PlugExtraData ped, out int sceneId, out string hubIp)
        {
            sceneId = 0;
            hubIp = null;
            try
            {
                // NOTE: This method does reverse-lookup (find which scene owns a device ref)
                // This requires scanning [Scenes] section which GetINISetting() doesn't support (key-based only)
                // This is a low-frequency fallback only called during device recovery if PlugExtraData is missing
                // It does NOT affect duplicate device creation (which uses FindDeviceByShadeId/FindDeviceBySceneId)
                var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                if (!File.Exists(iniPath))
                    return false;

                // INI mapping: [Scenes] hubIp:sceneId = deviceRef
                var lines = File.ReadAllLines(iniPath);
                string currentSection = string.Empty;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";")) continue;
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }
                    if (!string.Equals(currentSection, "Scenes", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    var key = parts[0].Trim();
                    var value = parts[1].Trim();

                    if (!int.TryParse(value, out int mappedRef)) continue;
                    if (mappedRef != deviceRef) continue;

                    // key is hubIp:sceneId
                    var keyParts = key.Split(':');
                    if (keyParts.Length != 2) continue;
                    hubIp = keyParts[0];
                    if (int.TryParse(keyParts[1], out var mappedSceneId))
                    {
                        sceneId = mappedSceneId;
                        ped.AddNamed("SceneId", sceneId.ToString());
                        ped.AddNamed("HubIp", hubIp);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Warning, $"TryPopulateSceneFromIni failed: {ex.Message}", Name);
            }
            return false;
        }

        private bool TryResolveSceneAliasFromIni(string deviceName, out int sceneId, out string hubIp)
        {
            sceneId = 0;
            hubIp = null;
            try
            {
                // NOTE: This method does section scan lookup (find scene by device alias)
                // This requires scanning [ScenesAliases] section which GetINISetting() doesn't support (key-based only)
                // This is a low-frequency fallback for legacy device aliases
                // It does NOT affect duplicate device creation
                var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                if (!File.Exists(iniPath))
                    return false;

                // INI alias: [ScenesAliases] AliasName = hubIp:sceneId
                var lines = File.ReadAllLines(iniPath);
                string currentSection = string.Empty;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith(";")) continue;
                    if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                    {
                        currentSection = trimmed.Substring(1, trimmed.Length - 2);
                        continue;
                    }
                    if (!string.Equals(currentSection, "ScenesAliases", StringComparison.OrdinalIgnoreCase)) continue;

                    var parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;

                    var alias = parts[0].Trim();
                    var value = parts[1].Trim();
                    if (!string.Equals(alias, deviceName, StringComparison.OrdinalIgnoreCase)) continue;

                    var keyParts = value.Split(':');
                    if (keyParts.Length != 2) continue;
                    hubIp = keyParts[0];
                    if (int.TryParse(keyParts[1], out var mappedSceneId))
                    {
                        sceneId = mappedSceneId;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Warning, $"TryResolveSceneAliasFromIni failed: {ex.Message}", Name);
            }
            return false;
        }

        private bool TryPopulateShadeFromIni(int deviceRef, PlugExtraData ped, out int shadeId, out string hubIp)
        {
            shadeId = 0;
            hubIp = null;
            try
            {
                // NOTE: This method does reverse-lookup (find which shade owns a device ref)
                // This requires scanning [Devices] section which GetINISetting() doesn't support (key-based only)
                // This is a low-frequency fallback only called during device recovery if PlugExtraData is missing
                // It does NOT affect duplicate device creation (which uses FindDeviceByShadeId)
                var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                HomeSeerSystem.WriteLog(ELogType.Info, $"TryPopulateShadeFromIni: Looking for ref {deviceRef} in {iniPath} (exists: {File.Exists(iniPath)})", Name);
                
                if (!File.Exists(iniPath))
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"INI file not found at {iniPath} for reverse lookup of ref {deviceRef}", Name);
                    return false;
                }

                var lines = File.ReadAllLines(iniPath);
                var inDevices = false;
                var allDeviceLines = new List<string>();
                int linesChecked = 0;
                var sectionsFound = new List<string>();
                
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith(";"))
                        continue;
                    if (line.StartsWith("["))
                    {
                        sectionsFound.Add(line);
                        inDevices = line.Equals("[Devices]", StringComparison.OrdinalIgnoreCase);
                        if (inDevices) HomeSeerSystem.WriteLog(ELogType.Info, $"Found [Devices] section in INI", Name);
                        continue;
                    }
                    if (!inDevices)
                        continue;

                    allDeviceLines.Add(line);
                    linesChecked++;
                    
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;
                    var key = parts[0].Trim();
                    var value = parts[1].Trim();
                    
                    // Parse ref from value (format: ref:openSceneId:closeSceneId:privacySceneId)
                    var valueParts = value.Split(':');
                    if (!int.TryParse(valueParts[0], out int mappedRef))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Line {linesChecked}: Could not parse ref from '{valueParts[0]}' in value '{value}'", Name);
                        continue;
                    }
                    
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Line {linesChecked}: Checking '{key}={value}' (mappedRef={mappedRef} vs deviceRef={deviceRef})", Name);
                    
                    if (mappedRef != deviceRef)
                        continue;

                    HomeSeerSystem.WriteLog(ELogType.Info, $"Found matching INI entry: key='{key}', value='{value}'", Name);
                    var keyParts = key.Split(':');
                    if (keyParts.Length != 2)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"INI key '{key}' doesn't have 2 parts (expected hubIp:shadeId)", Name);
                        continue;
                    }
                    hubIp = keyParts[0];
                    if (!int.TryParse(keyParts[1], out shadeId))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Could not parse shadeId from '{keyParts[1]}'", Name);
                        continue;
                    }

                    if (!ped.ContainsNamed("ShadeId")) ped.AddNamed("ShadeId", shadeId.ToString());
                    if (!ped.ContainsNamed("HubIp") && !string.IsNullOrWhiteSpace(hubIp)) ped.AddNamed("HubIp", hubIp);
                    
                    // Parse scene IDs from INI value if present (format: ref:openSceneId:closeSceneId:privacySceneId)
                    if (valueParts.Length > 1 && int.TryParse(valueParts[1], out int openSceneId))
                    {
                        ped.AddNamed("SceneOpenId", openSceneId.ToString());
                    }
                    if (valueParts.Length > 2 && int.TryParse(valueParts[2], out int closeSceneId))
                    {
                        ped.AddNamed("SceneCloseId", closeSceneId.ToString());
                    }
                    if (valueParts.Length > 3 && int.TryParse(valueParts[3], out int privacySceneId))
                    {
                        ped.AddNamed("ScenePrivacyId", privacySceneId.ToString());
                    }
                    
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Successfully recovered from INI: ShadeId={shadeId}, HubIp={hubIp}", Name);
                    return true;
                }
                
                HomeSeerSystem.WriteLog(ELogType.Warning, $"No matching entry found in INI for ref {deviceRef} (checked {linesChecked} lines in [Devices] section). Sections found: {string.Join(", ", sectionsFound)}", Name);
                
                // If no exact match found, log what we have
                LogVerbose($"No INI mapping found for device ref {deviceRef}. [Devices] section has {allDeviceLines.Count} entries.");
                if (allDeviceLines.Count > 0)
                {
                    LogVerbose($"Sample INI entries: {string.Join(", ", allDeviceLines.Take(5))}");
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error reverse-looking up device ref {deviceRef} in INI: {ex.Message}", Name);
            }
            return false;
        }

        private async Task DiscoverShadesAsync(PowerViewClient client, string hubIp)
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Discovering PowerView shades on hub {hubIp}...", Name);

                var shades = await client.GetShadesAsync();

                // Verify no duplicates in the returned list
                var shadeIds = new HashSet<int>();
                foreach (var shade in shades)
                {
                    if (shadeIds.Contains(shade.Id))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"ERROR: Duplicate shade ID {shade.Id} returned from GetShadesAsync!", Name);
                    }
                    shadeIds.Add(shade.Id);
                }

                // Clean up duplicate devices using the freshly discovered shade list
                CleanupDuplicateDevices(shades);

                foreach (var shade in shades)
                {
                    var shadeName = FormatShadeName(shade.Id, PowerViewClient.DecodeName(shade.Name));
                    // IMPORTANT: Always use the hubIp we discovered from, not shade.GatewayIp
                    // We only discover from the primary hub, so all shades belong to it for device tracking
                    var targetHubIp = hubIp;

                    // IMPORTANT: For multi-hub setup, check if this shade already exists on THIS HUB
                    // Use hubIp-specific lookup to avoid finding duplicates from other hubs
                    var existingDevice = FindDeviceByShadeId(shade.Id, targetHubIp, shadeName);
                    
                    if (existingDevice != null)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Shade device {shade.Id} already exists (ref {existingDevice.Ref}), updating data...", Name);
                        EnsureShadeControlsExist(existingDevice.Ref, shade.Id, targetHubIp);
                        // NOTE: Scene linking will be done in RelinkScenesToExistingShades() AFTER scenes are created
                        // await LinkScenesToShade(existingDevice.Ref, shade.Id, targetHubIp);
                        // Update existing device with current shade data
                        await UpdateShadeDeviceAsync(existingDevice, shade, client, targetHubIp);
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Shade device {shade.Id} NOT FOUND, creating new device...", Name);
                        await CreateShadeDevice(shade, targetHubIp);
                        // After creation, fetch the device and update it with current data
                        var newDevice = FindDeviceByShadeId(shade.Id, targetHubIp, shadeName);
                        if (newDevice != null)
                        {
                            await UpdateShadeDeviceAsync(newDevice, shade, client, targetHubIp);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error discovering shades: {ex.Message}", Name);
                HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
            }
        }

        private void CleanupDuplicateDevices(List<PowerViewShade> discoveredShades)
        {
            try
            {
                // Build the set of valid shade keys from discovery (shadeId:hubIp)
                var validKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var shade in discoveredShades)
                {
                    var hubIp = string.IsNullOrEmpty(shade.GatewayIp) ? string.Empty : shade.GatewayIp;
                    var key = $"{shade.Id}:{hubIp}";
                    validKeys.Add(key);
                }

                var devicesToDelete = new List<HsDevice>();
                var keeperRefs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Scan all HS devices to find PowerView shades
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null)
                        continue;

                    if (device.Interface != Id || device.PlugExtraData == null || !device.PlugExtraData.ContainsNamed("ShadeId"))
                        continue;

                    try
                    {
                        var shadeIdStr = device.PlugExtraData["ShadeId"].ToString();
                        var hubIp = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : string.Empty;

                        if (!int.TryParse(shadeIdStr, out int shadeId))
                        {
                            devicesToDelete.Add(device);
                            continue;
                        }

                        var key = $"{shadeId}:{hubIp}";

                        // If this device is not part of the current discovery set, delete it
                        if (!validKeys.Contains(key))
                        {
                            devicesToDelete.Add(device);
                            continue;
                        }

                        // If we already have a keeper for this key, delete duplicates
                        if (keeperRefs.ContainsKey(key))
                        {
                            devicesToDelete.Add(device);
                        }
                        else
                        {
                            keeperRefs[key] = device.Ref;
                            // refresh INI mapping to the keeper
                            HomeSeerSystem.SaveINISetting("Devices", key, device.Ref.ToString(), Id + ".ini");
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error processing device ref {ref_num} for cleanup: {ex.Message}", Name);
                    }
                }

                if (devicesToDelete.Count > 0)
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Cleaning up {devicesToDelete.Count} duplicate/obsolete shade devices...", Name);
                    foreach (var device in devicesToDelete)
                    {
                        try
                        {
                            var shadeName = PowerViewClient.DecodeName(device.Name);
                            HomeSeerSystem.DeleteDevice(device.Ref);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Deleted device: {shadeName} (ref {device.Ref})", Name);
                        }
                        catch (Exception ex)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting device ref {device.Ref}: {ex.Message}", Name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error cleaning up duplicate devices: {ex.Message}", Name);
            }
        }

        private HsDevice FindDeviceByShadeIdAcrossAllHubs(int shadeId)
        {
            try
            {
                // Search all devices by ref, regardless of HubIp
                for (int ref_num = 1; ref_num < 10000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null)
                        continue;

                    if (device.Interface == Id && device.PlugExtraData != null && device.PlugExtraData.ContainsNamed("ShadeId"))
                    {
                        try
                        {
                            var shadeIdStr = device.PlugExtraData["ShadeId"].ToString();
                            if (int.TryParse(shadeIdStr, out int storedShadeId))
                            {
                                if (storedShadeId == shadeId)
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Info, $"Found shade {shadeId} across hubs (ref {device.Ref})", Name);
                                    return device;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in FindDeviceByShadeIdAcrossAllHubs: {ex.Message}", Name);
            }
            return null;
        }

        private HsDevice FindDeviceByShadeId(int shadeId, string hubIp, string shadeName = null)
        {
            try
            {
                // First, check INI mapping using HomeSeer API
                var iniKey = $"{hubIp}:{shadeId}";
                var mappedRefStr = HomeSeerSystem.GetINISetting("Devices", iniKey, string.Empty, Id + ".ini");
                
                if (!string.IsNullOrEmpty(mappedRefStr) && int.TryParse(mappedRefStr, out int mappedRef))
                {
                    var mappedDevice = HomeSeerSystem.GetDeviceByRef(mappedRef);
                    if (mappedDevice != null)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Found shade {shadeId} via INI mapping (ref {mappedRef})", Name);
                        return mappedDevice;
                    }
                    else
                    {
                        // Mapped device no longer exists, clear the stale mapping
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Clearing stale INI mapping for {iniKey} (ref {mappedRef} not found)", Name);
                        HomeSeerSystem.SaveINISetting("Devices", iniKey, string.Empty, Id + ".ini");
                    }
                }

                // Search all devices by ref, starting from 1
                HsDevice foundDevice = null;
                int duplicateCount = 0;
                
                for (int ref_num = 1; ref_num < 10000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null)
                        continue;

                    if (device.Interface == Id && device.PlugExtraData != null && device.PlugExtraData.ContainsNamed("ShadeId"))
                    {
                        try
                        {
                            var shadeIdStr = device.PlugExtraData["ShadeId"].ToString();
                            var storedHubIp = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : string.Empty;
                            
                            if (int.TryParse(shadeIdStr, out int storedShadeId))
                            {
                                if (storedShadeId == shadeId && string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (foundDevice == null)
                                    {
                                        foundDevice = device;
                                        LogVerbose($"Found shade {shadeId} via device scan (ref {device.Ref})");
                                    }
                                    else
                                    {
                                        duplicateCount++;
                                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Found DUPLICATE device for shade {shadeId}: ref {device.Ref} (keeping ref {foundDevice.Ref})", Name);
                                    }
                                }
                            }
                        }
                        catch (Exception innerEx)
                        {
                            HomeSeerSystem.WriteLog(ELogType.Error, $"Error parsing shade data for device ref {device.Ref}: {innerEx.Message}", Name);
                        }
                    }
                }

                if (foundDevice != null)
                {
                    if (duplicateCount > 0)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Shade {shadeId} has {duplicateCount} duplicate devices that should be cleaned up", Name);
                    }
                    return foundDevice;
                }

                // Fallback: Search by name if ShadeId not found (for devices created before metadata was added)
                if (!string.IsNullOrEmpty(shadeName))
                {
                    LogVerbose($"ShadeId {shadeId} not found in PlugExtraData, searching by name: {shadeName}");
                    for (int ref_num = 1; ref_num < 10000; ref_num++)
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device == null)
                            continue;

                        // Look for shade device with matching name and location
                        if (device.Interface == Id && device.Relationship == ERelationship.Device && device.Location2 == "Shades")
                        {
                            // Match by name (case-insensitive)
                            if (string.Equals(device.Name, shadeName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Verify hub matches if PlugExtraData is available
                                bool isRightHub = true;
                                if (device.PlugExtraData != null && device.PlugExtraData.ContainsNamed("HubIp"))
                                {
                                    var storedHubIp = device.PlugExtraData["HubIp"].ToString();
                                    isRightHub = string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase);
                                }
                                
                                if (isRightHub)
                                {
                                    LogVerbose($"Found shade {shadeId} by name match: {shadeName} (ref {device.Ref})");
                                    return device;
                                }
                            }
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

        private void EnsureShadeControlsExist(int deviceRef, int shadeId, string hubIp)
        {
            try
            {
                var device = HomeSeerSystem.GetDeviceByRef(deviceRef);
                if (device == null)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Cannot ensure controls - device ref {deviceRef} not found", Name);
                    return;
                }

                // Ensure PlugExtraData exists on parent device
                var extraData = HomeSeerSystem.GetPropertyByRef(deviceRef, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                if (!extraData.ContainsNamed("ShadeId"))
                {
                    extraData.AddNamed("ShadeId", shadeId.ToString());
                    extraData.AddNamed("HubIp", hubIp);
                    HomeSeerSystem.UpdatePropertyByRef(deviceRef, EProperty.PlugExtraData, extraData);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Added missing ShadeId={shadeId}, HubIp={hubIp} to device {deviceRef}", Name);
                }

                // Find the "Shade Control" feature
                var childRefs = device.AssociatedDevices ?? new HashSet<int>();
                foreach (var childRef in childRefs)
                {
                    var child = HomeSeerSystem.GetDeviceByRef(childRef);
                    if (child == null) continue;

                    if (string.Equals(child.Name, "Shade Control", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ensure PlugExtraData exists on control feature
                        var childExtra = HomeSeerSystem.GetPropertyByRef(childRef, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                        if (!childExtra.ContainsNamed("ShadeId"))
                        {
                            childExtra.AddNamed("ShadeId", shadeId.ToString());
                            childExtra.AddNamed("HubIp", hubIp);
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.PlugExtraData, childExtra);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Added ShadeId={shadeId}, HubIp={hubIp} to Shade Control feature {childRef}", Name);
                        }

                        // Always ensure controls exist (HomeSeer will handle duplicates)
                        var svgClose = new StatusGraphic("/images/HomeSeer/status/down.png", 0, "Close");
                        svgClose.IsRange = false;
                        svgClose.TargetRange = new ValueRange(0, 0);
                        svgClose.ControlUse = EControlUse.On;
                        HomeSeerSystem.AddStatusGraphicToFeature(childRef, svgClose);

                        var svgOpen = new StatusGraphic("/images/HomeSeer/status/up.png", 100, "Open");
                        svgOpen.IsRange = false;
                        svgOpen.TargetRange = new ValueRange(100, 100);
                        svgOpen.ControlUse = EControlUse.Off;
                        HomeSeerSystem.AddStatusGraphicToFeature(childRef, svgOpen);

                        HomeSeerSystem.WriteLog(ELogType.Info, $"Ensured controls exist for Shade Control feature {childRef}", Name);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error ensuring controls exist: {ex.Message}", Name);
            }
        }

        private async Task CreateShadeDevice(PowerViewShade shade, string hubIp)
        {
            try
            {
                var originalName = PowerViewClient.DecodeName(shade.Name);
                var shadeName = FormatShadeName(shade.Id, originalName);
                HomeSeerSystem.WriteLog(ELogType.Info, $"Creating device for shade: {shadeName} (ID: {shade.Id}, Hub {hubIp}, Label: {originalName})", Name);

                // Use DeviceFactory to create the device
                var df = DeviceFactory.CreateDevice(Id);
                df = df.WithName(shadeName)
                       .WithLocation("PowerView")
                       .WithLocation2("Shades");

                // Create features:
                // 1) Control feature for scenes/position
                var controlFeature = FeatureFactory.CreateGenericBinaryControl(Id, "Shade Control", "Open", "Close", 100, 0)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");
                df.WithFeature(controlFeature);

                // 2) Position status feature (0-100)
                var positionFeature = FeatureFactory.CreateGenericBinaryControl(Id, "Position", "100%", "0%", 100, 0)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");
                df.WithFeature(positionFeature);

                // 3) Battery status feature (0-100)
                var batteryFeature = FeatureFactory.CreateGenericBinaryControl(Id, "Battery", "100%", "0%", 100, 0)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");
                df.WithFeature(batteryFeature);

                // 4) Signal strength status feature (0-100%)
                var signalFeature = FeatureFactory.CreateGenericBinaryControl(Id, "Signal", "100%", "0%", 100, 0)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");
                df.WithFeature(signalFeature);

                // Get the NewDeviceData
                var deviceData = df.PrepareForHs();

                // Create the device and get its reference
                var devRef = HomeSeerSystem.CreateDevice(deviceData);
                HomeSeerSystem.WriteLog(ELogType.Info, $"Device created with ref {devRef}, now storing extra data...", Name);
                
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
                    extraDataObj.AddNamed("HubIp", hubIp);
                    extraDataObj.AddNamed("OriginalName", originalName);
                    HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.PlugExtraData, extraDataObj);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Stored ShadeId={shade.Id}, HubIp={hubIp} in PlugExtraData", Name);
                }

                // Also copy metadata to child feature(s) so control events have it at the feature level
                var createdDevice = HomeSeerSystem.GetDeviceByRef(devRef);
                var childRefs = createdDevice?.AssociatedDevices ?? new HashSet<int>();
                foreach (var childRef in childRefs)
                {
                    var child = HomeSeerSystem.GetDeviceByRef(childRef);
                    if (child == null)
                        continue;
                    var childExtra = HomeSeerSystem.GetPropertyByRef(childRef, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                    childExtra.AddNamed("ShadeId", shade.Id.ToString());
                    childExtra.AddNamed("HubIp", hubIp);
                    childExtra.AddNamed("OriginalName", originalName);
                    
                    // Mark Position, Battery, and Signal as read-only status devices
                    if (string.Equals(child.Name, "Position", StringComparison.OrdinalIgnoreCase))
                    {
                        childExtra.AddNamed("StatusType", "Position");
                    }
                    else if (string.Equals(child.Name, "Battery", StringComparison.OrdinalIgnoreCase))
                    {
                        childExtra.AddNamed("StatusType", "Battery");
                    }
                    else if (string.Equals(child.Name, "Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        childExtra.AddNamed("StatusType", "Signal");
                    }
                    
                    HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.PlugExtraData, childExtra);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Stored ShadeId={shade.Id}, HubIp={hubIp} in feature PlugExtraData for {child.Name} (ref {childRef})", Name);
                    
                    // Add actual controls to Shade Control feature so HomeSeer routes clicks to SetIOMulti
                    if (string.Equals(child.Name, "Shade Control", StringComparison.OrdinalIgnoreCase))
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Adding control pairs to Shade Control feature (ref {childRef})", Name);
                        
                        // Add Close control (value=0)
                        var svgClose = new StatusGraphic("/images/HomeSeer/status/down.png", 0, "Close");
                        svgClose.IsRange = false;
                        svgClose.TargetRange = new ValueRange(0, 0);
                        svgClose.ControlUse = EControlUse.On;  // This makes it clickable!
                        HomeSeerSystem.AddStatusGraphicToFeature(childRef, svgClose);
                        
                        // Add Open control (value=100)
                        var svgOpen = new StatusGraphic("/images/HomeSeer/status/up.png", 100, "Open");
                        svgOpen.IsRange = false;
                        svgOpen.TargetRange = new ValueRange(100, 100);
                        svgOpen.ControlUse = EControlUse.Off;  // This makes it clickable!
                        HomeSeerSystem.AddStatusGraphicToFeature(childRef, svgOpen);
                        
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Added Close (0) and Open (100) controls to Shade Control", Name);
                    }
                }

                // Link scenes to this shade (Open/Close/Privacy)
                await LinkScenesToShade(devRef, shade.Id, hubIp);
                
                // Set initial position and battery
                if (shade.Positions?.Position1 != null)
                {
                    var percentage = (shade.Positions.Position1.Value / 65535.0) * 100;
                    HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.Value, percentage);

                    // update Position feature value
                    var created = HomeSeerSystem.GetDeviceByRef(devRef);
                    var childRefs2 = created?.AssociatedDevices ?? new HashSet<int>();
                    foreach (var childRef in childRefs2)
                    {
                        var child = HomeSeerSystem.GetDeviceByRef(childRef);
                        if (child == null) continue;
                        if (string.Equals(child.Name, "Position", StringComparison.OrdinalIgnoreCase))
                        {
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, percentage);
                        }
                    }
                }
                // Set initial battery and signal strength in one pass
                if (shade.BatteryStrength > 0 || shade.SignalStrength > 0)
                {
                    var created = HomeSeerSystem.GetDeviceByRef(devRef);
                    var childRefs2 = created?.AssociatedDevices ?? new HashSet<int>();
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: Setting initial battery={shade.BatteryStrength}%, signal={shade.SignalStrength}% on {childRefs2.Count} child devices", Name);
                    foreach (var childRef in childRefs2)
                    {
                        var child = HomeSeerSystem.GetDeviceByRef(childRef);
                        if (child == null) continue;
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: Checking child '{child.Name}' (ref {childRef})", Name);
                        if (string.Equals(child.Name, "Battery", StringComparison.OrdinalIgnoreCase))
                        {
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, (double)shade.BatteryStrength);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: Updated Battery child to {shade.BatteryStrength}%", Name);
                        }
                        else if (string.Equals(child.Name, "Signal", StringComparison.OrdinalIgnoreCase))
                        {
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, (double)shade.SignalStrength);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: Updated Signal child to {shade.SignalStrength}%", Name);
                        }
                    }
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Created device {shadeName} with ref {devRef}, initial position {(shade.Positions?.Position1 != null ? ((shade.Positions.Position1.Value / 65535.0) * 100).ToString("F1") + "%" : "unknown")}", Name);
                
                // Save mapping to INI as persistent lookup source
                try
                {
                    var iniKey = $"{hubIp}:{shade.Id}";
                    var iniValue = devRef.ToString();
                    HomeSeerSystem.SaveINISetting("Devices", iniKey, iniValue, Id + ".ini");
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Saved shade mapping to INI: {iniKey} = {iniValue}", Name);
                }
                catch (Exception ex)
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to save shade mapping to INI: {ex.Message}", Name);
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error creating shade device: {ex.Message}", Name);
            }
        }

        private async Task UpdateShadeDeviceAsync(HsDevice device, PowerViewShade shade, PowerViewClient client, string hubIp)
        {
            try
            {
                // Calculate position percentage
                double? percentage = null;
                if (shade.Positions?.Position1 != null && shade.Positions.Position1 > 0)
                {
                    percentage = (shade.Positions.Position1.Value / 65535.0) * 100;
                    var oldValue = device.Value;
                    HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.Value, percentage.Value);
                    
                    // Log if position changed
                    if (Math.Abs(oldValue - percentage.Value) > 0.5)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: position {oldValue:F1}% → {percentage.Value:F1}%", Name);
                    }
                }
                else if (shade.Positions?.Position1 == null)
                {
                    LogVerbose($"Shade {shade.Id}: No position data available (Positions is null)");
                }
                else
                {
                    LogVerbose($"Shade {shade.Id}: Position value is {shade.Positions?.Position1 ?? 0}, which is not valid");
                }

                // Single pass through child devices to update all properties (position, battery, signal)
                // All data comes from one API call, so update efficiently in one loop
                var childRefs = device.AssociatedDevices ?? new HashSet<int>();
                if (childRefs.Count == 0)
                {
                    LogVerbose($"Shade {shade.Id}: No child devices found for updating battery/signal");
                }

                foreach (var childRef in childRefs)
                {
                    var child = HomeSeerSystem.GetDeviceByRef(childRef);
                    if (child == null) continue;

                    // Update position child
                    if (percentage.HasValue && string.Equals(child.Name, "Position", StringComparison.OrdinalIgnoreCase))
                    {
                        HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, percentage.Value);
                        HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.StatusString, $"{percentage.Value:F0}%");
                        LogVerbose($"Shade {shade.Id}: Updated Position child (ref {childRef}) to {percentage.Value:F1}%");
                    }
                    // Update battery child (only if we have valid battery data: > 0)
                    else if (string.Equals(child.Name, "Battery", StringComparison.OrdinalIgnoreCase))
                    {
                        if (shade.BatteryStrength > 0)
                        {
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, (double)shade.BatteryStrength);
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.StatusString, $"{shade.BatteryStrength:F0}%");
                            LogVerbose($"Shade {shade.Id}: Updated Battery child (ref {childRef}) to {shade.BatteryStrength}%");
                        }
                        else
                        {
                            LogVerbose($"Shade {shade.Id}: Battery strength is {shade.BatteryStrength}, skipping update");
                        }
                    }
                    // Update signal strength child (only if we have valid signal data: > 0)
                    else if (string.Equals(child.Name, "Signal", StringComparison.OrdinalIgnoreCase))
                    {
                        if (shade.SignalStrength > 0)
                        {
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.Value, (double)shade.SignalStrength);
                            HomeSeerSystem.UpdatePropertyByRef(childRef, EProperty.StatusString, $"{shade.SignalStrength:F0}%");
                            LogVerbose($"Shade {shade.Id}: Updated Signal child (ref {childRef}) to {shade.SignalStrength}%");
                        }
                        else
                        {
                            LogVerbose($"Shade {shade.Id}: Signal strength is {shade.SignalStrength}, skipping update");
                        }
                    }
                }

                if ((percentage.HasValue && percentage.Value > 0) || shade.BatteryStrength > 0 || shade.SignalStrength > 0)
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shade.Id}: Position={percentage?.ToString("F1") ?? "N/A"}%, SignalStrength={shade.SignalStrength}, BatteryStrength={shade.BatteryStrength}", Name);
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error updating shade device: {ex.Message}", Name);
            }
        }

        // Creates or finds a read-only sibling status device (Position/Battery) when a shade lacks child features.
        private void UpdateDeviceValueAndStatus(int deviceRef, string statusType, double value)
        {
            // Update the value
            HomeSeerSystem.UpdatePropertyByRef(deviceRef, EProperty.Value, value);
            
            // Update the status string with formatting
            string displayString = $"{value:F0}%";
            HomeSeerSystem.UpdatePropertyByRef(deviceRef, EProperty.StatusString, displayString);
            
            // Log for debugging
            var device = HomeSeerSystem.GetDeviceByRef(deviceRef);
            if (device != null)
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Updated {statusType} device '{device.Name}' (ref {deviceRef}): value={value:F0}%, display='{displayString}'", Name);
            }
        }

        private void EnsureAndUpdateStatusDevice(HsDevice parent, string hubIp, int shadeId, string shadeName, string statusType, double value)
        {
            try
            {
                // If parent already has a child feature with this name, do not create a sibling device
                var childRefs = parent.AssociatedDevices ?? new HashSet<int>();
                foreach (var childRef in childRefs)
                {
                    var child = HomeSeerSystem.GetDeviceByRef(childRef);
                    if (child == null) continue;
                    if (string.Equals(child.Name, statusType, StringComparison.OrdinalIgnoreCase))
                    {
                        // Update the child feature's value and status string
                        UpdateDeviceValueAndStatus(childRef, statusType, value);
                        return;
                    }
                }

                // Try INI mapping first
                var iniKey = $"{hubIp}:{shadeId}:{statusType}";
                var mappedRefStr = HomeSeerSystem.GetINISetting("Status", iniKey, string.Empty, Id + ".ini");
                if (!string.IsNullOrEmpty(mappedRefStr) && int.TryParse(mappedRefStr, out int mappedRef))
                {
                    var mapped = HomeSeerSystem.GetDeviceByRef(mappedRef);
                    if (mapped != null && mapped.Interface == Id)
                    {
                        // Verify this is still the right device (metadata could have changed)
                        var ped = mapped.PlugExtraData;
                        if (ped != null && ped.ContainsNamed("ShadeId") && ped.ContainsNamed("StatusType"))
                        {
                            if (int.TryParse(ped["ShadeId"].ToString(), out int sid) && sid == shadeId 
                                && string.Equals(ped["StatusType"].ToString(), statusType, StringComparison.OrdinalIgnoreCase))
                            {
                                UpdateDeviceValueAndStatus(mappedRef, statusType, value);
                                return;
                            }
                        }
                    }
                    // INI ref is stale, clear it
                    HomeSeerSystem.SaveINISetting("Status", iniKey, string.Empty, Id + ".ini");
                }

                // Scan for an existing status sibling
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var d = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (d == null) continue;
                    if (d.Interface != Id || d.PlugExtraData == null) continue;
                    try
                    {
                        var ped = d.PlugExtraData;
                        if (!ped.ContainsNamed("StatusType")) continue;
                        if (!string.Equals(ped["StatusType"].ToString(), statusType, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!ped.ContainsNamed("ShadeId") || !ped.ContainsNamed("HubIp")) continue;
                        if (!int.TryParse(ped["ShadeId"].ToString(), out int sid)) continue;
                        var hip = ped["HubIp"].ToString();
                        if (sid == shadeId && string.Equals(hip, hubIp, StringComparison.OrdinalIgnoreCase))
                        {
                            HomeSeerSystem.SaveINISetting("Status", iniKey, d.Ref.ToString(), Id + ".ini");
                            UpdateDeviceValueAndStatus(d.Ref, statusType, value);
                            return;
                        }
                    }
                    catch { }
                }

                // Create new sibling status device (read-only)
                // Name format: "Position - Shade 123 - Ron's Office" to ensure uniqueness even when shades share labels
                var label = ExtractOriginalLabel(shadeName);
                var statusDeviceName = string.IsNullOrEmpty(label) 
                    ? $"{statusType} - Shade {shadeId}"
                    : $"{statusType} - Shade {shadeId} - {label}";

                // Double-check that no device with this exact name already exists (safety check)
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var existing = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (existing != null && existing.Interface == Id && existing.Name == statusDeviceName)
                    {
                        // Found an existing device with this name - update it instead of creating a duplicate
                        LogVerbose($"Found existing device by name match: '{statusDeviceName}' ref {existing.Ref}, updating instead of creating duplicate");
                        UpdateDeviceValueAndStatus(existing.Ref, statusType, value);
                        
                        // Ensure metadata is populated
                        var existingPed = HomeSeerSystem.GetPropertyByRef(existing.Ref, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                        if (!existingPed.ContainsNamed("ShadeId")) existingPed.AddNamed("ShadeId", shadeId.ToString());
                        if (!existingPed.ContainsNamed("HubIp")) existingPed.AddNamed("HubIp", hubIp);
                        if (!existingPed.ContainsNamed("StatusType")) existingPed.AddNamed("StatusType", statusType);
                        if (!existingPed.ContainsNamed("ParentRef")) existingPed.AddNamed("ParentRef", parent.Ref.ToString());
                        if (!existingPed.ContainsNamed("ReadOnly")) existingPed.AddNamed("ReadOnly", "true");
                        HomeSeerSystem.UpdatePropertyByRef(existing.Ref, EProperty.PlugExtraData, existingPed);
                        
                        HomeSeerSystem.SaveINISetting("Status", iniKey, existing.Ref.ToString(), Id + ".ini");
                        return;
                    }
                }

                var df = DeviceFactory.CreateDevice(Id)
                    .WithName(statusDeviceName)
                    .WithLocation("PowerView")
                    .WithLocation2("Shades");

                var deviceData = df.PrepareForHs();
                var newRef = HomeSeerSystem.CreateDevice(deviceData);

                var newPed = HomeSeerSystem.GetPropertyByRef(newRef, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                newPed.AddNamed("ShadeId", shadeId.ToString());
                newPed.AddNamed("HubIp", hubIp);
                newPed.AddNamed("ParentRef", parent.Ref.ToString());
                newPed.AddNamed("StatusType", statusType);
                newPed.AddNamed("ReadOnly", "true");
                HomeSeerSystem.UpdatePropertyByRef(newRef, EProperty.PlugExtraData, newPed);

                // Initialize value with proper formatting
                HomeSeerSystem.UpdatePropertyByRef(newRef, EProperty.Value, value);
                
                // Set display string based on type
                string displayString = $"{value:F0}";
                if (statusType == "Signal")
                {
                    displayString = $"{value:F0}%";
                }
                else if (statusType == "Position")
                {
                    displayString = $"{value:F0}%";
                }
                else if (statusType == "Battery")
                {
                    displayString = $"{value:F0}%";
                }
                HomeSeerSystem.UpdatePropertyByRef(newRef, EProperty.StatusString, displayString);

                // Persist mapping for quick lookup
                HomeSeerSystem.SaveINISetting("Status", iniKey, newRef.ToString(), Id + ".ini");
                HomeSeerSystem.WriteLog(ELogType.Info, $"Created read-only status device '{statusDeviceName}' ref {newRef} for shade {shadeId}", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error ensuring status device for shade {shadeId} ({statusType}): {ex.Message}", Name);
            }
        }

        // (Position/Battery status features are not added post-creation due to SDK limitations);
        // position and battery are surfaced via device value and string instead.

        private void StartPollingForHub(string hubIp, PowerViewClient client)
        {
            // IMPORTANT: Only poll from primary hub - secondary hub doesn't have complete shade information
            if (!string.Equals(hubIp, _primaryHubIp, StringComparison.OrdinalIgnoreCase))
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Skipping polling for secondary hub {hubIp} - only primary hub is polled for shade updates", Name);
                return;
            }
            
            if (!_pollTimers.ContainsKey(hubIp))
            {
                var timer = new System.Threading.Timer(
                    async _ => await PollShadesAndScenesAsync(client, hubIp),
                    null,
                    TimeSpan.FromSeconds(POLL_INTERVAL_SECONDS),
                    TimeSpan.FromSeconds(POLL_INTERVAL_SECONDS)
                );
                _pollTimers[hubIp] = timer;
                HomeSeerSystem.WriteLog(ELogType.Info, $"Started polling hub {hubIp} every {POLL_INTERVAL_SECONDS} seconds", Name);
            }
        }

        private void StopPolling()
        {
            foreach (var kvp in _pollTimers)
            {
                kvp.Value?.Dispose();
            }
            _pollTimers.Clear();
            HomeSeerSystem.WriteLog(ELogType.Info, "Stopped polling", Name);
        }

        private async Task PollShadesAndScenesAsync(PowerViewClient client, string hubIp)
        {
            try
            {
                // Skip polling while discovery is in progress to avoid task cancellations
                if (_discoveryInProgress)
                {
                    return;
                }
                
                var shades = await client.GetShadesAsync();
                
                foreach (var shade in shades)
                {
                    // Filter to shades that belong to this hub
                    if (!string.Equals(shade.GatewayIp, hubIp, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var device = FindDeviceByShadeId(shade.Id, hubIp, shade.Name);
                    if (device != null)
                    {
                        await UpdateShadeDeviceAsync(device, shade, client, hubIp);
                    }
                }
                
                // Periodically clean up any duplicates that might have appeared
                // Run every 10 polls (5 minutes at 30-second intervals)
                if (DateTime.UtcNow.Second % 300 < 30)
                {
                    GlobalCleanupStatusDevices();
                }

                // Scene sync is done at startup only to prevent duplicate scene devices during polling
                // Scenes are created once during initial discovery and stay in place
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error polling hub {hubIp}: {ex.Message}", Name);
            }
        }

        private HsDevice FindDeviceBySceneId(int sceneId, string hubIp, string sceneName = null, bool skipDeviceScan = false)
        {
            try
            {
                // PRIMARY LOOKUP: Try INI mapping first (fast, reliable, persistent)
                var iniKey = $"{hubIp}:{sceneId}";
                var iniMapping = HomeSeerSystem.GetINISetting("Scenes", iniKey, string.Empty, Id + ".ini");
                
                if (!string.IsNullOrEmpty(iniMapping) && int.TryParse(iniMapping, out int mappedRef))
                {
                    var mappedDevice = HomeSeerSystem.GetDeviceByRef(mappedRef);
                    if (mappedDevice != null)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Found scene {sceneId} via INI mapping (ref {mappedRef})", Name);
                        return mappedDevice;
                    }
                    else
                    {
                        // Mapped device no longer exists, clear stale mapping
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Clearing stale INI mapping for {iniKey} (ref {mappedRef} not found)", Name);
                        HomeSeerSystem.SaveINISetting("Scenes", iniKey, string.Empty, Id + ".ini");
                    }
                }

                // SECONDARY LOOKUP: Device scan by PlugExtraData (fallback if INI missing)
                // Scan HS devices for a match (skip during initial discovery for speed)
                if (!skipDeviceScan)
                {
                    for (int ref_num = 1; ref_num < 20000; ref_num++)
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device == null) continue;
                        if (device.Interface != Id)
                            continue;
                        
                        try
                        {
                            // First try to match by PlugExtraData (SceneId + HubIp)
                            if (device.PlugExtraData != null && device.PlugExtraData.ContainsNamed("SceneId"))
                            {
                                var sceneIdStr = device.PlugExtraData["SceneId"].ToString();
                                var storedHubIp = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : string.Empty;
                                if (int.TryParse(sceneIdStr, out int storedSceneId))
                                {
                                    if (storedSceneId == sceneId && string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                                    {
                                        HomeSeerSystem.WriteLog(ELogType.Info, $"Found scene {sceneId} via PlugExtraData (ref {device.Ref})", Name);
                                        return device;
                                    }
                                }
                            }
                            
                            // Fallback: If name matches and it's a scene device, it's likely ours
                            if (device.Relationship == ERelationship.Device && device.Location2 == "Scenes" && !string.IsNullOrEmpty(sceneName))
                            {
                                if (string.Equals(device.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Info, $"Found scene {sceneId} by name '{sceneName}' (ref {device.Ref})", Name);
                                    return device;
                                }
                            }
                        }
                        catch { }
                    }
                }

                // Fallback: match by name (only for scene devices, not shade devices)
                // Skip this during initial discovery when skipDeviceScan is true to avoid expensive scans
                if (!string.IsNullOrEmpty(sceneName))
                {
                    for (int ref_num = 1; ref_num < 20000; ref_num++)
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device == null) continue;
                        
                        // Must be our plugin, must be a device
                        if (device.Interface == Id && device.Relationship == ERelationship.Device && device.Location2 == "Scenes")
                        {
                            // Match by name - don't require PlugExtraData to be queryable
                            if (string.Equals(device.Name, sceneName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Also verify this device is for the right hub if PlugExtraData is available
                                bool isRightHub = true;
                                if (device.PlugExtraData != null && device.PlugExtraData.ContainsNamed("HubIp"))
                                {
                                    var storedHubIp = device.PlugExtraData["HubIp"].ToString();
                                    isRightHub = string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase);
                                }
                                
                                if (isRightHub)
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Info, $"Found scene by name '{sceneName}' (ref {device.Ref})", Name);
                                    return device;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error finding scene device: {ex.Message}", Name);
            }
            return null;
        }

        private int CreateSceneDevice(PowerViewScene scene, string hubIp)
        {
            try
            {
                var sceneName = string.IsNullOrWhiteSpace(scene.PtName) ? scene.Name : scene.PtName;
                
                // Extract room name from scene name for Floor organization
                // E.g., "Dining Room Open" → "Dining Room"
                var roomLocation = ExtractRoomFromSceneName(sceneName);
                if (string.IsNullOrEmpty(roomLocation))
                {
                    roomLocation = "PowerView"; // Fallback if extraction fails
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Creating scene device: {sceneName} (ID: {scene.Id}, Room: {roomLocation})", Name);

                var df = DeviceFactory.CreateDevice(Id);
                df = df.WithName(sceneName)
                       .WithLocation(roomLocation)
                       .WithLocation2("Scenes");

                // Single control to activate the scene
                var ff = FeatureFactory.CreateGenericBinaryControl(Id, "Activate", "Run", "Idle", 100, 0)
                    .WithLocation(roomLocation)
                    .WithLocation2("Scenes");
                df.WithFeature(ff);

                var deviceData = df.PrepareForHs();
                var devRef = HomeSeerSystem.CreateDevice(deviceData);

                // Get existing PlugExtraData (might be properly initialized by CreateDevice)
                // If not, create a new one
                var extraData = HomeSeerSystem.GetPropertyByRef(devRef, EProperty.PlugExtraData);
                var extra = extraData as PlugExtraData ?? new PlugExtraData();
                
                extra.AddNamed("SceneId", scene.Id.ToString());
                extra.AddNamed("HubIp", hubIp);
                extra.AddNamed("NetworkNumber", scene.NetworkNumber.ToString());
                HomeSeerSystem.UpdatePropertyByRef(devRef, EProperty.PlugExtraData, extra);
                
                // Save to INI as persistent backup (primary lookup source)
                try
                {
                    var iniKey = $"{hubIp}:{scene.Id}";
                    var iniValue = devRef.ToString();
                    HomeSeerSystem.SaveINISetting("Scenes", iniKey, iniValue, Id + ".ini");
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Scene device created: ref {devRef}, Location='{roomLocation}', Scene ID {scene.Id} (saved to INI)", Name);
                }
                catch (Exception ex)
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to save scene mapping to INI: {ex.Message}", Name);
                }

                return devRef;
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error creating scene device {scene.Id}: {ex.Message}", Name);
                return -1;
            }
        }

        private string ExtractRoomFromSceneName(string sceneName)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
                return null;

            // Keywords to remove (case-insensitive, longest first to avoid partial matches)
            var keywords = new[] { " privacy", " open", " close", " on", " off", " raise", " lower", " stop" };
            var name = sceneName.Trim();

            foreach (var keyword in keywords)
            {
                if (name.EndsWith(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    var extracted = name.Substring(0, name.Length - keyword.Length).Trim();
                    if (!string.IsNullOrEmpty(extracted))
                    {
                        return extracted;
                    }
                }
            }

            // If no keywords matched, return the whole name
            return string.IsNullOrEmpty(name) ? null : name;
        }

        private void CleanupObsoleteScenes(List<PowerViewScene> discoveredScenes, string hubIp)
        {
            try
            {
                var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in discoveredScenes)
                {
                    valid.Add($"{s.Id}:{hubIp}");
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Cleanup: {discoveredScenes.Count} valid scenes for hub {hubIp}", Name);

                var toDelete = new List<HsDevice>();
                var keepers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null) continue;
                    if (device.Interface != Id || device.PlugExtraData == null || !device.PlugExtraData.ContainsNamed("SceneId"))
                        continue;
                    try
                    {
                        var idStr = device.PlugExtraData["SceneId"].ToString();
                        var storedHub = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : string.Empty;
                        if (string.IsNullOrWhiteSpace(storedHub)) storedHub = hubIp; // treat missing HubIp as current hub
                        if (!int.TryParse(idStr, out int storedId)) continue;
                        var key = $"{storedId}:{storedHub}";
                        
                        // Only clean up scenes for THIS hub to avoid deleting scenes from other hubs
                        if (!string.Equals(storedHub, hubIp, StringComparison.OrdinalIgnoreCase))
                            continue;
                        
                        if (!valid.Contains(key))
                        {
                            LogVerbose($"Scene device ref {device.Ref} ({device.Name}) marked for deletion - not in current discovery");
                            toDelete.Add(device);
                        }
                        else
                        {
                            // Deduplicate: keep first device per scene key, delete extras
                            if (keepers.ContainsKey(key))
                            {
                                LogVerbose($"Scene device ref {device.Ref} ({device.Name}) marked for deletion - duplicate");
                                toDelete.Add(device);
                            }
                            else
                            {
                                keepers[key] = device.Ref;
                            }
                        }
                    }
                    catch (Exception ex) 
                    { 
                        HomeSeerSystem.WriteLog(ELogType.Error, $"ERROR in scene device scan at ref {ref_num}: {ex.Message}", Name);
                    }
                }

                // Remove any stray Scene devices (location "Scenes") that lack SceneId and ShadeId metadata
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null) continue;
                    if (device.Interface != Id) continue;
                    var ped = device.PlugExtraData as PlugExtraData;
                    if (ped != null && (ped.ContainsNamed("SceneId") || ped.ContainsNamed("ShadeId")))
                        continue;
                    // If the device was created as a scene (Location2 "Scenes"), but lacks metadata, delete it
                    if (string.Equals(device.Location2, "Scenes", StringComparison.OrdinalIgnoreCase))
                    {
                        LogVerbose($"Scene device ref {device.Ref} ({device.Name}) marked for deletion - missing metadata");
                        toDelete.Add(device);
                    }
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Cleanup: Deleting {toDelete.Count} obsolete/duplicate scene devices", Name);

                foreach (var d in toDelete)
                {
                    try
                    {
                        HomeSeerSystem.DeleteDevice(d.Ref);
                        LogVerbose($"Deleted scene device '{d.Name}' ref {d.Ref}");
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting scene device ref {d.Ref}: {ex.Message}", Name);
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"CRITICAL ERROR in CleanupObsoleteScenes: {ex.Message}", Name);
                HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
            }
        }

        private async Task SyncScenesAsync(PowerViewClient client, string hubIp)
        {
            try
            {
                var scenes = await client.GetScenesAsync();
                if (scenes == null)
                    scenes = new List<PowerViewScene>();

                // If no scenes returned, this is likely a secondary hub in multi-gateway setup
                if (scenes.Count == 0)
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, $"No scenes available from hub {hubIp} (expected for secondary hub in multi-gateway setup)", Name);
                    return;
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Syncing {scenes.Count} scenes from hub {hubIp}", Name);
                
                // Log each scene discovered
                foreach (var scene in scenes)
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, $"  Scene: ID={scene.Id}, Name={scene.Name}, PtName={scene.PtName}", Name);
                }

                var created = 0;
                var skipped = 0;
                foreach (var scene in scenes)
                {
                    try
                    {
                        // Always do device scan to find existing scenes by their metadata
                        // This ensures scene device reference codes persist across HomeSeer restarts
                        var existing = FindDeviceBySceneId(scene.Id, hubIp, scene.PtName ?? scene.Name, skipDeviceScan: false);
                        if (existing == null)
                        {
                            // EXTRA CHECK: Do one more scan to catch any devices we might have missed
                            // This prevents duplicate scene device creation
                            int duplicateCheckRef = -1;
                            for (int ref_num = 1; ref_num < 20000; ref_num++)
                            {
                                var checkDevice = HomeSeerSystem.GetDeviceByRef(ref_num);
                                if (checkDevice == null) continue;
                                if (checkDevice.Interface != Id || checkDevice.PlugExtraData == null) continue;
                                if (!checkDevice.PlugExtraData.ContainsNamed("SceneId")) continue;
                                
                                var checkSceneId = checkDevice.PlugExtraData["SceneId"].ToString();
                                var checkHubIp = checkDevice.PlugExtraData.ContainsNamed("HubIp") ? checkDevice.PlugExtraData["HubIp"].ToString() : "";
                                
                                if (checkSceneId == scene.Id.ToString() && string.Equals(checkHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                                {
                                    duplicateCheckRef = ref_num;
                                    HomeSeerSystem.WriteLog(ELogType.Warning, $"DUPLICATE CHECK: Found existing scene device {checkDevice.Name} (ref {ref_num}) matching scene {scene.Id} on hub {hubIp}. Using existing instead of creating new.", Name);
                                    break;
                                }
                            }
                            
                            if (duplicateCheckRef > 0)
                            {
                                var duplicateDevice = HomeSeerSystem.GetDeviceByRef(duplicateCheckRef);
                                skipped++;
                                var ped = HomeSeerSystem.GetPropertyByRef(duplicateCheckRef, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                                if (!ped.ContainsNamed("HubIp")) ped.AddNamed("HubIp", hubIp);
                                if (!ped.ContainsNamed("SceneId")) ped.AddNamed("SceneId", scene.Id.ToString());
                                if (!ped.ContainsNamed("NetworkNumber")) ped.AddNamed("NetworkNumber", scene.NetworkNumber.ToString());
                                HomeSeerSystem.UpdatePropertyByRef(duplicateCheckRef, EProperty.PlugExtraData, ped);
                            }
                            else
                            {
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Creating new scene device: {scene.PtName ?? scene.Name} (ID {scene.Id})", Name);
                                int newDevRef = CreateSceneDevice(scene, hubIp);
                                created++;
                                
                                if (newDevRef > 0)
                                {
                                    // Verify the device can be found via INI lookup (which we just saved in CreateSceneDevice)
                                    var verifyDevice = FindDeviceBySceneId(scene.Id, hubIp, scene.PtName ?? scene.Name, skipDeviceScan: true);
                                    if (verifyDevice != null)
                                    {
                                        HomeSeerSystem.WriteLog(ELogType.Info, $"Verified newly created scene {scene.Id} (ref {newDevRef}) is findable via INI", Name);
                                    }
                                    else
                                    {
                                        HomeSeerSystem.WriteLog(ELogType.Warning, $"WARNING: Just-created scene {scene.Id} (ref {newDevRef}) could not be found via INI lookup", Name);
                                    }
                                }
                                else
                                {
                                    HomeSeerSystem.WriteLog(ELogType.Error, $"ERROR: Failed to create scene device {scene.Id} - CreateSceneDevice returned {newDevRef}", Name);
                                }
                            }
                        }
                        else
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Scene {scene.Id} ({scene.PtName ?? scene.Name}) already exists at ref {existing.Ref}, skipping", Name);
                            skipped++;
                            // Ensure PlugExtraData has hub & type
                            var ped = HomeSeerSystem.GetPropertyByRef(existing.Ref, EProperty.PlugExtraData) as PlugExtraData ?? new PlugExtraData();
                            if (!ped.ContainsNamed("HubIp")) ped.AddNamed("HubIp", hubIp);
                            if (!ped.ContainsNamed("SceneId")) ped.AddNamed("SceneId", scene.Id.ToString());
                            if (!ped.ContainsNamed("NetworkNumber")) ped.AddNamed("NetworkNumber", scene.NetworkNumber.ToString());
                            HomeSeerSystem.UpdatePropertyByRef(existing.Ref, EProperty.PlugExtraData, ped);
                        }
                    }
                    catch (Exception sceneEx)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"ERROR processing scene {scene.Id} ({scene.PtName ?? scene.Name}): {sceneEx.Message}", Name);
                        HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {sceneEx.StackTrace}", Name);
                        // Continue to next scene instead of breaking
                    }
                }
                HomeSeerSystem.WriteLog(ELogType.Info, $"Scene sync complete: {created} created, {skipped} already existed. Total scenes: {scenes.Count}", Name);
                
                // After scenes are synced, update all existing shade devices with scene links
                await UpdateAllShadeScenesAsync(client, hubIp);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error syncing scenes on hub {hubIp}: {ex.Message}", Name);
                HomeSeerSystem.WriteLog(ELogType.Error, $"StackTrace: {ex.StackTrace}", Name);
            }
        }

        private async Task UpdateAllShadeScenesAsync(PowerViewClient client, string hubIp)
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Updating scene links for all existing shade devices on hub {hubIp}...", Name);
                
                // Get all scenes for this hub
                var scenes = await client.GetScenesAsync();
                if (scenes == null || scenes.Count == 0)
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, $"No scenes found to link to shades on hub {hubIp}", Name);
                    return;
                }

                // Find all shade devices for this hub
                var allDevices = HomeSeerSystem.GetAllDevices(withFeatures: false);
                int updated = 0;
                int skipped = 0;

                foreach (var device in allDevices)
                {
                    // Only process devices from PowerView plugin (check interface ID)
                    if (device.Interface != Id)
                        continue;

                    // Only process parent devices (not features)
                    if (device.Relationship != ERelationship.Device)
                        continue;

                    // Only process shade devices (Location2 = "Shades")
                    if (!string.Equals(device.Location2, "Shades", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Get PlugExtraData
                    var ped = HomeSeerSystem.GetPropertyByRef(device.Ref, EProperty.PlugExtraData) as PlugExtraData;
                    if (ped == null || !ped.ContainsNamed("ShadeId"))
                    {
                        skipped++;
                        continue;
                    }

                    // Check if this device is for this hub
                    string deviceHubIp = ped.ContainsNamed("HubIp") ? ped["HubIp"].ToString() : null;
                    if (!string.Equals(deviceHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped++;
                        continue;
                    }

                    // Get shade ID
                    if (!int.TryParse(ped["ShadeId"].ToString(), out int shadeId))
                    {
                        skipped++;
                        continue;
                    }

                    // Find scenes that control this shade (using both ShadeIds and name-based matching)
                    int? openSceneId = null;
                    int? closeSceneId = null;
                    int? privacySceneId = null;

                    // First pass: try ShadeIds-based matching (most reliable)
                    foreach (var scene in scenes)
                    {
                        if (scene.ShadeIds != null && scene.ShadeIds.Contains(shadeId))
                        {
                            if (scene.NetworkNumber == 45057)
                                openSceneId = scene.Id;
                            else if (scene.NetworkNumber == 45058)
                                closeSceneId = scene.Id;
                            else if (scene.NetworkNumber == 45060)
                                privacySceneId = scene.Id;
                        }
                    }

                    // Second pass: if ShadeIds matching didn't find scenes, try name-based matching
                    // This handles cases where scenes were reorganized and ShadeIds weren't properly updated on the hub
                    if (!openSceneId.HasValue || !closeSceneId.HasValue || !privacySceneId.HasValue)
                    {
                        foreach (var scene in scenes)
                        {
                            var sceneName = (scene.PtName ?? scene.Name ?? "").ToLowerInvariant();
                            
                            // Try name-based matching on all scenes (not just those listed in ShadeIds)
                            if (!openSceneId.HasValue && (sceneName.Contains("open") || sceneName.Contains("up") || sceneName.Contains("raise")))
                            {
                                openSceneId = scene.Id;
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Open scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id}) for shade {shadeId}", Name);
                            }
                            else if (!closeSceneId.HasValue && (sceneName.Contains("close") || sceneName.Contains("down") || sceneName.Contains("lower")))
                            {
                                closeSceneId = scene.Id;
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Close scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id}) for shade {shadeId}", Name);
                            }
                            else if (!privacySceneId.HasValue && sceneName.Contains("privacy"))
                            {
                                privacySceneId = scene.Id;
                                HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Privacy scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id}) for shade {shadeId}", Name);
                            }
                        }
                    }

                    // Update PlugExtraData if we found new scenes or need to update existing ones
                    bool needsUpdate = false;
                    if (openSceneId.HasValue && (!ped.ContainsNamed("SceneOpenId") || ped["SceneOpenId"].ToString() != openSceneId.Value.ToString()))
                    {
                        ped.AddNamed("SceneOpenId", openSceneId.Value.ToString());
                        needsUpdate = true;
                    }
                    if (closeSceneId.HasValue && (!ped.ContainsNamed("SceneCloseId") || ped["SceneCloseId"].ToString() != closeSceneId.Value.ToString()))
                    {
                        ped.AddNamed("SceneCloseId", closeSceneId.Value.ToString());
                        needsUpdate = true;
                    }
                    if (privacySceneId.HasValue && (!ped.ContainsNamed("ScenePrivacyId") || ped["ScenePrivacyId"].ToString() != privacySceneId.Value.ToString()))
                    {
                        ped.AddNamed("ScenePrivacyId", privacySceneId.Value.ToString());
                        needsUpdate = true;
                    }

                    if (needsUpdate)
                    {
                        HomeSeerSystem.UpdatePropertyByRef(device.Ref, EProperty.PlugExtraData, ped);
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Updated scenes for {device.Name}: Open={openSceneId?.ToString() ?? "none"}, Close={closeSceneId?.ToString() ?? "none"}, Privacy={privacySceneId?.ToString() ?? "none"}", Name);
                        updated++;
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Debug, $"Shade {device.Name} (ID {shadeId}) already has current scene links or no matching scenes found", Name);
                        skipped++;
                    }
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Scene link update complete: {updated} shades updated, {skipped} skipped", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error updating shade scene links: {ex.Message}", Name);
            }
        }

        private async Task LinkScenesToShade(int shadeDeviceRef, int shadeId, string hubIp)
        {
            try
            {
                var client = GetClientByHubIp(hubIp);
                if (client == null)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Cannot link scenes to shade {shadeId}: no client for hub {hubIp}", Name);
                    return;
                }

                var scenes = await client.GetScenesAsync();
                if (scenes == null || scenes.Count == 0)
                {
                    // This is expected for secondary hubs in multi-gateway setup
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Shade {shadeId}: No scenes available (expected for secondary hub)", Name);
                    return;
                }

                // Find scenes that control this shade
                int? openSceneId = null;
                int? closeSceneId = null;
                int? privacySceneId = null;

                // Get the shade device to extract its name for matching
                var shadeDevice = HomeSeerSystem.GetDeviceByRef(shadeDeviceRef);
                string shadeName = shadeDevice?.Name ?? "";

                foreach (var scene in scenes)
                {
                    // Check if this scene controls this shade
                    if (scene.ShadeIds != null && scene.ShadeIds.Contains(shadeId))
                    {
                        var sceneName = (scene.PtName ?? scene.Name ?? "").ToLowerInvariant();
                        
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Checking scene '{scene.PtName ?? scene.Name}' (ID: {scene.Id}, NetworkNumber: {scene.NetworkNumber}) for shade {shadeId}", Name);
                        
                        // First try NetworkNumber identification (for standard PowerView scenes)
                        if (scene.NetworkNumber == 45057)
                            openSceneId = scene.Id;
                        else if (scene.NetworkNumber == 45058)
                            closeSceneId = scene.Id;
                        else if (scene.NetworkNumber == 45060)
                            privacySceneId = scene.Id;
                        
                        // Also try name-based matching (for custom scenes)
                        // Look for keywords: "open", "up", "raise" vs "close", "down", "lower"
                        if (!openSceneId.HasValue && (sceneName.Contains("open") || sceneName.Contains("up") || sceneName.Contains("raise")))
                        {
                            openSceneId = scene.Id;
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Open scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id})", Name);
                        }
                        else if (!closeSceneId.HasValue && (sceneName.Contains("close") || sceneName.Contains("down") || sceneName.Contains("lower")))
                        {
                            closeSceneId = scene.Id;
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Close scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id})", Name);
                        }
                        else if (!privacySceneId.HasValue && sceneName.Contains("privacy"))
                        {
                            privacySceneId = scene.Id;
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Matched Privacy scene by name: '{scene.PtName ?? scene.Name}' (ID: {scene.Id})", Name);
                        }
                    }
                }

                // Store scene IDs in PlugExtraData
                var ped = HomeSeerSystem.GetPropertyByRef(shadeDeviceRef, EProperty.PlugExtraData) as PlugExtraData;
                if (ped != null)
                {
                    if (openSceneId.HasValue)
                    {
                        ped.AddNamed("SceneOpenId", openSceneId.Value.ToString());
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Linked Open scene {openSceneId.Value} to shade {shadeId}", Name);
                    }
                    if (closeSceneId.HasValue)
                    {
                        ped.AddNamed("SceneCloseId", closeSceneId.Value.ToString());
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Linked Close scene {closeSceneId.Value} to shade {shadeId}", Name);
                    }
                    if (privacySceneId.HasValue)
                    {
                        ped.AddNamed("ScenePrivacyId", privacySceneId.Value.ToString());
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Linked Privacy scene {privacySceneId.Value} to shade {shadeId}", Name);
                    }
                    HomeSeerSystem.UpdatePropertyByRef(shadeDeviceRef, EProperty.PlugExtraData, ped);
                    // Scene IDs are now stored in PlugExtraData and will be found via device scan in future polls
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error linking scenes to shade {shadeId}: {ex.Message}", Name);
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

        private void LogVerbose(string message)
        {
            if (_verboseLogging)
            {
                HomeSeerSystem.WriteLog(ELogType.Info, message, Name);
            }
        }

        private string FormatShadeName(int shadeId, string originalName)
        {
            // Keep just the label for user-friendly display; shade ID is stored in PlugExtraData
            var label = (originalName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(label)) return $"Shade {shadeId}";
            return label;
        }

        private string ExtractOriginalLabel(string formattedName)
        {
            // Extract label from "Shade 123 - Label" format
            if (string.IsNullOrEmpty(formattedName)) return string.Empty;
            var parts = formattedName.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
                return parts[parts.Length - 1];
            return formattedName;
        }

        private void CleanupOldStatusDevices(int shadeId, string hubIp)
        {
            try
            {
                // Remove all old-style and duplicate status devices for this shade
                // Keep only one device per status type (Position, Battery, etc)
                var statusDevicesByType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var toDelete = new List<int>();

                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var d = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (d == null) continue;
                    if (d.Interface != Id || d.PlugExtraData == null) continue;
                    try
                    {
                        var ped = d.PlugExtraData;
                        if (!ped.ContainsNamed("StatusType")) continue;
                        if (!ped.ContainsNamed("ShadeId") || !ped.ContainsNamed("HubIp")) continue;
                        if (!int.TryParse(ped["ShadeId"].ToString(), out int sid)) continue;
                        var hip = ped["HubIp"].ToString();
                        if (sid != shadeId || !string.Equals(hip, hubIp, StringComparison.OrdinalIgnoreCase)) continue;

                        var statusType = ped["StatusType"].ToString();
                        
                        // If this is an old-style name (missing shade ID), mark for deletion
                        if (!d.Name.Contains($"Shade {shadeId}"))
                        {
                            toDelete.Add(d.Ref);
                        }
                        // If we already have a keeper for this status type, mark this as duplicate
                        else if (statusDevicesByType.ContainsKey(statusType))
                        {
                            toDelete.Add(d.Ref);
                        }
                        else
                        {
                            statusDevicesByType[statusType] = d.Ref;
                        }
                    }
                    catch { }
                }

                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var oldDev = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (oldDev != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                            LogVerbose($"Deleted duplicate/old status device '{oldDev.Name}' (ref {ref_num})");
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting status device ref {ref_num}: {ex.Message}", Name);
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error cleaning up old status devices for shade {shadeId}: {ex.Message}", Name);
            }
        }

        private void DeleteAllPowerViewDevices()
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Warning, "DELETING ALL POWERVIEW DEVICES - This will clean up all duplicates...", Name);
                var toDelete = new List<int>();
                
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var d = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (d == null) continue;
                    if (d.Interface != Id) continue;
                    
                    toDelete.Add(d.Ref);
                }
                
                HomeSeerSystem.WriteLog(ELogType.Warning, $"Found {toDelete.Count} PowerView devices to delete", Name);
                
                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var dev = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (dev != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting device ref {ref_num}: {ex.Message}", Name);
                    }
                }
                
                // Clear all INI sections using HomeSeer API
                try
                {
                    HomeSeerSystem.ClearIniSection("Devices", Id + ".ini");
                    HomeSeerSystem.ClearIniSection("Status", Id + ".ini");
                    HomeSeerSystem.ClearIniSection("Scenes", Id + ".ini");
                    HomeSeerSystem.WriteLog(ELogType.Info, "Cleared all INI sections via API", Name);
                }
                catch (Exception iniEx)
                {
                    HomeSeerSystem.WriteLog(ELogType.Warning, $"Error clearing INI sections via API: {iniEx.Message}", Name);
                }
                
                HomeSeerSystem.WriteLog(ELogType.Warning, $"Deleted {toDelete.Count} PowerView devices. Ready for fresh discovery.", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting all PowerView devices: {ex.Message}", Name);
            }
        }

        private void GlobalCleanupStatusDevices()
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, "Running global cleanup of duplicate status devices...", Name);
                // Build unique key per shade+statusType across all hubs
                var statusDevices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var toDelete = new List<int>();
                int totalFound = 0;
                var duplicatesByShade = new Dictionary<int, int>();

                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var d = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (d == null) continue;
                    if (d.Interface != Id || d.PlugExtraData == null) continue;
                    try
                    {
                        var ped = d.PlugExtraData;
                        if (!ped.ContainsNamed("StatusType")) continue;
                        if (!ped.ContainsNamed("ShadeId") || !ped.ContainsNamed("HubIp")) continue;
                        if (!int.TryParse(ped["ShadeId"].ToString(), out int sid)) continue;
                        var hip = ped["HubIp"].ToString();
                        var statusType = ped["StatusType"].ToString();
                        var key = $"{hip}:{sid}:{statusType}";
                        
                        totalFound++;
                        
                        // Old-style name without shade ID - always delete
                        if (!d.Name.Contains($"Shade {sid}"))
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"  Marking old-style device for deletion: {d.Name} (ref {d.Ref})", Name);
                            toDelete.Add(d.Ref);
                        }
                        // Duplicate for this shade+type
                        else if (statusDevices.ContainsKey(key))
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"  Marking duplicate for deletion: {d.Name} (ref {d.Ref}), keeping ref {statusDevices[key]}", Name);
                            toDelete.Add(d.Ref);
                            if (!duplicatesByShade.ContainsKey(sid)) duplicatesByShade[sid] = 0;
                            duplicatesByShade[sid]++;
                        }
                        else
                        {
                            statusDevices[key] = d.Ref;
                        }
                    }
                    catch { }
                }

                HomeSeerSystem.WriteLog(ELogType.Info, $"Found {totalFound} status devices, {toDelete.Count} marked for deletion", Name);
                if (duplicatesByShade.Count > 0)
                {
                    foreach (var kvp in duplicatesByShade)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"  Shade {kvp.Key} has {kvp.Value} duplicate status devices", Name);
                    }
                }
                
                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var oldDev = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (oldDev != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                            LogVerbose($"Deleted duplicate status device '{oldDev.Name}' (ref {ref_num})");
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting status device ref {ref_num}: {ex.Message}", Name);
                    }
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Global status device cleanup complete. Deleted {toDelete.Count} duplicates.", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in GlobalCleanupStatusDevices: {ex.Message}", Name);
            }
        }

        private void DeleteAllShadeDevicesForHub(string hubIp)
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Deleting ALL shade devices for hub {hubIp} before re-discovery...", Name);
                var toDelete = new List<int>();
                var shadeIdsForThisHub = new List<int>();
                
                // Scan all devices for any shades belonging to this hub
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null) continue;
                    if (device.Interface != Id || device.PlugExtraData == null) continue;
                    if (!device.PlugExtraData.ContainsNamed("ShadeId")) continue;
                    
                    try
                    {
                        var storedHubIp = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : null;
                        
                        // If this shade belongs to the hub we're re-discovering, mark it for deletion
                        if (string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(device.PlugExtraData["ShadeId"].ToString(), out int shadeId))
                            {
                                shadeIdsForThisHub.Add(shadeId);
                            }
                            HomeSeerSystem.WriteLog(ELogType.Info, $"  Marking shade for deletion: {device.Name} (ref {device.Ref})", Name);
                            toDelete.Add(device.Ref);
                        }
                    }
                    catch { }
                }
                
                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Deleted shade device '{device.Name}' (ref {ref_num})", Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting shade ref {ref_num}: {ex.Message}", Name);
                    }
                }
                
                // Also clear INI mappings for these shades so FindDeviceByShadeId starts fresh
                try
                {
                    foreach (var shadeId in shadeIdsForThisHub)
                    {
                        var iniKey = $"{hubIp}:{shadeId}";
                        HomeSeerSystem.SaveINISetting("Devices", iniKey, string.Empty, Id + ".ini");
                        HomeSeerSystem.WriteLog(ELogType.Info, $"  Cleared INI mapping for shade {shadeId}", Name);
                    }
                }
                catch (Exception ex)
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"Error clearing INI mappings for hub {hubIp}: {ex.Message}", Name);
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Shade deletion complete for hub {hubIp}. Deleted {toDelete.Count} devices and cleared INI mappings.", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in DeleteAllShadeDevicesForHub: {ex.Message}", Name);
            }
        }

        private void DeleteAllSceneDevicesForHub(string hubIp)
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Deleting ALL scene devices for hub {hubIp} before re-sync...", Name);
                var toDelete = new List<int>();
                var sceneIdsForThisHub = new List<int>();
                
                // Scan all devices for any scenes belonging to this hub
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null) continue;
                    if (device.Interface != Id || device.PlugExtraData == null) continue;
                    if (!device.PlugExtraData.ContainsNamed("SceneId")) continue;
                    
                    try
                    {
                        var storedHubIp = device.PlugExtraData.ContainsNamed("HubIp") ? device.PlugExtraData["HubIp"].ToString() : null;
                        
                        // If this scene belongs to the hub we're re-syncing, mark it for deletion
                        if (string.Equals(storedHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(device.PlugExtraData["SceneId"].ToString(), out int sceneId))
                            {
                                sceneIdsForThisHub.Add(sceneId);
                            }
                            HomeSeerSystem.WriteLog(ELogType.Info, $"  Marking scene for deletion: {device.Name} (ref {device.Ref})", Name);
                            toDelete.Add(device.Ref);
                        }
                    }
                    catch { }
                }
                
                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Deleted scene device '{device.Name}' (ref {ref_num})", Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting scene ref {ref_num}: {ex.Message}", Name);
                    }
                }
                
                // Also clear INI mappings for these scenes so FindDeviceBySceneId starts fresh
                try
                {
                    foreach (var sceneId in sceneIdsForThisHub)
                    {
                        var iniKey = $"{hubIp}:{sceneId}";
                        HomeSeerSystem.SaveINISetting("Scenes", iniKey, string.Empty, Id + ".ini");
                        HomeSeerSystem.WriteLog(ELogType.Info, $"  Cleared INI mapping for scene {sceneId}", Name);
                    }
                }
                catch (Exception ex)
                {
                    HomeSeerSystem.WriteLog(ELogType.Error, $"Error clearing INI mappings for scenes on hub {hubIp}: {ex.Message}", Name);
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Scene deletion complete for hub {hubIp}. Deleted {toDelete.Count} devices and cleared INI mappings.", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in DeleteAllSceneDevicesForHub: {ex.Message}", Name);
            }
        }

        private void CleanupOrphanSceneDevices()
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, "Cleaning up orphan scene devices (scenes no longer on hubs)...", Name);
                var toDelete = new List<int>();
                
                // Get current hubs and their scenes
                var hubScenes = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
                foreach (var client in _clients)
                {
                    try
                    {
                        var scenes = client.GetScenesAsync().Result;
                        if (scenes != null)
                        {
                            var sceneIds = new HashSet<int>(scenes.Select(s => s.Id));
                            hubScenes[client.HubIp] = sceneIds;
                        }
                    }
                    catch { /* Skip if can't reach hub */ }
                }
                
                // Scan all devices for orphan scenes
                for (int ref_num = 1; ref_num < 20000; ref_num++)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                    if (device == null) continue;
                    if (device.Interface != Id || device.PlugExtraData == null) continue;
                    if (!device.PlugExtraData.ContainsNamed("SceneId")) continue;
                    
                    try
                    {
                        var ped = device.PlugExtraData;
                        if (!int.TryParse(ped["SceneId"].ToString(), out int sceneId)) continue;
                        var hubIp = ped.ContainsNamed("HubIp") ? ped["HubIp"].ToString() : null;
                        
                        if (string.IsNullOrEmpty(hubIp)) continue;
                        
                        // Check if this scene exists on its hub
                        if (!hubScenes.ContainsKey(hubIp) || !hubScenes[hubIp].Contains(sceneId))
                        {
                            HomeSeerSystem.WriteLog(ELogType.Info, $"  Marking orphan scene for deletion: {device.Name} (ref {device.Ref}, sceneId={sceneId}, hub={hubIp})", Name);
                            toDelete.Add(device.Ref);
                        }
                    }
                    catch { }
                }
                
                foreach (var ref_num in toDelete)
                {
                    try
                    {
                        var device = HomeSeerSystem.GetDeviceByRef(ref_num);
                        if (device != null)
                        {
                            HomeSeerSystem.DeleteDevice(ref_num);
                            HomeSeerSystem.WriteLog(ELogType.Info, $"Deleted orphan scene device '{device.Name}' (ref {ref_num})", Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Error, $"Error deleting orphan scene ref {ref_num}: {ex.Message}", Name);
                    }
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Orphan scene cleanup complete. Deleted {toDelete.Count} devices.", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error in CleanupOrphanSceneDevices: {ex.Message}", Name);
            }
        }

        private List<string> GetHubIps()
        {
            var ips = new List<string>();
            try
            {
                var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                
                if (File.Exists(configPath))
                {
                    var lines = File.ReadAllLines(configPath);
                    
                    foreach (var line in lines)
                    {
                        // Look for HubIPs or HubIP key
                        if (line.Contains("HubIPs="))
                        {
                            var value = line.Split('=')[1].Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                var parts = value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var part in parts)
                                {
                                    var ip = part.Trim();
                                    if (!string.IsNullOrEmpty(ip))
                                    {
                                        ips.Add(ip);
                                    }
                                }
                            }
                        }
                        else if (line.Contains("HubIP=") && !line.Contains("HubIPs="))
                        {
                            var value = line.Split('=')[1].Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                var parts = value.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var part in parts)
                                {
                                    var ip = part.Trim();
                                    if (!string.IsNullOrEmpty(ip))
                                    {
                                        ips.Add(ip);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error reading hub IPs from {SettingsFileName}: {ex.Message}", Name);
            }
            
            return ips;
        }

        private PowerViewClient GetClientByHubIp(string hubIp)
        {
            var hubIps = GetHubIps();
            var index = hubIps.FindIndex(h => string.Equals(h, hubIp, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < _clients.Count)
                return _clients[index];
            
            // Check if we already have a client for this gateway IP
            var existingClient = _clients.FirstOrDefault(c => string.Equals(c.HubIp, hubIp, StringComparison.OrdinalIgnoreCase));
            if (existingClient != null)
                return existingClient;
            
            // Gateway IP not found - create a new client for it dynamically
            HomeSeerSystem.WriteLog(ELogType.Info, $"Creating dynamic client for gateway IP {hubIp}", Name);
            try
            {
                var newClient = new PowerViewClient(hubIp, msg => HomeSeerSystem.WriteLog(ELogType.Info, msg, Name), _cloudClient);
                _clients.Add(newClient);
                return newClient;
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Failed to create client for gateway {hubIp}: {ex.Message}", Name);
            }
            
            // Last resort - use primary client
            if (_clients.Count == 1)
                return _clients[0];
            return null;
        }

        private void InitializeCloudApi()
        {
            try
            {
                var iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SettingsFileName);
                HomeSeerSystem.WriteLog(ELogType.Info, $"Looking for INI file at: {iniPath}", Name);
                HomeSeerSystem.WriteLog(ELogType.Info, $"INI file exists: {File.Exists(iniPath)}", Name);
                
                var cloudEmail = HomeSeerSystem.GetINISetting("CloudAPI", "Email", string.Empty, SettingsFileName);
                var cloudPassword = HomeSeerSystem.GetINISetting("CloudAPI", "Password", string.Empty, SettingsFileName);

                HomeSeerSystem.WriteLog(ELogType.Info, $"Read Email: '{cloudEmail}' (empty={string.IsNullOrEmpty(cloudEmail)})", Name);
                HomeSeerSystem.WriteLog(ELogType.Info, $"Read Password: '{new string('*', cloudPassword?.Length ?? 0)}' (empty={string.IsNullOrEmpty(cloudPassword)})", Name);

                // If credentials aren't found, try writing them using HomeSeer's API (in case manual file edit had encoding issues)
                if (string.IsNullOrEmpty(cloudEmail) || string.IsNullOrEmpty(cloudPassword))
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, "Attempting to write cloud credentials using HomeSeer API...", Name);
                    HomeSeerSystem.SaveINISetting("CloudAPI", "Email", "rnicol@1975.usna.com", SettingsFileName);
                    HomeSeerSystem.SaveINISetting("CloudAPI", "Password", "$H756363s!", SettingsFileName);
                    
                    // Re-read them
                    cloudEmail = HomeSeerSystem.GetINISetting("CloudAPI", "Email", string.Empty, SettingsFileName);
                    cloudPassword = HomeSeerSystem.GetINISetting("CloudAPI", "Password", string.Empty, SettingsFileName);
                    HomeSeerSystem.WriteLog(ELogType.Info, $"After API write - Email: '{cloudEmail}' Password: '{new string('*', cloudPassword?.Length ?? 0)}'", Name);
                }

                if (string.IsNullOrEmpty(cloudEmail) || string.IsNullOrEmpty(cloudPassword))
                {
                    HomeSeerSystem.WriteLog(ELogType.Info, "Cloud API credentials not configured. Shade control will only work with Gen2 hubs or if cloud credentials are added.", Name);
                    return;
                }

                _cloudClient = new HunterDouglasCloudClient(cloudEmail, cloudPassword, msg => HomeSeerSystem.WriteLog(ELogType.Info, msg, Name));
                
                // Attempt authentication synchronously to ensure it completes before shade discovery
                HomeSeerSystem.WriteLog(ELogType.Info, "Starting Hunter Douglas cloud authentication...", Name);
                Task.Run(async () =>
                {
                    bool authenticated = await _cloudClient.AuthenticateAsync();
                    if (authenticated)
                    {
                        HomeSeerSystem.WriteLog(ELogType.Info, $"Successfully authenticated with Hunter Douglas cloud API. IsAuthenticated={_cloudClient.IsAuthenticated}", Name);
                    }
                    else
                    {
                        HomeSeerSystem.WriteLog(ELogType.Warning, $"Failed to authenticate with Hunter Douglas cloud API. IsAuthenticated={_cloudClient.IsAuthenticated}. Check credentials in settings.", Name);
                    }
                }).Wait(TimeSpan.FromSeconds(30)); // Wait up to 30 seconds for authentication
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Cloud client authentication status after init: IsAuthenticated={_cloudClient?.IsAuthenticated ?? false}", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error initializing cloud API: {ex.Message}", Name);
            }
        }

        /// <summary>
        /// Re-link scenes to existing shade devices on plugin startup
        /// </summary>
        private async Task RelinkScenesToExistingShades(string hubIp)
        {
            try
            {
                HomeSeerSystem.WriteLog(ELogType.Info, $"Re-linking scenes for hub {hubIp}...", Name);
                
                // Get all devices from HomeSeer
                var allDeviceRefs = HomeSeerSystem.GetRefsByInterface(Id, true);
                
                // Filter to shade devices (parent devices with ShadeId in PlugExtraData)
                foreach (var deviceRef in allDeviceRefs)
                {
                    var device = HomeSeerSystem.GetDeviceByRef(deviceRef);
                    if (device == null || device.Relationship != ERelationship.Device)
                        continue;
                    
                    var ped = HomeSeerSystem.GetPropertyByRef(deviceRef, EProperty.PlugExtraData) as PlugExtraData;
                    if (ped == null || !ped.ContainsNamed("ShadeId"))
                        continue;
                    
                    // Check if this device belongs to the specified hub
                    if (ped.ContainsNamed("HubIp"))
                    {
                        var deviceHubIp = ped["HubIp"]?.ToString();
                        if (!string.Equals(deviceHubIp, hubIp, StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    
                    // This is a shade device for this hub - re-link its scenes
                    var shadeId = int.Parse(ped["ShadeId"].ToString());
                    HomeSeerSystem.WriteLog(ELogType.Info, $"Re-linking scenes for shade {shadeId} ({device.Name})...", Name);
                    await LinkScenesToShade(deviceRef, shadeId, hubIp);
                }
                
                HomeSeerSystem.WriteLog(ELogType.Info, $"Finished re-linking scenes for hub {hubIp}", Name);
            }
            catch (Exception ex)
            {
                HomeSeerSystem.WriteLog(ELogType.Error, $"Error re-linking scenes: {ex.Message}", Name);
            }
        }
    }
}
