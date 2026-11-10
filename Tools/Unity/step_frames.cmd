rem
@echo off
rem Первая строка пустая: cmd спотыкается о метку UTF-8 в начале файла.
rem Прокручивает Play mode на паузе по одному кадру и пишет строку ProbeFrame.cs на каждый кадр.
rem Запуск из корня проекта: Tools\Unity\step_frames.cmd <число кадров>
set P=--project-path "D:\Game Tactick" --caller plugin --skill unity-cli --format json
unity command eval %P% -- --code "UnityEditor.EditorApplication.isPaused = true; return 0;" >NUL 2>NUL
for /l %%i in (1,1,%1) do (
    unity command eval %P% -- --code "UnityEditor.EditorApplication.Step(); return 0;" >NUL 2>NUL
    unity command run_script %P% -- --file Tools/Unity/ProbeFrame.cs 2>NUL | findstr /c:"\"result\": \"f="
)
unity command eval %P% -- --code "UnityEditor.EditorApplication.isPaused = false; return 0;" >NUL 2>NUL
