# Builds the solver and the in-game advisor mod, then enables the advisor by writing advisor.json
# next to the deployed mod. Run from anywhere:
#   powershell -ExecutionPolicy Bypass -File scripts\deploy_advisor.ps1 [-Disable] [-Jobs 6] [-MaxPlans 32] [-Verify 3]
param(
    [switch]$Disable,
    [int]$Jobs = 6,
    [int]$MaxPlans = 32,
    [int]$Verify = 3,
    [int]$TimeoutSeconds = 240
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$managed = 'F:\SteamLibrary\steamapps\common\LONESTAR\LONESTAR_Data\Managed'
$modDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Shuxi\LONESTAR\Mods\Dev\SolverLogger'

Write-Host "== building solver =="
dotnet build (Join-Path $root 'tools\Solve\Solve.csproj') -c Release -v q
$solveDll = Join-Path $root 'tools\Solve\bin\Release\net10.0\Solve.dll'
if (-not (Test-Path $solveDll)) { throw "solver build missing: $solveDll" }

Write-Host "== building + deploying mod =="
dotnet build (Join-Path $root 'src\SolverLogger\SolverLogger.csproj') -c Release -v q

$dotnetExe = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnetExe) {
    $candidate = 'C:\Program Files\dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnetExe = $candidate }
}
if (-not $dotnetExe) { throw 'dotnet.exe not found; set "dotnet" manually in advisor.json' }

$configPath = Join-Path $modDir 'advisor.json'
if ($Disable) {
    if (Test-Path $configPath) { Remove-Item $configPath -Force }
    Write-Host "advisor disabled (removed $configPath)"
    exit 0
}

$config = [ordered]@{
    enabled        = $true
    dotnet         = $dotnetExe
    solveDll       = $solveDll
    root           = $root
    jobs           = $Jobs
    maxPlans       = $MaxPlans
    verify         = $Verify
    timeoutSeconds = $TimeoutSeconds
}
New-Item -ItemType Directory -Force -Path $modDir | Out-Null
$config | ConvertTo-Json | Set-Content -Path $configPath -Encoding UTF8

Write-Host "advisor enabled:"
Write-Host "  mod:     $modDir"
Write-Host "  config:  $configPath"
Write-Host "  solver:  $solveDll"
Write-Host "Start the game; on each player turn the HUD (top-left) shows the recommended actions after the solver finishes."
