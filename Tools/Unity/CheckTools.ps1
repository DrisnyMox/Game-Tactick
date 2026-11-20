# Сухая компиляция скриптов Tools/Unity в открытом Unity Editor: run_script --dry_run компилирует скрипт в памяти
# и ничего не запускает. Печатает только скрипты с ошибками и сами ошибки. Код выхода 1 - есть ошибки.
# Нужна после переименований в коде игры: эти скрипты компилируются отдельно и сборка проекта их ошибок не показывает.
# Ссылки по имени через рефлексию (GetField("имя")) так не проверить - их искать поиском по Tools/Unity.
# Запуск из корня проекта: powershell -NoProfile -File Tools\Unity\CheckTools.ps1 [Tools/Unity/ProbeLoop.cs ...]
# Без аргументов - все скрипты папки (около 5 минут).
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$files)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not $files) { $files = Get-ChildItem Tools\Unity\*.cs | ForEach-Object { "Tools/Unity/" + $_.Name } }

$failed = 0
foreach ($file in $files) {
    $text = (unity command run_script --file $file --dry_run true --result-only 2>$null | Out-String).Trim()
    $result = $null
    try { $result = $text | ConvertFrom-Json } catch { }
    if ($result -and $result.success) { continue }
    $failed++
    "НЕ КОМПИЛИРУЕТСЯ: $file"
    if (-not $result) { "  " + $text; continue }
    foreach ($d in $result.diagnostics) {
        if ($d.severity -eq "error") { "  строка $($d.line + 1): $($d.id) $($d.message)" }
    }
}
if ($failed -eq 0) { "Все скрипты компилируются: $($files.Count)" } else { "С ошибками: $failed из $($files.Count)" }
exit [int]($failed -gt 0)
