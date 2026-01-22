# Deploy Daily Report Script to HomeSeer

$scriptSource = "backup_1_1_2026\scripts\DailyReport.vb"
$scriptDest = "\\192.168.3.139\C$\Program Files (x86)\HomeSeer HS4\scripts\DailyReport.vb"

Write-Host "Deploying Daily Report Script..." -ForegroundColor Cyan
Write-Host "Copying $scriptSource to HomeSeer scripts folder..."
Copy-Item -Path $scriptSource -Destination $scriptDest -Force

Write-Host ""
Write-Host "Script deployed successfully!" -ForegroundColor Green
Write-Host ""
Write-Host "NEXT STEPS:" -ForegroundColor Yellow
Write-Host "1. Log into HomeSeer web interface at http://192.168.3.139"
Write-Host "2. Go to Events and click New Event"
Write-Host "3. Configure the event:"
Write-Host "   - Name: Daily Morning Report"
Write-Host "   - Trigger: At Time 7:00 AM"
Write-Host "   - Trigger Options: Every Day"
Write-Host "   - Action: Run Script"
Write-Host "   - Script: DailyReport.vb"
Write-Host "4. Save the event"
Write-Host ""
Write-Host "To test immediately, go to Events and click Run Now on the Daily Morning Report event"
Write-Host ""
