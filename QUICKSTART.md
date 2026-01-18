# Quick Start Guide - Hunter Douglas PowerView Plugin

## Installation (5 minutes)

### 1. Deploy Plugin Files
Copy these files to the Hometrooler at `C:\ProgramData\HomeSeer\Plugins\`:
```
HSPI_PowerView.exe
PluginSdk.dll
Newtonsoft.Json.dll
HSCF.dll
HSPI_PowerView.exe.config
```

Or use the deployment script:
```powershell
cd 'c:\Users\Ron.Nicol\OneDrive - ENS\Thermostats\HSPI_PowerView'
.\Deploy-Plugin.ps1
```

### 2. Restart HomeSeer
On the Hometrooler, restart the HomeSeer 4 service

### 3. Verify Installation
Visit http://192.168.3.139 → Settings → Plugins → Installed Plugins  
Look for **"PowerView"** in the list

---

## Configuration (2 minutes)

### 1. Enter Hub IP
In PowerView plugin settings, enter your PowerView Hub IP address

### 2. Test Connection
Click **"Test Connection"** button  
Should see: ✓ Connected to Hub

### 3. Discover Shades
Click **"Discover Shades"** button  
Plugin creates HomeSeer devices for each shade

---

## Control Shades (Immediate)

### From HomeSeer Web Interface
1. Go to **Devices**
2. Find shades (Location: "PowerView", Location2: "Shades")
3. Click **Open** or **Close** buttons
4. Or drag position slider to set position

### Create Scenes for Quick Access
1. Go to **Scenes**
2. Create scene "Open All Shades" - all shades to 100%
3. Create scene "Close All Shades" - all shades to 0%
4. Pin scenes to dashboard for one-click access

### Set Automated Schedules
1. Go to **Automation** → **Scheduled Events**
2. Create trigger (e.g., 9 AM daily)
3. Action: Run "Open All Shades" scene
4. Save and activate

---

## Files Overview

### Core Files
- **HSPI.cs** - Main plugin logic, device management
- **PowerViewClient.cs** - REST API client for Hub communication
- **PowerViewModels.cs** - Data models for API responses

### Documentation
- **README.md** - Full project documentation
- **DEPLOY.md** - Detailed deployment instructions
- **TESTING.md** - Comprehensive testing procedures
- **QUICKSTART.md** - This file

### Build Output
- **bin/Release/HSPI_PowerView.exe** - Compiled plugin (ready to deploy)

---

## Troubleshooting Quick Reference

| Problem | Solution |
|---------|----------|
| Plugin doesn't appear | Restart HomeSeer, check file permissions |
| Can't connect to Hub | Verify Hub IP, ping Hub, check firewall |
| Shades not discovered | Power on Hub, register shades in Hub app |
| Shade won't move | Check battery, try manual control via app |
| Status not updating | Wait 30 seconds, check network connectivity |

---

## Key Features

✅ Automatic shade discovery  
✅ Real-time status updates every 30 seconds  
✅ Individual shade control (Open/Close/Position)  
✅ Scene integration for multi-shade automation  
✅ Scheduled automations (sunrise/sunset, time-based)  
✅ Battery level monitoring  
✅ Error logging and recovery  

---

## Next Steps

1. **Deploy** the plugin to Hometrooler
2. **Configure** PowerView Hub IP in plugin settings
3. **Test** shade control functionality
4. **Automate** using scenes and schedules
5. **Monitor** HomeSeer logs for any issues

---

## Getting Help

- **Plugin Issues**: https://github.com/rnicol75/HomeseerPowerview/issues
- **PowerView API**: https://github.com/jlaur/hdpowerview-doc
- **HomeSeer Docs**: http://192.168.3.139/Html/WebGuide/
- **Plugin Logs**: HomeSeer → Settings → System Log

---

**Plugin Version**: 1.0  
**HomeSeer Compatibility**: 4.x  
**Last Updated**: January 2026
