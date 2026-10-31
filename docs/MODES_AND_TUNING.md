# Режимы работы и ручная настройка порогов (ветка `vision`)

Состояние (2026-10-31): код написан в `Assets/Modes/`, **компилируется** (Roslyn из Unity 2022.3.62f3 против сборок
проекта: Meta XR SDK 201, MRUK 201, Sentis; вариант плеера Android и вариант редактора, ошибок и предупреждений в
`Assets/Modes` нет). Чистая логика панели (`TuningModel`) прогнана вне Unity: все проверки прошли.
**На шлеме не проверялось ничего.** Файлы коллеги `AudioSonar.cs` и `ExperimentController.cs` не менялись:
всё сделано через их публичные поля и методы, предложения правок к ним даны ниже как «было / стало».

## 1. Что добавлено

| Файл | Роль |
|---|---|
| `Assets/Modes/OperatingModeController.cs` | `enum OperatingMode` и контроллер режимов: переключение левым стиком по кругу, голос + тон + вибрация на каждую смену, настройки режима через публичные поля `AudioSonar` / `SignReader` / `DoorFinder` / `DoorVision` с возвратом прежних значений при выходе, проверка «опасность и стоп слышны», включение `ExperimentController` только в режиме «Эксперимент», действие режима на правом курке |
| `Assets/Modes/TuningPanel.cs` | Панель порогов в пространстве (TextMesh ~1 м перед головой), управление правым стиком, голос на каждое действие, PlayerPrefs, «сбросить всё» |
| `Assets/Modes/TuningModel.cs` | Чистая логика панели: список параметров (мин, макс, шаг, по умолчанию), ограничения, прижатие с текстом предупреждения, ручные пороги, запись в поля `AudioSonar` |
| `Assets/Modes/ThresholdLog.cs` | `persistentDataPath/thresholds.csv`: фактические пороги рядом с `trials.csv` |
| `Assets/Modes/SignReaderBridge.cs` | «Прочитать сейчас» для курка в режиме «Текст» без правки `SignReader.cs` |
| `Assets/Modes/ModeVoice.cs` | Фразы на языке голоса (ro / ru / en, как `DoorFinder.DoorWord`), числа и метры для речи, тоны, короткая вибрация |
| `Assets/Modes/Editor/ModesCheck.cs` | Тест для временного проекта: `-executeMethod ModesCheck.Run`, последняя строка `MODESCHECK: OK DONE` |

Все скрипты создаются сами (`RuntimeInitializeOnLoadMethod(AfterSceneLoad)` + `FindFirstObjectByType`), сцену трогать не нужно.
Хранилище только `PlayerPrefs` (ключи `quest-sonar.tune.*`), журналы - CSV рядом с `trials.csv` и события `op_mode` / `trigger_read` в `VisionLog`.

## 2. Кнопки

### 2.1. Сейчас (по коду ветки `vision`)

| Кнопка | `OVRInput` | Кто занимает | Когда |
|---|---|---|---|
| A / X | `Button.One` / `Button.Three` | `AudioSonar`: звук вкл / выкл | только если `toggleWithButtons`; `ExperimentController.Start` ставит `toggleWithButtons = false`, поэтому сейчас A/X **ничего не делают** |
| B / Y | `Button.Two` / `Button.Four` | `DepthPointCloud`: passthrough вкл / выкл | всегда |
| Левый хват | `Button.PrimaryHandTrigger` | `DepthSampler`: `poseFromHead` (отладка позы камеры глубины) | всегда |
| Правый хват | `Button.SecondaryHandTrigger` | `SignReader`: прочитать вслух всё, что видно | `readOnGrip` |
| Левый стик (нажать) | `Button.PrimaryThumbstick` | `ExperimentController`: следующее условие A-D | всегда |
| Правый стик (нажать) | `Button.SecondaryThumbstick` | `ExperimentController`: столкновение | всегда |
| Левый курок | `Button.PrimaryIndexTrigger` | `ExperimentController`: ложная тревога | всегда |
| Правый курок | `Button.SecondaryIndexTrigger` | `ExperimentController`: конец попытки (строка в `trials.csv`) | всегда |
| Оси стиков | `Axis2D.PrimaryThumbstick` / `SecondaryThumbstick` | никто | - |

Проблема сейчас: кнопки эксперимента работают и вне эксперимента (случайное нажатие стика меняет условие, в том числе на
**A = Off, сонар молчит**, без всякого сообщения).

### 2.2. Предложено (реализовано в `Assets/Modes`)

| Кнопка | «Эксперимент» | Остальные режимы | Панель открыта |
|---|---|---|---|
| Левый стик ← / → (держать 0.3 с) | предыдущий / следующий режим | предыдущий / следующий режим | заблокировано: «сначала закройте настройки» |
| Левый стик ↑ / ↓ | свободно | свободно | свободно |
| Левый стик (нажать) | `ExperimentController`: условие | назвать текущий режим | назвать текущий режим |
| Левый курок | `ExperimentController`: ложная тревога | свободно | свободно |
| Правый курок | `ExperimentController`: конец попытки (+ строка `trial_end` в `thresholds.csv`) | действие режима: «Текст» = прочитать, «Ориентиры» = ближайшая дверь / окно, иначе назвать режим | действие режима |
| Правый стик ↑ ↓ ← → | свободно | свободно | выбор параметра / значение |
| Правый стик (нажать) | `ExperimentController`: столкновение | свободно | значение по умолчанию |
| X | «настройки закрыты в режиме эксперимент» | открыть / закрыть панель | закрыть панель |
| A | свободно (резерв) | свободно (резерв) | свободно |
| B / Y | `DepthPointCloud` (без изменений) | без изменений | без изменений |
| Левый хват | `DepthSampler` (без изменений) | без изменений | без изменений |
| Правый хват | `SignReader` (без изменений) | без изменений | без изменений |

Как это сделано без правки файлов коллеги:
- `ExperimentController` выключается (`enabled = false`) во всех режимах, кроме «Эксперимента». Unity в выключенном
  компоненте не вызывает `Update`, а `Start` вызывает при первом включении, поэтому его TTS, метка и `trials.csv` появятся
  при первом входе в «Эксперимент». Его метка `ExperimentLabel` вне эксперимента скрывается.
- `AudioSonar.toggleWithButtons = false` всегда (то же, что уже делает `ExperimentController`): молча заглушить сонар
  кнопкой нельзя, для этого есть явный режим «Тишина» с голосовым предупреждением. A освобождается.
- Рекомендация (не сделано, файл не из `Assets/Modes`): левый хват `DepthSampler` убрать за флажок отладки - случайное
  сжатие на ходу переключает позу камеры глубины.

## 3. Режимы (`OperatingMode`)

`OperatingMode` ортогонален `AudioSonar.FeedbackMode`: условия эксперимента A-D меняет только `ExperimentController`.
Контроллер режимов трогает `AudioSonar.Mode` в двух случаях: при выходе из «Эксперимента» возвращает условие, которое было
до входа, и вне эксперимента не оставляет `Off` (ставит `Spatial` и говорит об этом).

| № | Режим | Что меняется (только публичные поля; при выходе всё возвращается) | Голос при входе |
|---|---|---|---|
| 1 | Навигация (при старте) | ничего: весь сонар как настроен в панели | «Режим: навигация.» |
| 2 | Только опасность | `nearVolume = farVolume = 0`, `minInterval = maxInterval ≥ 1 с` (дальние писки беззвучны и редки), `sideWarning = false`, `landmarkCues = false`, `detectDropOff = true`; двери (`doorSound`, `doorVoice`), `DoorVision.detectDoors`, `SignReader.readSigns`, `speakConfirmed` выключены | «… Только опасность, стоп и обрыв.» |
| 3 | Текст | сонар как в «Только опасность»; `SignReader.readSigns = true`, `speakConfirmed = true`, `scanInterval ≤ 1.5 с`, `maxReadDistance ≥ 3 м`; голос дверей и `DoorVision` выключены (камера и GPU для OCR). **Правый курок = прочитать то, что перед тобой** | «… Сонар: только опасность. Курок: прочитать текст.» |
| 4 | Ориентиры | сонар как в «Только опасность», но `landmarkCues = true`; `DoorFinder.doorSound = doorVoice = true`, `DoorVision.detectDoors = true`. **Правый курок = «Ближайшая дверь справа, 3 метра»** (двери и окна из `SonarLandmarks`, все стороны, до 8 м, обновлявшиеся за 20 с) | «… Курок: ближайшая дверь.» |
| 5 | Узкий проход | `pathHalfWidth ≤ 0.25`, `maxRange ≤ 1.5` (но не меньше `dangerDistance + 0.1`), `rangeSlow/rangeFast/rangeFixed/sideRange ≤ maxRange`, `sideWarning = true`, `sideVolume ≥ 0.85` | «Режим: узкий проход.» |
| 6 | Тишина | по умолчанию (`silenceKeepStop = true`): как «Только опасность» + `dangerVolume = 0`; **«стоп» и обрыв остаются**. Если `silenceKeepStop = false`: `SetMuted(true)`, молчит всё. Распознавание и голос дверей выключены | «… Остаются только стоп и обрыв.» или «… Внимание: опасность и стоп выключены.» Две вибрации вместо одной |
| 7 | Эксперимент | `ExperimentController.enabled = true`, его метка видна; пороги фиксированы (`experimentManualThresholds`: `contactMin = contactDistance`, `dangerMin = dangerDistance`, дальность стоя ≥ опасность + 0.1); панель закрыта | «… Условие C.» (для A: «сонар выключен, только трость»); смена условия тоже произносится |

**Подтверждение смены режима** (без него менять режим нельзя: экран пользователь не видит):
голос «Режим: …» (с обрывом текущей фразы), тон - столько коротких нот 523 Гц, каков номер режима (1-7; частота не
совпадает ни с одним сигналом сонара: писки 140-320 Гц, «стоп» 1400 Гц, дверь 660/880 Гц), короткая вибрация 0.12 с
обоих контроллеров (не вмешивается, если `AudioSonar` уже вибрирует из-за опасности). Если голос не готов
(`SpeechOutput.ready = false`), режим не меняется: из «Навигации» - низкий тон отказа и двойная вибрация, из любого
другого режима - переход в «Навигацию».

**Безопасность.** После каждой смены в любом режиме, кроме «Тишины», проверяется и при необходимости исправляется:
`muted = false`, `FeedbackMode ≠ Off` (кроме «Эксперимента»), `dangerVolume` и `contactVolume` не нулевые,
`masterVolume ≥ 0.2`, `detectDropOff = true`. Если что-то исправлено, голос добавляет «Опасность и стоп снова включены».
«Опасность» и «стоп» в режимах 2-5 не трогаются: они звучат по `dangerVolume` / `contactVolume`, а не по
`nearVolume` / `farVolume`. При запуске приложения всегда «Навигация» (режим не сохраняется).

## 4. Окно ручной настройки порогов (`TuningPanel`)

Открыть / закрыть: **X**. Правый стик вверх / вниз - параметр (голосом: название и значение), влево / вправо - значение
(удержание = автоповтор 0.5 / 0.15 с; переключатели: вправо = вкл, влево = выкл), нажатие правого стика - значение по
умолчанию. «Сбросить всё» - вправо два раза за 3 с. Значение сохраняется в PlayerPrefs сразу, `PlayerPrefs.Save()` - при
закрытии панели и при сворачивании приложения. Панель стоит в мире на 1 м перед курсом головы и переезжает перед
взгляд, если голова отвернулась больше чем на 35°. Изменённые значения отмечены `*`, заданные режимом - серым «(режим)»:
их панель не меняет и говорит «Сейчас задано режимом узкий проход».

| Параметр (поле `AudioSonar`) | Мин | Макс | Шаг | По умолчанию* |
|---|---|---|---|---|
| Ручные пороги (служебный) | выкл | вкл | - | выкл |
| `dangerDistance` | 0.3 | 2.0 | 0.05 | 0.9 |
| `contactDistance` | 0.2 | 1.0 | 0.05 | 0.4 |
| `speedAdaptiveRange` (автодальность) | выкл | вкл | - | вкл |
| `maxRange` | 1.0 | 6.0 | 0.25 | 3.0 |
| `rangeSlow` (видно при автодальности) | 0.3 | 3.0 | 0.1 | 0.5 |
| `rangeFast` (видно при автодальности) | 0.5 | 6.0 | 0.25 | 2.0 |
| `rangeFixed` (видно без автодальности) | 0.5 | 6.0 | 0.25 | 1.8 |
| `sideRange` | 0.3 | 3.0 | 0.1 | 1.0 |
| `pathHalfWidth` | 0.2 | 0.8 | 0.05 | 0.4 |
| `dropDepth` | 0.1 | 0.6 | 0.05 | 0.3 |
| `landmarkRange` | 1.0 | 6.0 | 0.5 | 3.0 |
| `masterVolume` | 0.2 | 1.0 | 0.1 | 1.0 |
| `haptics` | выкл | вкл | - | вкл, но выкл, пока работает `ControllerVibrationOff` |

\* По умолчанию = **фактическое значение поля `AudioSonar` при старте** (таблица - инициализаторы на 2026-10-30). Если
коллега поменяет значение в `AudioSonar.cs`, панель возьмёт новое; границы расширяются, чтобы его вместить.
`masterVolume` не ниже 20 %: при 0 молчат и «опасность», и «стоп». `maxRange` выше `ObstacleMap.maxDistance` (4 м)
разрешён, но панель предупреждает: дальше карта точек не даёт.

**Ограничения** (`Gap = 0.1 м`): `contactDistance + 0.1 ≤ dangerDistance ≤ maxRange − 0.1`; `sideRange ≤ maxRange`;
`rangeSlow ≤ rangeFast ≤ maxRange`; `rangeFixed ≤ maxRange`; при ручных порогах ещё `rangeSlow` (или `rangeFixed` без
автодальности) `≥ dangerDistance + 0.1`. Нарушение: меняемое значение прижимается к границе, голос: «0,8 метра.
Ограничение: не больше 0,8 метра из-за параметра Опасность.» (то же оранжевой строкой на панели). Остальные параметры не
меняются; исключение - переключатели «ручные пороги» и «автодальность»: они поднимают зависимые дальности сами и говорят
«Изменено: Дальность стоя 1 метр». Сохранённые значения при загрузке приводятся к тем же ограничениям.

`haptics`: `ControllerVibrationOff` (просьба пользователя) каждые 2 с ставит `haptics = false`. Если в панели вибрацию
включить явно, панель снимает его флажок `disableVibration`, при выключении возвращает исходный.

### 4.1. Ручные пороги, `UpdateThresholds()` и автодальность

`AudioSonar.UpdateThresholds()` (закрытый, вызывается каждый кадр из `UpdateSpeedAndRange`) считает:

```
curSideRange = Min(sideRange, curRange)
contactNow   = Clamp(curRange * contactFraction, contactMin, contactDistance)
dangerNow    = Clamp(curRange * dangerFraction,  dangerMin,  dangerDistance)
if (dangerNow < contactNow + 0.08) dangerNow = contactNow + 0.08
```

то есть `contactDistance` и `dangerDistance` - это **верхние пределы**, а фактические пороги растут с текущей дальностью
`curRange`, которая сама растёт со скоростью ходьбы (`speedAdaptiveRange`: 0.5 м стоя → 2 м при 3 км/ч → до `maxRange`).
Стоя на месте по умолчанию «опасность» = 0.3 м и «стоп» = 0.2 м, хотя в полях записано 0.9 и 0.4.

**Ручные пороги** (переключатель в панели, и всегда в «Эксперименте») ставят `contactMin = contactDistance` и
`dangerMin = dangerDistance`. `Clamp(x, a, a) = a`, поэтому `contactNow = contactDistance` и `dangerNow = dangerDistance`
при любой дальности и скорости; правка `AudioSonar.cs` не нужна, изменения подхватываются в следующем кадре. При
выключении возвращаются исходные `contactMin` / `dangerMin` (запомненные при старте, 0.2 / 0.3).

Взаимодействие с автодальностью: `AudioSonar.OnHit` отбрасывает точки центральной полосы дальше `curRange`. Если стоя
`curRange = rangeSlow = 0.5 м`, а `dangerDistance = 0.9 м`, препятствие на 0.7 м просто не видно, и ручной порог 0.9 м
не сработает, пока человек не пойдёт. Поэтому при ручных порогах панель держит `rangeSlow ≥ dangerDistance + 0.1`
(без автодальности - `rangeFixed ≥ dangerDistance + 0.1`). Автодальность при этом продолжает работать: дальние писки
по-прежнему зависят от скорости, фиксированы только «опасность» и «стоп». Выключить автодальность - отдельный
переключатель («Автодальность»), тогда `curRange = Min(rangeFixed, maxRange)`. Боковая дальность всегда
`Min(sideRange, curRange)`. В CSV пишется и `danger_min` / `contact_min`, и `current_range`, так что по строке видно,
какие пороги действовали.

## 5. Пороги в CSV эксперимента (`thresholds.csv`)

`ExperimentController` пишет `trials.csv` и не знает порогов; его код не трогается. Рядом, в
`Application.persistentDataPath/thresholds.csv`, `ThresholdLog` пишет **фактические** значения полей `AudioSonar` (уже с
учётом режима) в моменты: `boot` (после загрузки PlayerPrefs), `tune` (закрытие панели или 3 с без правок), `reset_all`,
`mode` (каждая смена режима), `condition` (смена условия A-D в «Эксперименте»), `trial_end` (правый курок в
«Эксперименте» - тот же кадр, в котором `ExperimentController.EndTrial` пишет строку). Столбцы:

```
timestamp,participant,event,op_mode,feedback_mode,manual_thresholds,speed_adaptive,danger_distance,contact_distance,
danger_min,contact_min,danger_fraction,contact_fraction,max_range,range_slow,range_fast,range_fixed,current_range,
side_range,path_half_width,drop_depth,detect_drop,landmark_range,landmark_cues,master_volume,haptics,muted,near_volume,
far_volume,danger_volume,contact_volume,side_warning,side_volume
```

Связь с `trials.csv`: `participant` (тот же `participant.txt`) и `timestamp` (тот же формат `DateTime.Now:s`); строке
попытки соответствует строка `trial_end` с той же секундой. Числа - с точкой (InvariantCulture). Если набор столбцов
поменяется, старый файл переименовывается в `thresholds-old-<дата>.csv`. Забрать: `adb pull
/sdcard/Android/data/com.anikor.questsonar/files/thresholds.csv`.

Замечание по `trials.csv`: `{Time.time - trialStart:F1}` в `EndTrial` форматируется текущей культурой устройства; если на
шлеме включена ro/ru локаль, длительность может записаться с запятой и сломать столбцы CSV (не проверено).

## 6. Режим «Текст»: интеграция с SignReader

- OCR уже есть: `SignReader` раз в `scanInterval` снимает кадр с `PassthroughCameraFeed` (Passthrough Camera API через
  `PassthroughCameraAccess` MRUK), Tesseract в фоновом потоке, подтверждение по 2 сканам, точка по глубине,
  `SonarLandmarks.Report(Text)` и чтение вслух через `SpeechOutput`. Правый хват = прочитать всё, что видно, сразу.
- Режим «Текст» включает фоновое чтение (`readSigns`, `speakConfirmed`), сканирует чаще (1.5 с), читает дальше (3 м),
  выключает `DoorVision` (камера и GPU) и голос дверей, сонар сводит к «только опасность».
- **Правый курок = «прочитать то, что перед тобой»**: `SignReaderBridge.ReadNow` делает ровно то, что обработчик правого
  хвата в `SignReader.Update` (`forceScan = speakAfterScan = true`, `forceSince = Time.time`). Флаги закрытые, поэтому
  через отражение; если в `SignReader` появится публичный `ReadNow()`, мост вызовет его. Перед этим проверяется
  готовность камеры и OCR («Камера не готова», «Распознавание не готово»). Подтверждение - нота 784 Гц и «Читаю», результат
  читает сам `SignReader` («текста не видно» или до трёх надписей, крупные первыми).
- Предлагаемая правка `SignReader.cs` (файл ветки `vision`, не коллеги; не сделана, чтобы не выходить за `Assets/Modes`):

```csharp
// было: только в Update, по правому хвату
if (readOnGrip && OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
{
    VisionLog.Event("grip", ...);
    forceScan = true;
    speakAfterScan = true;
    forceSince = now;
    if (!feed.IsReady) { ... } else if (!ocrReady) { ... }
}

// стало: тот же код в публичном методе, Update зовёт его
if (readOnGrip && OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger)) ReadNow();

/// <summary>Прочитать вслух всё, что видно сейчас (правый хват, курок в режиме «Текст»).</summary>
public void ReadNow()
{
    float now = Time.time;
    VisionLog.Event("grip", $"\"cameraReady\":{VisionLog.B(feed != null && feed.IsReady)},\"ocrReady\":{VisionLog.B(ocrReady)}");
    forceScan = true;
    speakAfterScan = true;
    forceSince = now;
    if (feed == null || !feed.IsReady) { SpeechOutput.Speak("камера не готова", true); forceScan = false; speakAfterScan = false; }
    else if (!ocrReady) { SpeechOutput.Speak("распознавание не готово", true); forceScan = false; speakAfterScan = false; }
}
```

### 6.1. Варианты OCR на Quest 3

Цифры задержки - оценки по опыту и документации, **на шлеме не замерены** (кроме ожидания 0.5-3 с для Tesseract из
`VISION.md`). Замерять: `lastOcrMs` в метке `SignReader` и событие `ocr` в `VisionLog`.

| Вариант | Задержка на кадр | Батарея / нагрузка | Приватность | Офлайн | Языки ro/ru/en | Работа |
|---|---|---|---|---|---|---|
| **Tesseract 5 через JNI** (сейчас: `tesseract4android` AAR, LSTM, два прохода) | 0.5-3 с (оценка) | одно ядро CPU на время распознавания каждые 1.5-2.5 с; GPU свободен для рендера и `DoorVision` | кадры не покидают шлем | да | есть (`ron`, `rus`, `eng` traineddata) | готово; слаб на мелком, наклонном, бликующем тексте |
| **PaddleOCR (DBNet + распознаватель) в Unity Sentis** | ~0.1-0.5 с на GPU (оценка) | GPU, конкурирует с рендером и `DoorVision` (тоже Sentis) | не покидают шлем | да | нужна многоязычная модель с латиницей (ș ț ă î â) и кириллицей; проверить, есть ли готовая под оба алфавита | экспорт ONNX, пред/постобработка (контуры DB, CTC-декод) на C#; интерфейс `ITextRecognizer` уже есть, остальной конвейер не меняется |
| **Нативный плагин** (Paddle Lite / ONNX Runtime Mobile / NCNN в AAR; ML Kit) | ~0.1-0.3 с (оценка) | CPU/NNAPI, эффективнее Sentis на CPU | не покидают шлем | да | ML Kit Text Recognition v2, по документации, - латиница и несколько азиатских письменностей, **кириллицы нет** (не уверен, проверить); ML Kit через Play Services на Horizon OS нет (только встроенная модель, не проверено) | JNI, сборка под arm64, поддержка двух кодовых баз |
| **Облако** (Google Cloud Vision, Azure AI Vision Read) - только запасной | 0.5-2 с + сеть | Wi-Fi, CPU мало | **кадры квартиры, лиц, документов уходят на сервер**: нужно явное согласие, GDPR | нет | лучшее качество, все три языка | HTTP-клиент, ключ, деньги за запрос |

Рекомендация: оставить Tesseract, замерить на шлеме; если качества не хватит - PaddleOCR в Sentis (пакет уже в проекте,
интерфейс готов). Облако - только по явному запросу (хват / курок), с голосовым предупреждением «отправляю снимок», никогда
не в фоне.

## 7. Правки файлов коллеги: «было / стало» (не применены)

### 7.1. ExperimentController → общий голос SpeechOutput (минимум: 3 места)

После слияния `vision` в `main` (в ветке `sound` файла `SpeechOutput.cs` нет). Смысл: два экземпляра Android TTS на одном
движке перебивают друг друга, а `SpeechOutput` уже умеет выбирать движок (eSpeak NG), язык и не морозит главный поток.

```csharp
// было (Start)
        InitTts();
        CreateLabel();
// стало
        CreateLabel();
```

```csharp
// было (Voice)
        if (!ttsReady || Time.time - lastSpeak < voiceInterval) return;
// стало
        if (SpeechOutput.Instance == null || !SpeechOutput.Instance.ready || Time.time - lastSpeak < voiceInterval) return;
        ttsRu = SpeechOutput.Instance.language.StartsWith("ru");
```

```csharp
// было
    void Speak(string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { tts.Call<int>("speak", text, 0, null, null); }
        catch (Exception e) { Debug.LogWarning("ExperimentController: speak: " + e.Message); }
#else
        Debug.Log("TTS: " + text);
#endif
    }
// стало (interrupt = true - то же, что QUEUE_FLUSH = 0 в старом вызове; в редакторе SpeechOutput сам пишет в консоль)
    void Speak(string text) { SpeechOutput.Speak(text, true); }
```

`InitTts`, `FinishTtsInit`, `TtsInit`, поля `tts`/`ttsReady`/`initStatus` после этого не нужны, но могут остаться
(без `InitTts()` `tts` = null, строка `FinishTtsInit` в `Update` не срабатывает). Внимание: `SpeechOutput` предпочитает
румынский (`languagesPreferred = ro, ru, en`); фразы условия D есть только ru/en, при ro будут английские фразы
румынским голосом - стоит добавить ro-вариант («în față / la stânga / la dreapta», «aproape / un metru / doi metri»).

### 7.2. ExperimentController: кнопки только в «Эксперименте» (не обязательно)

Уже сделано снаружи (`enabled = false`). Если коллега хочет явную проверку в своём файле:

```csharp
// было
    void Update()
    {
        if (sonar == null) return;
// стало
    void Update()
    {
        if (sonar == null) return;
        if (OperatingModeController.Instance != null && OperatingModeController.Instance.Current != OperatingMode.Experiment) return;
```

Минус: файл ветки `sound` начнёт зависеть от `Assets/Modes`. Пороги в сам `trials.csv` не добавляются: это сломало бы
формат уже собранных файлов; связь через `thresholds.csv` (раздел 5).

### 7.3. AudioSonar (не обязательно, всё работает и без этого)

Ручные пороги явным флажком вместо `contactMin = contactDistance`:

```csharp
// было
    void UpdateThresholds()
    {
        curSideRange = Mathf.Min(sideRange, curRange);
        contactNow = Mathf.Clamp(curRange * contactFraction, contactMin, contactDistance);
        dangerNow = Mathf.Clamp(curRange * dangerFraction, dangerMin, dangerDistance);
// стало
    [Tooltip("Ручные пороги: опасность и стоп = dangerDistance и contactDistance при любой скорости")]
    public bool fixedThresholds = false;

    void UpdateThresholds()
    {
        curSideRange = Mathf.Min(sideRange, curRange);
        contactNow = fixedThresholds ? contactDistance : Mathf.Clamp(curRange * contactFraction, contactMin, contactDistance);
        dangerNow = fixedThresholds ? dangerDistance : Mathf.Clamp(curRange * dangerFraction, dangerMin, dangerDistance);
```

«Без дальних писков» явным флажком. Сейчас режимы 2-4 и 6 делают `nearVolume = farVolume = 0` и растягивают интервал до
1 с: писки идут беззвучно, но отмечают `lastOnset`, а `UpdateLandmarks` молчит 0.35 с после каждого писка. Флажок убирает
и беззвучные писки:

```csharp
// было (UpdateCenter, ветка дальних писков)
        else
        {
            clip = c.kind == ObjectKind.Tall ? clipTall
// стало
        else
        {
            if (!farBeeps) return;   // режимы «только опасность»: опасность и стоп остаются
            clip = c.kind == ObjectKind.Tall ? clipTall
// и поле рядом с sideWarning:
    [Tooltip("Писки препятствий дальше границы опасности (выкл = только опасность и стоп)")]
    public bool farBeeps = true;
```

Если коллега добавит эти поля, в `OperatingModeController.QuietSonar` и `TuningModel.ApplyTo` достаточно заменить
по две строки (поставить флажки вместо громкостей / `contactMin`).

## 8. План (6 шагов)

1. **Сборка и тест без шлема.** Во временном проекте (`tools/unity-check/README.md`) дополнительно скопировать
   `Assets/Modes/*.cs` и `Assets/Modes/Editor/ModesCheck.cs`, запустить `-executeMethod ModesCheck.Run`
   (последняя строка `MODESCHECK: OK DONE`).
2. **Шлем, кнопки и голос** - чек-лист раздела 9, пункты 1-6.
3. **Подбор порогов** в помещении через панель (сначала автопороги, затем ручные), значения остаются в PlayerPrefs;
   проверить `thresholds.csv`.
4. **Пилот эксперимента** в режиме «Эксперимент»: условия A-D, попытки, сверка `trials.csv` и `thresholds.csv` по `trial_end`.
5. **Правки коллеги** по разделу 7 (TTS в `ExperimentController` после слияния; по желанию `fixedThresholds` и
   `farBeeps` в `AudioSonar`) и `SignReader.ReadNow()` вместо отражения.
6. **OCR**: замер Tesseract на шлеме (`lastOcrMs`, батарея за 30 мин в режиме «Текст»), решение о PaddleOCR / Sentis.

## 9. Проверка на шлеме (чек-лист)

1. Старт: тишина в голосе, режим «Навигация» (Inspector `OperatingModeController.status`), сонар звучит как раньше.
   В `persistentDataPath` появилась строка `boot` в `thresholds.csv`.
2. Левый стик вправо и подержать: «Режим: только опасность…», 2 ноты, короткая вибрация. Дальние писки пропали;
   подойти к стене: на 0.3-0.9 м частые низкие импульсы, вплотную высокий «стоп». Ещё вправо: «текст» (3 ноты) и т. д.
   по кругу, 7 режимов, влево - обратно. Случайное короткое задевание стика (< 0.3 с) режим не меняет.
3. «Текст»: встать перед табличкой, правый курок → нота, «Читаю», через 1-3 с текст или «текста не видно».
   Без камеры - «Камера не готова». Если сказано «Используйте правый хват» - отражение под IL2CPP не сработало (журнал).
4. «Ориентиры»: постоять перед открытой дверью 5-10 с, правый курок → «Ближайшая дверь справа, 3 метра» (проверить
   сторону и расстояние рулеткой). Без дверей - «Дверей и окон рядом не найдено».
5. «Узкий проход»: в проходе 0.8-1 м боковые писки слева/справа громче, дальность короче.
6. «Тишина»: «Остаются только стоп и обрыв», две вибрации; подойти к стене вплотную - звучит только «стоп».
   Следующий режим - «Эксперимент»: «Условие C», метка `ExperimentController` появилась. Нажать левый стик: «Условие D»
   (голос), строка `condition` в CSV. Правый курок: строка в `trials.csv` и `trial_end` в `thresholds.csv`.
   Выйти в «Навигацию»: метка скрылась, условие вернулось, кнопки эксперимента больше не действуют.
7. Панель: X → панель перед глазами, голос «Настройки. Ручные пороги, выключено». Правым стиком пройти все строки,
   изменить «Опасность» вправо до упора: на 2.0 м (или ниже) - предупреждение «Ограничение: … из-за параметра …».
   «Стоп» вверх до упора: прижат к «опасность − 0.1». Нажать правый стик: «По умолчанию, …». «Сбросить всё»: вправо
   дважды. Закрыть X, перезапустить приложение: значения сохранились. При открытой панели левый стик режим не меняет.
8. Ручные пороги вкл: «Изменено: Дальность стоя 1 метр»; стоя на месте импульсы опасности начинаются на 0.9 м (а не 0.3).
9. Вибрация: при включённом `ControllerVibrationOff` в панели «Вибрация, выключено»; включить - вибрация при опасности
   вернулась и через 2 с не пропала.
10. В режиме «Эксперимент» X → «Настройки закрыты в режиме эксперимент».
11. Голос не готов (например, eSpeak не стоит и движки не запустились): левый стик → низкий тон отказа, режим не меняется.

## 10. Что не проверено и в чём я не уверен

Не проверено на шлеме вообще ничего: кнопки, голос, тоны (слышны ли поверх писков), вибрация, вид панели (размер шрифта
0.0065 × 48, фон 0.95 × 0.68 м подобраны на глаз; `<size>` в rich text `TextMesh`), догоняние взгляда, PlayerPrefs и запись
CSV на Android, включение и выключение `ExperimentController` и его метки, отражение закрытых полей `SignReader` под IL2CPP
(может вырезаться стрипингом), правильность стороны «справа / слева» в запросе двери (`Vector3.SignedAngle`, плюс = справа),
беззвучные дальние писки и мелодии ориентиров в режимах 2-4, совместная работа с `ControllerVibrationOff` (он каждые 2 с
выключает вибрацию и может оборвать подтверждающий импульс). Румынские фразы написаны без проверки носителем языка.

API, сверенные по исходникам Meta XR Core SDK 201 (`Library/PackageCache/com.meta.xr.sdk.core@201.0.0/Scripts/OVRInput.cs`):
`Button.One / Three / PrimaryThumbstick / SecondaryThumbstick / PrimaryIndexTrigger / SecondaryIndexTrigger /
SecondaryHandTrigger`, `Axis2D.PrimaryThumbstick / SecondaryThumbstick`, `OVRInput.Get(Axis2D)`, `GetDown(Button)`,
`SetControllerVibration(frequency, amplitude, controller)`. При `Controller.Active` (по умолчанию) Primary = левый
Touch, Secondary = правый, One = A, Three = X. Из Unity: `Behaviour.enabled = false` останавливает `Update`, но не
корутины; `Start` выполняется при первом включении компонента; `GameObject.Find` находит только активные объекты.
Не уверен: ML Kit без Play Services на Horizon OS и наличие кириллицы в ML Kit v2; готовая многоязычная PaddleOCR-модель
под латиницу с диакритикой и кириллицу одновременно.

Открытый вопрос (не блокирует, есть переключатель): в «Эксперименте» пороги сейчас **ручные** (опасность и стоп не
зависят от скорости, `experimentManualThresholds = true`). Если для эксперимента нужно сохранить штатное масштабирование
коллеги и только запретить правки, выключить этот флажок в Inspector у `OperatingModeController`.
