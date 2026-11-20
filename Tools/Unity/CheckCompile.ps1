# Проверка компиляции проекта в открытом Unity Editor через Unity CLI: обновляет ассеты (новые .cs получают .meta),
# перекомпилирует, ждёт конца и печатает ошибки компиляции. Код выхода 1 - есть ошибки.
# Скрипты Tools/Unity этим не проверяются: для них CheckTools.ps1.
# Запуск из корня проекта: powershell -NoProfile -File Tools\Unity\CheckCompile.ps1
[Console]::OutputEncoding = [Text.Encoding]::UTF8

# Результат команды Unity CLI как объект. Часть команд отдаёт JSON внутри строки JSON
function Read-UnityResult {
    $text = (unity command @args --result-only 2>$null | Out-String).Trim()
    if (-not $text) { return $null }
    $value = $text | ConvertFrom-Json
    if ($value -is [string] -and $value.StartsWith("{")) { $value = $value | ConvertFrom-Json }
    return $value
}

Read-UnityResult clear_console | Out-Null
Read-UnityResult eval --code "UnityEditor.AssetDatabase.Refresh(); return 1;" | Out-Null
# Refresh сам запускает компиляцию изменённых скриптов: дать ей начаться, прежде чем просить ещё одну
Start-Sleep -Seconds 3
Read-UnityResult recompile | Out-Null

$deadline = (Get-Date).AddMinutes(3)
do {
    Start-Sleep -Seconds 2
    $status = Read-UnityResult recompile_status
} while ($status.status -notin @("completed", "up_to_date", "idle") -and (Get-Date) -lt $deadline)

if ($status.status -notin @("completed", "up_to_date", "idle")) {
    "Компиляция не закончилась за 3 минуты: $($status.status)"
    exit 1
}
if (-not $status.compilationFailed) {
    "Компиляция без ошибок"
    exit 0
}
"ОШИБКИ КОМПИЛЯЦИИ:"
if ($status.errors.Count -gt 0) {
    foreach ($item in $status.errors) { if ($item -is [string]) { $item } else { $item | ConvertTo-Json -Compress -Depth 4 } }
} else {
    # Список ошибок пуст: взять их из консоли Editor
    $console = Read-UnityResult console --level error
    foreach ($entry in $console.entries) { $entry.message }
}
exit 1
