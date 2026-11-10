# Копирует ассеты из другого проекта Unity вместе со всем, на что они ссылаются.
# Файлы идут вместе с .meta, поэтому GUID сохраняются и ссылки между файлами не рвутся.
# Ссылки ищутся по строкам "guid: ..." в текстовых ассетах (.prefab .mat .controller .asset .anim .mask .unity).
# Скрипты (.cs .dll) не копируются: ссылки на них в префабах станут пустыми компонентами.
#
# Запуск (из корня проекта):
#   powershell -ExecutionPolicy Bypass -File Tools/Unity/CopyAssetsWithDependencies.ps1 `
#       -SourceRoot "<папка-источник внутри Assets другого проекта>" -TargetRoot "Assets/ThirdParty/..." `
#       -Roots "путь1;путь2" [-DryRun]
# Пути в -Roots считаются от SourceRoot. Относительная раскладка папок сохраняется.
param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$TargetRoot,
    [Parameter(Mandatory = $true)][string]$Roots,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path.TrimEnd('\')
# GUID ищутся по всему Assets проекта-источника: префаб может ссылаться и за пределы SourceRoot
$assetsRoot = $SourceRoot.Substring(0, $SourceRoot.IndexOf('\Assets') + 7)

$guidToPath = @{}
Get-ChildItem -LiteralPath $assetsRoot -Recurse -Filter *.meta | ForEach-Object {
    $line = Get-Content -LiteralPath $_.FullName -TotalCount 2 | Where-Object { $_ -match '^guid: ([0-9a-f]{32})' } | Select-Object -First 1
    if ($line -match '^guid: ([0-9a-f]{32})') {
        $guidToPath[$Matches[1]] = $_.FullName.Substring(0, $_.FullName.Length - 5)
    }
}

$textExt = @('.prefab', '.mat', '.controller', '.asset', '.anim', '.mask', '.unity', '.overrideController', '.physicMaterial')
$skipExt = @('.cs', '.dll', '.asmdef', '.shader', '.cginc')
$queue = New-Object System.Collections.Queue
$seen = @{}
foreach ($r in $Roots.Split(';')) {
    $full = Join-Path $SourceRoot $r
    if (-not (Test-Path -LiteralPath $full)) { throw "root not found: $full" }
    $queue.Enqueue((Resolve-Path -LiteralPath $full).Path)
}

$outside = @()
while ($queue.Count -gt 0) {
    $path = $queue.Dequeue()
    if ($seen.ContainsKey($path)) { continue }
    $seen[$path] = $true
    if (-not $path.StartsWith($SourceRoot)) { $outside += $path; continue }
    $ext = [System.IO.Path]::GetExtension($path)
    if ($textExt -contains $ext) {
        $text = [System.IO.File]::ReadAllText($path)
        foreach ($m in [regex]::Matches($text, 'guid: ([0-9a-f]{32})')) {
            $g = $m.Groups[1].Value
            if ($guidToPath.ContainsKey($g)) {
                $dep = $guidToPath[$g]
                if ($skipExt -notcontains [System.IO.Path]::GetExtension($dep) -and (Test-Path -LiteralPath $dep -PathType Leaf)) {
                    $queue.Enqueue($dep)
                }
            }
        }
    }
}

$files = $seen.Keys | Where-Object { $_.StartsWith($SourceRoot) } | Sort-Object
$total = 0
foreach ($f in $files) {
    $size = (Get-Item -LiteralPath $f).Length
    $total += $size
    $rel = $f.Substring($SourceRoot.Length + 1)
    "{0,10:N0} {1}" -f $size, $rel
    if (-not $DryRun) {
        $dst = Join-Path $TargetRoot $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath $f -Destination $dst -Force
        Copy-Item -LiteralPath ($f + '.meta') -Destination ($dst + '.meta') -Force
    }
}
"files: {0}, size: {1:N1} MB{2}" -f $files.Count, ($total / 1MB), $(if ($DryRun) { ' (dry run, nothing copied)' } else { '' })
foreach ($o in $outside) { "outside SourceRoot, not copied: $o" }
