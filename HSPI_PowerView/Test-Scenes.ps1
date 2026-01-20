#!/usr/bin/env pwsh
# Test scene activation for Ron's Office shade

$hubIp = "192.168.3.164"
$shadeId = 168

Write-Host "`n=== Ron's Office Shade Scenes ===" -ForegroundColor Cyan
Write-Host "Shade ID: $shadeId"
Write-Host "Hub IP: $hubIp`n"

$scenes = @{
    "Close" = 169
    "Privacy" = 171
    "Open" = 173
}

Write-Host "Available scenes:" -ForegroundColor Yellow
$scenes.GetEnumerator() | Sort-Object Name | ForEach-Object {
    Write-Host "  $($_.Key.PadRight(10)) - Scene ID $($_.Value)"
}

Write-Host "`nTesting scene activation..." -ForegroundColor Cyan

foreach ($scene in $scenes.GetEnumerator() | Sort-Object Name) {
    $sceneId = $scene.Value
    $sceneName = $scene.Key
    
    Write-Host "`nActivating '$sceneName' (Scene $sceneId)..." -ForegroundColor Yellow
    
    try {
        $response = Invoke-WebRequest -Uri "http://$hubIp/home/scenes/$sceneId/activate" `
            -Method PUT `
            -UseBasicParsing `
            -TimeoutSec 10
        
        if ($response.StatusCode -eq 200) {
            $content = $response.Content | ConvertFrom-Json
            Write-Host "  SUCCESS! Affected shades: $($content.shadeIds -join ', ')" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
    
    Start-Sleep -Seconds 3
}

Write-Host "`n=== Test Complete ===" -ForegroundColor Cyan
