# Проверка кода без шлема (временный Unity-проект)

Что проверяется: компиляция C# со всеми зависимостями Meta и работа compute-шейдера `DepthSample.compute`
на GPU редактора: синтетическая текстура глубины по формулам Meta -> kernel `Sample` -> сравнение с геометрической
истиной (без обратной матрицы); kernel `BuildQuads` -> вершинный буфер меша, созданного как в `DepthPointCloud`.

1. Создать пустой проект: `Unity.exe -batchmode -nographics -createProject <dir> -quit -logFile <log>`
   (нужен Unity 6000.0.66 или новее для Meta SDK 207; проверено на 6000.6.6f1).
2. Заменить `<dir>/Packages/manifest.json` на `manifest.json` из этой папки: реестр Meta `https://npm.developer.oculus.com`,
   пакеты `com.meta.xr.sdk.core` и `com.meta.xr.mrutilitykit` 207.0.1, остальное - стандартные модули.
3. Скопировать `Assets/*.cs` и `Assets/Resources/DepthSample.compute` из репозитория в `<dir>/Assets/`,
   а `CompileCheck.cs` - в `<dir>/Assets/Editor/`.
4. Запустить: `Unity.exe -batchmode -projectPath <dir> -executeMethod CompileCheck.Run -quit -logFile <log>`
   (без `-nographics`: тесту нужен графический API). Первый запуск скачивает пакеты, несколько минут.
5. В логе искать `error CS` (ошибки C#), `Shader error` (HLSL) и строки `COMPILECHECK:`; последняя должна
   заканчиваться `OK DONE`. Код выхода: 0 - всё хорошо, 3 - тест не прошёл, 2 - шейдер не найден.

Замечание: `ShaderUtil.GetComputeShaderMessages` в batch-режиме ничего не возвращает (compute-шейдер при импорте
только разбирается, а компилируется при первом Dispatch), поэтому проверка идёт через реальный запуск kernel'ов.
Результат 2026-10-23 (Direct3D12): 4096/4096 точек, максимальная ошибка 4.6e-6 м; квады без ошибок.
