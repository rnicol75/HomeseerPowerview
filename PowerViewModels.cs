using System;
using System.Collections.Generic;

namespace HSPI_PowerView
{
    // PowerView Hub user data structure
    public class PowerViewUserData
    {
        public string SerialNumber { get; set; }
        public string RfID { get; set; }
        public int RfStatus { get; set; }
        public string HubName { get; set; }
        public string Firmware { get; set; }
    }

    // Shade data structure
    public class PowerViewShade
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Type { get; set; }
        public int BatteryStrength { get; set; }
        public int BatteryStatus { get; set; }
        public PowerViewPosition Positions { get; set; }
    }

    // Position data for shades
    public class PowerViewPosition
    {
        public int? Position1 { get; set; }  // Primary position (0-65535)
        public int? Position2 { get; set; }  // Secondary position for dual shades
        public int? PositionKind1 { get; set; }
        public int? PositionKind2 { get; set; }
    }

    // Scene data structure
    public class PowerViewScene
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int RoomId { get; set; }
        public int Order { get; set; }
        public int ColorId { get; set; }
        public int IconId { get; set; }
    }

    // Room data structure
    public class PowerViewRoom
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public int Order { get; set; }
        public int ColorId { get; set; }
        public int IconId { get; set; }
    }

    // API Response wrappers
    public class PowerViewShadesResponse
    {
        public List<PowerViewShade> ShadeData { get; set; }
        public List<int> ShadeIds { get; set; }
    }

    public class PowerViewShadeResponse
    {
        public PowerViewShade Shade { get; set; }
    }

    public class PowerViewScenesResponse
    {
        public List<PowerViewScene> SceneData { get; set; }
        public List<int> SceneIds { get; set; }
    }

    public class PowerViewRoomsResponse
    {
        public List<PowerViewRoom> RoomData { get; set; }
        public List<int> RoomIds { get; set; }
    }

    public class PowerViewUserDataResponse
    {
        public PowerViewUserData UserData { get; set; }
    }
}
