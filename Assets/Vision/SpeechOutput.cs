using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Голос для всего приложения: Android TextToSpeech (язык ru, запасной en). Один экземпляр и общая очередь фраз.
/// Создаётся сам при старте сцены и подключает себя к SonarLandmarks.SpeakHandler, если туда ещё никто не подключён:
/// так AudioSonar произносит текст табличек, которые находит ветка vision (SignReader).
/// В редакторе и не на Android фразы пишутся в консоль.
///
/// Использование: SpeechOutput.Speak("выход");  SpeechOutput.Speak("стоп", interrupt: true);
/// </summary>
public class SpeechOutput : MonoBehaviour
{
    public static SpeechOutput Instance { get; private set; }

    [Tooltip("Языки голоса по порядку предпочтения (java.util.Locale): берётся первый, который есть у движка на шлеме")]
    public string[] languagesPreferred = { "ro", "ru", "en" };
    [Tooltip("Какой язык выбран (только чтение)")]
    public string language = "";
    [Tooltip("Движки TTS по порядку: пусто = движок по умолчанию; на Quest 3 системный движок называется com.oculus.systemintelligence (HzOS Text-to-Speech)")]
    public string[] engines = { "", "com.oculus.systemintelligence", "com.google.android.tts" };
    [Tooltip("Одна и та же фраза не повторяется чаще, сек (чтобы табличка не читалась снова и снова)")]
    public float repeatCooldown = 4f;
    [Range(0.5f, 2f)] public float speechRate = 1.1f;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public bool russian;
    public string status = "не запущен";

    AndroidJavaObject tts;
    AndroidJavaObject emptyBundle;
    AndroidJavaObject activity;
    volatile int initStatus = -99;
    int engineIndex;
    string engineName = "";
    readonly List<string> pending = new List<string>();
    readonly Dictionary<string, float> lastSaid = new Dictionary<string, float>();
    int utterance;

    class TtsInit : AndroidJavaProxy
    {
        readonly Action<int> done;
        public TtsInit(Action<int> done) : base("android.speech.tts.TextToSpeech$OnInitListener") { this.done = done; }
        public void onInit(int status) { done(status); }
    }

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
        if (Application.platform != RuntimePlatform.Android)
        {
            ready = true;
            status = "не Android: фразы в консоль";
            return;
        }
        try
        {
            var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            engineIndex = 0;
            StartEngine();
        }
        catch (Exception e)
        {
            status = "TTS недоступен: " + e.Message;
            Debug.LogWarning("SpeechOutput: " + status);
        }
    }

    /// <summary>Создаёт TextToSpeech для движка engines[engineIndex] (пустое имя = движок по умолчанию).</summary>
    void StartEngine()
    {
        if (engines == null || engines.Length == 0) engines = new[] { "" };
        engineName = engineIndex < engines.Length ? (engines[engineIndex] ?? "") : "";
        initStatus = -99;
        if (tts != null) { try { tts.Call("shutdown"); } catch { /* не важно */ } tts = null; }
        if (engineName.Length == 0)
            tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s));
        else
            tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s), engineName);
        status = "запуск TTS " + (engineName.Length == 0 ? "(по умолчанию)" : engineName);
        Debug.Log("SpeechOutput: " + status);
    }

    void Update()
    {
        if (tts != null && !ready && initStatus != -99) FinishInit();
        if (ready && pending.Count > 0)
        {
            var copy = pending.ToArray();
            pending.Clear();
            foreach (var p in copy) Say(p, false);
        }
    }

    void FinishInit()
    {
        if (initStatus != 0)
        {
            Debug.LogWarning($"SpeechOutput: TTS {(engineName.Length == 0 ? "по умолчанию" : engineName)} не запустился, статус {initStatus}");
            VisionLog.Event("tts_init", $"\"engine\":{VisionLog.Str(engineName)},\"ready\":false,\"initStatus\":{initStatus}");
            initStatus = -99;
            if (++engineIndex < engines.Length)
            {
                try { StartEngine(); return; }
                catch (Exception e) { Debug.LogWarning("SpeechOutput: " + e.Message); }
            }
            status = "TTS не запустился ни с одним движком";
            tts = null;
            return;
        }
        try
        {
            string engine = "";
            try { engine = tts.Call<string>("getDefaultEngine") ?? ""; } catch { /* не важно */ }
            // первый язык из списка, который движок умеет (0 = есть, 1 = есть и страна, 2 = есть и вариант; < 0 = нет)
            int r = -2;
            language = "";
            var tried = new StringBuilder();
            foreach (string lang in languagesPreferred ?? new[] { "en" })
            {
                if (string.IsNullOrEmpty(lang)) continue;
                r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", lang));
                if (tried.Length > 0) tried.Append(' ');
                tried.Append(lang).Append('=').Append(r);
                if (r >= 0) { language = lang; break; }
            }
            if (language.Length == 0)
            {
                r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", "en"));
                if (r >= 0) language = "en";
            }
            russian = language == "ru";
            tts.Call<int>("setSpeechRate", speechRate);
            emptyBundle = new AndroidJavaObject("android.os.Bundle");
            ready = language.Length > 0;
            string who = engineName.Length > 0 ? engineName : engine;
            status = ready ? "готов (" + language + ", " + who + ")" : "у TTS " + who + " нет нужных языков (" + tried + ")";
        }
        catch (Exception e) { status = "TTS язык: " + e.Message; }
        Debug.Log("SpeechOutput: " + status);
        VisionLog.Event("tts_init", $"\"engine\":{VisionLog.Str(engineName)},\"ready\":{VisionLog.B(ready)},\"status\":{VisionLog.Str(status)}");
        if (!ready)
        {
            // этот движок без нужных языков: пробуем следующий
            if (++engineIndex < engines.Length)
            {
                try { StartEngine(); return; }
                catch (Exception e) { Debug.LogWarning("SpeechOutput: " + e.Message); }
            }
            tts = null;
            initStatus = -99;
        }
    }

    /// <summary>Произнести фразу. interrupt = true обрывает текущую речь (для срочного), иначе ставится в очередь.</summary>
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

        if (!ready)
        {
            if (pending.Count < 3) pending.Add(text);
            return;
        }
        if (tts == null)
        {
            Debug.Log("TTS: " + text);
            return;
        }
        try
        {
            // speak(CharSequence text, int queueMode, Bundle params, String utteranceId): 0 = QUEUE_FLUSH, 1 = QUEUE_ADD
            int r = tts.Call<int>("speak", text, interrupt ? 0 : 1, emptyBundle, "sonar" + (utterance++));
            VisionLog.Event("speak", $"\"text\":{VisionLog.Str(text)},\"interrupt\":{VisionLog.B(interrupt)},\"result\":{r},\"engine\":{VisionLog.Str(status)}");
        }
        catch (Exception e) { Debug.LogWarning("SpeechOutput speak: " + e.Message); }
    }

    public bool IsSpeaking
    {
        get
        {
            if (tts == null || !ready) return false;
            try { return tts.Call<bool>("isSpeaking"); } catch { return false; }
        }
    }

    public void Stop()
    {
        if (tts == null || !ready) return;
        try { tts.Call<int>("stop"); } catch { /* не важно */ }
    }

    void OnDestroy()
    {
        if (tts != null) { try { tts.Call("shutdown"); } catch { /* не важно */ } }
        if (Instance == this) Instance = null;
    }
}
