# PowerView Plugin Deployment Script
# Deploys HSPI_PowerView plugin to remote HomeSeer 4 instance

param(
    [string]$RemoteHost = "192.168.3.139",
    # Default to HomeSeer HS4 installation root (no Plugins subfolder)
    [string]$RemotePluginsPath = "C:\Program Files (x86)\HomeSeer HS4",
    [string]$LocalBuildDir = "c:\Users\Ron.Nicol\OneDrive - ENS\Thermostats\HSPI_PowerView\bin\Release"
)

# Determine plugin binary type (DLL vs EXE)
$dllPath = Join-Path $LocalBuildDir 'HSPI_PowerView.dll'
$exePath = Join-Path $LocalBuildDir 'HSPI_PowerView.exe'
$isDll = Test-Path $dllPath
$isExe = Test-Path $exePath

if (-not $isDll -and -not $isExe) {
    Write-Host "[FAIL] Neither HSPI_PowerView.dll nor HSPI_PowerView.exe found in $LocalBuildDir" -ForegroundColor Red
    Write-Host "  Build the project first: MSBuild.exe .\\HSPI_PowerView.csproj /t:Build /p:Configuration=Release" -ForegroundColor Yellow
    exit 1
}

# Plugin files to deploy based on build output
if ($isDll) {
    $pluginFiles = @(
        'HSPI_PowerView.dll',
        'HSPI_PowerView.dll.config',
        'PluginSdk.dll',
        'Newtonsoft.Json.dll',
        'HSCF.dll',
        'PowerView.ini'
    )
} else {
    $pluginFiles = @(
        'HSPI_PowerView.exe',
        'HSPI_PowerView.exe.config',
        'PluginSdk.dll',
        'Newtonsoft.Json.dll',
        'HSCF.dll',
        'PowerView.ini'
    )
}

Write-Host "PowerView Plugin Deployment Script"
Write-Host "===================================" -ForegroundColor Green
Write-Host ""

# Verify source files exist
Write-Host "[1/5] Verifying plugin files..." -ForegroundColor Cyan
$missingFiles = @()
foreach ($file in $pluginFiles) {
    $path = Join-Path $LocalBuildDir $file
    if (Test-Path $path) {
        Write-Host "  [OK] $file" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] $file (NOT FOUND)" -ForegroundColor Red
        $missingFiles += $file
    }
}

if ($missingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "Error: Missing plugin files. Build the project first:" -ForegroundColor Red
    Write-Host "  cd '$LocalBuildDir\..'" -ForegroundColor Yellow
    Write-Host "  MSBuild.exe .\HSPI_PowerView.csproj /t:Build /p:Configuration=Release" -ForegroundColor Yellow
    exit 1
}

# Test connectivity
Write-Host ""
Write-Host "[2/5] Testing connectivity to $RemoteHost..." -ForegroundColor Cyan
$pingTest = Test-NetConnection -ComputerName $RemoteHost -Port 80 -WarningAction SilentlyContinue
if ($pingTest.TcpTestSucceeded) {
    Write-Host "  [OK] Connected to $RemoteHost:80" -ForegroundColor Green
} else {
    Write-Host "  [FAIL] Cannot connect to $RemoteHost:80" -ForegroundColor Red
    Write-Host "  Ensure HomeSeer web interface is accessible at http://$RemoteHost" -ForegroundColor Yellow
    exit 1
}

# Copy files to remote
Write-Host ""
Write-Host "[3/5] Copying plugin files to remote..." -ForegroundColor Cyan

# Build the remote share path from the provided RemotePluginsPath (e.g. C:\ProgramData\HomeSeer\Plugins)
if (-not $RemotePluginsPath -or $RemotePluginsPath.Length -lt 4 -or ($RemotePluginsPath[1] -ne ':')) {
    Write-Host "  [FAIL] Invalid -RemotePluginsPath: $RemotePluginsPath" -ForegroundColor Red
    exit 1
}

$subPath = $RemotePluginsPath.Substring(3) # everything after drive colon and backslash
$driveLetter = $RemotePluginsPath.Substring(0,1)
$remoteSharePath = "\\$RemoteHost\$driveLetter`$\$subPath"

if (Test-Path $remoteSharePath) {
    # Ensure destination directory exists
    if (-not (Test-Path $remoteSharePath)) {
        try {
            New-Item -ItemType Directory -Path $remoteSharePath -Force | Out-Null
        } catch {
            Write-Host "  [FAIL] Could not create remote directory: $remoteSharePath" -ForegroundColor Red
            exit 1
        }
    }
    foreach ($file in $pluginFiles) {
        $sourcePath = Join-Path $LocalBuildDir $file
        $destPath = Join-Path $remoteSharePath $file
        
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force -ErrorAction Stop
            Write-Host "  [OK] Copied $file" -ForegroundColor Green
        } catch {
            Write-Host "  [FAIL] Failed to copy $file : $_" -ForegroundColor Red
            Write-Host "  Ensure you have admin access to the Hometrooler" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "  [FAIL] Remote share not accessible: $remoteSharePath" -ForegroundColor Red
    Write-Host "  You may need to manually copy files via RDP/SSH" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Manual deployment steps:" -ForegroundColor Cyan
    Write-Host "  1. Connect to $RemoteHost via RDP (Hometrooler device)" -ForegroundColor Yellow
    Write-Host "  2. Copy files from: $LocalBuildDir" -ForegroundColor Yellow
    Write-Host "  3. Paste to: $RemotePluginsPath" -ForegroundColor Yellow
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
        Write-Host "  [OK] $file deployed" -ForegroundColor Green
        $deployedCount++
    } else {
        Write-Host "  [FAIL] $file not found after copy" -ForegroundColor Red
    }
}

# Summary
Write-Host ""
Write-Host "[5/5] Deployment Summary" -ForegroundColor Cyan
Write-Host "  Files deployed: $deployedCount / $($pluginFiles.Count)" -ForegroundColor Green
Write-Host ""

if ($deployedCount -eq $pluginFiles.Count) {
    Write-Host "[OK] Deployment successful!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Restart HomeSeer 4 service on the Hometrooler" -ForegroundColor Yellow
    Write-Host "  2. Check http://$RemoteHost Settings -> Plugins -> Installed Plugins" -ForegroundColor Yellow
    Write-Host "  3. Look for 'PowerView' in the installed plugins list" -ForegroundColor Yellow
    Write-Host "  4. Configure the plugin with your PowerView Hub IP address" -ForegroundColor Yellow
} else {
    Write-Host "[FAIL] Deployment incomplete - see errors above" -ForegroundColor Red
    exit 1
}
