# quest-sonar: звуковой сонар для слепых на Quest 3

Unity-приложение для Meta Quest 3: датчик глубины (Meta Depth API) → карта препятствий вокруг пользователя → пространственные звуки и вибрация контроллеров. Дополнение к трости или собаке-проводнику, а не замена.

## Ветки и версии

| Ветка / тег | Что | Состояние |
|---|---|---|
| `main`, тег `v4.0-stable` | **Стабильная версия с ПК со шлемом (2026-10-30).** Звук v4: дальность по скорости ходьбы, три независимые полосы (центр / лево / право), три класса предмета по высоте, «опасно» и «стоп», обрыв, исключение руки и трости; реестр ориентиров `SonarLandmarks` (двери, окна, текст) и голос Android TTS; режимы эксперимента и запись попыток в CSV (`ExperimentController`); `DepthSampler` с позой камеры глубины из положения глаза. Полная папка `Assets` (сцена `oculus3`, настройки) и `Packages` | проверена на шлеме, работает |
| `sound` | **Ветка коллеги: только звук.** Создана от `v4.0-stable`. Тембры, ритмы, громкости, голосовые подсказки, вибрация: всё в `AudioSonar.cs` и `SonarLandmarks.SpeakHandler` | в работе |
| `vision` | **Ветка зрения: двери, таблички, текст вслух.** Создана от `v4.0-stable`. Поиск дверей (модель комнаты MRUK + проёмы по карте), кадры с камер passthrough, распознавание текста табличек (Tesseract, рус + англ), чтение вслух (Android TTS). Сообщает ориентиры в `SonarLandmarks`, звук их озвучивает | в работе, на шлеме не проверена |
| `archive/v3.1-audio-zones`, тег `v3.1-audio-zones` | Звуковая навигация v3.1: зоны слева / прямо / справа, «путь свободен», «тупик», стук двери, обзор по курку (`NavZones.cs`, `SonarSounds.cs`) | компилировалась и проходила тесты, на шлеме не проверялась; в `main` не вошла |
| тег `v3.0-stable` | Карта и звук v1, `DepthSampler` с автоподбором соглашений и GPU-облако | проверена на шлеме |
| тег `v1-stable` | стабильная v1: только `Assets/*.cs` | архив |
| `dev`, теги `v2-depth-gpu`, `v2.1-depth-gpu`, `v2.2-depth-gpu` | Экспериментальная v2: карта и звук по точкам текстуры глубины, GPU-облако мешем | на шлеме точки неверны, отладка остановлена |
| `stable/v1-audio-sonar`, тег `v1-audio-sonar` | исходная v1 до чистки кода | архив |

## Как взять

```bash
git clone https://github.com/Anikor/quest-sonar.git              # стабильная v4.0 (ветка main)
git clone -b sound  https://github.com/Anikor/quest-sonar.git     # ветка звука
git clone -b vision https://github.com/Anikor/quest-sonar.git     # ветка зрения (двери, таблички, текст)
```

`Assets/` и `Packages/` - это соответствующие папки Unity-проекта (Unity 2022.3.62f3, Meta XR SDK 201.0.0 через `com.meta.xr.sdk.all`,
Meta XR Audio SDK 85.0.0). Достаточно скопировать `Assets/*.cs` и `Assets/Resources/DepthSample.compute`, `DepthPointRender.shader`
в свой проект с Meta XR Core SDK и MRUK: скрипты создают свои объекты сами при старте сцены, вешать ничего не нужно.

## Как работают вместе ветки `sound` и `vision`

Граница между ветками - файл `Assets/SonarLandmarks.cs` (реестр ориентиров) и голос:

- **`vision` сообщает** ориентиры: `SonarLandmarks.Report(id, LandmarkKind.Door | Window | Text, мировая_точка, текст)`.
  Сама ничего не озвучивает, кроме текста табличек через `SonarLandmarks.SpeakHandler`.
- **`sound` озвучивает** то, что лежит в `SonarLandmarks.All` (сейчас: `AudioSonar.UpdateLandmarks`, короткие мелодии
  `clipDoor`, `clipWindow`, `clipText`), и может подключить свой голос: `SonarLandmarks.SpeakHandler = ...`.
- Правило: `vision` не меняет `AudioSonar.cs`, `sound` не меняет файлы зрения. Тогда ветки сливаются в `main` без конфликтов.

Подробности: `PROJECT_OVERVIEW.md` (в каждой ветке описана её версия).
