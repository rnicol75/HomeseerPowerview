# PowerView Plugin Deployment Script
# Deploys HSPI_PowerView plugin to remote HomeSeer 4 instance

param(
    [string]$RemoteHost = "192.168.3.139",
    [string]$RemotePluginsPath = "C:\ProgramData\HomeSeer\Plugins",
    [string]$LocalBuildDir = "c:\Users\Ron.Nicol\OneDrive - ENS\Thermostats\HSPI_PowerView\bin\Release"
)

# Plugin files to deploy
$pluginFiles = @(
    'HSPI_PowerView.exe',
    'PluginSdk.dll',
    'Newtonsoft.Json.dll',
    'HSCF.dll',
    'HSPI_PowerView.exe.config'
)

Write-Host "PowerView Plugin Deployment Script"
Write-Host "===================================" -ForegroundColor Green
Write-Host ""

# Verify source files exist
Write-Host "[1/5] Verifying plugin files..." -ForegroundColor Cyan
$missingFiles = @()
foreach ($file in $pluginFiles) {
    $path = Join-Path $LocalBuildDir $file
    if (Test-Path $path) {
        Write-Host "  ✓ $file" -ForegroundColor Green
    } else {
        Write-Host "  ✗ $file (NOT FOUND)" -ForegroundColor Red
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "Error: Missing plugin files. Build the project first:" -ForegroundColor Red
    Write-Host "  cd '$LocalBuildDir\..';" -ForegroundColor Yellow
    Write-Host "  MSBuild.exe .\HSPI_PowerView.csproj /t:Build /p:Configuration=Release" -ForegroundColor Yellow
    exit 1
}

# Test connectivity
Write-Host ""
Write-Host "[2/5] Testing connectivity to $RemoteHost..." -ForegroundColor Cyan
$pingTest = Test-NetConnection -ComputerName $RemoteHost -Port 80 -WarningAction SilentlyContinue
if ($pingTest.TcpTestSucceeded) {
    Write-Host "  ✓ Connected to $RemoteHost:80" -ForegroundColor Green
} else {
    Write-Host "  ✗ Cannot connect to $RemoteHost:80" -ForegroundColor Red
    Write-Host "  Ensure HomeSeer web interface is accessible at http://$RemoteHost" -ForegroundColor Yellow
    exit 1
}

# Copy files to remote
Write-Host ""
Write-Host "[3/5] Copying plugin files to remote..." -ForegroundColor Cyan

$remoteSharePath = "\\$RemoteHost\c$\ProgramData\HomeSeer\Plugins"
if (Test-Path $remoteSharePath) {
    foreach ($file in $pluginFiles) {
        $sourcePath = Join-Path $LocalBuildDir $file
        $destPath = Join-Path $remoteSharePath $file
        
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force -ErrorAction Stop
            Write-Host "  ✓ Copied $file" -ForegroundColor Green
        } catch {
            Write-Host "  ✗ Failed to copy $file : $_" -ForegroundColor Red
            Write-Host "  Ensure you have admin access to the Hometrooler" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  ✗ Remote share not accessible: $remoteSharePath" -ForegroundColor Red
    Write-Host "  You may need to manually copy files via RDP/SSH" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Manual deployment steps:" -ForegroundColor Cyan
    Write-Host "  1. Connect to $RemoteHost via RDP (Hometrooler device)" -ForegroundColor Yellow
    Write-Host "  2. Copy files from: $LocalBuildDir" -ForegroundColor Yellow
    Write-Host "  3. Paste to: C:\ProgramData\HomeSeer\Plugins" -ForegroundColor Yellow
    Write-Host "  4. Restart HomeSeer 4 service" -ForegroundColor Yellow
    exit 1
}

# Verify deployment
Write-Host ""
Write-Host "[4/5] Verifying deployment..." -ForegroundColor Cyan
$deployedCount = 0
foreach ($file in $pluginFiles) {
    $destPath = Join-Path $remoteSharePath $file
    if (Test-Path $destPath) {
        Write-Host "  ✓ $file deployed" -ForegroundColor Green
        $deployedCount++
    } else {
        Write-Host "  ✗ $file not found after copy" -ForegroundColor Red
    }
}

# Summary
Write-Host ""
Write-Host "[5/5] Deployment Summary" -ForegroundColor Cyan
Write-Host "  Files deployed: $deployedCount / $($pluginFiles.Count)" -ForegroundColor Green
Write-Host ""

if ($deployedCount -eq $pluginFiles.Count) {
    Write-Host "✓ Deployment successful!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Restart HomeSeer 4 service on the Hometrooler" -ForegroundColor Yellow
    Write-Host "  2. Check http://$RemoteHost Settings → Plugins → Installed Plugins" -ForegroundColor Yellow
    Write-Host "  3. Look for 'PowerView' in the installed plugins list" -ForegroundColor Yellow
    Write-Host "  4. Configure the plugin with your PowerView Hub IP address" -ForegroundColor Yellow
} else {
    Write-Host "✗ Deployment incomplete - see errors above" -ForegroundColor Red
    exit 1
}
