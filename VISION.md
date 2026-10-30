# Ветка `vision`: двери, таблички и текст вслух

Состояние (2026-10-30): написано, компилируется с Meta SDK 207 во временном проекте, логика проверена тестами
(`tools/unity-check/VisionCheck.cs`). **На шлеме не проверялось.** Что именно надо проверить на шлеме, см. в конце.

Ветка создана от `v4.0-stable` и **не трогает звук**: `AudioSonar.cs` не менялся. Вся связь со звуком идёт через реестр
`SonarLandmarks` (см. `README.md`, раздел «Как работают вместе ветки»). В `ObstacleMap.cs` добавлен один метод `QueryCell`
(что карта знает о клетке), в манифест - разрешения на камеру.

## Что добавлено

| Файл | Роль |
|---|---|
| `Assets/Vision/SpeechOutput.cs` | **Голос.** Android TextToSpeech (ru, запасной en), одна очередь фраз на всё приложение, одна и та же фраза не чаще раза в 4 с. Сам подключается к `SonarLandmarks.SpeakHandler`, если туда никто не подключён, поэтому `AudioSonar` произносит текст табличек. `SpeechOutput.Speak("текст")`, `Speak("стоп", interrupt: true)` |
| `Assets/Vision/PassthroughCameraFeed.cs` | **Кадры с камеры passthrough** через компонент `PassthroughCameraAccess` (MR Utility Kit, Horizon OS v74+). Запрашивает разрешение `horizonos.permission.HEADSET_CAMERA`, отдаёт серый кадр 1280×960 (строки сверху вниз) с позой камеры на момент кадра; `PixelToRay` переводит пиксель в мировой луч |
| `Assets/Vision/TextRecognizer.cs` | **OCR.** `ITextRecognizer` и реализация `TesseractRecognizer` (Tesseract 5.5 через `tesseract4android-4.8.0.aar`, только LSTM, режим «разрежённый текст», слова с рамками и уверенностью). Чистая логика `SignText` (нормализация, отсев мусора, сборка слов в строки и строк в таблички, похожесть строк) и `TextTracker` (подтверждение текста несколькими сканами) |
| `Assets/Vision/SignReader.cs` | **Чтение табличек.** Раз в 2.5 с снимает кадр, распознаёт в фоновом потоке, собирает таблички, после 2 подтверждений находит точку таблички по глубине (луч через пиксель → `EnvironmentRaycastManager`) и сообщает `SonarLandmarks.Report(Text, точка, текст)`. **Правый хват** - прочитать вслух всё, что видно сейчас. Копирует языковые данные из `StreamingAssets/tessdata` в `persistentDataPath/tesseract/tessdata` при первом запуске. Метка справа в поле зрения: состояние камеры, OCR, голоса, дверей |
| `Assets/Vision/DoorFinder.cs` | **Двери.** (1) По карте препятствий: `NavZones.FindPassages` - проём 0.6-1.6 м между высокими препятствиями, за которым свободно ≥ 1 м, подтверждённый в 3 сканах подряд (0.5 с каждый) → `Report(Door)`; точно открыт. (2) Из модели комнаты MRUK (Space Setup): якоря `DOOR_FRAME` и `WINDOW_FRAME` как постоянные ориентиры; проём по карте рядом с такой дверью не дублируется |
| `Assets/NavZones.cs` | Геометрия проёмов и коридоров по карте (из v3.1, проверена тестом) |
| `Assets/Plugins/Android/tesseract4android-4.8.0.aar` | Tesseract 5.5.0 + Leptonica для arm64 (3.2 МБ; из оригинального AAR с JitPack удалены armeabi-v7a, x86, x86_64) |
| `Assets/StreamingAssets/tessdata/rus.traineddata`, `eng.traineddata` | Языковые данные `tessdata_fast` (3.9 + 4.1 МБ) |
| `Assets/Plugins/Android/AndroidManifest.xml` | + `horizonos.permission.HEADSET_CAMERA`, `android.permission.CAMERA` |
| `tools/unity-check/VisionCheck.cs` | Тесты: двери по карте (NavZones), `SignText`, `TextTracker`, переворот кадра |

Все скрипты создаются сами при старте сцены (`RuntimeInitializeOnLoadMethod`); вешать ничего не нужно.

## Как это работает

```
PassthroughCameraAccess (MRUK) ──GetColors()──► PassthroughCameraFeed: серый кадр + поза камеры
        │  раз в 2.5 с (или правый хват)
        ▼
 SignReader ──► фоновый поток: TesseractRecognizer.Recognize → слова (текст, уверенность, рамка, строка)
        │
        ├─ SignText.GroupIntoSigns: отсев (уверенность ≥ 60, ≥ 2 букв/цифр, без «ШШШ»), строки → таблички
        ├─ TextTracker.Observe: тот же текст (похожесть ≥ 0.75) виден 2 скана подряд → подтверждён
        ├─ PixelToRay(центр таблички) → EnvironmentRaycastManager.Raycast → мировая точка (иначе 2 м по лучу)
        └─ SonarLandmarks.Report(id, Text, точка, текст)
                 │
                 ▼
 AudioSonar.UpdateLandmarks (ветка sound, не менялась): мелодия «текст» + SpeakHandler(текст) → SpeechOutput → TTS

 ObstacleMap.QueryCell ──► DoorFinder: NavZones.FindPassages (каждые 0.5 с) → треки проёмов → Report(Door)
 MRUK.Instance.Rooms  ──► DoorFinder: DOOR_FRAME / WINDOW_FRAME (каждые 3 с) → Report(Door / Window, persistent)
                 │
                 ▼
 AudioSonar.UpdateLandmarks: «динь-дон» двери / две ноты окна из направления на ориентир (±50°, до 3 м, раз в 8 с)
```

Голосом произносится **только текст табличек** (и короткие служебные фразы по правому хвату: «текста не видно»,
«камера не готова»). Названия «дверь» / «окно» произносятся, только если в `AudioSonar` включить `speakLandmarkNames`.

## Что сделать на ПК со шлемом

1. Взять ветку: `git clone -b vision https://github.com/Anikor/quest-sonar.git` или `git checkout vision` в проекте.
2. Убедиться, что в проекте есть **MR Utility Kit** (`com.meta.xr.mrutilitykit`, входит в `com.meta.xr.sdk.all` 201):
   в нём и `EnvironmentRaycastManager`, и `PassthroughCameraAccess`. Для дверей из модели комнаты в сцене должен быть
   Building Block **MR Utility Kit** (в сцене `oculus3` он есть) и размеченная комната (Space Setup на шлеме).
3. Unity сам импортирует `tesseract4android-4.8.0.aar` (Plugins/Android) и создаст `.meta` для новых файлов - их стоит закоммитить.
4. Manifest: Unity может перегенерировать `AndroidManifest.xml` из настроек Meta (Meta > Tools > Update AndroidManifest.xml).
   После этого проверить, что строка `<uses-permission android:name="horizonos.permission.HEADSET_CAMERA" />` осталась.
5. Собрать и запустить. При первом запуске шлем спросит разрешение на камеру - разрешить.
6. Minimum API / target: Horizon OS v74+ на шлеме (камера passthrough). Если `PassthroughCameraAccess.IsSupported` = false,
   в метке будет «камера passthrough не поддерживается».

## Как проверить на шлеме

1. **Голос.** Сразу после запуска в метке справа: `Голос: готов (ru)`. Если `готов (en, русского голоса нет)` - в настройках
   шлема нет русского голоса TTS (текст будет читаться английским голосом, кириллица может пропускаться).
2. **Камера.** Метка: `камера 1280x960`. Если «запрос разрешения на камеру» висит - разрешение не дали; переустановить или
   разрешить в настройках приложения.
3. **OCR.** Метка: `Текст: текста не видно, N мс` - распознавание работает; `N` ожидается 500-3000 мс.
   Если `Tesseract: init не удался` - не скопировались `*.traineddata` (смотреть logcat `SignReader:`).
4. **Табличка.** Встать в 0.7-1.5 м от двери с табличкой (крупный печатный текст, например «ВЫХОД», номер кабинета),
   смотреть прямо на неё. Через 2 скана (≈5 с) должна прозвучать мелодия «три щелчка» из направления таблички и голос прочитать текст.
   Метка показывает последний текст в «кавычках». Записать: что было на табличке и что прочитано.
5. **Правый хват.** Сжать правый хват - через 1-3 с голос читает всё, что видно (до 3 надписей, крупные первыми)
   или говорит «текста не видно».
6. **Дверь по карте.** Встать в 2-4 м от открытой двери лицом к ней, 5-10 с посмотреть на дверь и косяки.
   Метка: `Двери: по карте 1 ... ближайшая 2.4 м прямо, ширина 0.9 м`, из направления двери «динь-дон» (раз в 8 с).
7. **Дверь из модели.** Если комната размечена: `из модели 1`, «динь-дон» идёт и у закрытой двери.
8. Если текст читается с мусором: поднять `minConfidence` (70) или `confirmScans` (3) в Inspector у объекта `SignReader`.
   Если таблички не находятся: опустить `minConfidence` (50), подойти ближе, проверить освещение.

## Журнал для разбора после прогона (VisionLog)

Всё, что видела и решала ветка vision, пишется на шлем в `Android/data/com.anikor.questsonar/files/vision-log/<дата-время>/`
(отдельно от журналов эксперимента `trials.csv`, их код не трогается):

| Файл | Что внутри |
|---|---|
| `events.jsonl` | по строке JSON на событие: `capture` (кадр, поза головы и камеры, скорость головы), `ocr` (все слова Tesseract **до отсева** с уверенностью и рамками, таблички, треки, что сообщено в реестр и на каком расстоянии), `speak`, `tts_init`, `doors` (проёмы по карте и треки каждый скан), `door_report`/`door_forget`, `scene` (двери из модели комнаты), `map` |
| `frames/scan-NNNN.jpg` | серый кадр камеры, по которому шло распознавание (до 600 кадров, ~200 КБ каждый) |
| `map-NNN.txt` | ASCII-карта препятствий 8×8 м вокруг пользователя раз в 5 с: `#` высокое, `o` низкое, `.` свободно, пробел неизвестно, `@` голова, `^` взгляд, `D`/`d` проём, `S` дверь из модели |

Забрать на ПК: `tools\pull-vision-logs.cmd` (или `adb pull /sdcard/Android/data/com.anikor.questsonar/files/vision-log`).
По кадрам и словам видно, что именно видел OCR и почему текст отсеян; по картам - почему проём не найден (нет высоких
клеток у косяков, за дверью неизвестно, ширина не в 0.6-1.6 м). Метка в шлеме показывает «Журнал: сканов N, кадров M».

## Параметры

| Параметр | Значение | Где |
|---|---|---|
| `languages` / `scanInterval` / `minConfidence` / `confirmScans` / `textSimilarity` | rus+eng / 2.5 с / 60 / 2 / 0.75 | SignReader |
| `maxRange` / `fallbackDistance` / `readOnGrip` | 4 м / 2 м / вкл | SignReader |
| `cameraPosition` / `requestedResolution` / `maxFramerate` / `flipVertical` | Left / 1280×960 / 15 / вкл | PassthroughCameraFeed |
| `language` / `repeatCooldown` / `speechRate` | ru / 4 с / 1.1 | SpeechOutput |
| `interval` / `range` / `minWidth` / `maxWidth` / `minDepthBeyond` / `confirmScans` / `forgetAfter` | 0.5 с / 5 м / 0.6 / 1.6 / 1 м / 3 / 3 с | DoorFinder |
| `useSceneModel` / `sceneWindows` / `sceneRange` | вкл / вкл / 6 м | DoorFinder |

## Ограничения и что дальше

- **Tesseract** хорошо читает крупный печатный текст с близкого расстояния и плохо - мелкий, под углом, при плохом свете.
  Отсев и подтверждение по двум сканам убирают большую часть мусора, но не весь. Если качества не хватит, следующий шаг -
  нейросетевой OCR (PaddleOCR в Unity Sentis: детектор текста DBNet + распознавание CRNN с кириллицей); интерфейс
  `ITextRecognizer` для этого и сделан, остальной конвейер не меняется.
- Дверь по карте = любой сквозной проём 0.6-1.6 м между высокими предметами (и проход между шкафами тоже). Дверь по
  картинке (нейросеть) не делалась. Закрытая дверь находится только из модели комнаты (MRUK).
- `flipVertical`: предполагается, что массив пикселей `GetColors()` идёт снизу вверх (как у текстур Unity). Если на шлеме
  OCR ничего не находит даже на крупном тексте, первым делом выключить `flipVertical` в Inspector у `PassthroughCameraFeed`.
- `ExperimentController` держит свой экземпляр TTS (условие D); при слиянии веток его стоит перевести на `SpeechOutput`.
- Распознавание в фоне занимает одно ядро CPU на 0.5-3 с каждые 2.5 с; если FPS просядет, увеличить `scanInterval`.

## Проверка без шлема
Во временном проекте (`tools/unity-check/README.md`) дополнительно скопировать `Assets/Vision/*.cs` и `Assets/NavZones.cs`,
`VisionCheck.cs` - в `Assets/Editor/`, запустить `-executeMethod VisionCheck.Run`. Последняя строка `VISIONCHECK: OK DONE`.
