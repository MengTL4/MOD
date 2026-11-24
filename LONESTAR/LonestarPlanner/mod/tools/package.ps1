# 打出完整的 mod 目录（创意工坊上传的就是它），可选部署到游戏的 Dev mod 目录。
#
#   pwsh tools/package.ps1            # 打包到 dist/LonestarPlanner
#   pwsh tools/package.ps1 -Deploy    # 打包并部署到 %LocalLow%\Shuxi\LONESTAR\Mods\Dev\LonestarPlanner
#
# 产物：
#   LonestarPlanner.dll / LonestarPlanner.Core.dll   mod 本体（游戏只加载顶层的 dll）
#   mod.json / preview.png                           创意工坊标题、介绍、预览图
#   Brain/                                           求解器：自带 .NET 运行时（玩家不用装），
#     data/                                          无头引擎要的游戏资源（从游戏导出）
#     brain.json                                     版本 + 做它时游戏 Assembly-CSharp.dll 的 SHA256（检测游戏更新）
#
# 注意：Brain/ 里的 Lonestar.Game.dll 是用游戏代码编译的，data/ 是游戏资源。公开发布前先确认开发者同意。
param(
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\LONESTAR",
    [string]$SolverRoot = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "solver"),
    [string]$ModsRoot = "$env:USERPROFILE\AppData\LocalLow\Shuxi\LONESTAR\Mods\Dev",
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$managed = Join-Path $GameDir "LONESTAR_Data\Managed"
$dist = Join-Path $root "dist\LonestarPlanner"
$brainOut = Join-Path $dist "Brain"

Write-Host "==> build mod"
dotnet build (Join-Path $root "src\Planner.Mod\Planner.Mod.csproj") -c Release -p:GameManaged=$managed -p:SolverRoot=$SolverRoot --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "mod build failed" }

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Write-Host "==> publish solver (self-contained win-x64)"
dotnet publish (Join-Path $SolverRoot "tools\Brain") -c Release -r win-x64 --self-contained `
    -p:DebugType=none -p:SatelliteResourceLanguages=en --nologo -v q -o $brainOut
if ($LASTEXITCODE -ne 0) { throw "solver publish failed" }

Write-Host "==> game data"
New-Item -ItemType Directory -Force -Path (Join-Path $brainOut "data") | Out-Null
Copy-Item -Recurse (Join-Path $SolverRoot "data\export") (Join-Path $brainOut "data\export")
Copy-Item (Join-Path $SolverRoot "data\resources_index.tsv") (Join-Path $brainOut "data")

$version = (Select-String -Path (Join-Path $SolverRoot "tools\Brain\Service.cs") -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
$gameHash = (Get-FileHash (Join-Path $managed "Assembly-CSharp.dll") -Algorithm SHA256).Hash
$info = [ordered]@{ version = $version; game = $gameHash; built = (Get-Date).ToString("o") }
$info | ConvertTo-Json | Set-Content -Encoding utf8NoBOM (Join-Path $brainOut "brain.json")

Write-Host "==> mod files"
$modBin = Join-Path $root "src\Planner.Mod\bin\Release\netstandard2.1"
Copy-Item (Join-Path $modBin "LonestarPlanner.dll") $dist
Copy-Item (Join-Path $modBin "LonestarPlanner.Core.dll") $dist
Copy-Item (Join-Path $PSScriptRoot "mod.json") $dist
Copy-Item (Join-Path $PSScriptRoot "preview.png") $dist

$size = (Get-ChildItem -Recurse $dist | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("==> dist: {0} ({1:N0} MB, solver {2}, game {3})" -f $dist, $size, $version, $gameHash.Substring(0, 12))

if ($Deploy) {
    $target = Join-Path $ModsRoot "LonestarPlanner"
    Write-Host "==> deploy to $target"
    if (Get-Process LONESTAR -ErrorAction SilentlyContinue) { throw "close the game first (the mod DLL is in use)" }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    # 保留玩家的设置；Translation/ 由 mod 启动时按游戏语言生成；logs/ 是 1.0 版的日志，mod 启动时搬走
    robocopy $dist $target /MIR /XF config.json /XD Translation logs /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
    Write-Host "==> done (restart the game to load it)"
}
