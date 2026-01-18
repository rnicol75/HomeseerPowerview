# Hunter Douglas PowerView Plugin for HomeSeer 4

A HomeSeer 4 plugin for controlling Hunter Douglas PowerView smart shades and blinds.

## Features

- **Automatic Shade Discovery** - Automatically discovers and creates devices for all PowerView shades
- **Full Shade Control** - Open, close, and set precise positions (0-100%)
- **Real-time Status Updates** - Polls shades every 30 seconds for current position and battery status
- **Scene Support** - Activate PowerView scenes (future enhancement)
- **Battery Monitoring** - Tracks battery levels for battery-powered shades
- **Simple Configuration** - Easy web-based setup with connection testing

## Requirements

- HomeSeer 4 (HS4 or HS4 Pro)
- .NET Framework 4.6.2 or higher
- Hunter Douglas PowerView Hub (Gen 2 or Gen 3)
- PowerView Hub connected to local network

## Installation

1. Download the plugin files to your HomeSeer installation directory
2. Place files in: `C:\Program Files (x86)\HomeSeer HS4\bin\PowerView\`
3. Restart HomeSeer
4. The plugin should appear in the Plugins list

## Configuration

1. Go to **Plugins** → **PowerView** → **Settings**
2. Enter your PowerView Hub IP address (e.g., `192.168.1.100`)
3. Click **Test Connection** to verify connectivity
4. Click **Save Settings**
5. Click **Discover Shades** to automatically create devices for all shades

## Usage

### Controlling Shades

Once devices are created, you can control shades through:

- **HomeSeer Device Controls** - Open, Close, or set specific position (0-100%)
- **Events** - Create automation events using shade devices as triggers or actions
- **Voice Control** - Use HomeSeer's voice commands if enabled

### Shade Positions

- **0%** = Fully closed
- **100%** = Fully open
- Any value in between for partial opening

### Automatic Updates

The plugin polls your PowerView Hub every 30 seconds to update:
- Current shade positions
- Battery levels
- Shade availability status

## Device Structure

Each shade creates a HomeSeer device with:
- **Name**: Shade name from PowerView app
- **Location**: PowerView
- **Location2**: Shades
- **Controls**: Open, Close, Set Position
- **Status**: Current position (0-100%)

## API Integration

This plugin uses the Hunter Douglas PowerView Hub REST API v2 to communicate with your shades. The API provides:
- Shade discovery and status
- Position control
- Scene activation
- Battery monitoring

## Building from Source

### Prerequisites

- Visual Studio 2019 or later
- .NET Framework 4.6.2 SDK
- NuGet Package Manager

### Build Steps

1. Open `HSPI_PowerView.csproj` in Visual Studio
2. Restore NuGet packages:
   - HomeSeer-PluginSDK (v1.5.0)
   - Newtonsoft.Json (v13.0.3)
3. Build the solution (Release configuration)
4. Copy output files to HomeSeer's `bin\PowerView\` directory

### Project Structure

```
HSPI_PowerView/
├── HSPI.cs                  # Main plugin class
├── PowerViewClient.cs       # API client for PowerView Hub
├── PowerViewModels.cs       # Data models for API responses
├── Program.cs               # Entry point
├── HSPI_PowerView.csproj   # Project file
├── App.config              # Configuration
├── packages.config         # NuGet packages
├── html/
│   └── settings.html       # Settings page
└── Properties/
    └── AssemblyInfo.cs     # Assembly metadata
```

## Troubleshooting

### Plugin won't start
- Verify .NET Framework 4.6.2 is installed
- Check HomeSeer logs for error messages
- Ensure all DLL dependencies are present

### Can't connect to Hub
- Verify Hub IP address is correct
- Ensure Hub is on the same network
- Check firewall settings
- Try pinging the Hub from command line

### Shades not discovered
- Ensure shades are paired with PowerView Hub
- Check PowerView app shows shades
- Try clicking "Discover Shades" again
- Check plugin logs for errors

### Position updates not working
- Verify polling is enabled (check logs)
- Ensure Hub is responding to API calls
- Check network connectivity

## Known Limitations

- Requires local network access to PowerView Hub
- Does not support PowerView Hub Gen 1 (uses API v1)
- Scene support is basic (future enhancement)
- Multi-position shades (top-down/bottom-up) use primary position only

## Future Enhancements

- Full scene management
- Room-based organization
- Schedule synchronization
- Multi-position shade support
- PowerView automation integration
- Firmware update notifications

## Support

For issues or questions:
- Check HomeSeer forums
- Review plugin logs in HomeSeer
- Verify PowerView Hub firmware is up to date

## License

This plugin is provided as-is for use with HomeSeer 4.

## Credits

Built using:
- HomeSeer Plugin SDK v1.5.0
- Hunter Douglas PowerView API documentation
- Community-maintained PowerView API specs

## Version History

### v1.0.0 (2026-01-18)
- Initial release
- Shade discovery and control
- Position control (0-100%)
- Battery monitoring
- Automatic status polling
- Web-based settings page
