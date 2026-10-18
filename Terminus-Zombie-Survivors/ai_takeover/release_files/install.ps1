param([switch]$Uninstall, [switch]$Status)
# 主角 AI 托管 MOD 安装 / 卸载（无需 Python）
# 可以从创意工坊下载目录、任意解压位置或游戏目录\ai_takeover 里运行：
# 自动找到游戏目录，把 MOD 复制到 <游戏目录>\ai_takeover，
# 再把 _internal\base_library.zip 里的 linecache.pyc 换成预编译的引导版本。卸载时还原。

$ErrorActionPreference = 'Stop'
$AppId   = '1534980'
$Entry   = 'linecache.pyc'
$OrigSha = '__ORIG_SHA256__'
$Marker  = 'TERMINUS_AI_TAKEOVER_BOOTSTRAP'
$Here    = $PSScriptRoot

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

function Test-Patched([byte[]]$bytes) {
    return [Text.Encoding]::GetEncoding(28591).GetString($bytes).Contains($Marker)
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

# MOD 文件来源：安装包（脚本旁有 ai_takeover\）或已安装目录（脚本旁就是 ai_takeover.py）
if (Test-Path (Join-Path $Here 'ai_takeover\ai_takeover.py')) {
    $SrcMod = Join-Path $Here 'ai_takeover'; $SrcScripts = $Here
} elseif (Test-Path (Join-Path $Here 'ai_takeover.py')) {
    $SrcMod = $Here; $SrcScripts = $Here
} else {
    Fail '安装包不完整，请重新下载。'
}

$GameDir = Find-GameDir
if (-not $GameDir) { Fail '找不到游戏目录，已取消。' }
$ModDir     = Join-Path $GameDir 'ai_takeover'
$Zip        = Join-Path $GameDir '_internal\base_library.zip'
$OrigBackup = Join-Path $ModDir 'backup\linecache.pyc.orig'
$InPlace    = (Resolve-Path $SrcMod).Path.TrimEnd('\') -ieq $ModDir.TrimEnd('\')
Write-Host "游戏目录：$GameDir"

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$current = Read-Entry
$patched = Test-Patched $current

if ($Status) {
    if ($patched) { Ok '已安装' } else { Write-Host '未安装' }
    exit 0
}

if (Get-Process -Name Terminus -ErrorAction SilentlyContinue) { Fail '请先退出游戏再运行。' }

if ($Uninstall) {
    if ($patched) {
        if (-not (Test-Path $OrigBackup)) { Fail '找不到备份文件。请在 Steam 里对游戏「验证游戏文件的完整性」来还原。' }
        $orig = [IO.File]::ReadAllBytes($OrigBackup)
        if ((Get-Sha256 $orig) -ne $OrigSha) { Fail '备份文件校验失败。请在 Steam 里对游戏「验证游戏文件的完整性」来还原。' }
        Write-Entry $orig
        if (Test-Patched (Read-Entry)) { Fail '还原失败。' }
    }
    if ($InPlace) {
        Ok "已卸载。现在可以删除 $ModDir 文件夹。"
    } else {
        if (Test-Path $ModDir) { Remove-Item -Recurse -Force $ModDir }
        Ok '已卸载，游戏已恢复原样。创意工坊里取消订阅即可。'
    }
    exit 0
}

# 复制 / 更新 MOD 文件到游戏目录
if (-not $InPlace) {
    New-Item -ItemType Directory -Force (Join-Path $ModDir 'payload') | Out-Null
    Copy-Item -Force (Join-Path $SrcMod 'ai_takeover.py') $ModDir
    Copy-Item -Force (Join-Path $SrcMod 'payload\linecache.pyc') (Join-Path $ModDir 'payload')
    foreach ($f in 'install.ps1', 'install.bat', 'uninstall.bat', '使用说明.txt', 'README_EN.txt') {
        $p = Join-Path $SrcScripts $f
        if (Test-Path $p) { Copy-Item -Force $p $ModDir }
    }
    Write-Host "MOD 文件已复制到：$ModDir"
}

if (-not $patched) {
    if ((Get-Sha256 $current) -ne $OrigSha) {
        Fail "游戏内置的 Python 运行库与本 MOD 不匹配（游戏可能更新了 Python 版本）。`n为安全起见没有修改游戏文件，请等待 MOD 更新。"
    }
    New-Item -ItemType Directory -Force (Split-Path -Parent $OrigBackup) | Out-Null
    [IO.File]::WriteAllBytes($OrigBackup, $current)
    Write-Entry ([IO.File]::ReadAllBytes((Join-Path $ModDir 'payload\linecache.pyc')))
    if (-not (Test-Patched (Read-Entry))) { Fail '写入失败。' }
}
Ok '安装完成！启动游戏后，右下角动作面板里会出现机器人图标的「AI 托管」按钮（快捷键 F6）。'
