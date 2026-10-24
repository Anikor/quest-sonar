using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Читает текстуру глубины Quest 3 (EnvironmentDepthManager, Meta XR Core SDK) compute-шейдером
/// Resources/DepthSample.compute и даёт облако мировых точек:
///  - кольцевой GPU-буфер PointsBuffer на historyFrames кадров: из него DepthPointCloud рисует десятки тысяч точек;
///  - копия последнего кадра на CPU (Points, задержка 1-3 кадра): по ней ObstacleMap строит карту, а AudioSonar звучит.
/// Каждая точка: xyz = мир, w = расстояние от головы (0 = нет данных).
///
/// Чтение идёт в Application.onBeforeRender сразу после EnvironmentDepthManager: текстура и матрицы берутся в том же
/// кадре, в котором их выставил Meta (так делают и шейдеры окклюзии Meta).
///
/// Автокалибровка (autoCalibrate): соглашения текстуры глубины на устройстве могут отличаться от исходников Meta
/// (порядок глубины, переворот Y, способ привязки depth-текстуры к compute-шейдеру). При старте перебираются все
/// варианты, точки каждого сравниваются с EnvironmentRaycastManager.Raycast (стабильная v1), выбирается вариант
/// с наименьшей медианной ошибкой. Пока calibrated = false, карта остаётся на лучах, а облако - частицами.
/// Скрипт создаётся сам при старте сцены. Если глубина недоступна, ready = false и всё работает как в v1.
/// </summary>
public class DepthSampler : MonoBehaviour
{
    public static DepthSampler Instance { get; private set; }

    [Header("Сетка точек за кадр (менять до старта)")]
    [Tooltip("Точек по горизонтали в кадр. Текстура глубины квадратная, поэтому сетка тоже")]
    public int gridW = 96;
    public int gridH = 96;
    [Tooltip("Сколько кадров хранится в GPU-буфере для отрисовки облака")]
    public int historyFrames = 6;

    [Header("Чтение")]
    [Tooltip("Читать оба глаза по очереди (чуть шире покрытие)")]
    public bool alternateEyes = true;
    [Tooltip("Какой глаз читать, если alternateEyes выключен: 0 = левый, 1 = правый")]
    public int eye = 0;
    public float minDistance = 0.2f;
    public float maxDistance = 4f;
    [Tooltip("Убирать руки из текстуры глубины (иначе они дают близкие точки)")]
    public bool removeHands = true;

    [Header("Фильтр границ")]
    [Tooltip("Отбрасывать 'летящие' пиксели на границах предметов: точка остаётся, если хотя бы 2 из 4 соседей близки к ней по глубине")]
    public bool edgeFilter = true;
    [Tooltip("Допуск близости соседей как доля глубины")]
    public float edgeRelTol = 0.04f;
    [Tooltip("Допуск близости соседей, не меньше этого (м)")]
    public float edgeMinTol = 0.04f;

    [Header("Соглашения текстуры глубины")]
    [Tooltip("Подобрать zMode / flipY / bindDepthElement автоматически по лучам Raycast при старте. Выкл = использовать значения ниже как есть")]
    public bool autoCalibrate = true;
    [Tooltip("0: NDC-z = raw*2-1 (как в шейдерах Meta); 1: обратный порядок глубины, NDC-z = 1-2*raw")]
    public int zMode = 0;
    [Tooltip("Перевернуть текстуру по вертикали")]
    public bool flipY = false;
    [Tooltip("Привязать к compute-шейдеру именно depth-часть RenderTexture (если обычная привязка даёт мусор)")]
    public bool bindDepthElement = false;
    [Tooltip("Проверочных лучей на один readback во время калибровки")]
    public int calibrationSamples = 24;
    [Tooltip("Сколько readback-кадров проверять каждый вариант")]
    public int calibrationRounds = 2;
    [Tooltip("Вариант принимается, если медианная ошибка меньше (м)")]
    public float calibrationAccept = 0.12f;
    [Tooltip("Если ни один вариант не прошёл, повторить калибровку через столько секунд")]
    public float calibrationRetrySeconds = 10f;

    [Header("Отладка")]
    [Tooltip("Читать в Update, как в v2.0 (на кадр позже менеджера Meta) - только для сравнения")]
    public bool sampleInUpdate = false;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public bool failed;
    [Tooltip("Калибровка прошла: точки совпадают с лучами, карта и облако могут их использовать")]
    public bool calibrated;
    public string status = "старт";
    public string calibrationInfo = "-";
    public string textureInfo = "-";
    [Tooltip("Валидных точек в последнем прочитанном кадре")]
    public int validCount;
    public float pointsPerSecond;
    [Tooltip("Медианное расстояние между точкой и попаданием Raycast в ту же сторону, м (-1 = ещё нет). Хорошо: < 0.05")]
    public float calibErrorMedian = -1f;

    /// <summary>Кольцевой GPU-буфер: SlotSize * History точек float4.</summary>
    public ComputeBuffer PointsBuffer { get; private set; }
    /// <summary>Точек в одном кадре (слоте) = gridW * gridH.</summary>
    public int SlotSize { get; private set; }
    /// <summary>Слотов в кольце = historyFrames.</summary>
    public int History { get; private set; }
    /// <summary>Слот, записанный последним Dispatch (самый свежий кадр).</summary>
    public int WriteSlot { get; private set; }
    /// <summary>Копия последнего прочитанного кадра на CPU (SlotSize точек).</summary>
    public Vector4[] Points { get; private set; }
    /// <summary>Растёт на 1 с каждым готовым readback: потребители обрабатывают каждый кадр один раз.</summary>
    public int ReadbackFrame { get; private set; }

    const int Stride = sizeof(float) * 4;
    const float WaitDepthSeconds = 15f;
    const float MonitorInterval = 0.5f;   // как часто пускать проверочные лучи после калибровки, сек
    const int MonitorSamples = 8;
    const int MonitorMedianOf = 16;
    const float LogInterval = 2f;
    const int MinSamplesPerRound = 6;     // меньше успешных сравнений за readback = "нет данных"
    const int MaxEmptyRounds = 8;         // столько пустых readback подряд = вариант не даёт точек
    const int HistBins = 32;

    static readonly int DepthTexId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int MatricesId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");
    static readonly int ZBufferId = Shader.PropertyToID("_EnvironmentDepthZBufferParams");

    enum CalibPhase { NotStarted, Running, Done }

    ComputeShader cs;
    int kernelArray, kernel2D, kernelBuild;
    Transform head;
    EnvironmentRaycastManager raycaster;
    readonly List<Matrix4x4> matrices = new();
    readonly List<float> monitorErrors = new();
    Texture lastTex;
    bool readbackSupported, readbackBusy, warnedDimension, subscribed;
    int frame, pointsThisSecond;
    float rateTimer, monitorTimer, logTimer;

    // калибровка: вариант m = бит0 zMode, бит1 flipY, бит2 bindDepthElement
    CalibPhase calibPhase;
    int calibMode, calibModeCount, calibRoundsDone, calibEmptyRounds;
    int requestedMode = -1;               // вариант, с которым заказан текущий readback
    readonly List<float> calibErrors = new();
    readonly float[] modeMedians = new float[8];
    float calibRetryTimer;

    // диагностика сырых значений глубины (один раз)
    ComputeBuffer histBuffer;
    bool diagRequested;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DepthSampler>() == null)
            new GameObject("DepthSampler").AddComponent<DepthSampler>();
    }

    // ---------------------------------------------------------------- инициализация
    IEnumerator Start()
    {
        Instance = this;
        yield return SonarCommon.WaitUntilReady();
        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        if (head == null) { Fail("нет головы (OVRCameraRig)"); yield break; }
        if (!SystemInfo.supportsComputeShaders) { Fail("compute-шейдеры не поддерживаются"); yield break; }

        cs = Resources.Load<ComputeShader>("DepthSample");
        if (cs == null) { Fail("не найден Assets/Resources/DepthSample.compute"); yield break; }
        kernelArray = cs.FindKernel("Sample");
        kernel2D = cs.FindKernel("Sample2D");
        kernelBuild = cs.FindKernel("BuildQuads");

        if (!EnvironmentDepthManager.IsSupported) { Fail("Depth API не поддерживается (нужен Quest 3 и разрешение на сцену)"); yield break; }

        var mgr = FindFirstObjectByType<EnvironmentDepthManager>();
        if (mgr == null)
        {
            mgr = gameObject.AddComponent<EnvironmentDepthManager>();
            mgr.OcclusionShadersMode = OcclusionShadersMode.None;   // окклюзия не нужна, экономим препроцессинг на GPU
        }
        mgr.RemoveHands = removeHands;
        mgr.enabled = true;

        status = "жду глубину";
        float t = 0f;
        while (!mgr.IsDepthAvailable && t < WaitDepthSeconds) { t += Time.deltaTime; yield return null; }
        if (!mgr.IsDepthAvailable) { Fail($"глубина недоступна {WaitDepthSeconds:F0} с (разрешение на сцену? Quest 3?)"); yield break; }

        gridW = Mathf.Max(8, gridW);
        gridH = Mathf.Max(8, gridH);
        History = Mathf.Max(1, historyFrames);
        SlotSize = gridW * gridH;
        PointsBuffer = new ComputeBuffer(SlotSize * History, Stride);
        PointsBuffer.SetData(new Vector4[SlotSize * History]);   // содержимое нового буфера не гарантировано нулевое
        Points = new Vector4[SlotSize];
        WriteSlot = History - 1;
        histBuffer = new ComputeBuffer(HistBins, sizeof(uint));
        histBuffer.SetData(new uint[HistBins]);

        readbackSupported = SystemInfo.supportsAsyncGPUReadback;
        if (!readbackSupported)
            Debug.LogWarning("DepthSampler: AsyncGPUReadback не поддерживается, точек на CPU (карта, звук, калибровка) не будет, только облако.");

        // подписываемся после EnvironmentDepthManager (он подписан с момента своего OnEnable), порядок 1000 - для надёжности
        Application.onBeforeRender += OnBeforeRender;
        subscribed = true;

        ready = true;
        status = "работает";
        Debug.Log($"DepthSampler: готов, сетка {gridW}x{gridH}, история {History} кадров, чтение в onBeforeRender, Raycast для калибровки: {(raycaster != null ? "есть" : "НЕТ")}.");
    }

    void Fail(string why)
    {
        failed = true;
        status = why;
        Debug.LogError("DepthSampler: " + why);
    }

    void OnDestroy()
    {
        if (subscribed) Application.onBeforeRender -= OnBeforeRender;
        PointsBuffer?.Release();
        PointsBuffer = null;
        histBuffer?.Release();
        histBuffer = null;
        if (Instance == this) Instance = null;
    }

    // ---------------------------------------------------------------- главный цикл
    void Update()
    {
        if (!ready) return;

        if (sampleInUpdate) SampleFrame();

        rateTimer += Time.unscaledDeltaTime;
        if (rateTimer >= 1f)
        {
            pointsPerSecond = pointsThisSecond / rateTimer;
            pointsThisSecond = 0;
            rateTimer = 0f;
        }
    }

    [BeforeRenderOrder(1000)]   // после EnvironmentDepthManager (у него порядок по умолчанию, 0)
    void OnBeforeRender()
    {
        if (!ready || sampleInUpdate || !isActiveAndEnabled) return;
        SampleFrame();
    }

    /// <summary>Один кадр: взять текстуру и матрицы Meta, запустить kernel Sample в текущий слот кольца, заказать readback.</summary>
    void SampleFrame()
    {
        if (head == null || cs == null) return;

        var tex = Shader.GetGlobalTexture(DepthTexId);
        if (tex == null) { status = "нет текстуры"; return; }

        int kernel;
        string texProp;
        if (tex.dimension == TextureDimension.Tex2DArray) { kernel = kernelArray; texProp = "_Depth"; }
        else if (tex.dimension == TextureDimension.Tex2D) { kernel = kernel2D; texProp = "_Depth2D"; }
        else
        {
            status = "неизвестный тип текстуры";
            if (!warnedDimension)
            {
                warnedDimension = true;
                Debug.LogError("DepthSampler: _EnvironmentDepthTexture " + Describe(tex) + " - не умею читать. Пришлите это сообщение.");
            }
            return;
        }

        matrices.Clear();
        Shader.GetGlobalMatrixArray(MatricesId, matrices);
        if (matrices.Count == 0) { status = "нет матриц"; return; }
        Vector4 zb = Shader.GetGlobalVector(ZBufferId);

        if (tex != lastTex)
        {
            if (lastTex == null)
                Debug.Log($"DepthSampler: текстура глубины {Describe(tex)}, матриц {matrices.Count}, ZBufferParams {zb:F4}, матрица[0]:\n{matrices[0]:F4}");
            lastTex = tex;
            textureInfo = Describe(tex);
        }

        bool isRT = tex is RenderTexture;
        if (calibPhase == CalibPhase.NotStarted) StartCalibration(isRT);
        bool calibrating = calibPhase == CalibPhase.Running;
        int zm = calibrating ? (calibMode & 1) : zMode;
        bool fy = calibrating ? (calibMode & 2) != 0 : flipY;
        bool bd = calibrating ? (calibMode & 4) != 0 : bindDepthElement;
        if (calibrating) calibrationInfo = $"проверка {calibMode + 1}/{calibModeCount} ({ModeName(calibMode)})";

        frame++;
        int slice = calibrating ? 0 : Mathf.Clamp(alternateEyes ? (frame & 1) : eye, 0, matrices.Count - 1);
        WriteSlot = (WriteSlot + 1) % History;

        if (bd && tex is RenderTexture rt) cs.SetTexture(kernel, texProp, rt, 0, RenderTextureSubElement.Depth);
        else cs.SetTexture(kernel, texProp, tex);
        cs.SetBuffer(kernel, "_Points", PointsBuffer);
        cs.SetBuffer(kernel, "_RawHist", histBuffer);
        cs.SetMatrix("_InvVP", matrices[slice].inverse);
        cs.SetVector("_HeadPos", head.position);
        cs.SetVector("_ZB", zb);
        cs.SetInt("_GridW", gridW);
        cs.SetInt("_GridH", gridH);
        cs.SetInt("_DepthW", tex.width);
        cs.SetInt("_DepthH", tex.height);
        cs.SetInt("_Slice", slice);
        cs.SetInt("_FlipY", fy ? 1 : 0);
        cs.SetInt("_ZMode", zm);
        cs.SetInt("_BaseIndex", WriteSlot * SlotSize);
        cs.SetVector("_Jitter", new Vector2(Halton(frame, 2), Halton(frame, 3)));
        cs.SetFloat("_MinDist", minDistance);
        cs.SetFloat("_MaxDist", maxDistance);
        cs.SetInt("_EdgeFilter", edgeFilter && !calibrating ? 1 : 0);
        cs.SetFloat("_EdgeRelTol", edgeRelTol);
        cs.SetFloat("_EdgeMinTol", edgeMinTol);
        bool diag = !diagRequested && readbackSupported;
        cs.SetInt("_Diag", diag ? 1 : 0);
        cs.Dispatch(kernel, Mathf.CeilToInt(gridW / 8f), Mathf.CeilToInt(gridH / 8f), 1);

        if (diag)
        {
            diagRequested = true;
            AsyncGPUReadback.Request(histBuffer, OnHistReadback);
        }

        if (readbackSupported && !readbackBusy)
        {
            readbackBusy = true;
            requestedMode = calibrating ? calibMode : -1;
            AsyncGPUReadback.Request(PointsBuffer, SlotSize * Stride, WriteSlot * SlotSize * Stride, OnReadback);
        }

        status = "работает";
    }

    /// <summary>
    /// Строит квады для всех точек кольцевого буфера прямо в вершинный буфер меша (см. DepthPointCloud).
    /// verts: GraphicsBuffer вершин меша (Target.Raw), 4 вершины по 20 байт на точку.
    /// </summary>
    public void BuildQuads(GraphicsBuffer verts, Vector3 right, Vector3 up, float size, float floorY, float colorRange, float minAlpha)
    {
        if (!ready || verts == null) return;
        int total = SlotSize * History;
        cs.SetBuffer(kernelBuild, "_Points", PointsBuffer);
        cs.SetBuffer(kernelBuild, "_Verts", verts);
        cs.SetInt("_TotalPoints", total);
        cs.SetInt("_SlotSize", SlotSize);
        cs.SetInt("_History", History);
        cs.SetInt("_WriteSlot", WriteSlot);
        cs.SetVector("_CamRight", right);
        cs.SetVector("_CamUp", up);
        cs.SetFloat("_PointSize", size);
        cs.SetFloat("_FloorY", floorY);
        cs.SetFloat("_ColorHeightRange", Mathf.Max(0.01f, colorRange));
        cs.SetFloat("_MinAlpha", minAlpha);
        cs.Dispatch(kernelBuild, Mathf.CeilToInt(total / 64f), 1, 1);
    }

    void OnReadback(AsyncGPUReadbackRequest req)
    {
        readbackBusy = false;
        if (req.hasError || Points == null) return;

        var data = req.GetData<Vector4>();
        int n = Mathf.Min(data.Length, Points.Length);
        NativeArray<Vector4>.Copy(data, Points, n);

        int valid = 0;
        for (int i = 0; i < n; i++) if (Points[i].w > 0f) valid++;
        validCount = valid;
        pointsThisSecond += valid;
        ReadbackFrame++;

        if (calibPhase == CalibPhase.Running) { CalibrationRound(); return; }

        if (calibPhase == CalibPhase.Done && !calibrated)
        {
            // ни один вариант не прошёл: периодически пробуем снова (может, в кадре просто не было поверхностей)
            calibRetryTimer += Time.unscaledDeltaTime;
            if (calibRetryTimer >= calibrationRetrySeconds) { calibRetryTimer = 0f; StartCalibration(lastTex is RenderTexture); }
            return;
        }

        if (raycaster != null)
        {
            monitorTimer += Time.unscaledDeltaTime;
            if (monitorTimer >= MonitorInterval) { monitorTimer = 0f; Monitor(); }
        }
    }

    void OnHistReadback(AsyncGPUReadbackRequest req)
    {
        if (req.hasError) { Debug.LogWarning("DepthSampler: гистограмма сырых значений не прочиталась."); return; }
        var h = req.GetData<uint>();
        if (h.Length < HistBins) return;
        var sb = new System.Text.StringBuilder();
        uint total = 0;
        for (int i = 0; i < HistBins; i++) total += h[i];
        sb.Append($"DepthSampler: сырые значения глубины первого кадра ({total} пикселей): raw<=0: {h[30]}, raw>=1: {h[31]}, доли по корзинам [0..1) шаг 1/30:");
        for (int i = 0; i < 30; i++) sb.Append(' ').Append(total > 0 ? (100f * h[i] / total).ToString("F0") : "0");
        sb.Append(" %. Почти всё в последних корзинах (0.85..1) = обычный порядок глубины (z0); в первых (0..0.15) = обратный (z1); размазано ровно = читается мусор (d1).");
        Debug.Log(sb.ToString());
    }

    // ---------------------------------------------------------------- калибровка соглашений
    void StartCalibration(bool isRT)
    {
        if (!autoCalibrate || raycaster == null || !readbackSupported)
        {
            calibPhase = CalibPhase.Done;
            calibrated = true;
            calibrationInfo = !autoCalibrate ? $"вручную {ModeName(ManualMode())}" : $"без проверки {ModeName(ManualMode())} (нет Raycast или readback)";
            Debug.Log("DepthSampler: калибровка пропущена: " + calibrationInfo);
            return;
        }
        calibPhase = CalibPhase.Running;
        calibrated = false;
        calibMode = 0;
        calibModeCount = isRT ? 8 : 4;    // без RenderTexture вариант привязки depth-части смысла не имеет
        calibRoundsDone = 0;
        calibEmptyRounds = 0;
        calibErrors.Clear();
        Debug.Log($"DepthSampler: калибровка соглашений текстуры глубины, вариантов {calibModeCount} (z = порядок глубины, y = переворот, d = привязка depth-части).");
    }

    int ManualMode() => (zMode & 1) | (flipY ? 2 : 0) | (bindDepthElement ? 4 : 0);

    static string ModeName(int m) => $"z{m & 1} y{(m >> 1) & 1} d{(m >> 2) & 1}";

    static string FormatErr(float e) => e >= 999f ? "нет точек" : (e * 100f).ToString("F1") + " см";

    void CalibrationRound()
    {
        if (requestedMode != calibMode) return;   // этот readback заказан ещё для предыдущего варианта

        int got = SampleErrors(calibrationSamples, calibErrors);
        if (got < MinSamplesPerRound) calibEmptyRounds++; else calibRoundsDone++;
        if (calibRoundsDone < calibrationRounds && calibEmptyRounds < MaxEmptyRounds) return;

        float median = calibErrors.Count >= MinSamplesPerRound ? Median(calibErrors) : 999f;
        modeMedians[calibMode] = median;
        Debug.Log($"DepthSampler: вариант {ModeName(calibMode)}: валидных точек {validCount}/{SlotSize}, сравнений {calibErrors.Count}, медианная ошибка {FormatErr(median)}");
        calibErrors.Clear();
        calibRoundsDone = 0;
        calibEmptyRounds = 0;
        calibMode++;
        if (calibMode >= calibModeCount) FinishCalibration();
    }

    void FinishCalibration()
    {
        int best = 0;
        for (int m = 1; m < calibModeCount; m++) if (modeMedians[m] < modeMedians[best]) best = m;
        zMode = best & 1;
        flipY = (best & 2) != 0;
        bindDepthElement = (best & 4) != 0;
        calibPhase = CalibPhase.Done;
        calibrated = modeMedians[best] < calibrationAccept;
        calibErrorMedian = modeMedians[best] < 999f ? modeMedians[best] : -1f;
        calibrationInfo = (calibrated ? "" : "не удалась, лучший ") + $"{ModeName(best)} {FormatErr(modeMedians[best])}";
        calibRetryTimer = 0f;
        monitorErrors.Clear();

        var all = new System.Text.StringBuilder();
        for (int m = 0; m < calibModeCount; m++) all.Append(m > 0 ? ", " : "").Append(ModeName(m)).Append(' ').Append(FormatErr(modeMedians[m]));
        Debug.Log($"DepthSampler: калибровка {(calibrated ? "УСПЕШНА" : "НЕ УДАЛАСЬ (повтор через " + calibrationRetrySeconds + " с)")}: {calibrationInfo}. Все варианты: {all}");
    }

    /// <summary>Сравнивает до n случайных валидных точек последнего кадра с Raycast в ту же сторону; возвращает число сравнений.</summary>
    int SampleErrors(int n, List<float> into)
    {
        if (validCount < MinSamplesPerRound || raycaster == null) return 0;
        int added = 0;
        Vector3 origin = head.position;
        for (int s = 0; s < n; s++)
        {
            Vector4 p = default;
            for (int tries = 0; tries < 20 && p.w <= 0f; tries++)
                p = Points[Random.Range(0, Points.Length)];
            if (p.w <= 0f) continue;

            Vector3 point = p;
            Vector3 dir = point - origin;
            if (dir.sqrMagnitude < 1e-4f) continue;
            dir.Normalize();
            if (!raycaster.Raycast(new Ray(origin, dir), out EnvironmentRaycastHit hit, maxDistance + 1f)) continue;
            if (hit.status != EnvironmentRaycastHitStatus.Hit) continue;

            into.Add(Vector3.Distance(hit.point, point));
            added++;
        }
        return added;
    }

    static float Median(List<float> values)
    {
        var sorted = new List<float>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    // ---------------------------------------------------------------- мониторинг после калибровки
    void Monitor()
    {
        SampleErrors(MonitorSamples, monitorErrors);
        if (monitorErrors.Count >= MonitorMedianOf)
        {
            calibErrorMedian = Median(monitorErrors);
            monitorErrors.Clear();
        }

        logTimer += MonitorInterval;
        if (logTimer >= LogInterval)
        {
            logTimer = 0f;
            string err = calibErrorMedian >= 0f ? (calibErrorMedian * 100f).ToString("F1") + " см" : "-";
            Debug.Log($"DepthSampler: точек {validCount}/{SlotSize}, {pointsPerSecond:F0}/с, медианная ошибка vs Raycast {err} " +
                      $"(режим {ModeName(ManualMode())}, alternateEyes={alternateEyes}, eye={eye}, edgeFilter={edgeFilter}, sampleInUpdate={sampleInUpdate})");
        }
    }

    static string Describe(Texture t)
    {
        string fmt = t is RenderTexture r ? r.graphicsFormat + "/" + r.depthStencilFormat : t.graphicsFormat.ToString();
        return $"{t.width}x{t.height} {t.dimension} {fmt} ({t.GetType().Name})";
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0) { f /= b; r += f * (index % b); index /= b; }
        return r;
    }
}
