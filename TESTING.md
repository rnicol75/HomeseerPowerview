# Testing Guide - Hunter Douglas PowerView Plugin for HomeSeer 4

## Overview
This document provides step-by-step instructions for testing the HSPI_PowerView plugin after deployment to your remote HomeSeer 4 instance.

## Environment
- **Remote HomeSeer 4**: http://192.168.3.139
- **Plugin Location**: C:\ProgramData\HomeSeer\Plugins\
- **PowerView Hub**: Local network (IP address needed)

## Pre-Testing Checklist

### Prerequisites
- [ ] Plugin files deployed to HomeSeer plugins directory
- [ ] HomeSeer 4 service restarted
- [ ] PowerView Hub powered on and accessible on network
- [ ] Shades registered and responsive on the Hub
- [ ] Network connectivity: Your PC → HomeSeer (192.168.3.139) → PowerView Hub

### Hub Information
Gather the following information for testing:
- PowerView Hub IP: `__________________`
- Number of registered shades: `__________________`
- Shade names: `__________________`

## Test Phase 1: Plugin Installation & Registration

### 1.1 Verify Plugin Installation
1. Navigate to http://192.168.3.139
2. Go to **Settings** → **Plugins** → **Installed Plugins**
3. Look for **"PowerView"** in the list
4. **Expected Result**: Plugin should appear with status showing

### 1.2 Check Plugin Logs
1. In HomeSeer web interface, go to **Settings** → **System Log**
2. Filter for **"PowerView"** entries
3. **Expected Result**: Should see initialization messages like:
   ```
   [PowerView] Initializing PowerView Plugin...
   [PowerView] PowerView Plugin initialized successfully.
   ```

### 1.3 Access Plugin Settings Page
1. In Installed Plugins, click on **PowerView**
2. Should navigate to plugin settings page
3. **Expected Result**: Settings page loads without errors

---

## Test Phase 2: Hub Configuration & Connection

### 2.1 Configure Hub IP Address
1. On PowerView plugin settings page, locate **"Hub IP Address"** field
2. Enter your PowerView Hub IP address (e.g., `192.168.3.XXX`)
3. Click **"Test Connection"** button
4. **Expected Result**: Connection test succeeds, displays:
   ```
   ✓ Connected to PowerView Hub at 192.168.3.XXX
   Firmware version: X.X.X
   ```

### 2.2 Handle Connection Failures
If connection test fails:
1. Verify Hub IP address is correct
2. Verify Hub is powered on (check LED status)
3. Verify network connectivity: From PC, run `ping 192.168.3.XXX`
4. If still failing, check HomeSeer logs for error details
5. **Expected Result**: After fixing connectivity, test succeeds

### 2.3 Save Configuration
1. Ensure Hub IP is entered in settings
2. Click **"Save"** button
3. **Expected Result**: Configuration saved, plugin should reconnect

---

## Test Phase 3: Shade Discovery

### 3.1 Discover Shades
1. On PowerView settings page, click **"Discover Shades"** button
2. Plugin will query Hub for all registered shades
3. Wait for discovery to complete (typically 5-10 seconds)
4. **Expected Result**: Shades discovered and displayed:
   ```
   Found X shades:
   - Shade Name 1
   - Shade Name 2
   - Shade Name 3
   ```

### 3.2 Verify Devices Created
1. Go to **Devices** tab in HomeSeer
2. Look for devices with **Location: "PowerView"** and **Location2: "Shades"**
3. **Expected Result**: Should see one device for each discovered shade with:
   - Device Name: Same as shade name
   - Interface: "PowerView"
   - Status: Value between 0-100 (shade position)

### 3.3 Check Device Details
1. Click on a PowerView shade device
2. Click **"Edit"** or **"Details"**
3. **Expected Result**: Should show:
   - Name: (Shade name)
   - Location: PowerView
   - Location2: Shades
   - Features: Open, Close, Position controls

---

## Test Phase 4: Shade Control

### 4.1 Test Open/Close Controls
1. In Devices tab, find a PowerView shade device
2. Click **"Open"** button
3. **Expected Result**: 
   - Shade should move to open position (visible on physical shade)
   - Device status shows 100

### 4.2 Test Close Control
1. From same device, click **"Close"** button
2. **Expected Result**: 
   - Shade should move to closed position
   - Device status shows 0

### 4.3 Test Position Control
1. From device controls, enter position value (e.g., 50)
2. Click button to set position
3. **Expected Result**: 
   - Shade moves to intermediate position (partially open)
   - Device status shows entered value

### 4.4 Verify Position Feedback
1. Manually move shade using physical controls or app
2. Wait 30 seconds (default poll interval)
3. **Expected Result**: Device status in HomeSeer updates to reflect actual shade position

---

## Test Phase 5: Automation & Scenes

### 5.1 Create Open All Scene
1. Go to **Scenes** → **Create New Scene**
2. Add actions for each PowerView shade device to "Open" (100%)
3. Name it "Open All Shades"
4. **Expected Result**: 
   - Scene created and runnable
   - All shades open when scene runs

### 5.2 Create Close All Scene
1. Create another scene named "Close All Shades"
2. Add actions for each shade to "Close" (0%)
3. **Expected Result**: 
   - All shades close when scene runs

### 5.3 Test Scheduled Automation
1. Create a scheduled trigger (e.g., daily at 9 AM)
2. Set action to run "Open All Shades" scene
3. **Expected Result**: 
   - Scene runs at scheduled time
   - All shades open automatically

---

## Test Phase 6: Advanced Testing

### 6.1 Test Polling Updates
1. Set shade to position 50 via HomeSeer
2. Manually adjust shade position using physical controller
3. Wait 30 seconds (poll interval)
4. **Expected Result**: Device status updates automatically to reflect manual change

### 6.2 Test Concurrent Control
1. Open multiple shade devices in separate browser tabs
2. Control one shade from each tab simultaneously
3. **Expected Result**: 
   - No conflicts or errors
   - All shades respond correctly
   - Status updates properly

### 6.3 Test Network Interruption Recovery
1. Temporarily disconnect PowerView Hub from network
2. Attempt to control shades from HomeSeer
3. Reconnect Hub to network
4. **Expected Result**: 
   - Clear error message during disconnection
   - Plugin recovers automatically when Hub reconnects

### 6.4 Test Plugin Restart
1. In HomeSeer Plugins, disable PowerView plugin
2. Verify devices no longer controllable
3. Re-enable PowerView plugin
4. **Expected Result**: 
   - Plugin reinitializes
   - Devices reconnect
   - Shades controllable again

---

## Test Phase 7: Performance & Stability

### 7.1 Stress Test
1. Rapidly open/close multiple shades
2. Create automation running multiple times quickly
3. Monitor HomeSeer performance
4. **Expected Result**: 
   - No crashes or freezes
   - All commands executed
   - HomeSeer remains responsive

### 7.2 Long-Term Stability
1. Leave plugin running for 24+ hours
2. Periodically check device status
3. Monitor HomeSeer logs for errors
4. **Expected Result**: 
   - No memory leaks
   - No repeated errors
   - Consistent polling intervals

### 7.3 Log Rotation Test
1. Monitor: Settings → System Log
2. Verify logs don't grow excessively
3. Check for verbose or debug messages
4. **Expected Result**: 
   - Reasonable log sizes
   - Informative log entries
   - No spamming of repeated messages

---

## Troubleshooting

### Common Issues & Solutions

#### Issue: Plugin doesn't appear in Installed Plugins
**Solutions:**
- Verify files copied to: C:\ProgramData\HomeSeer\Plugins\
- Check file permissions (readable by HomeSeer service account)
- Restart HomeSeer 4 service
- Check HomeSeer logs for load errors

#### Issue: Connection Test Fails
**Solutions:**
- Verify Hub IP address is correct
- Ping Hub IP from PC: `ping 192.168.3.XXX`
- Verify Hub is powered on
- Check Hub network configuration
- Verify firewall isn't blocking port 80

#### Issue: Shades Not Discovered
**Solutions:**
- Verify shades registered in Hub (check via PowerView app)
- Verify shade batteries have power
- Restart Hub and try discovery again
- Check plugin logs for HTTP errors

#### Issue: Shade Won't Respond to Commands
**Solutions:**
- Verify shade has power (check battery)
- Try manual control via PowerView app
- Check Hub network connectivity
- Verify shade isn't in error state

#### Issue: Polling Not Updating Status
**Solutions:**
- Verify polling interval setting (default 30 seconds)
- Check network connectivity to Hub
- Verify Hub is returning position data
- Check plugin logs for polling errors

---

## Test Results Summary

| Test Phase | Status | Notes |
|-----------|--------|-------|
| Installation | [ ] Pass [ ] Fail | |
| Hub Connection | [ ] Pass [ ] Fail | |
| Shade Discovery | [ ] Pass [ ] Fail | |
| Open/Close Control | [ ] Pass [ ] Fail | |
| Position Control | [ ] Pass [ ] Fail | |
| Automation | [ ] Pass [ ] Fail | |
| Performance | [ ] Pass [ ] Fail | |

---

## Next Steps After Testing

### If All Tests Pass
- [ ] Document any custom configurations
- [ ] Create backup of plugin settings
- [ ] Document shade names and locations
- [ ] Create common automation scenes
- [ ] Set up scheduled automations

### If Issues Found
- [ ] Document error details with timestamps
- [ ] Check HomeSeer logs for diagnostic information
- [ ] Review [DEPLOY.md](DEPLOY.md) troubleshooting section
- [ ] Report issues on GitHub with:
  - Error messages
  - HomeSeer version
  - .NET Framework version
  - Log excerpts

## Support Resources

- **GitHub Issues**: https://github.com/rnicol75/HomeseerPowerview/issues
- **PowerView API Docs**: https://github.com/jlaur/hdpowerview-doc
- **HomeSeer Support**: http://192.168.3.139/Html/WebGuide/index.html
- **Plugin Logs**: HomeSeer → Settings → System Log (filter: "PowerView")
