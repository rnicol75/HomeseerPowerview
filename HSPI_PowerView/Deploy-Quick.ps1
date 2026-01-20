# Quick deployment script with cached credentials
param(
    [switch]$RebuildFirst,
    [switch]$ClearCache
)

$credPath = "$env:USERPROFILE\.homeseer_cred.xml"

# Clear cache if requested
if ($ClearCache) {
    if (Test-Path $credPath) {
        Remove-Item $credPath -Force
        Write-Host "Credential cache cleared" -ForegroundColor Yellow
    }
    exit
}

# Load or create cached credentials
if (Test-Path $credPath) {
    $cred = Import-Clixml $credPath
    Write-Host "Using cached credentials for $($cred.UserName)" -ForegroundColor Green
} else {
    $cred = Get-Credential -Message "Enter credentials for 192.168.3.139 (will be cached)"
    $cred | Export-Clixml -Path $credPath
    Write-Host "Credentials cached at $credPath" -ForegroundColor Cyan
}

# Rebuild if requested
if ($RebuildFirst) {
    Write-Host "Building plugin..." -ForegroundColor Cyan
    msbuild HSPI_PowerView.csproj /p:Configuration=Release /p:Platform="Any CPU" /v:minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
}

# Deploy
Write-Host "Deploying to 192.168.3.139..." -ForegroundColor Cyan
$session = New-PSSession -ComputerName 192.168.3.139 -Credential $cred

$files = @(
    "bin\Release\HSPI_PowerView.exe",
    "bin\Release\PluginSdk.dll",
    "bin\Release\Newtonsoft.Json.dll",
    "bin\Release\HSCF.dll",
    "bin\Release\HSPI_PowerView.exe.config"
)

foreach ($file in $files) {
    if (Test-Path $file) {
        Copy-Item $file -Destination "C:\ProgramData\HomeSeer\Plugins\" -ToSession $session -Force
        Write-Host "  Copied $(Split-Path $file -Leaf)" -ForegroundColor Gray
    }
}

# Restart HomeSeer service
Invoke-Command -Session $session -ScriptBlock { 
    Restart-Service HomeSeer -Force -ErrorAction SilentlyContinue 
}

Write-Host "Deployed successfully!" -ForegroundColor Green
Remove-PSSession $session
