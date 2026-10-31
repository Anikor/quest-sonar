using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using Unity.Sentis;
using UnityEngine;

/// <summary>
/// Двери по картинке: нейросеть (YOLOv8, обучена на дверях для слепых, Assets/Vision/Resources/doors.onnx) смотрит на кадр
/// камеры passthrough раз в detectInterval секунд и находит двери, в том числе ЗАКРЫТЫЕ, которых датчик глубины не видит
/// (закрытая дверь для него - стена). Рамка двери переводится в мировую точку лучом через её центр и глубиной
/// (EnvironmentRaycastManager), затем DoorFinder.ReportVisionDoor: подтверждение, звонок, голос, жёлтая метка на карте.
/// Сеть работает на GPU через Unity Sentis; чтение результата асинхронное, главный поток не ждёт.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DoorVision : MonoBehaviour
{
    public static DoorVision Instance { get; private set; }

    [Header("Нейросеть")]
    public bool detectDoors = true;
    [Tooltip("Имя модели в Resources (ONNX, импортированный Sentis). doors-oiv7 = YOLOv8n Open Images V7 (601 класс, лёгкая), doors = YOLOv8s только двери (тяжёлая)")]
    public string modelName = "doors-oiv7";
    [Tooltip("Индекс класса «дверь» в выходе сети (-1 = брать максимум по всем классам; для doors-oiv7 задаётся скриптом экспорта)")]
    public int doorClass = -1;
    [Tooltip("Как часто распознавать, сек. Сеть делит GPU с рендером и глубиной: чаще 1 с заметно тормозит сонар")]
    public float detectInterval = 1.5f;
    [Tooltip("Не распознавать, пока голова поворачивается быстрее, град/с (смазанный кадр)")]
    public float maxHeadSpeedDeg = 40f;
    [Tooltip("Порог уверенности сети (0..1)")]
    [Range(0.1f, 0.95f)] public float confidence = 0.35f;
    [Tooltip("Порог перекрытия рамок для слияния дубликатов")]
    [Range(0.1f, 0.9f)] public float nmsIou = 0.5f;
    [Tooltip("Размер входа сети; берётся из модели, если она его задаёт")]
    public int inputSize = 416;
    [Tooltip("Рамка двери должна быть выше этой доли кадра (далёкие и мелкие не считаем)")]
    [Range(0.05f, 0.9f)] public float minBoxHeight = 0.18f;

    [Header("Положение")]
    public float maxRange = 6f;
    public float fallbackDistance = 3f;

    [Header("Состояние (только чтение)")]
    public string status = "запуск";
    public int lastDetections;
    public float lastInferenceMs;
    public int runs;

    public struct Box
    {
        public float x, y, w, h;   // пиксели кадра, центр и размер, y вниз от верхнего края
        public float score;
        public float XMin => x - w * 0.5f;
        public float XMax => x + w * 0.5f;
        public float YMin => y - h * 0.5f;
        public float YMax => y + h * 0.5f;
    }

    PassthroughCameraFeed feed;
    EnvironmentRaycastManager raycaster;
    Transform head;
    Model model;
    Worker worker;
    Tensor<float> output;
    bool pending;
    Pose framePose;
    Vector2Int frameRes;
    float nextRun;
    System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();
    readonly List<Box> boxes = new List<Box>();
    readonly List<Box> kept = new List<Box>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DoorVision>() == null)
            new GameObject("DoorVision").AddComponent<DoorVision>();
    }

    void Awake()
    {
        Instance = this;
        VisionLog.Init();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady();
        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        feed = PassthroughCameraFeed.GetOrCreate();

        var asset = Resources.Load<ModelAsset>(modelName);
        if (asset == null)
        {
            status = "нет модели Resources/" + modelName + ".onnx";
            Debug.LogWarning("DoorVision: " + status);
            VisionLog.Event("doorvision", $"\"ok\":false,\"status\":{VisionLog.Str(status)}");
            yield break;
        }
        try
        {
            model = ModelLoader.Load(asset);
            worker = new Worker(model, BackendType.GPUCompute);
            var sb = new StringBuilder();
            foreach (var i in model.inputs)
            {
                sb.Append(i.name).Append(':').Append(i.shape.ToString()).Append(' ');
                // вход [1,3,H,W]: размер берём из модели
                if (i.shape.rank == 4 && i.shape.IsStatic())
                {
                    var ts = i.shape.ToTensorShape();
                    if (ts[3] > 0) inputSize = ts[3];
                }
            }
            // индекс класса «дверь» из файла doors-oiv7.txt рядом с моделью (его пишет скрипт экспорта), если не задан
            var idx = Resources.Load<TextAsset>(modelName + "-door-class");
            if (doorClass < 0 && idx != null && int.TryParse(idx.text.Trim(), out int dc)) doorClass = dc;
            foreach (var o in model.outputs) sb.Append("-> ").Append(o.name).Append(' ');
            status = "сеть загружена";
            VisionLog.Event("doorvision", $"\"ok\":true,\"model\":{VisionLog.Str(sb.ToString())},\"interval\":{VisionLog.F(detectInterval)},\"confidence\":{VisionLog.F(confidence)}");
            Debug.Log("DoorVision: " + sb);
        }
        catch (Exception e)
        {
            status = "сеть: " + e.Message;
            Debug.LogWarning("DoorVision: " + status);
            VisionLog.Event("doorvision", $"\"ok\":false,\"status\":{VisionLog.Str(status)}");
        }
    }

    void Update()
    {
        if (worker == null || feed == null || head == null) return;
        float now = Time.time;

        if (pending)
        {
            if (!output.IsReadbackRequestDone()) return;
            pending = false;
            lastInferenceMs = (float)sw.Elapsed.TotalMilliseconds;
            runs++;
            try
            {
                using (var cpu = output.ReadbackAndClone())
                    ParseOutput(cpu);
            }
            catch (Exception e) { status = "разбор: " + e.Message; }
            return;
        }

        if (!detectDoors || now < nextRun || !feed.IsReady) return;
        if (SignReader.Instance != null && SignReader.Instance.headSpeedDeg > maxHeadSpeedDeg) return;   // смаз
        Texture tex = feed.CurrentTexture;
        if (tex == null) return;
        nextRun = now + detectInterval;
        framePose = feed.CurrentPose;
        frameRes = feed.CurrentResolution;
        try
        {
            sw.Restart();
            using (var input = TextureConverter.ToTensor(tex, inputSize, inputSize, 3))
                worker.Schedule(input);
            output = worker.PeekOutput() as Tensor<float>;
            if (output == null) { status = "сеть не дала выход"; return; }
            output.ReadbackRequest();
            pending = true;
            status = "распознаю";
        }
        catch (Exception e)
        {
            status = "сеть: " + e.Message;
            Debug.LogWarning("DoorVision: " + status);
        }
    }

    /// <summary>Выход YOLOv8: [1, 4 + классы, N] (cx, cy, w, h в пикселях входа 640, затем уверенности классов).</summary>
    void ParseOutput(Tensor<float> t)
    {
        boxes.Clear();
        var shape = t.shape;
        if (shape.rank != 3) { status = "выход сети " + shape; return; }
        int rows = shape[1], n = shape[2];
        int classes = rows - 4;
        if (classes < 1) { status = "выход сети " + shape; return; }
        float sx = frameRes.x / (float)inputSize, sy = frameRes.y / (float)inputSize;
        for (int i = 0; i < n; i++)
        {
            float best = 0f;
            if (doorClass >= 0 && doorClass < classes) best = t[0, 4 + doorClass, i];
            else for (int c = 0; c < classes; c++) { float v = t[0, 4 + c, i]; if (v > best) best = v; }
            if (best < confidence) continue;
            boxes.Add(new Box { x = t[0, 0, i] * sx, y = t[0, 1, i] * sy, w = t[0, 2, i] * sx, h = t[0, 3, i] * sy, score = best });
        }
        Nms(boxes, nmsIou, kept);
        lastDetections = kept.Count;

        var log = new StringBuilder();
        int reported = 0;
        foreach (var b in kept)
        {
            if (log.Length > 0) log.Append(',');
            bool bigEnough = b.h >= minBoxHeight * frameRes.y;
            Vector3 pos = Vector3.zero; float dist = -1f; bool byDepth = false;
            if (bigEnough)
            {
                pos = Locate(b, out byDepth, out dist);
                DoorFinder.Instance?.ReportVisionDoor(pos, b.score, byDepth);
                reported++;
            }
            log.Append($"{{\"s\":{VisionLog.F(b.score)},\"r\":[{VisionLog.F(b.XMin)},{VisionLog.F(b.YMin)},{VisionLog.F(b.w)},{VisionLog.F(b.h)}],\"big\":{VisionLog.B(bigEnough)},\"pos\":{VisionLog.V(pos)},\"dist\":{VisionLog.F(dist)},\"byDepth\":{VisionLog.B(byDepth)}}}");
        }
        VisionLog.Event("vision_door", $"\"run\":{runs},\"ms\":{VisionLog.F(lastInferenceMs)},\"found\":{kept.Count},\"reported\":{reported},\"boxes\":[{log}]");
        status = kept.Count > 0 ? $"дверей в кадре: {kept.Count}, {lastInferenceMs:F0} мс" : $"дверей не видно, {lastInferenceMs:F0} мс";
    }

    /// <summary>Мировая точка двери: луч через центр рамки (чуть ниже центра, в полотно) и глубина; иначе fallbackDistance.</summary>
    Vector3 Locate(Box b, out bool byDepth, out float dist)
    {
        float u = b.x / frameRes.x;
        float v = 1f - (b.y + b.h * 0.15f) / frameRes.y;   // viewport: (0,0) внизу слева
        Ray ray = feed.ViewportToRay(new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v)), framePose);
        byDepth = false;
        if (raycaster != null)
        {
            try
            {
                if (raycaster.Raycast(ray, out EnvironmentRaycastHit hit, maxRange) && hit.status == EnvironmentRaycastHitStatus.Hit)
                {
                    byDepth = true;
                    dist = Vector3.Distance(ray.origin, hit.point);
                    return hit.point;
                }
            }
            catch (Exception e) { Debug.LogWarning("DoorVision: глубина: " + e.Message); }
        }
        dist = fallbackDistance;
        return ray.origin + ray.direction.normalized * fallbackDistance;
    }

    /// <summary>Подавление дублей: рамки, перекрывающиеся сильнее iou с более уверенной, выбрасываются.</summary>
    public static void Nms(List<Box> input, float iou, List<Box> result)
    {
        result.Clear();
        input.Sort((a, b) => b.score.CompareTo(a.score));
        foreach (var b in input)
        {
            bool dup = false;
            foreach (var k in result) if (IoU(b, k) > iou) { dup = true; break; }
            if (!dup) result.Add(b);
        }
    }

    public static float IoU(Box a, Box b)
    {
        float x0 = Mathf.Max(a.XMin, b.XMin), y0 = Mathf.Max(a.YMin, b.YMin);
        float x1 = Mathf.Min(a.XMax, b.XMax), y1 = Mathf.Min(a.YMax, b.YMax);
        if (x1 <= x0 || y1 <= y0) return 0f;
        float inter = (x1 - x0) * (y1 - y0);
        float union = a.w * a.h + b.w * b.h - inter;
        return union > 0f ? inter / union : 0f;
    }

    void OnDestroy()
    {
        worker?.Dispose();
        worker = null;
        if (Instance == this) Instance = null;
    }
}
