# PowerView Hub Telemetry Test Script
# Queries the hub directly to verify battery, signal strength, and position values

param(
    [string]$HubIP = "192.168.3.140"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "PowerView Hub Telemetry Test" -ForegroundColor Cyan
Write-Host "Hub IP: $HubIP" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

try {
    # Step 1: Get home data (contains list of shades with basic info)
    Write-Host "[1/2] Fetching shade list from /home..." -ForegroundColor Yellow
    $homeUrl = "http://$HubIP/home"
    $homeResponse = Invoke-RestMethod -Uri $homeUrl -Method Get -ContentType "application/json"
    
    # Extract shade IDs from all gateways
    $shadeIds = @()
    foreach ($gateway in $homeResponse.gateways) {
        if ($gateway.shd_Ids) {
            $shadeIds += $gateway.shd_Ids
        }
    }
    
    Write-Host "  Found $($shadeIds.Count) shades across all gateways" -ForegroundColor Green
    Write-Host ""
    
    if ($shadeIds.Count -eq 0) {
        Write-Host "No shades found on hub. Exiting." -ForegroundColor Red
        exit
    }
    
    # Step 2: Query each shade for detailed telemetry
    Write-Host "[2/2] Querying detailed telemetry for each shade..." -ForegroundColor Yellow
    Write-Host ""
    
    $shadeCount = 0
    foreach ($shadeId in $shadeIds) {
        $shadeCount++
        
        try {
            $shadeUrl = "http://$HubIP/home/shades/$shadeId"
            $shadeResponse = Invoke-RestMethod -Uri $shadeUrl -Method Get -ContentType "application/json"
            $shade = $shadeResponse.shade
            
            # Decode name (Base64)
            $shadeName = if ($shade.ptName) {
                [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($shade.ptName))
            } elseif ($shade.name) {
                [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($shade.name))
            } else {
                "Shade $shadeId"
            }
            
            Write-Host "──────────────────────────────────────" -ForegroundColor Cyan
            Write-Host "Shade #$shadeCount : $shadeName" -ForegroundColor White
            Write-Host "  Shade ID: $shadeId" -ForegroundColor Gray
            
            # Battery Status (1-4 scale, 1=low, 4=full)
            if ($shade.batteryStatus) {
                $batteryPct = switch ($shade.batteryStatus) {
                    1 { "25%" }
                    2 { "50%" }
                    3 { "75%" }
                    4 { "100%" }
                    default { "Unknown" }
                }
                Write-Host "  Battery Status: $($shade.batteryStatus) → $batteryPct" -ForegroundColor Green
            } else {
                Write-Host "  Battery Status: NOT AVAILABLE" -ForegroundColor Red
            }
            
            # Battery Strength (0-100% if available)
            if ($shade.batteryStrength) {
                Write-Host "  Battery Strength: $($shade.batteryStrength)%" -ForegroundColor Green
            } else {
                Write-Host "  Battery Strength: NOT AVAILABLE" -ForegroundColor DarkGray
            }
            
            # Signal Strength (negative dBm value)
            if ($shade.signalStrength) {
                Write-Host "  Signal Strength: $($shade.signalStrength) dBm" -ForegroundColor Green
            } else {
                Write-Host "  Signal Strength: NOT AVAILABLE" -ForegroundColor Red
            }
            
            # Position (0-65535, where 65535 = fully open/down)
            if ($shade.positions -and $shade.positions.primary) {
                $rawPosition = $shade.positions.primary
                $positionPct = [math]::Round(($rawPosition / 65535.0) * 100, 1)
                Write-Host "  Position (primary): $rawPosition → $positionPct%" -ForegroundColor Green
            } else {
                Write-Host "  Position: NOT AVAILABLE" -ForegroundColor Red
            }
            
            # Type
            if ($shade.type) {
                Write-Host "  Shade Type: $($shade.type)" -ForegroundColor Gray
            }
            
            Write-Host ""
            
        } catch {
            Write-Host "  ERROR querying shade $shadeId : $($_.Exception.Message)" -ForegroundColor Red
            Write-Host ""
        }
    }
    
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Test Complete - $shadeCount shades queried" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    
} catch {
    Write-Host "CRITICAL ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host $_.Exception.StackTrace -ForegroundColor DarkRed
}
