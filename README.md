# quest-sonar: звуковой сонар для слепых на Quest 3

Unity-приложение для Meta Quest 3: датчик глубины (Meta Depth API) → карта препятствий вокруг пользователя → пространственные звуки и вибрация контроллеров. Дополнение к трости или собаке-проводнику, а не замена.

## Ветки и версии

| Ветка / тег | Что | Состояние |
|---|---|---|
| `main`, тег `v3.0-stable` | **Стабильная версия (v3.0)** с ПК со шлемом: карта и звук v1 на лучах `EnvironmentRaycastManager`, облако точек частицами; дополнительно `DepthSampler` (чтение текстуры глубины с автоподбором соглашений) и GPU-облако, которое включается только при ошибке самопроверки < 5 см. Полная папка `Assets` проекта (сцена `oculus3`, настройки XR/Oculus) и `Packages/manifest.json` | проверена на шлеме, работает |
| `main`, тег `v1-stable` | стабильная v1: только `Assets/*.cs` | архив |
| `dev`, теги `v2-depth-gpu`, `v2.1-depth-gpu`, `v2.2-depth-gpu` | **Экспериментальная v2:** карта и звук по точкам текстуры глубины, GPU-облако мешем | на шлеме точки неверны, отладка остановлена |
| `stable/v1-audio-sonar`, тег `v1-audio-sonar` | исходная v1 до чистки кода | архив |

## Как взять

```bash
git clone https://github.com/Anikor/quest-sonar.git            # стабильная v3 (ветка main)
git clone -b dev https://github.com/Anikor/quest-sonar.git     # экспериментальная v2
```

`Assets/` и `Packages/` - это соответствующие папки Unity-проекта (Unity 2022.3.62f3, Meta XR SDK 201.0.0 через `com.meta.xr.sdk.all`,
Meta XR Audio SDK 85.0.0). Достаточно скопировать `Assets/*.cs` и `Assets/Resources/DepthSample.compute`, `DepthPointRender.shader`
в свой проект с Meta XR Core SDK и MRUK: скрипты создают свои объекты сами при старте сцены, вешать ничего не нужно.

Подробности: `PROJECT_OVERVIEW.md` (в каждой ветке описана её версия); план и история работ по v2: `DEPTH_GPU_PLAN.md` (ветка `dev`).
