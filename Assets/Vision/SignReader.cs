using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Meta.XR;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Чтение табличек: кадр с камеры passthrough → OCR (Tesseract, rus+eng) в фоновом потоке → отсев мусора и сборка строк
/// в таблички (SignText) → подтверждение по нескольким сканам (TextTracker) → мировая точка по глубине
/// (луч через пиксель, EnvironmentRaycastManager) → SonarLandmarks.Report(Text). Дальше AudioSonar озвучивает мелодию
/// текста и произносит его через SonarLandmarks.SpeakHandler (= SpeechOutput).
///
/// Правый хват: прочитать вслух прямо сейчас всё, что видно (без ожидания подтверждения).
/// Скрипт создаётся сам при старте сцены. На шлеме ещё не проверялся.
/// </summary>
public class SignReader : MonoBehaviour
{
    public static SignReader Instance { get; private set; }

    [Header("Распознавание")]
    public bool readSigns = true;
    [Tooltip("Языки Tesseract через плюс; файлы Assets/StreamingAssets/tessdata/<язык>.traineddata")]
    public string languages = "rus+eng";
    [Tooltip("Как часто снимать кадр и распознавать, сек (само распознавание 0.5-3 с в фоне)")]
    public float scanInterval = 2.5f;
    [Tooltip("Слова с уверенностью ниже этой выбрасываются (0..100)")]
    [Range(0f, 100f)] public float minConfidence = 60f;
    [Tooltip("Сколько сканов подряд должен быть виден похожий текст, чтобы прочитать его вслух")]
    public int confirmScans = 2;
    [Tooltip("Похожесть текста между сканами (0..1), чтобы считать его той же табличкой")]
    [Range(0.5f, 1f)] public float textSimilarity = 0.75f;

    [Header("Положение таблички")]
    [Tooltip("Дальше этого расстояния точка по глубине не ищется, м")]
    public float maxRange = 4f;
    [Tooltip("Если глубина не ответила, табличка ставится на этом расстоянии вдоль луча, м")]
    public float fallbackDistance = 2f;

    [Header("Управление и отладка")]
    [Tooltip("Правый хват: прочитать вслух всё, что видно сейчас")]
    public bool readOnGrip = true;
    public bool showLabel = true;
    public Vector3 labelOffset = new Vector3(0.36f, 0.02f, 1.0f);

    [Header("Состояние (только чтение)")]
    public string status = "запуск";
    public string ocrStatus = "";
    public string lastText = "";
    public int signsInLastScan;
    public float lastOcrMs;
    public int scans;

    PassthroughCameraFeed feed;
    EnvironmentRaycastManager raycaster;
    Transform head;
    TextMesh label;
    string dataDir;
    bool isAndroid;

    // рабочий поток OCR
    Thread worker;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    volatile bool stopWorker;
    volatile int state;            // 0 свободен, 1 кадр готов, 2 распознаёт, 3 результат готов
    volatile bool ocrReady;
    readonly PassthroughCameraFeed.Frame frame = new PassthroughCameraFeed.Frame();
    readonly List<OcrWord> words = new List<OcrWord>();
    float workerMs;

    readonly TextTracker tracker = new TextTracker();
    readonly List<TextTracker.Track> confirmed = new List<TextTracker.Track>();
    readonly List<TextTracker.Track> updated = new List<TextTracker.Track>();
    List<SignCandidate> signs = new List<SignCandidate>();
    float nextScan, labelTimer;
    bool forceScan, speakAfterScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<SignReader>() == null)
            new GameObject("SignReader").AddComponent<SignReader>();
    }

    void Awake() { Instance = this; }

    IEnumerator Start()
    {
        isAndroid = Application.platform == RuntimePlatform.Android;
        yield return SonarCommon.WaitUntilReady();
        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        feed = PassthroughCameraFeed.GetOrCreate();
        if (showLabel && head != null) CreateLabel();

        yield return InstallTessdata();

        tracker.similarity = textSimilarity;
        tracker.needed = Mathf.Max(1, confirmScans);
        worker = new Thread(WorkerMain) { Name = "SignReader OCR", IsBackground = true };
        worker.Start();
        status = "OCR запускается";
    }

    /// <summary>Копирует *.traineddata из StreamingAssets (внутри APK) в persistentDataPath/tesseract/tessdata, где их может открыть Tesseract.</summary>
    IEnumerator InstallTessdata()
    {
        dataDir = Path.Combine(Application.persistentDataPath, "tesseract");
        string tessdata = Path.Combine(dataDir, "tessdata");
        try { Directory.CreateDirectory(tessdata); }
        catch (Exception e) { status = "tessdata: " + e.Message; yield break; }

        foreach (string lang in languages.Split('+'))
        {
            string name = lang.Trim();
            if (name.Length == 0) continue;
            string dst = Path.Combine(tessdata, name + ".traineddata");
            if (File.Exists(dst) && new FileInfo(dst).Length > 0) continue;

            string src = Path.Combine(Application.streamingAssetsPath, "tessdata", name + ".traineddata");
            status = "копирую " + name + ".traineddata";
            if (src.Contains("://") || src.StartsWith("jar:"))
            {
                using (var req = UnityWebRequest.Get(src))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        status = "нет " + name + ".traineddata в StreamingAssets: " + req.error;
                        Debug.LogWarning("SignReader: " + status);
                        continue;
                    }
                    try { File.WriteAllBytes(dst, req.downloadHandler.data); }
                    catch (Exception e) { status = "tessdata: " + e.Message; }
                }
            }
            else if (File.Exists(src))
            {
                try { File.Copy(src, dst, true); }
                catch (Exception e) { status = "tessdata: " + e.Message; }
            }
            else
            {
                status = "нет файла " + src;
                Debug.LogWarning("SignReader: " + status);
            }
        }
    }

    void WorkerMain()
    {
        if (isAndroid) AndroidJNI.AttachCurrentThread();
        ITextRecognizer ocr = null;
        try
        {
            ocr = isAndroid ? (ITextRecognizer)new TesseractRecognizer() : new NullRecognizer();
            ocrReady = ocr.Init(dataDir, languages);
            ocrStatus = ocr.Status;
            Debug.Log("SignReader: " + ocrStatus);

            var sw = new System.Diagnostics.Stopwatch();
            while (!stopWorker)
            {
                wake.WaitOne(500);
                if (stopWorker) break;
                if (state != 1) continue;
                state = 2;
                sw.Restart();
                try { ocr.Recognize(frame.gray, frame.width, frame.height, words); }
                catch (Exception e) { words.Clear(); ocrStatus = "OCR: " + e.Message; }
                workerMs = (float)sw.Elapsed.TotalMilliseconds;
                state = 3;
            }
        }
        catch (Exception e) { ocrStatus = "поток OCR: " + e.Message; }
        finally
        {
            try { ocr?.Dispose(); } catch { /* не важно */ }
            if (isAndroid) AndroidJNI.DetachCurrentThread();
        }
    }

    void Update()
    {
        if (head == null || worker == null) return;
        float now = Time.time;

        if (state == 3)
        {
            state = 0;
            lastOcrMs = workerMs;
            scans++;
            ProcessResult(now);
        }

        if (readOnGrip && OVRInput.GetDown(OVRInput.Button.SecondaryHandTrigger))
        {
            forceScan = true;
            speakAfterScan = true;
            if (!feed.IsReady) { SpeechOutput.Speak("камера не готова", true); forceScan = false; speakAfterScan = false; }
            else if (!ocrReady) { SpeechOutput.Speak("распознавание не готово", true); forceScan = false; speakAfterScan = false; }
        }

        bool due = readSigns && now >= nextScan;
        if ((due || forceScan) && state == 0 && ocrReady && feed != null && feed.IsReady)
        {
            if (feed.TryCapture(frame))
            {
                forceScan = false;
                nextScan = now + scanInterval;
                state = 1;
                wake.Set();
                status = "распознаю";
            }
        }
        else if (!ocrReady && worker.IsAlive) status = ocrStatus.Length > 0 ? ocrStatus : "OCR запускается";
        else if (feed != null && !feed.IsReady) status = feed.status;

        labelTimer += Time.unscaledDeltaTime;
        if (label != null && labelTimer > 0.25f)
        {
            labelTimer = 0f;
            UpdateLabel();
        }
    }

    void ProcessResult(float now)
    {
        signs = SignText.GroupIntoSigns(words, minConfidence);
        signsInLastScan = signs.Count;
        tracker.Observe(signs, now, confirmed, updated);

        var toReport = speakAfterScan ? updated : confirmed;
        foreach (var tr in toReport)
        {
            Vector3 pos = LocatePixel(tr.pixel);
            int id = SonarLandmarks.MakeId(LandmarkKind.Text, pos, 0.7f);
            SonarLandmarks.Report(id, LandmarkKind.Text, pos, tr.text);
            lastText = tr.text;
        }
        status = signs.Count > 0 ? $"видно надписей: {signs.Count}, {lastOcrMs:F0} мс" : $"текста не видно, {lastOcrMs:F0} мс";

        if (speakAfterScan)
        {
            speakAfterScan = false;
            if (signs.Count == 0) SpeechOutput.Speak("текста не видно", true);
            else
            {
                var sb = new StringBuilder();
                int n = Mathf.Min(3, signs.Count);
                for (int i = 0; i < n; i++) { if (i > 0) sb.Append(". "); sb.Append(signs[i].text); }
                SpeechOutput.Speak(sb.ToString(), true);
            }
        }
    }

    /// <summary>Мировая точка таблички: луч через пиксель кадра (по позе камеры на момент кадра) и глубина; иначе fallbackDistance по лучу.</summary>
    Vector3 LocatePixel(Vector2 px)
    {
        Ray ray = feed.PixelToRay(frame, px.x, px.y);
        if (raycaster != null)
        {
            try
            {
                if (raycaster.Raycast(ray, out EnvironmentRaycastHit hit, maxRange) && hit.status == EnvironmentRaycastHitStatus.Hit)
                    return hit.point;
            }
            catch (Exception e) { Debug.LogWarning("SignReader: глубина: " + e.Message); }
        }
        return ray.origin + ray.direction.normalized * fallbackDistance;
    }

    void CreateLabel()
    {
        var go = new GameObject("VisionLabel");
        go.transform.SetParent(head, false);
        go.transform.localPosition = labelOffset;
        label = go.AddComponent<TextMesh>();
        label.characterSize = 0.009f;
        label.fontSize = 48;
        label.anchor = TextAnchor.UpperCenter;
        label.color = new Color(0.6f, 0.9f, 1f);
    }

    void UpdateLabel()
    {
        var sb = new StringBuilder();
        sb.Append("Текст: ").AppendLine(status);
        if (!string.IsNullOrEmpty(lastText)) sb.Append("«").Append(lastText).AppendLine("»");
        if (feed != null && !feed.IsReady) sb.AppendLine(feed.status);
        if (SpeechOutput.Instance != null) sb.Append("Голос: ").AppendLine(SpeechOutput.Instance.status);
        if (DoorFinder.Instance != null) sb.AppendLine(DoorFinder.Instance.status);
        label.text = sb.ToString();
    }

    void OnDestroy()
    {
        stopWorker = true;
        wake.Set();
        if (worker != null && worker.IsAlive) worker.Join(2000);
        if (Instance == this) Instance = null;
    }
}
