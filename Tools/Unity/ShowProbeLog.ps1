# Печатает результаты run_script из файла, куда их дописывали подряд (например Logs\slow.txt от ProbeSlowFrames.cmd):
# текст поля result с настоящими переносами строк, и ошибки, если были. Второй аргумент - регулярное выражение:
# печатаются только результаты, где оно находится.
# Запуск: powershell -NoProfile -File Tools\Unity\ShowProbeLog.ps1 Logs\slow.txt "кадр \d+:"
param([string]$path, [string]$filter = '.')
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$text = Get-Content -Raw -Encoding UTF8 $path
foreach ($match in [regex]::Matches($text, '"result":"(.*?)"\}')) {
    $result = $match.Groups[1].Value -replace '\\r\\n|\\n', "`n" -replace '\\"', '"'
    if ($result -match $filter) { $result }
}
foreach ($match in [regex]::Matches($text, '"errorDetails":"([^"]{0,400})')) {
    'ОШИБКА: ' + $match.Groups[1].Value
}
