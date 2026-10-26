# 一次性把工作区里所有 ClassDailyLand* 改成 ClassDailyLand2*（文件夹 + 文本）。
# 只跑一次；可重复执行（幂等）：已经改过的文件夹/文件会被跳过。
$ErrorActionPreference = 'Stop'

$root = 'E:\Code\Class Daily Land 2'
Set-Location $root

# ———————————————— 1. 重命名工程文件夹 ————————————————
$folders = @(
    @{ Old = 'ClassDailyLand.Plugin.Abstractions'; New = 'ClassDailyLand2.Plugin.Abstractions' },
    @{ Old = 'ClassDailyLand.Updater';             New = 'ClassDailyLand2.Updater' },
    @{ Old = 'ClassDailyLand.Installer.Online';    New = 'ClassDailyLand2.Installer.Online' },
    @{ Old = 'tests\ClassDailyLand.Core.Tests';    New = 'tests\ClassDailyLand2.Core.Tests' }
)

foreach ($f in $folders) {
    $oldPath = Join-Path $root $f.Old
    $newPath = Join-Path $root $f.New

    if (Test-Path -LiteralPath $newPath) { Write-Output "SKIP(folder exists) $($f.New)"; continue }
    if (-not (Test-Path -LiteralPath $oldPath)) { Write-Output "SKIP(folder missing) $($f.Old)"; continue }

    Move-Item -LiteralPath $oldPath -Destination $newPath
    Write-Output "RENAMED $($f.Old) -> $($f.New)"
}

# ———————————————— 2. 批量替换文本 ————————————————
# (?!2) 是关键：已经叫 ClassDailyLand2 的地方不会被变成 ClassDailyLand2 2
$pattern = 'ClassDailyLand(?!2)'
$utf8 = New-Object System.Text.UTF8Encoding($false)

$exts = @('.cs', '.xaml', '.csproj', '.slnx', '.sln', '.md', '.mjs', '.json', '.props', '.targets', '.editorconfig')

$files = Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
    ($exts -contains $_.Extension.ToLower() -or $_.Name -eq 'LICENSE') -and
    $_.FullName -notmatch '\\obj\\|\\bin\\|\\\.git\\|\\\.vs\\' -and
    $_.Length -lt 2097152
}

$changed = 0
foreach ($file in $files) {
    $text = [System.IO.File]::ReadAllText($file.FullName)
    if ($text -notmatch $pattern) { continue }

    $updated = [regex]::Replace($text, $pattern, 'ClassDailyLand2')
    [System.IO.File]::WriteAllText($file.FullName, $updated, $utf8)

    $changed++
    Write-Output "PATCHED $($file.FullName.Substring($root.Length + 1))"
}

Write-Output "TEXT FILES CHANGED: $changed"

# ———————————————— 3. 残留检查 ————————————————
$left = @()
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.FullName)) { continue }
    $text = [System.IO.File]::ReadAllText($file.FullName)
    if ($text -match $pattern) { $left += $file.FullName }
}

if ($left.Count -gt 0) {
    Write-Output 'LEFTOVER FILES:'
    $left | ForEach-Object { Write-Output "  $_" }
} else {
    Write-Output 'NO LEFT VARS'
}

# ———————————————— 4. 结构核对 ————————————————
Write-Output 'CSPROJ:'
Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.csproj |
    Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
    ForEach-Object { Write-Output ('  ' + $_.FullName.Substring($root.Length + 1)) }

Write-Output 'FOLDERS:'
Get-ChildItem -LiteralPath $root -Directory |
    ForEach-Object { Write-Output ('  ' + $_.Name) }
