using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

/// <summary>
/// Сборка APK для Quest 3 из командной строки или меню Sonar > Build Quest APK.
/// В репозитории нет папки ProjectSettings (она осталась на ПК со шлемом), поэтому перед сборкой здесь выставляются
/// нужные настройки плеера и регистрируются настройки XR из Assets/XR. На ПК со шлемом этот скрипт тоже безвреден:
/// он только выставляет те же значения.
///
/// Запуск: Unity.exe -batchmode -projectPath <папка> -executeMethod BuildQuest.Build -quit -logFile <лог>
/// Результат: Builds/quest-sonar.apk; в логе строка BUILDQUEST: Succeeded / Failed.
/// </summary>
public static class BuildQuest
{
    const string Scene = "Assets/oculus3.unity";
    const string Apk = "Builds/quest-sonar.apk";
    const string PackageName = "com.anikor.questsonar";

    [MenuItem("Sonar/Build Quest APK")]
    public static void Build()
    {
        try
        {
            ApplyPlayerSettings();
            EnsureXr();
            AddAlwaysIncludedShader("Sprites/Default");
            ChooseSpatializer();
            AssetDatabase.SaveAssets();

            var opts = new BuildPlayerOptions
            {
                scenes = new[] { Scene },
                locationPathName = Apk,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(opts);
            var s = report.summary;
            Debug.Log($"BUILDQUEST: {s.result} размер {s.totalSize / (1024 * 1024)} МБ ошибок {s.totalErrors} предупреждений {s.totalWarnings} время {s.totalTime.TotalMinutes:F1} мин");
            if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 2);
        }
        catch (Exception e)
        {
            Debug.LogError("BUILDQUEST: исключение " + e);
            if (Application.isBatchMode) EditorApplication.Exit(2);
        }
    }

    static void ApplyPlayerSettings()
    {
        PlayerSettings.companyName = "Anikor";
        PlayerSettings.productName = "quest-sonar";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, PackageName);
        PlayerSettings.bundleVersion = "4.1";
        PlayerSettings.Android.bundleVersionCode = Math.Max(PlayerSettings.Android.bundleVersionCode, 2);

        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.Android, Il2CppCompilerConfiguration.Release);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)32;      // Horizon OS требует 32+
        PlayerSettings.Android.targetSdkVersion = (AndroidSdkVersions)32;
        PlayerSettings.Android.optimizedFramePacing = false;
        PlayerSettings.Android.forceInternetPermission = false;

        PlayerSettings.colorSpace = ColorSpace.Linear;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });   // Depth API и passthrough: Vulkan
        PlayerSettings.MTRendering = true;
        PlayerSettings.SetMobileMTRendering(BuildTargetGroup.Android, true);
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;
        PlayerSettings.Android.startInFullscreen = true;
        PlayerSettings.Android.renderOutsideSafeArea = true;

        EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.development = false;
        Debug.Log("BUILDQUEST: настройки плеера выставлены (IL2CPP, ARM64, Vulkan, Linear, minSdk 32)");
    }

    /// <summary>Настройки XR лежат в Assets/XR (с ПК со шлемом); без ProjectSettings они не зарегистрированы в EditorBuildSettings.</summary>
    static void EnsureXr()
    {
        var general = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>("Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
        if (general == null)
        {
            Debug.LogError("BUILDQUEST: нет Assets/XR/XRGeneralSettingsPerBuildTarget.asset");
            return;
        }
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, general, true);

        XRGeneralSettings settings = general.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (settings == null)
        {
            general.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            settings = general.SettingsForBuildTarget(BuildTargetGroup.Android);
        }
        if (settings == null)
        {
            Debug.LogError("BUILDQUEST: не удалось создать настройки XR для Android");
            return;
        }
        settings.InitManagerOnStart = true;
        var mgr = settings.Manager;
        bool hasOculus = mgr != null && mgr.activeLoaders != null && mgr.activeLoaders.Any(l => l != null && l.GetType().Name == "OculusLoader");
        if (!hasOculus)
        {
            bool ok = XRPackageMetadataStore.AssignLoader(mgr, "Unity.XR.Oculus.OculusLoader", BuildTargetGroup.Android);
            Debug.Log("BUILDQUEST: OculusLoader назначен для Android: " + ok);
        }
        else Debug.Log("BUILDQUEST: OculusLoader уже назначен для Android");
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(settings);

        var oculus = AssetDatabase.LoadAssetAtPath<ScriptableObject>("Assets/XR/Settings/OculusSettings.asset");
        if (oculus != null) EditorBuildSettings.AddConfigObject("Unity.XR.Oculus.Settings", oculus, true);
        else Debug.LogWarning("BUILDQUEST: нет Assets/XR/Settings/OculusSettings.asset, будут настройки Oculus по умолчанию");
    }

    static void AddAlwaysIncludedShader(string name)
    {
        var shader = Shader.Find(name);
        if (shader == null) { Debug.LogWarning("BUILDQUEST: шейдер не найден: " + name); return; }
        var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
        if (gs == null) { Debug.LogWarning("BUILDQUEST: нет GraphicsSettings.asset"); return; }
        var so = new SerializedObject(gs);
        var arr = so.FindProperty("m_AlwaysIncludedShaders");
        if (arr == null) return;
        for (int i = 0; i < arr.arraySize; i++)
            if (arr.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
        arr.InsertArrayElementAtIndex(arr.arraySize);
        arr.GetArrayElementAtIndex(arr.arraySize - 1).objectReferenceValue = shader;
        so.ApplyModifiedProperties();
        Debug.Log("BUILDQUEST: шейдер добавлен в Always Included: " + name);
    }

    /// <summary>Пространственный звук: плагин Meta XR Audio, если он есть в проекте (иначе стереопанорама Unity).</summary>
    static void ChooseSpatializer()
    {
        string[] names;
        try { names = AudioSettings.GetSpatializerPluginNames(); }
        catch (Exception e) { Debug.LogWarning("BUILDQUEST: список спатиализаторов: " + e.Message); return; }
        string pick = names.FirstOrDefault(n => n.IndexOf("Meta", StringComparison.OrdinalIgnoreCase) >= 0)
                   ?? names.FirstOrDefault(n => n.IndexOf("Oculus", StringComparison.OrdinalIgnoreCase) >= 0);
        Debug.Log("BUILDQUEST: спатиализаторы: [" + string.Join(", ", names) + "] выбран: " + (pick ?? "нет"));
        if (pick != null) AudioSettings.SetSpatializerPluginName(pick);
    }
}
