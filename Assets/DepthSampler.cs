using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Часть 1. Читает текстуру глубины Quest 3 (EnvironmentDepthManager) compute-шейдером
/// и даёт облако мировых точек: GPU-буфер (PointsBuffer) и копию на CPU (Points).
/// Каждая точка: xyz = мир, w = расстояние от головы (0 = нет данных).
/// Самопроверка: сравнивает точки с Raycast (calibErrorMedian, в метрах).
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DepthSampler : MonoBehaviour
{
    public static DepthSampler Instance { get; private set; }

    [Header("Сетка точек за кадр")]
    public int gridW = 128;
    public int gridH = 96;
    [Tooltip("Сколько последних кадров хранить в GPU-буфере (облако = gridW*gridH*historyFrames точек). Менять до запуска.")]
    public int historyFrames = 12;
    [Tooltip("Какой глаз читать: 0 = левый, 1 = правый")]
    public int eye = 0;
    public float minDistance = 0.2f;
    public float maxDistance = 4f;
    public bool removeHands = true;
    [Tooltip("Если самопроверка даёт большую ошибку, переключите в Play-режиме")]
    public bool flipY = false;
    [Tooltip("Соглашение по Z: 0 z=d, 1 z=2d-1, 2 z=1-d, 3 z=2(1-d)-1")]
    public int zMode = 0;
    [Tooltip("Сам перебирает eye/flipY/zMode и берёт первую комбинацию с ошибкой < 5 см. Нужен стоя неподвижно лицом к комнате")]
    public bool autoSearch = true;
    public string status = "";

    [Header("Самопроверка относительно Raycast")]
    public bool calibrate = true;
    public int calibrationSamples = 8;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public int validCount;
    public float pointsPerSecond;
    [Tooltip("Медианная ошибка расстояния, м (-1 = ещё нет). Хорошо: < 0.05")]
    public float calibErrorMedian = -1f;

    public ComputeBuffer PointsBuffer { get; private set; }
    public Vector4[] Points { get; private set; }
    /// <summary>Всего точек в GPU-буфере (все кадры истории).</summary>
    /// <summary>Слот истории, записанный последним (нужен шейдеру отрисовки для затухания по возрасту).</summary>
    public int CurrentSlot { get; private set; }
    public int PointsPerFrame => gridW * gridH;
    public int TotalPoints => gridW * gridH * Mathf.Max(1, historyFrames);

    static readonly int DepthTexId = Shader.PropertyToID("_EnvironmentDepthTexture");
    static readonly int MatricesId = Shader.PropertyToID("_EnvironmentDepthReprojectionMatrices");

    ComputeShader cs;
    int kernel;
    Transform head;
    EnvironmentRaycastManager raycaster;
    readonly List<Matrix4x4> matrices = new();
    readonly List<float> errors = new();
    bool readbackBusy;
    int frame;
    int pointsThisSecond;
    float rateTimer, calibTimer, logTimer;
    bool warnedNotArray;
    bool diagLogged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DepthSampler>() == null)
            new GameObject("DepthSampler").AddComponent<DepthSampler>();
    }

    IEnumerator Start()
    {
        Instance = this;
        yield return SonarCommon.WaitUntilReady();
        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();

        var mgr = FindFirstObjectByType<EnvironmentDepthManager>();
        if (mgr == null) mgr = gameObject.AddComponent<EnvironmentDepthManager>();
        mgr.enabled = true;
        mgr.RemoveHands = removeHands;

        float t = 0f;
        while (!mgr.IsDepthAvailable && t < 15f) { t += Time.deltaTime; yield return null; }
        if (!mgr.IsDepthAvailable)
        {
            Debug.LogError("DepthSampler: глубина недоступна (проверьте разрешение на сцену и Quest 3).");
            yield break;
        }

        cs = Resources.Load<ComputeShader>("DepthSample");
        if (cs == null)
        {
            Debug.LogError("DepthSampler: не найден Assets/Resources/DepthSample.compute");
            yield break;
        }
        kernel = cs.FindKernel("Sample");

        int n = gridW * gridH;
        PointsBuffer = new ComputeBuffer(n * Mathf.Max(1, historyFrames), sizeof(float) * 4);
        Points = new Vector4[n];
        ready = true;
    }

    void OnDestroy()
    {
        PointsBuffer?.Release();
        PointsBuffer = null;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        if (!ready || head == null) return;

        var tex = Shader.GetGlobalTexture(DepthTexId);
        if (tex == null) return;
        if (tex.dimension != TextureDimension.Tex2DArray)
        {
            if (!warnedNotArray)
            {
                Debug.LogError("DepthSampler: _EnvironmentDepthTexture не Texture2DArray (" + tex.dimension + "). Напишите мне это сообщение.");
                warnedNotArray = true;
            }
            return;
        }

        matrices.Clear();
        Shader.GetGlobalMatrixArray(MatricesId, matrices);
        int slice = Mathf.Clamp(eye, 0, Mathf.Max(0, matrices.Count - 1));
        if (matrices.Count == 0) return;

        if (!diagLogged)
        {
            diagLogged = true;
            Debug.Log($"DepthSampler DIAG: tex {tex.width}x{tex.height} {tex.dimension} fmt={(tex is RenderTexture rtx ? rtx.graphicsFormat.ToString() : tex.GetType().Name)}, " +
                      $"matrices={matrices.Count}, zbuf={Shader.GetGlobalVector("_EnvironmentDepthZBufferParams")}\nM0=\n{matrices[0]}\nM1=\n{(matrices.Count > 1 ? matrices[1].ToString() : "-")}");
        }
        if (autoSearch && combos.Count == 0) BeginSearch();
        if (autoSearch && combos.Count > 0 && Time.unscaledTime - comboStart > 3f)
        {
            comboErr.Add(9.99f);
            Debug.Log($"DepthSampler AUTO {comboIdx + 1}/{combos.Count}: нет данных (eye={eye} flipY={flipY} z={zMode})");
            NextCombo();
        }

        frame++;
        int baseIndex = (frame % Mathf.Max(1, historyFrames)) * gridW * gridH;
        CurrentSlot = frame % Mathf.Max(1, historyFrames);
        cs.SetTexture(kernel, "_Depth", tex);
        cs.SetBuffer(kernel, "_Points", PointsBuffer);
        cs.SetMatrix("_InvVP", matrices[slice].inverse);
        cs.SetVector("_CamPos", head.position);
        cs.SetInt("_GridW", gridW);
        cs.SetInt("_GridH", gridH);
        cs.SetInt("_DepthW", tex.width);
        cs.SetInt("_DepthH", tex.height);
        cs.SetInt("_Slice", slice);
        cs.SetInt("_BaseIndex", baseIndex);
        cs.SetInt("_FlipY", flipY ? 1 : 0);
        cs.SetInt("_ZMode", zMode);
        cs.SetVector("_Jitter", new Vector2(Halton(frame, 2), Halton(frame, 3)));
        cs.SetFloat("_MinDist", minDistance);
        cs.SetFloat("_MaxDist", maxDistance);
        cs.Dispatch(kernel, Mathf.CeilToInt(gridW / 8f), Mathf.CeilToInt(gridH / 8f), 1);

        if (!readbackBusy)
        {
            readbackBusy = true;
            AsyncGPUReadback.Request(PointsBuffer, gridW * gridH * 16, baseIndex * 16, OnReadback);
        }

        rateTimer += Time.unscaledDeltaTime;
        if (rateTimer >= 1f)
        {
            pointsPerSecond = pointsThisSecond / rateTimer;
            pointsThisSecond = 0;
            rateTimer = 0f;
        }
    }

    void OnReadback(AsyncGPUReadbackRequest req)
    {
        readbackBusy = false;
        if (req.hasError || Points == null) return;

        req.GetData<Vector4>().CopyTo(Points);

        int n = 0;
        for (int i = 0; i < Points.Length; i++) if (Points[i].w > 0f) n++;
        validCount = n;
        pointsThisSecond += n;

        if (calibrate && raycaster != null)
        {
            calibTimer += Time.unscaledDeltaTime;
            if (calibTimer >= 0.5f) { calibTimer = 0f; Calibrate(); }
        }
    }

    // ---------------------------------------------------------------- самопроверка
    void Calibrate()
    {
        if (Time.unscaledTime < ignoreUntil) return;
        if (validCount < calibrationSamples * 4) return;

        Vector3 origin = head.position;
        for (int s = 0; s < calibrationSamples; s++)
        {
            Vector4 p = default;
            for (int tries = 0; tries < 20 && p.w <= 0f; tries++)
                p = Points[Random.Range(0, Points.Length)];
            if (p.w <= 0f) continue;

            Vector3 dir = ((Vector3)p - origin).normalized;
            if (!raycaster.Raycast(new Ray(origin, dir), out EnvironmentRaycastHit hit, maxDistance + 0.5f)) continue;
            if (hit.status != EnvironmentRaycastHitStatus.Hit) continue;

            errors.Add(Mathf.Abs(Vector3.Distance(origin, hit.point) - p.w));
        }

        if (errors.Count >= 16)
        {
            errors.Sort();
            calibErrorMedian = errors[errors.Count / 2];
            errors.Clear();
            if (autoSearch) OnComboResult(calibErrorMedian);
        }

        logTimer += 0.5f;
        if (logTimer >= 2f)
        {
            logTimer = 0f;
            Debug.Log($"DepthSampler: точек {validCount}/{Points.Length}, {pointsPerSecond:F0}/с, " +
                      $"медианная ошибка vs Raycast {calibErrorMedian * 100f:F1} см (flipY={flipY}, eye={eye}, z={zMode})");
        }
    }

    // ---------------- автоподбор соглашений (глаз, flipY, режим Z) ----------------
    readonly List<Vector3Int> combos = new();   // x = eye, y = flipY, z = zMode
    readonly List<float> comboErr = new();
    int comboIdx;
    float comboStart, ignoreUntil;
    const float GoodError = 0.05f;

    void BeginSearch()
    {
        combos.Clear(); comboErr.Clear();
        int eyes = Mathf.Max(1, Mathf.Min(2, matrices.Count));
        for (int e = 0; e < eyes; e++)
            for (int f = 0; f < 2; f++)
                for (int z = 0; z < 4; z++)
                    combos.Add(new Vector3Int(e, f, z));

        // удачная комбинация с прошлого запуска проверяется первой
        var parts = PlayerPrefs.GetString("DepthSamplerCombo", "").Split(',');
        if (parts.Length == 3 && int.TryParse(parts[0], out int se) && int.TryParse(parts[1], out int sf) && int.TryParse(parts[2], out int sz))
        {
            var sc = new Vector3Int(se, sf, sz);
            int i = combos.IndexOf(sc);
            if (i > 0) { combos.RemoveAt(i); combos.Insert(0, sc); }
        }
        comboIdx = -1;
        NextCombo();
    }

    void ApplyCombo(Vector3Int c) { eye = c.x; flipY = c.y != 0; zMode = c.z; }

    void NextCombo()
    {
        comboIdx++;
        if (comboIdx >= combos.Count) { FinishSearch(); return; }
        ApplyCombo(combos[comboIdx]);
        errors.Clear();
        calibErrorMedian = -1f;
        comboStart = Time.unscaledTime;
        ignoreUntil = Time.unscaledTime + 0.4f;
        status = $"подбор {comboIdx + 1}/{combos.Count}";
    }

    void OnComboResult(float median)
    {
        comboErr.Add(median);
        var c = combos[comboIdx];
        Debug.Log($"DepthSampler AUTO {comboIdx + 1}/{combos.Count}: eye={c.x} flipY={c.y} z={c.z} -> {median * 100f:F1} см");
        if (median <= GoodError)
        {
            autoSearch = false;
            PlayerPrefs.SetString("DepthSamplerCombo", $"{c.x},{c.y},{c.z}");
            PlayerPrefs.Save();
            status = "OK";
            Debug.Log($"DepthSampler AUTO: выбрано eye={c.x} flipY={c.y} z={c.z}, ошибка {median * 100f:F1} см");
            return;
        }
        NextCombo();
    }

    void FinishSearch()
    {
        int best = 0;
        for (int i = 1; i < comboErr.Count; i++) if (comboErr[i] < comboErr[best]) best = i;
        autoSearch = false;
        var c = combos[best];
        ApplyCombo(c);
        errors.Clear();
        calibErrorMedian = -1f;
        status = $"лучшее {comboErr[best] * 100f:F0} см";
        Debug.LogWarning($"DepthSampler AUTO: ни одна комбинация не дала < 5 см. Лучшая eye={c.x} flipY={c.y} z={c.z}: {comboErr[best] * 100f:F1} см. Пришлите весь лог DepthSampler.");
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0) { f /= b; r += f * (index % b); index /= b; }
        return r;
    }
}
