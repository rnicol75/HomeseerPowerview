# PowerView Plugin Deployment Guide

## Overview
The HSPI_PowerView plugin is built and ready for deployment to your remote HomeSeer 4 instance running on the Hometrooler at `192.168.3.139`.

## Prerequisites
- Built plugin: `bin/Release/HSPI_PowerView.exe` and dependencies
- Remote HomeSeer 4 instance accessible at `192.168.3.139`
- Admin/SSH access to the Hometrooler device

## Deployment Methods

### Method 1: Remote Desktop/SSH Access
If you have remote access to the Hometrooler:

1. **Connect to Hometrooler** via RDP or SSH
2. **Stop HomeSeer 4** service
3. **Copy plugin files** to: `C:\ProgramData\HomeSeer\Plugins\`
4. **Required files:**
   - `HSPI_PowerView.exe` (main plugin executable)
   - `PluginSdk.dll` (dependency from bin/Release)
   - `Newtonsoft.Json.dll` (dependency from bin/Release)
   - `HSCF.dll` (dependency from bin/Release)
5. **Restart HomeSeer 4** service
6. **Verify** in HomeSeer web interface: Settings → Plugins → Installed Plugins

### Method 2: Network Share Upload (if available)
If the Hometrooler exposes plugin folder via network share:

```powershell
$pluginFiles = @(
    'HSPI_PowerView.exe',
    'PluginSdk.dll',
    'Newtonsoft.Json.dll',
    'HSCF.dll'
)

$sourceDir = 'c:\Users\Ron.Nicol\OneDrive - ENS\Thermostats\HSPI_PowerView\bin\Release'
$destDir = '\\192.168.3.139\c$\ProgramData\HomeSeer\Plugins'

foreach ($file in $pluginFiles) {
    Copy-Item "$sourceDir\$file" "$destDir\" -Force
}
```

### Method 3: Manual Upload via HomeSeer Web Interface
1. Navigate to http://192.168.3.139
2. Go to **Settings** → **Plugins** → **Manage Plugins**
3. Look for upload/import option for the PowerView plugin
4. Upload `HSPI_PowerView.exe` with dependencies

## Post-Deployment Testing

### 1. Verify Plugin Registration
- Check HomeSeer logs for initialization messages
- Navigate to: Settings → Plugins → Installed Plugins
- Look for "PowerView" plugin in the list

### 2. Configure Plugin Settings
- Go to PowerView plugin settings page
- Enter PowerView Hub IP address (e.g., `192.168.3.XXX`)
- Click "Test Connection" to verify Hub communication

### 3. Discover Shades
- Click "Discover Shades" to scan for PowerView shades
- Devices should appear in the Devices list
- Test opening/closing individual shades

### 4. Create Scenes/Automations
- Use discovered shade devices to create:
  - Simple open/close buttons
  - Timed automations
  - Event triggers

## Plugin Files

### Main Executable
- **HSPI_PowerView.exe** - Main plugin DLL renamed to .exe format (HomeSeer convention)

### Dependencies
- **PluginSdk.dll** - HomeSeer SDK (v1.5.0)
- **Newtonsoft.Json.dll** - JSON serialization (v13.0.3)
- **HSCF.dll** - HomeSeer core framework

### Configuration
- **HSPI_PowerView.exe.config** - .NET runtime configuration
- **PowerView.ini** - Plugin settings file (created after first run)

## Troubleshooting

### Plugin Won't Load
1. Check file permissions - plugin folder must be readable by HomeSeer service account
2. Verify .NET Framework 4.8.1 is installed on Hometrooler
3. Check HomeSeer logs: `C:\ProgramData\HomeSeer\log\latest.log`

### Hub Connection Fails
1. Verify PowerView Hub IP is correct and reachable
2. Check network connectivity: `ping <hub-ip>`
3. Verify Hub API port (default 80)
4. Check Hub firmware version compatibility

### Shades Not Discovered
1. Verify Hub has shades registered and powered on
2. Check Hub batteries are in good condition
3. Restart hub and try discovery again
4. Check plugin log messages in HomeSeer interface

## Development & Rebuilding

### Rebuild Plugin
```powershell
cd 'c:\Users\Ron.Nicol\OneDrive - ENS\Thermostats\HSPI_PowerView'
& 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe' `
  .\HSPI_PowerView.csproj /t:Build /p:Configuration=Release
```

### Redeploy After Changes
1. Rebuild the solution
2. Stop HomeSeer plugin
3. Replace plugin files in deployment directory
4. Start HomeSeer plugin
5. Verify in web interface

## Support & Documentation

- **HomeSeer SDK**: https://github.com/HomeSeer/Sample-Plugin-CS
- **PowerView API**: https://github.com/jlaur/hdpowerview-doc
- **HomeSeer 4 Docs**: http://192.168.3.139/Html/WebGuide/index.html
