using System;
using System.Collections;
using System.IO;
using UnityEngine;

/// <summary>
/// Эксперимент: условия обратной связи и запись попыток в CSV.
/// Кнопки помощника: левый стик (нажать) = следующее условие, правый стик = столкновение,
/// левый триггер = ложная тревога, правый триггер = конец попытки (запись строки).
/// Условия по кругу: A Off, B Mono, C Spatial, D SpatialVoice.
/// Номер участника: файл participant.txt в папке приложения.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class ExperimentController : MonoBehaviour
{
    public string voiceLanguage = "ru";
    public float voiceInterval = 2f;
    public Vector3 labelOffset = new Vector3(0f, -0.42f, 1.0f);

    string csvPath, participantPath;
    float trialStart, lastSpeak, labelTimer;
    int collisions, falseAlarms, trialIndex;
    AudioSonar sonar;
    Transform head;
    TextMesh label;

    AndroidJavaObject tts;
    bool ttsReady, ttsRu;
    volatile int initStatus = -99;

    class TtsInit : AndroidJavaProxy
    {
        readonly Action<int> done;
        public TtsInit(Action<int> done) : base("android.speech.tts.TextToSpeech$OnInitListener") { this.done = done; }
        public void onInit(int status) { done(status); }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ExperimentController>() == null)
            new GameObject("ExperimentController").AddComponent<ExperimentController>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => FindFirstObjectByType<AudioSonar>() != null);
        sonar = FindFirstObjectByType<AudioSonar>();
        head = SonarCommon.FindHead();
        if (sonar != null) sonar.toggleWithButtons = false;   // A/X больше не глушат звук: кнопки заняты экспериментом

        csvPath = Path.Combine(Application.persistentDataPath, "trials.csv");
        participantPath = Path.Combine(Application.persistentDataPath, "participant.txt");
        if (!File.Exists(csvPath))
            File.WriteAllText(csvPath, "timestamp,participant,trial,mode,duration_s,collisions,false_alarms\n");
        Debug.Log("ExperimentController: CSV " + csvPath);

        InitTts();
        CreateLabel();
        trialStart = Time.time;
    }

    void Update()
    {
        if (sonar == null) return;

        if (tts != null && !ttsReady && initStatus != -99) FinishTtsInit();

        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick))
        {
            AudioSonar.Mode = (AudioSonar.FeedbackMode)(((int)AudioSonar.Mode + 1) % 4);
            Debug.Log("ExperimentController: режим " + AudioSonar.Mode);
        }
        if (OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick)) collisions++;
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger)) falseAlarms++;
        if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger)) EndTrial();

        if (AudioSonar.Mode == AudioSonar.FeedbackMode.SpatialVoice) Voice();

        labelTimer += Time.unscaledDeltaTime;
        if (label != null && labelTimer > 0.25f)
        {
            labelTimer = 0f;
            string letter = "ABCD"[(int)AudioSonar.Mode].ToString();
            label.text = $"Условие {letter} ({AudioSonar.Mode})  столкн. {collisions}  ложн. {falseAlarms}  попытка #{trialIndex + 1}";
        }
    }

    void EndTrial()
    {
        string p = File.Exists(participantPath) ? File.ReadAllText(participantPath).Trim() : "P00";
        trialIndex++;
        File.AppendAllText(csvPath,
            $"{DateTime.Now:s},{p},{trialIndex},{AudioSonar.Mode},{Time.time - trialStart:F1},{collisions},{falseAlarms}\n");
        Debug.Log($"ExperimentController: запись {p} #{trialIndex} {AudioSonar.Mode} столкн.={collisions} ложн.={falseAlarms}");
        collisions = 0; falseAlarms = 0; trialStart = Time.time;
    }

    // ---------------- голос (условие D) ----------------
    void Voice()
    {
        if (!ttsReady || Time.time - lastSpeak < voiceInterval) return;
        if (!sonar.TryGetNearest(out float d, out Vector3 dir)) return;

        float az = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        string side = Mathf.Abs(az) < 20f ? (ttsRu ? "впереди" : "ahead")
                    : az < 0f ? (ttsRu ? "слева" : "left")
                    : (ttsRu ? "справа" : "right");
        string dist = d < 0.8f ? (ttsRu ? "близко" : "close")
                    : d < 1.5f ? (ttsRu ? "метр" : "one meter")
                    : d < 2.5f ? (ttsRu ? "два метра" : "two meters")
                    : null;
        if (dist == null) return;

        Speak(side + " " + dist);
        lastSpeak = Time.time;
    }

    void InitTts()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
            tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new TtsInit(s => initStatus = s));
        }
        catch (Exception e) { Debug.LogWarning("ExperimentController: TTS недоступен: " + e.Message); }
#endif
    }

    void FinishTtsInit()
    {
        if (initStatus != 0)
        {
            Debug.LogWarning("ExperimentController: TTS не запустился, статус " + initStatus + ". Условие D будет без голоса.");
            initStatus = -98;
            tts = null;
            return;
        }
        try
        {
            int r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", voiceLanguage));
            ttsRu = voiceLanguage == "ru" && r >= 0;
            if (r < 0) r = tts.Call<int>("setLanguage", new AndroidJavaObject("java.util.Locale", "en"));
            ttsReady = r >= 0;
            Debug.Log("ExperimentController: TTS " + (ttsReady ? (ttsRu ? "готов (ru)" : "готов (en, русского нет)") : "без языка, голоса не будет"));
        }
        catch (Exception e) { Debug.LogWarning("ExperimentController: TTS язык: " + e.Message); }
    }

    void Speak(string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try { tts.Call<int>("speak", text, 0, null, null); }
        catch (Exception e) { Debug.LogWarning("ExperimentController: speak: " + e.Message); }
#else
        Debug.Log("TTS: " + text);
#endif
    }

    void CreateLabel()
    {
        if (head == null) return;
        var go = new GameObject("ExperimentLabel");
        go.transform.SetParent(head, false);
        go.transform.localPosition = labelOffset;
        label = go.AddComponent<TextMesh>();
        label.characterSize = 0.01f;
        label.fontSize = 48;
        label.anchor = TextAnchor.UpperCenter;
        label.color = Color.yellow;
    }
}
