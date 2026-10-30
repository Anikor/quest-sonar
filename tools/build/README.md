# Сборка APK на ПК без папки ProjectSettings

`Assets/Editor/BuildQuest.cs` выставляет настройки плеера (IL2CPP, ARM64, Vulkan, Linear, minSdk 32), регистрирует XR-настройки
из `Assets/XR`, добавляет шейдер Sprites/Default и спатиализатор Meta XR Audio, собирает `Builds/quest-sonar.apk`:

```
Unity.exe -batchmode -projectPath <папка проекта> -executeMethod BuildQuest.Build -quit -logFile build.log
adb install -r Builds/quest-sonar.apk
adb shell am start -n com.anikor.questsonar/com.unity3d.player.UnityPlayerActivity
```

`manifest-this-pc.json` - манифест пакетов для ПК, где Meta XR SDK не куплен в Asset Store: те же пакеты 201.0.0
по отдельности из реестра `https://npm.developer.oculus.com` (пакета `com.meta.xr.sdk.all` в этом реестре нет).
Чтобы им воспользоваться: скопировать поверх `Packages/manifest.json` и удалить `Packages/packages-lock.json` (локально, не коммитить).
