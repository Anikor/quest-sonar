using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Голос для всего приложения: Android TextToSpeech. Один экземпляр и общая очередь фраз.
/// Создаётся сам при старте сцены и подключает себя к SonarLandmarks.SpeakHandler, если туда ещё никто не подключён:
/// так AudioSonar произносит текст табличек, которые находит ветка vision (SignReader).
///
/// ВСЕ вызовы Java идут из отдельного потока: системный движок Quest 3 (com.oculus.systemintelligence) отвечает на
/// setLanguage по 6-12 секунд и не знает языков, из главного потока это замораживало приложение (журнал 2026-10-30).
/// Движки пробуются по списку engines: первым eSpeak NG (ставится на шлем отдельно, умеет ro/ru/en, работает без сети),
/// затем движок по умолчанию, затем системный Quest. Берётся первый, который запустился и знает один из языков;
/// если языков не знает никто, говорит последний запустившийся своим голосом по умолчанию.
///
/// Использование: SpeechOutput.Speak("ieșire");  SpeechOutput.Speak("стоп", interrupt: true);
/// В редакторе и не на Android фразы пишутся в консоль.
/// </summary>
public class SpeechOutput : MonoBehaviour
{
    public static SpeechOutput Instance { get; private set; }

    [Tooltip("Языки голоса по порядку предпочтения (теги BCP-47 или коды): берётся первый, который есть у движка")]
    public string[] languagesPreferred = { "ro", "ru", "en" };
    [Tooltip("Движки TTS по порядку: пакет или пусто = движок по умолчанию. eSpeak NG = com.reecedunn.espeak (F-Droid), системный Quest = com.oculus.systemintelligence")]
    public string[] engines = { "com.reecedunn.espeak", "", "com.oculus.systemintelligence" };
    [Tooltip("Одна и та же фраза не повторяется чаще, сек (чтобы табличка не читалась снова и снова)")]
    public float repeatCooldown = 4f;
    [Range(0.5f, 2f)] public float speechRate = 1.1f;
    [Tooltip("Сколько секунд ждать ответа движка при запуске, прежде чем пробовать следующий")]
    public float engineTimeout = 15f;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public string status = "не запущен";
    public string language = "";
    public string engine = "";

    class TtsInit : AndroidJavaProxy
    {
        readonly Action<int> done;
        public TtsInit(Action<int> done) : base("android.speech.tts.TextToSpeech$OnInitListener") { this.done = done; }
        public void onInit(int status) { done(status); }
    }

    struct Phrase { public string text; public bool interrupt; }

    AndroidJavaObject activity;
    Thread worker;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly Queue<Phrase> queue = new Queue<Phrase>();
    readonly object gate = new object();
    volatile bool stopWorker;
    volatile bool readyV;
    volatile string statusV = "не запущен";
    volatile string languageV = "";
    volatile string engineV = "";
    volatile int initStatus = -99;
    readonly Dictionary<string, float> lastSaid = new Dictionary<string, float>();
    bool isAndroid;
    int utterance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<SpeechOutput>() == null)
            new GameObject("SpeechOutput").AddComponent<SpeechOutput>();
    }

    void Awake()
    {
        Instance = this;
        if (SonarLandmarks.SpeakHandler == null) SonarLandmarks.SpeakHandler = s => Say(s, false);
    }

    void Start()
    {
        isAndroid = Application.platform == RuntimePlatform.Android;
        if (!isAndroid)
        {
            readyV = true;
            statusV = "не Android: фразы в консоль";
            return;
        }
        try
        {
            var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
        }
        catch (Exception e)
        {
            statusV = "TTS недоступен: " + e.Message;
            Debug.LogWarning("SpeechOutput: " + statusV);
            return;
        }
        statusV = "запуск TTS";
        worker = new Thread(WorkerMain) { Name = "SpeechOutput TTS", IsBackground = true };
        worker.Start();
    }

    void Update()
    {
        ready = readyV;
        status = statusV;
        language = languageV;
        engine = engineV;
    }

    /// <summary>Произнести фразу. interrupt = true обрывает текущую речь (для срочного), иначе ставится в очередь. Главный поток.</summary>
    public static void Speak(string text, bool interrupt = false)
    {
        if (Instance != null) Instance.Say(text, interrupt);
        else Debug.Log("TTS (SpeechOutput ещё нет): " + text);
    }

    public void Say(string text, bool interrupt)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        float now = Time.time;
        if (!interrupt && lastSaid.TryGetValue(text, out float t) && now - t < repeatCooldown) return;
        lastSaid[text] = now;

        if (!isAndroid || worker == null)
        {
            Debug.Log("TTS: " + text);
            VisionLog.Event("speak", $"\"text\":{VisionLog.Str(text)},\"interrupt\":{VisionLog.B(interrupt)},\"result\":\"console\"");
            return;
        }
        lock (gate)
        {
            if (interrupt) queue.Clear();
            if (queue.Count < 6) queue.Enqueue(new Phrase { text = text, interrupt = interrupt });
        }
        wake.Set();
    }

    public void Stop()
    {
        lock (gate) queue.Clear();
        lock (gate) queue.Enqueue(new Phrase { text = "", interrupt = true });   // пустая фраза с interrupt = остановить речь
        wake.Set();
    }

    // ---------------------------------------------------------------- поток TTS
    void WorkerMain()
    {
        AndroidJNI.AttachCurrentThread();
        AndroidJavaObject tts = null, bundle = null;
        try
        {
            tts = StartBestEngine();
            if (tts == null)
            {
                statusV = "TTS не запустился ни с одним движком";
                return;
            }
            bundle = new AndroidJavaObject("android.os.Bundle");
            readyV = true;

            while (!stopWorker)
            {
                wake.WaitOne(500);
                if (stopWorker) break;
                while (true)
                {
                    Phrase p;
                    lock (gate)
                    {
                        if (queue.Count == 0) break;
                        p = queue.Dequeue();
                    }
                    try
                    {
                        if (p.text.Length == 0) { tts.Call<int>("stop"); continue; }
                        // speak(CharSequence, int queueMode, Bundle, String utteranceId): 0 = QUEUE_FLUSH, 1 = QUEUE_ADD
                        int r = tts.Call<int>("speak", p.text, p.interrupt ? 0 : 1, bundle, "sonar" + (utterance++));
                        VisionLog.Event("speak", $"\"text\":{VisionLog.Str(p.text)},\"interrupt\":{VisionLog.B(p.interrupt)},\"result\":{r},\"engine\":{VisionLog.Str(engineV)},\"language\":{VisionLog.Str(languageV)}");
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("SpeechOutput speak: " + e.Message);
                        VisionLog.Event("speak", $"\"text\":{VisionLog.Str(p.text)},\"error\":{VisionLog.Str(e.Message)}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            statusV = "поток TTS: " + e.Message;
            Debug.LogWarning("SpeechOutput: " + statusV);
        }
        finally
        {
            try { tts?.Call("shutdown"); } catch { /* не важно */ }
            tts?.Dispose();
            bundle?.Dispose();
            AndroidJNI.DetachCurrentThread();
        }
    }

    /// <summary>Перебирает движки: первый, который запустился и знает язык из списка; иначе последний запустившийся.</summary>
    AndroidJavaObject StartBestEngine()
    {
        var list = engines ?? new[] { "" };
        AndroidJavaObject fallback = null;
        string fallbackName = "", fallbackStatus = "";
        for (int i = 0; i < list.Length; i++)
        {
            string name = list[i] ?? "";
            AndroidJavaObject tts = StartEngine(name, out int st);
            if (tts == null)
            {
                string why = st == -99 ? "нет ответа за " + engineTimeout + " с" : "статус " + st;
                Debug.LogWarning($"SpeechOutput: TTS {(name.Length == 0 ? "по умолчанию" : name)} не запустился ({why})");
                VisionLog.Event("tts_init", $"\"engine\":{VisionLog.Str(name)},\"ready\":false,\"initStatus\":{st}");
                continue;
            }
            string lang = ChooseLanguage(tts, out string tried, out string available);
            string who = name.Length > 0 ? name : SafeDefaultEngine(tts);
            string info = $"движок {who}; языки движка: {(available.Length > 0 ? available : "не сообщает")}; пробы: {tried}";
            VisionLog.Event("tts_init", $"\"engine\":{VisionLog.Str(who)},\"ready\":true,\"language\":{VisionLog.Str(lang)},\"available\":{VisionLog.Str(available)},\"tried\":{VisionLog.Str(tried)}");
            if (lang.Length > 0)
            {
                if (fallback != null) { try { fallback.Call("shutdown"); } catch { } fallback.Dispose(); }
                try { tts.Call<int>("setSpeechRate", speechRate); } catch { /* не важно */ }
                languageV = lang;
                engineV = who;
                statusV = "готов (" + lang + ", " + who + ")";
                Debug.Log("SpeechOutput: " + statusV + "; " + info);
                return tts;
            }
            // языков нет: запомним как запасной, но попробуем следующий
            if (fallback != null) { try { fallback.Call("shutdown"); } catch { } fallback.Dispose(); }
            fallback = tts;
            fallbackName = who;
            fallbackStatus = info;
        }
        if (fallback != null)
        {
            try { fallback.Call<int>("setSpeechRate", speechRate); } catch { /* не важно */ }
            languageV = "по умолчанию движка";
            engineV = fallbackName;
            statusV = "готов (язык по умолчанию, " + fallbackName + ")";
            Debug.Log("SpeechOutput: " + statusV + "; " + fallbackStatus);
        }
        return fallback;
    }

    AndroidJavaObject StartEngine(string name, out int status)
    {
        initStatus = -99;
        AndroidJavaObject tts;
        try
        {
            tts = name.Length == 0
                ? new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s))
                : new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s), name);
        }
        catch (Exception e)
        {
            Debug.LogWarning("SpeechOutput: " + e.Message);
            status = -98;
            return null;
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (initStatus == -99 && sw.Elapsed.TotalSeconds < engineTimeout && !stopWorker) Thread.Sleep(50);
        status = initStatus;
        if (status == 0) return tts;
        try { tts.Call("shutdown"); } catch { /* не важно */ }
        tts.Dispose();
        return null;
    }

    static string SafeDefaultEngine(AndroidJavaObject tts)
    {
        try { return tts.Call<string>("getDefaultEngine") ?? "?"; } catch { return "?"; }
    }

    /// <summary>Первый язык из languagesPreferred, который движок принимает (по его списку или по полному тегу). "" = ни один.</summary>
    string ChooseLanguage(AndroidJavaObject tts, out string tried, out string availableStr)
    {
        var available = new List<string>();
        try
        {
            var set = tts.Call<AndroidJavaObject>("getAvailableLanguages");
            if (set != null)
            {
                var it = set.Call<AndroidJavaObject>("iterator");
                while (it.Call<bool>("hasNext"))
                {
                    var loc = it.Call<AndroidJavaObject>("next");
                    available.Add(loc.Call<string>("toLanguageTag"));
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("SpeechOutput: getAvailableLanguages: " + e.Message); }
        available.Sort(StringComparer.OrdinalIgnoreCase);
        availableStr = string.Join(",", available);

        var localeClass = new AndroidJavaClass("java.util.Locale");
        var sb = new StringBuilder();
        foreach (string lang in languagesPreferred ?? new[] { "en" })
        {
            if (string.IsNullOrEmpty(lang)) continue;
            var candidates = new List<string>();
            string match = available.Find(a => a.Equals(lang, StringComparison.OrdinalIgnoreCase))
                        ?? available.Find(a => a.StartsWith(lang + "-", StringComparison.OrdinalIgnoreCase));
            if (match != null) candidates.Add(match);
            candidates.Add(lang);
            if (lang == "en") candidates.Add("en-US");
            if (lang == "ro") candidates.Add("ro-RO");
            if (lang == "ru") candidates.Add("ru-RU");
            foreach (string tag in candidates)
            {
                int r;
                try { r = tts.Call<int>("setLanguage", localeClass.CallStatic<AndroidJavaObject>("forLanguageTag", tag)); }
                catch (Exception e) { r = -97; Debug.LogWarning("SpeechOutput: setLanguage " + tag + ": " + e.Message); }
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(tag).Append('=').Append(r);
                if (r >= 0) { tried = sb.ToString(); return tag; }
                if (stopWorker) break;
            }
        }
        tried = sb.ToString();
        return "";
    }

    void OnDestroy()
    {
        stopWorker = true;
        wake.Set();
        if (worker != null && worker.IsAlive) worker.Join(3000);
        if (Instance == this) Instance = null;
    }
}
