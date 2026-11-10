@echo off
rem ProbeSlowFrames в Play: запись профайлера и проверка кадров каждые 4 секунды, минуту с лишним
unity command run_script --file Tools/Unity/ProbeSlowFrames.cs --project-path "D:\Game Tactick" -- --args "[\"start\"]" > nul
for /l %%i in (1,1,16) do (
  timeout /t 4 > nul
  unity command run_script --file Tools/Unity/ProbeSlowFrames.cs --project-path "D:\Game Tactick" -- --args "[\"scan\",\"45\"]" >> Logs\slow.txt
)
unity command run_script --file Tools/Unity/ProbeSlowFrames.cs --project-path "D:\Game Tactick" -- --args "[\"stop\"]" > nul
