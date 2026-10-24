# Проверка кода без шлема (временный Unity-проект)

Два набора проверок:

## StableCheck (ветка main, v3.x)
Компиляция всех скриптов с пакетами Meta и тесты логики звуковой навигации (`NavZones`, `SonarSounds`) на синтетических комнатах:
дверь 0.9 м находится (и когда за ней ничего не видно, и слева от взгляда), проём 2.5 м / зазор между низкой мебелью /
ниша 0.5 м дверями не считаются; зоны: стена прямо на 1.5 м → лучшая зона левая, стена поперёк на 0.7 м → свободной нет;
коридор 0.6 м не проходит в щель 0.4 м; все звуки конечны, пик ≤ 1, не тишина.

1. Создать пустой проект: `Unity.exe -batchmode -nographics -createProject <dir> -quit -logFile <log>`.
2. Заменить `<dir>/Packages/manifest.json`:
   - Unity 2022.3 (как на ПК со шлемом): `manifest-2022.json` (Meta SDK 201.0.0 - версия проекта);
   - Unity 6000.0.66+: `manifest.json` (Meta SDK 207.0.1; SDK 201 под Unity 6000.6 не компилируется: `GetInstanceID` obsolete).
3. Скопировать `Assets/*.cs`, `Assets/Resources/DepthSample.compute` и `DepthPointRender.shader` из репозитория в `<dir>/Assets/`,
   `StableCheck.cs` - в `<dir>/Assets/Editor/`.
4. Запустить: `Unity.exe -batchmode -projectPath <dir> -executeMethod StableCheck.Run -quit -logFile <log>`.
5. В логе искать `error CS` и строки `STABLECHECK:`; последняя должна быть `OK DONE`. Код выхода: 0 - всё хорошо, 3 - тест не прошёл.

## CompileCheck (ветка dev, v2.x)
GPU-тест compute-шейдера `DepthSample.compute` (kernels `Sample`, `BuildQuads`): синтетическая текстура глубины по формулам Meta →
сравнение с геометрической истиной. Запуск: `-executeMethod CompileCheck.Run` (без `-nographics`). Подробности в `DEPTH_GPU_PLAN.md` ветки `dev`.

Замечание: `ShaderUtil.GetComputeShaderMessages` в batch-режиме ничего не возвращает (compute-шейдер компилируется при первом
Dispatch), поэтому шейдеры проверяются только реальным запуском.
