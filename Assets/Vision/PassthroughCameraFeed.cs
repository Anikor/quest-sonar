using System;
using Meta.XR;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// Кадры с камеры passthrough Quest 3 (Meta Passthrough Camera Access, компонент PassthroughCameraAccess из MR Utility Kit).
/// Запрашивает разрешение horizonos.permission.HEADSET_CAMERA, создаёт компонент камеры и отдаёт по запросу
/// кадр в оттенках серого (строки сверху вниз) вместе с позой камеры на момент кадра, чтобы после долгого распознавания
/// пиксель можно было превратить в мировой луч (PixelToRay) и найти точку по глубине.
///
/// Требования: Horizon OS v74+, Quest 3 / 3S, в манифесте разрешение HEADSET_CAMERA (есть в репозитории).
/// Скрипт создаётся сам при старте сцены (его создаёт SignReader, если ещё нет).
/// </summary>
public class PassthroughCameraFeed : MonoBehaviour
{
    public static PassthroughCameraFeed Instance { get; private set; }

    public const string CameraPermission = "horizonos.permission.HEADSET_CAMERA";

    [Tooltip("Какая камера: левая или правая")]
    public PassthroughCameraAccess.CameraPositionType cameraPosition = PassthroughCameraAccess.CameraPositionType.Left;
    [Tooltip("Запрошенное разрешение. Если не поддерживается, берётся ближайшее меньшее")]
    public Vector2Int requestedResolution = new Vector2Int(1280, 960);
    [Tooltip("Частота кадров камеры. Для чтения табличек раз в пару секунд хватает малой")]
    public int maxFramerate = 15;
    [Tooltip("Массив пикселей Unity идёт снизу вверх; для распознавания текста строки переворачиваются сверху вниз")]
    public bool flipVertical = true;

    [Header("Состояние (только чтение)")]
    public string status = "не запущена";
    public Vector2Int resolution;
    public bool permissionGranted;

    /// <summary>Один кадр для обработки на CPU: серый, строки сверху вниз (если flipVertical).</summary>
    public sealed class Frame
    {
        public byte[] gray;
        public int width, height;
        public Pose cameraPose;
        public float time;
        public bool flipped;
    }

    PassthroughCameraAccess cam;
    bool asked;

    public bool IsReady => cam != null && cam.IsPlaying;

    public static PassthroughCameraFeed GetOrCreate()
    {
        if (Instance != null) return Instance;
        var existing = FindFirstObjectByType<PassthroughCameraFeed>();
        if (existing != null) return existing;
        return new GameObject("PassthroughCameraFeed").AddComponent<PassthroughCameraFeed>();
    }

    void Awake() { Instance = this; }

    void Start()
    {
        if (!PassthroughCameraAccess.IsSupported)
        {
            status = "камера passthrough не поддерживается (нужен Quest 3 / 3S, Horizon OS v74+)";
            Debug.LogWarning("PassthroughCameraFeed: " + status);
            return;
        }
        RequestPermissionThenStart();
    }

    void RequestPermissionThenStart()
    {
        if (Permission.HasUserAuthorizedPermission(CameraPermission))
        {
            permissionGranted = true;
            CreateCamera();
            return;
        }
        if (asked) return;
        asked = true;
        status = "запрос разрешения на камеру";
        var cb = new PermissionCallbacks();
        cb.PermissionGranted += _ => { permissionGranted = true; CreateCamera(); };
        cb.PermissionDenied += _ => { status = "разрешение на камеру отклонено"; Debug.LogWarning("PassthroughCameraFeed: " + status); };
        Permission.RequestUserPermission(CameraPermission, cb);
    }

    void CreateCamera()
    {
        if (cam != null) return;
        try
        {
            // Поля компонента надо задать до его включения: создаём на выключенном объекте.
            var go = new GameObject("PassthroughCameraAccess");
            go.SetActive(false);
            go.transform.SetParent(transform, false);
            cam = go.AddComponent<PassthroughCameraAccess>();
            cam.CameraPosition = cameraPosition;
            cam.RequestedResolution = requestedResolution;
            cam.enabled = false;
            cam.MaxFramerate = Mathf.Max(1, maxFramerate);
            cam.enabled = true;
            go.SetActive(true);
            status = "камера запускается";
        }
        catch (Exception e)
        {
            status = "камера: " + e.Message;
            Debug.LogWarning("PassthroughCameraFeed: " + status);
        }
    }

    void Update()
    {
        if (cam == null)
        {
            if (!permissionGranted && asked && Permission.HasUserAuthorizedPermission(CameraPermission)) { permissionGranted = true; CreateCamera(); }
            return;
        }
        if (cam.IsPlaying)
        {
            if (resolution != cam.CurrentResolution)
            {
                resolution = cam.CurrentResolution;
                status = $"камера {resolution.x}x{resolution.y}";
            }
        }
    }

    /// <summary>
    /// Снять текущий кадр в серый буфер (строки сверху вниз). Дорогая операция (чтение с GPU), вызывать не чаще раза в секунду.
    /// false, если камера ещё не готова.
    /// </summary>
    public bool TryCapture(Frame frame)
    {
        if (!IsReady || frame == null) return false;
        NativeArray<Color32> colors;
        try { colors = cam.GetColors(); }
        catch (Exception e) { status = "кадр: " + e.Message; return false; }
        if (!colors.IsCreated || colors.Length == 0) return false;

        int w = cam.CurrentResolution.x, h = cam.CurrentResolution.y;
        if (w <= 0 || h <= 0 || colors.Length < w * h) return false;

        if (frame.gray == null || frame.gray.Length != w * h) frame.gray = new byte[w * h];
        ToGray(colors, frame.gray, w, h, flipVertical);
        frame.width = w;
        frame.height = h;
        frame.flipped = flipVertical;
        frame.cameraPose = cam.GetCameraPose();
        frame.time = Time.time;
        return true;
    }

    /// <summary>Серый по яркости (BT.601), при flip строка 0 результата = верхняя строка картинки.</summary>
    public static void ToGray(NativeArray<Color32> src, byte[] dst, int w, int h, bool flip)
    {
        for (int y = 0; y < h; y++)
        {
            int srcRow = (flip ? h - 1 - y : y) * w;
            int dstRow = y * w;
            for (int x = 0; x < w; x++)
            {
                Color32 c = src[srcRow + x];
                dst[dstRow + x] = (byte)((c.r * 77 + c.g * 150 + c.b * 29) >> 8);
            }
        }
    }

    /// <summary>Мировой луч через пиксель кадра (px вправо, py вниз от верхнего края, как в результатах OCR) по позе кадра.</summary>
    public Ray PixelToRay(Frame frame, float px, float py)
    {
        float u = (px + 0.5f) / frame.width;
        float v = (py + 0.5f) / frame.height;
        if (frame.flipped) v = 1f - v;   // viewport Meta: (0,0) внизу слева
        if (cam == null || !cam.IsPlaying)
            return new Ray(frame.cameraPose.position, frame.cameraPose.rotation * Vector3.forward);
        return cam.ViewportPointToRay(new Vector2(u, v), frame.cameraPose);
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
