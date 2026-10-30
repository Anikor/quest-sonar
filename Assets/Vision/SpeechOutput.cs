using System;
using System.Collections.Generic;
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

    [Tooltip("Язык голоса (java.util.Locale). Если такого голоса на шлеме нет, берётся en")]
    public string language = "ru";
    [Tooltip("Одна и та же фраза не повторяется чаще, сек (чтобы табличка не читалась снова и снова)")]
    public float repeatCooldown = 4f;
    [Range(0.5f, 2f)] public float speechRate = 1.1f;

    [Header("Состояние (только чтение)")]
    public bool ready;
    public bool russian;
    public string status = "не запущен";

    AndroidJavaObject tts;
    AndroidJavaObject emptyBundle;
    volatile int initStatus = -99;
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
            var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s));
            status = "запуск TTS";
        }
        catch (Exception e)
        {
            status = "TTS недоступен: " + e.Message;
            Debug.LogWarning("SpeechOutput: " + status);
        }
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
            status = "TTS не запустился, статус " + initStatus;
            Debug.LogWarning("SpeechOutput: " + status);
            initStatus = -99;
            tts = null;
            return;
        }
        try
        {
            int r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", language));
            russian = language == "ru" && r >= 0;
            if (r < 0) r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", "en"));
            tts.Call<int>("setSpeechRate", speechRate);
            emptyBundle = new AndroidJavaObject("android.os.Bundle");
            ready = r >= 0;
            status = ready ? (russian ? "готов (ru)" : "готов (en, русского голоса нет)") : "у TTS нет ни ru, ни en";
        }
        catch (Exception e) { status = "TTS язык: " + e.Message; }
        Debug.Log("SpeechOutput: " + status);
        if (!ready) { tts = null; initStatus = -99; }
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
            tts.Call<int>("speak", text, interrupt ? 0 : 1, emptyBundle, "sonar" + (utterance++));
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
