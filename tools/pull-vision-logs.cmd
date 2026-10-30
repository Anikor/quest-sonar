@echo off
rem Забрать журналы ветки vision со шлема (vision-log: events.jsonl, кадры, ASCII-карты) в папку logs\<дата-время>.
rem Нужен adb: из Android SDK Unity (путь ниже) или любой другой в PATH.
setlocal
set ADB="C:\Program Files\Unity\Hub\Editor\2022.3.62f3\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if not exist %ADB% set ADB=adb
set OUT=%~dp0..\logs
if not exist "%OUT%" mkdir "%OUT%"
%ADB% devices
%ADB% pull /sdcard/Android/data/com.anikor.questsonar/files/vision-log "%OUT%"
echo.
echo Журналы в %OUT%\vision-log\<сессия>\ : events.jsonl, frames\*.jpg, map-*.txt
endlocal
