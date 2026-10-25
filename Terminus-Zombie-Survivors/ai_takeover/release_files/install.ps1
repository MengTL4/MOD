param([switch]$Uninstall, [switch]$Status)
# __MOD_TITLE__ 安装 / 卸载（无需 Python）
# 可以从创意工坊下载目录、任意解压位置或 <游戏目录>\__MOD__ 里运行：
# 自动找到游戏目录，把 MOD 复制到 <游戏目录>\__MOD__，
# 再把 _internal\base_library.zip 里的 linecache.pyc 换成预编译的 MOD 加载器。
# MOD 加载器由多个 MOD 共用（加载游戏目录下所有 <名字>\<名字>.py），
# 卸载时只要还有其他 MOD 在用，就只删除本 MOD；最后一个 MOD 卸载时才还原游戏文件。

$ErrorActionPreference = 'Stop'
$AppId    = '1534980'
$Mod      = '__MOD__'
$Entry    = 'linecache.pyc'
$OrigSha  = '__ORIG_SHA256__'
$Marker   = 'TERMINUS_AI_TAKEOVER_BOOTSTRAP'               # 任意版本的加载器
$MarkerV2 = 'TERMINUS_AI_TAKEOVER_BOOTSTRAP_V2_MODLOADER'  # 通用 MOD 加载器
$Here     = $PSScriptRoot

function Fail($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }
function Ok($msg) { Write-Host $msg -ForegroundColor Green }

function Test-GameDir($dir) {
    return $dir -and (Test-Path (Join-Path $dir 'Terminus.exe')) -and (Test-Path (Join-Path $dir '_internal\base_library.zip'))
}

function Get-InstallDirFromLibrary($steamapps) {
    $acf = Join-Path $steamapps "appmanifest_$AppId.acf"
    if (-not (Test-Path $acf)) { return $null }
    $m = Select-String -Path $acf -Pattern '"installdir"\s+"([^"]+)"' | Select-Object -First 1
    if ($m) { return Join-Path $steamapps ('common\' + $m.Matches[0].Groups[1].Value) }
    return $null
}

function Find-GameDir {
    # 1. 自己就在游戏目录里（或其子目录）
    $d = $Here
    while ($d) {
        if (Test-GameDir $d) { return $d }
        # 2. 在某个 Steam 库的 steamapps 下（例如创意工坊下载目录）
        if ((Split-Path -Leaf $d) -eq 'steamapps') {
            $g = Get-InstallDirFromLibrary $d
            if (Test-GameDir $g) { return $g }
        }
        $d = Split-Path -Parent $d
    }
    # 3. 查 Steam 的所有库
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath -replace '/', '\'
        $libs = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in (Select-String -Path $vdf -Pattern '"path"\s+"([^"]+)"')) {
                $libs += ($m.Matches[0].Groups[1].Value -replace '\\\\', '\')
            }
        }
        foreach ($lib in $libs) {
            $g = Get-InstallDirFromLibrary (Join-Path $lib 'steamapps')
            if (Test-GameDir $g) { return $g }
        }
    } catch { }
    # 4. 让用户自己选
    Add-Type -AssemblyName System.Windows.Forms
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog
    $dlg.Description = '没有自动找到游戏，请选择 Terminus 游戏目录（有 Terminus.exe 的文件夹）'
    if ($dlg.ShowDialog() -eq 'OK' -and (Test-GameDir $dlg.SelectedPath)) { return $dlg.SelectedPath }
    return $null
}

function Get-Sha256([byte[]]$bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLower() }
    finally { $sha.Dispose() }
}

function Test-Contains([byte[]]$bytes, $text) {
    return [Text.Encoding]::GetEncoding(28591).GetString($bytes).Contains($text)
}

function Read-Entry {
    $zip = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $e = $zip.GetEntry($Entry)
        if ($null -eq $e) { Fail "base_library.zip 里找不到 $Entry，游戏文件可能已损坏。" }
        $ms = New-Object IO.MemoryStream
        $s = $e.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
        return ,$ms.ToArray()
    } finally { $zip.Dispose() }
}

function Write-Entry([byte[]]$bytes) {
    $zip = [IO.Compression.ZipFile]::Open($Zip, 'Update')
    try {
        $old = $zip.GetEntry($Entry)
        if ($null -ne $old) { $old.Delete() }
        $new = $zip.CreateEntry($Entry, [IO.Compression.CompressionLevel]::NoCompression)
        $s = $new.Open(); try { $s.Write($bytes, 0, $bytes.Length) } finally { $s.Dispose() }
    } finally { $zip.Dispose() }
}

# 游戏目录下其他启用中的 MOD（<名字>\<名字>.py，且没有 DISABLED 标记）
function Get-OtherMods {
    $result = @()
    foreach ($d in (Get-ChildItem -Directory $GameDir)) {
        if ($d.Name -ieq $Mod) { continue }
        if ((Test-Path (Join-Path $d.FullName ($d.Name + '.py'))) -and -not (Test-Path (Join-Path $d.FullName 'DISABLED'))) {
            $result += $d.Name
        }
    }
    return ,$result
}

# MOD 文件来源：安装包（脚本旁有 <MOD>\）或已安装目录（脚本旁就是 <MOD>.py）
if (Test-Path (Join-Path $Here "$Mod\$Mod.py")) {
    $SrcMod = Join-Path $Here $Mod; $SrcScripts = $Here
} elseif (Test-Path (Join-Path $Here "$Mod.py")) {
    $SrcMod = $Here; $SrcScripts = $Here
} else {
    Fail '安装包不完整，请重新下载。'
}

$GameDir  = Find-GameDir
if (-not $GameDir) { Fail '找不到游戏目录，已取消。' }
$ModDir   = Join-Path $GameDir $Mod
$Zip      = Join-Path $GameDir '_internal\base_library.zip'
$Disabled = Join-Path $ModDir 'DISABLED'
$InPlace  = (Resolve-Path $SrcMod).Path.TrimEnd('\') -ieq $ModDir.TrimEnd('\')
Write-Host "游戏目录：$GameDir"

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$current = Read-Entry
$patched = Test-Contains $current $Marker
$isV2    = Test-Contains $current $MarkerV2

if ($Status) {
    if (-not $patched) { Write-Host '未安装' }
    elseif (Test-Path $Disabled) { Write-Host '已停用' }
    elseif (-not (Test-Path (Join-Path $ModDir "$Mod.py"))) { Write-Host '未安装（游戏里有其他 MOD 的加载器）' }
    elseif ($isV2) { Ok '已安装' }
    else { Write-Host '已安装旧版加载器，请运行 install.bat 升级' }
    exit 0
}

if (Get-Process -Name Terminus -ErrorAction SilentlyContinue) { Fail '请先退出游戏再运行。' }

if ($Uninstall) {
    $others = Get-OtherMods
    if ($patched -and $others.Count -eq 0) {
        # 最后一个 MOD：还原原始 linecache.pyc（安装包自带，已校验）
        $origPath = Join-Path $SrcMod 'payload\linecache.orig.pyc'
        if (-not (Test-Path $origPath)) { $origPath = Join-Path $ModDir 'payload\linecache.orig.pyc' }
        if (-not (Test-Path $origPath)) { Fail '找不到原始文件。请在 Steam 里对游戏「验证游戏文件的完整性」来还原。' }
        $orig = [IO.File]::ReadAllBytes($origPath)
        if ((Get-Sha256 $orig) -ne $OrigSha) { Fail '原始文件校验失败。请在 Steam 里对游戏「验证游戏文件的完整性」来还原。' }
        Write-Entry $orig
        if (Test-Contains (Read-Entry) $Marker) { Fail '还原失败。' }
        $note = '游戏已恢复原样。'
    } elseif ($patched) {
        $note = "其他 MOD（$($others -join '、')）仍在使用，保留了 MOD 加载器。"
    } else {
        $note = ''
    }
    if ($InPlace) {
        # 正在从本文件夹运行，删不掉自己：放一个 DISABLED 标记让加载器跳过本 MOD
        if (Test-Path $ModDir) { New-Item -ItemType File -Force $Disabled | Out-Null }
        Ok "已卸载。$($note)现在可以删除 $ModDir 文件夹。"
    } else {
        if (Test-Path $ModDir) { Remove-Item -Recurse -Force $ModDir }
        Ok "已卸载。$($note)创意工坊里取消订阅即可。"
    }
    exit 0
}

# 校验：游戏未被修改时，内置 linecache.pyc 必须与本 MOD 预编译时的一致
if (-not $patched -and (Get-Sha256 $current) -ne $OrigSha) {
    Fail "游戏内置的 Python 运行库与本 MOD 不匹配（游戏可能更新了 Python 版本）。`n为安全起见没有修改游戏文件，请等待 MOD 更新。"
}

# 复制 / 更新 MOD 文件到游戏目录
if (-not $InPlace) {
    New-Item -ItemType Directory -Force (Join-Path $ModDir 'payload') | Out-Null
    Copy-Item -Force (Join-Path $SrcMod "$Mod.py") $ModDir
    Copy-Item -Force (Join-Path $SrcMod 'payload\*') (Join-Path $ModDir 'payload')
    foreach ($f in 'install.ps1', 'install.bat', 'uninstall.bat', '使用说明.txt', 'README_EN.txt') {
        $p = Join-Path $SrcScripts $f
        if (Test-Path $p) { Copy-Item -Force $p $ModDir }
    }
    Write-Host "MOD 文件已复制到：$ModDir"
}
if (Test-Path $Disabled) { Remove-Item -Force $Disabled }

# 写入（或从旧版升级为）通用 MOD 加载器
if (-not $isV2) {
    Write-Entry ([IO.File]::ReadAllBytes((Join-Path $ModDir 'payload\linecache.pyc')))
    if (-not (Test-Contains (Read-Entry) $MarkerV2)) { Fail '写入失败。' }
}
$others = Get-OtherMods
if ($others.Count -gt 0) { Write-Host "同时加载的其他 MOD：$($others -join '、')" }
Ok '__DONE_MSG__'
