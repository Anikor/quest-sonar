# Проверка кода без шлема (временный Unity-проект)

## StableCheck (ветка main, v4.x)
Компиляция всех скриптов проекта с пакетами Meta и тесты реестра ориентиров `SonarLandmarks` (стабильный id, Report без дублей,
сохранение текста, устаревание по `ttl`, Remove/Clear).

1. Создать пустой проект: `Unity.exe -batchmode -nographics -createProject <dir> -quit -logFile <log>`.
2. Заменить `<dir>/Packages/manifest.json`:
   - Unity 2022.3 (как на ПК со шлемом): `manifest-2022.json` (Meta SDK 201.0.0 - версия проекта);
   - Unity 6000.x: `manifest-6000.json` (Meta SDK 207.0.1; SDK 201 под Unity 6000 не компилируется: `GetInstanceID` obsolete).
   Первый запуск `Unity.exe -batchmode -nographics -projectPath <dir> -quit` скачивает пакеты (несколько минут).
3. Скопировать `Assets/*.cs`, `Assets/Resources/DepthSample.compute` и `DepthPointRender.shader` из репозитория в `<dir>/Assets/`,
   `StableCheck.cs` - в `<dir>/Assets/Editor/`.
4. Запустить: `Unity.exe -batchmode -nographics -projectPath <dir> -executeMethod StableCheck.Run -quit -logFile <log>`.
5. В логе искать `error CS` и строки `STABLECHECK:`; последняя должна быть `OK DONE`. Код выхода: 0 - всё хорошо, 3 - тест не прошёл.

## VisionCheck (ветка vision)
Дополнительно скопировать `Assets/Vision/*.cs` (в `<dir>/Assets/Vision/`) и `Assets/NavZones.cs`, `VisionCheck.cs` - в `<dir>/Assets/Editor/`,
запустить `-executeMethod VisionCheck.Run`. Тесты: двери по карте (`NavZones`), отсев и сборка текста (`SignText`),
подтверждение текста (`TextTracker`), переворот кадра (`PassthroughCameraFeed.ToGray`). Последняя строка `VISIONCHECK: OK DONE`.

Что не проверяется без шлема: Android TTS (`ExperimentController`), Depth API, OVRInput, шейдер глубины (compute-шейдер
компилируется при первом Dispatch, `ShaderUtil.GetComputeShaderMessages` в batch-режиме ничего не возвращает).

## Ветка dev (v2.x): CompileCheck
GPU-тест compute-шейдера `DepthSample.compute`: `-executeMethod CompileCheck.Run` (без `-nographics`). Подробности в `DEPTH_GPU_PLAN.md` ветки `dev`.
