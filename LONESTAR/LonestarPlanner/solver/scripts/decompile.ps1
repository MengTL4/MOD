# Re-decompiles the game's script assemblies into decompiled/. Run after every game update,
# then rebuild the engine; diffs in decompiled/ show exactly what the patch changed.
# The output is the game's own code: keep it local, never publish it.
param(
    [string]$GameDir = "F:\SteamLibrary\steamapps\common\LONESTAR"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$managed = Join-Path $GameDir "LONESTAR_Data\Managed"

foreach ($asm in @("Assembly-CSharp", "Assembly-CSharp-firstpass")) {
    $out = Join-Path $root "decompiled\$asm"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    New-Item -ItemType Directory -Force $out | Out-Null
    Write-Host "Decompiling $asm -> $out"
    & ilspycmd -p -o $out (Join-Path $managed "$asm.dll")
    if ($LASTEXITCODE -ne 0) { throw "ilspycmd failed for $asm" }
}
