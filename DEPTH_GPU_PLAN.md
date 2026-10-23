# Quest 3: переход с Raycast на чтение текстуры глубины шейдером

Дата среза: 2026-10-23
Связанный файл: `PROJECT_OVERVIEW.md` (проект quest-sonar, версия `v2-depth-gpu`)

> Статус: **все 4 части сделаны, скомпилированы и проверены тестом на GPU редактора** (Unity 6000.6.6f1, Meta XR Core SDK 207.0.1, MRUK 207.0.1, Direct3D12): kernel `Sample` восстанавливает точки из синтетической текстуры глубины с ошибкой ~5 мкм относительно геометрической истины, слой «нет глубины» даёт 0 точек, `flipY` действует, `BuildQuads` корректно пишет квады в вершинный буфер меша. **На шлеме не проверялось.** Математика сверена по исходникам Meta, а не по памяти. Как повторить проверку: `tools/unity-check/README.md`.

---

## 1. Что было (v1-audio-sonar)

Git-репозиторий `quest-sonar`: тег `v1-audio-sonar`, ветки `main`, `dev` (работа), `stable/v1-audio-sonar` (не менять). Копия файлов в `snapshots/v1-audio-sonar/`.

Проблема: `EnvironmentRaycastManager.Raycast` даёт сотни точек за кадр (бюджет `scanBudgetMs` = 5 мс, 32–512 лучей). Нужны тысячи.

---

## 2. Идея

`EnvironmentDepthManager` (Meta XR Core SDK) кладёт в **глобальные** шейдерные переменные:

| Переменная | Что это |
|---|---|
| `_EnvironmentDepthTexture` | карта глубины: `RenderTexture`, `Tex2DArray`, 2 слоя (глаза), depth-формат, квадратная |
| `_EnvironmentDepthReprojectionMatrices` | 2 матрицы: мировая точка → clip-пространство камеры глубины |
| `_EnvironmentDepthZBufferParams` | параметры линеаризации глубины |

Compute-шейдер читает текстуру и обратной матрицей восстанавливает мировые точки на GPU. Для отрисовки данные остаются на GPU, для карты и звука последний кадр читается на CPU через `AsyncGPUReadback` (задержка 1–3 кадра).

---

## 3. Факты, подтверждённые по исходникам Meta (com.meta.xr.sdk.core 207.0.1, com.meta.xr.mrutilitykit 207.0.1)

| Факт | Где в исходниках | Следствие для кода |
|---|---|---|
| `_EnvironmentDepthReprojectionMatrices[eye] = proj · view · trackingSpace.worldToLocal`, где `proj` — off-axis проекция **в стиле OpenGL** (NDC-z ∈ [−1,1], w = −z_eye), `view` — обратная поза камеры глубины в tracking space | `Scripts/EnvironmentDepth/EnvironmentDepthUtils.cs`, `EnvironmentDepthManager.OnBeforeRender` | вход — мировая точка; `invVP = matrices[slice].inverse` на CPU; в шейдере `h = invVP·(uv·2−1, raw·2−1, 1)`, `мир = h.xyz/h.w` |
| `uv = (clip.xy / clip.w + 1) / 2`, переворота Y **нет** | `Shaders/EnvironmentDepth/EnvironmentOcclusion.cginc`, `CalculateEnvironmentDepthOcclusion` | `flipY` по умолчанию выключен |
| NDC-z = `raw·2 − 1`; линейная глубина `= ZB.x / (ndcZ + ZB.y)`; `raw == 1` → «нет глубины» | там же, `SampleEnvironmentDepthLinear`; `ComputeNdcToLinearDepthParameters` | фильтр валидности в шейдере |
| MRUK восстанавливает точки на CPU ровно так: `clip = (u·2−1, v·2−1, ndcZ, 1)`, `мир = inv(VP)·clip / w` | `EnvironmentDepthRaycaster.WorldPosAtDepthTexCoord` (MRUK) | наш kernel `Sample` — та же формула на GPU |
| Собственный compute-шейдер Meta биндит глобальную `RenderTexture` обычным `SetTexture` в `Texture2DArray<float>` | `BuildingBlocks/AIBlocks/Resources/CopyDepthTextureIntoNativeArray.compute`, `DepthTextureAccess.cs` | тот же способ; запас: `bindDepthElement` → `RenderTextureSubElement.Depth` |
| Менеджер всё делает в `Application.onBeforeRender`; текстура и матрицы ставятся **независимо** от `OcclusionShadersMode`; `SoftOcclusion` (по умолчанию) добавляет препроцессинг | `EnvironmentDepthManager.cs` | читаем глобальные в `Update` (согласованная пара прошлого кадра); своему менеджеру ставим `OcclusionShadersMode.None` |
| `IsSupported` (static), `IsDepthAvailable`, `RemoveHands`, `OcclusionShadersMode` — публичные; событие `onDepthTextureUpdate` — internal; `Awake` требует ровно один менеджер | `EnvironmentDepthManager.cs` | найти существующий или добавить один |
| `EnvironmentRaycastManager` — пакет **MRUK** (namespace `Meta.XR`), провайдер по умолчанию нативный, менеджер глубины ему не нужен и не мешает | `Core/Scripts/EnvironmentRaycastManager.cs` (MRUK) | пакет MRUK должен остаться в проекте |

---

## 4. Что сделано (все части)

| Часть | Что | Статус |
|---|---|---|
| 1 | `Resources/DepthSample.compute` (kernel `Sample`/`Sample2D`) + `DepthSampler.cs`: сетка 112×112 точек за кадр, кольцевой GPU-буфер на 8 кадров + копия последнего кадра на CPU, самопроверка vs Raycast | сделано, kernel проверен GPU-тестом |
| 2 | Отрисовка из GPU-буфера: kernel `BuildQuads` пишет квады прямо в вершинный буфер меша (`Mesh.GetVertexBuffer`), материал `Sprites/Default`; `DepthPointCloud` переключается на меш, частицы — запасной режим | сделано, kernel и путь через вершинный буфер проверены GPU-тестом |
| 3 | `ObstacleMap.ScanTexture()`: обход точек с бюджетом времени, общая `ProcessHit` для лучей и точек, подвыборка для очистки клеток (`carveEvery`) и событий `OnDepthHit` (`eventEvery`); `AudioSonar` без изменений | сделано, скомпилировано |
| 4 | `depthSource` = Auto / Raycast / Texture с автооткатом на лучи, строка `Глубина:` в HUD, документация, коммит и тег `v2-depth-gpu` | сделано |

### Что изменилось относительно черновика Части 1 (из прошлого чата)
1. **NDC-z.** Черновик подставлял сырое значение `d` как NDC-z; по исходникам Meta нужно `d·2−1`. Это была единственная ошибка математики.
2. **Кольцевой буфер истории** сразу в `DepthSampler` (`historyFrames`), а не отдельной частью.
3. **Запасной kernel** для `Texture2D` и переключатель `bindDepthElement` вместо просто ошибки в консоль.
4. **Чередование глаз** (`alternateEyes`), сетка 112×112 (текстура квадратная).
5. Буфер обнуляется при создании (`SetData`), иначе старые слоты кольца могли бы содержать мусор.
6. Самопроверка сравнивает **расстояние между точкой и попаданием луча** (обе в мире), а не два расстояния от головы: не зависит от движения головы между кадрами.

### Почему отрисовка без собственного шейдера
Собственный вершинный шейдер + `Graphics.RenderPrimitives` на Quest требует правильных stereo-макросов (multiview) — главный «слепой» риск. Вместо этого compute-шейдер пишет готовые квады в вершинный буфер обычного меша, а рисует его `Sprites/Default`, который в проекте уже рисует частицы в стерео. Цена: 4 вершины × 20 байт на точку, ≈ 400 тыс. вершин при 8 кадрах истории — для Quest 3 приемлемо.

---

## 5. Что ждёт проверки на шлеме

| Что проверить | Как увидеть | Если плохо |
|---|---|---|
| Математика точек | HUD: `ошибка < 5 см`, облако совпадает с комнатой | `flipY`, затем `alternateEyes=false` + `eye=1`, затем `bindDepthElement`; прислать HUD и консоль |
| Тип/формат текстуры на версии SDK пользователя | консоль: `DepthSampler: текстура глубины 320x320 Tex2DArray …` | если `не умею читать` — прислать строку |
| Переход карты на точки | HUD: `Глубина: текстура` через 1–2 с | `textureMinPoints`, `textureMaxError` в `ObstacleMap`; `depthSource = Texture` принудительно |
| GPU-облако в стерео | плотное облако, одинаковое в обоих глазах | `useGpuCloud = false` → частицы |
| Нагрузка | HUD: `Скан: 72/с`, `мс` | уменьшить `historyFrames`/`gridW`/`gridH`, увеличить `carveEvery` |
| Звук при 3 000 событий/кадр | писки не чаще, чем раньше (окно 0.12 с) | увеличить `eventEvery` |

---

## 6. Известные риски

| Риск | Что делать |
|---|---|
| Формат или тип `_EnvironmentDepthTexture` отличается от ожидаемого (старый SDK) | Kernel для `Texture2D`, `bindDepthElement`, подробный лог. Присылай текст дословно |
| Матрицы на старом SDK в другом пространстве | Самопроверка покажет большую ошибку, Auto останется на лучах. Присылай HUD |
| `Mesh.GetVertexBuffer` + compute на Vulkan/Android | Документировано с Unity 2021.2; `useGpuCloud = false` → частицы |
| Нагрузка на CPU при обходе тысяч точек | Бюджет `scanBudgetMs`, подвыборка `carveEvery`/`eventEvery` |
| `RemoveHands` требует настройки | Если ошибка, `removeHands = false` |
| Датчик не видит стекло, зеркала, тонкие предметы | Ограничение железа |

---

## 7. Как продолжить работу

В новом чате: приложить `PROJECT_OVERVIEW.md`, этот файл и архив репозитория (`quest-sonar-v2-depth-gpu.zip`), написать, что показал шлем (строка HUD, консоль), и что делать дальше.

Идеи на следующий шаг — в `PROJECT_OVERVIEW.md`, раздел «Идеи на потом».
