using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Режим работы приложения. Ортогонален AudioSonar.FeedbackMode (условия эксперимента A-D): FeedbackMode не меняется,
/// кроме двух случаев - восстановление при выходе из «Эксперимента» и защита от Off вне эксперимента (см. VerifySafety).
/// </summary>
public enum OperatingMode { Navigation, DangerOnly, Text, Landmarks, Narrow, Silence, Experiment }

/// <summary>
/// Режимы работы: что звучит и что распознаётся. Меняет только публичные поля AudioSonar / SignReader / DoorFinder /
/// DoorVision, запоминает прежние значения и возвращает их при выходе из режима (файлы коллег не правятся).
///
/// Управление (см. docs/MODES_AND_TUNING.md, таблица кнопок):
///   левый стик влево / вправо (подержать 0.3 с) - предыдущий / следующий режим по кругу, в любом режиме;
///   левый стик нажать - назвать текущий режим (кроме «Эксперимента»: там это кнопка ExperimentController);
///   правый курок - действие режима: «Текст» = прочитать то, что перед тобой; «Ориентиры» = ближайшая дверь / окно;
///                  в остальных режимах = назвать режим (в «Эксперименте» это конец попытки ExperimentController).
/// Каждая смена режима: голос («Режим: текст»), свой тон (число коротких нот = номер режима) и короткая вибрация.
/// Если голос не готов, режим не меняется (можно только вернуться в «Навигацию»), звучит низкий тон отказа.
///
/// Безопасность: во всех режимах, кроме «Тишины», после смены проверяется, что «опасность» и «стоп» слышны
/// (звук не заглушён, громкости не нулевые, FeedbackMode не Off вне эксперимента, обрыв включён), иначе это
/// исправляется и произносится. «Тишина» по умолчанию оставляет «стоп» и обрыв (silenceKeepStop).
/// ExperimentController выключен (enabled = false) во всех режимах, кроме «Эксперимента»: его кнопки (стики, курки)
/// работают только там. При старте всегда «Навигация». Скрипт создаётся сам при старте сцены.
/// </summary>
public class OperatingModeController : MonoBehaviour
{
    public static OperatingModeController Instance { get; private set; }
    public const int ModeCount = 7;

    [Header("Переключение режимов: левый стик влево / вправо")]
    public bool switchWithLeftStick = true;
    [Tooltip("Отклонение стика, после которого начинается удержание (0..1)")]
    public float stickThreshold = 0.75f;
    [Tooltip("Стик должен вернуться ближе к центру, чтобы можно было переключить ещё раз")]
    public float stickRelease = 0.3f;
    [Tooltip("Сколько держать стик отклонённым, сек (защита от случайного задевания на ходу)")]
    public float holdToSwitch = 0.3f;
    [Tooltip("Без готового голоса режим не меняется (только возврат в навигацию)")]
    public bool requireVoice = true;
    public bool tone = true;
    [Range(0f, 1f)] public float toneVolume = 0.7f;
    public bool vibrate = true;

    [Header("Кнопки вне режима «Эксперимент»")]
    [Tooltip("Левый стик нажать: назвать текущий режим")]
    public bool statusOnLeftStickClick = true;
    [Tooltip("Правый курок: действие режима (прочитать текст, ближайшая дверь)")]
    public bool actionOnRightTrigger = true;

    [Header("Только опасность / Текст / Ориентиры")]
    [Tooltip("Дальние писки беззвучны (nearVolume = farVolume = 0); их интервал растягивается до стольких секунд, чтобы беззвучные писки не мешали мелодиям ориентиров")]
    public float quietBeepInterval = 1f;

    [Header("Текст")]
    public float textScanInterval = 1.5f;
    public float textMaxReadDistance = 3f;

    [Header("Ориентиры")]
    [Tooltip("Ориентир участвует в запросе «ближайшая дверь», если обновлялся не дольше столько секунд назад")]
    public float landmarkMemorySec = 20f;
    public float landmarkQueryRange = 8f;

    [Header("Узкий проход")]
    public float narrowHalfWidth = 0.25f;
    public float narrowMaxRange = 1.5f;
    [Range(0f, 1f)] public float narrowSideVolume = 0.85f;

    [Header("Тишина")]
    [Tooltip("Оставить «стоп» (вплотную) и обрыв. Выключить = полная тишина (SetMuted), это произносится")]
    public bool silenceKeepStop = true;

    [Header("Эксперимент")]
    [Tooltip("Пороги фиксированы: dangerNow = dangerDistance, contactNow = contactDistance при любой скорости")]
    public bool experimentManualThresholds = true;
    [Tooltip("Произносить смену условия A-D")]
    public bool announceCondition = true;

    [Header("Безопасность")]
    public float minMasterVolume = 0.2f;

    [Header("Состояние (только чтение)")]
    public string status = "запуск";

    OperatingMode current = OperatingMode.Navigation;
    public OperatingMode Current { get { return current; } }
    /// <summary>В «Эксперименте» панель настройки закрыта: пороги фиксированы.</summary>
    public bool PanelLocked { get { return current == OperatingMode.Experiment; } }

    AudioSonar sonar;
    Transform head;
    ExperimentController experiment;
    GameObject expLabel;
    AudioSource toneSrc;
    AudioClip[] modeClips;
    AudioClip refuseClip, actionClip;
    readonly List<Action> undo = new List<Action>();
    readonly HashSet<string> overridden = new HashSet<string>();
    AudioSonar.FeedbackMode conditionBefore = AudioSonar.FeedbackMode.Spatial, lastCondition;
    bool started, stickArmed = true;
    int holdDir;
    float holdStart, labelCheck;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<OperatingModeController>() == null)
            new GameObject("OperatingModeController").AddComponent<OperatingModeController>();
    }

    void Awake() { Instance = this; }

    IEnumerator Start()
    {
        // все Boot (AfterSceneLoad) уже отработали: ExperimentController есть, если он в проекте
        experiment = FindFirstObjectByType<ExperimentController>();
        if (experiment != null) experiment.enabled = false;

        yield return SonarCommon.WaitUntilReady(() => FindFirstObjectByType<AudioSonar>() != null);
        sonar = FindFirstObjectByType<AudioSonar>();
        head = SonarCommon.FindHead();
        if (sonar == null) { status = "нет AudioSonar: режимы не работают"; Debug.LogError("OperatingModeController: " + status); yield break; }
        if (experiment == null) experiment = FindFirstObjectByType<ExperimentController>();
        if (experiment != null) experiment.enabled = false;

        toneSrc = ModeVoice.MakeUiSource("ModeTone");
        modeClips = new AudioClip[ModeCount];
        for (int i = 0; i < ModeCount; i++) modeClips[i] = ModeVoice.MakePips("modeTone" + i, 523.25f, i + 1);
        refuseClip = ModeVoice.MakePips("modeRefuse", 196f, 2, 0.09f, 0.06f);
        actionClip = ModeVoice.MakePips("modeAction", 784f, 1, 0.04f, 0.02f);

        lastCondition = AudioSonar.Mode;
        current = OperatingMode.Navigation;
        Enter(current);
        VerifySafety(current);
        started = true;
        UpdateStatus();
    }

    void OnDestroy() { if (Instance == this) Instance = null; }

    void Update()
    {
        if (!started) return;
        float now = Time.unscaledTime;
        if (now >= labelCheck) { labelCheck = now + 1f; SyncExperimentLabel(); }

        if (switchWithLeftStick) HandleStick(now);

        if (current == OperatingMode.Experiment)
        {
            // кнопки здесь принадлежат ExperimentController; мы только записываем пороги рядом с его событиями
            if (AudioSonar.Mode != lastCondition)
            {
                lastCondition = AudioSonar.Mode;
                if (announceCondition) ModeVoice.Say(ConditionText());
                ThresholdLog.Write(sonar, "condition");
                UpdateStatus();
            }
            if (OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger)) ThresholdLog.Write(sonar, "trial_end");
            return;
        }

        lastCondition = AudioSonar.Mode;
        if (statusOnLeftStickClick && OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick)) SayStatus();
        if (actionOnRightTrigger && OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger)) ModeAction();
    }

    // ---------------------------------------------------------------- переключение
    void HandleStick(float now)
    {
        Vector2 a = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
        if (!stickArmed)
        {
            if (Mathf.Abs(a.x) < stickRelease) stickArmed = true;
            return;
        }
        int dir = Mathf.Abs(a.x) > stickThreshold && Mathf.Abs(a.x) > 1.5f * Mathf.Abs(a.y) ? (a.x > 0f ? 1 : -1) : 0;
        if (dir == 0) { holdDir = 0; return; }
        if (dir != holdDir) { holdDir = dir; holdStart = now; return; }
        if (now - holdStart < holdToSwitch) return;
        stickArmed = false;
        holdDir = 0;
        RequestSwitch(dir);
    }

    /// <summary>Следующий (dir = +1) или предыдущий (dir = −1) режим по кругу, с проверками.</summary>
    public void RequestSwitch(int dir)
    {
        if (!started) return;
        if (TuningPanel.Instance != null && TuningPanel.Instance.IsOpen)
        {
            Refuse(ModeVoice.T("Сначала закройте настройки, кнопка X", "Mai întâi închideți setările, butonul X", "Close the settings first, button X"));
            return;
        }
        var target = (OperatingMode)((((int)current + dir) % ModeCount + ModeCount) % ModeCount);
        if (requireVoice && !ModeVoice.VoiceReady)
        {
            // без голоса пользователь не узнает, куда попал: разрешаем только безопасный возврат в навигацию
            if (current == OperatingMode.Navigation) { Refuse(null); return; }
            target = OperatingMode.Navigation;
        }
        SetMode(target, true);
    }

    /// <summary>Включить режим: вернуть прежние значения, применить новый, проверить безопасность, сообщить, записать в CSV.</summary>
    public void SetMode(OperatingMode m, bool announce = true)
    {
        if (!started) return;
        OperatingMode prev = current;
        Leave(prev);
        current = m;
        Enter(m);
        List<string> fixes = VerifySafety(m);
        SyncExperimentLabel();
        UpdateStatus();
        if (announce) Announce(m, fixes);
        ThresholdLog.Write(sonar, "mode");
        VisionLog.Event("op_mode", $"\"from\":{VisionLog.Str(prev.ToString())},\"to\":{VisionLog.Str(m.ToString())},\"fixes\":{VisionLog.Str(string.Join(" ", fixes.ToArray()))}");
        Debug.Log($"OperatingModeController: {prev} -> {m}" + (fixes.Count > 0 ? " (исправлено: " + string.Join(", ", fixes.ToArray()) + ")" : ""));
    }

    /// <summary>Параметр панели сейчас задаётся режимом (панель его не меняет).</summary>
    public bool IsOverridden(string key) { return key != null && overridden.Contains(key); }

    void Leave(OperatingMode m)
    {
        for (int i = undo.Count - 1; i >= 0; i--)
        {
            try { undo[i](); }
            catch (Exception e) { Debug.LogWarning("OperatingModeController: возврат значения: " + e.Message); }
        }
        undo.Clear();
        overridden.Clear();
        if (m == OperatingMode.Experiment)
        {
            if (experiment != null) experiment.enabled = false;
            AudioSonar.Mode = conditionBefore;
        }
        // базовые значения панели могли поменяться, пока режим их задавал (например, «сбросить всё»)
        if (TuningPanel.Instance != null) TuningPanel.Instance.ApplyAll();
    }

    /// <summary>Запоминает текущее значение и ставит новое; при выходе из режима значение вернётся. key = параметр панели или null.</summary>
    void Set<T>(string key, T currentValue, T value, Action<T> setter)
    {
        T saved = currentValue;
        undo.Add(() => setter(saved));
        if (key != null) overridden.Add(key);
        setter(value);
    }

    void Enter(OperatingMode m)
    {
        AudioSonar s = sonar;
        s.toggleWithButtons = false;   // A/X больше не глушат звук молча: для этого есть явный режим «Тишина»
        SignReader sr = SignReader.Instance;
        DoorFinder df = DoorFinder.Instance;
        DoorVision dv = DoorVision.Instance;

        switch (m)
        {
            case OperatingMode.Navigation:
                break;

            case OperatingMode.DangerOnly:
                QuietSonar(s, false);
                VisionQuiet(sr, df, dv);
                break;

            case OperatingMode.Text:
                QuietSonar(s, false);
                if (df != null)
                {
                    Set(null, df.doorSound, false, v => df.doorSound = v);
                    Set(null, df.doorVoice, false, v => df.doorVoice = v);
                }
                if (dv != null) Set(null, dv.detectDoors, false, v => dv.detectDoors = v);   // камера и GPU для OCR
                if (sr != null)
                {
                    Set(null, sr.readSigns, true, v => sr.readSigns = v);
                    Set(null, sr.speakConfirmed, true, v => sr.speakConfirmed = v);
                    Set(null, sr.scanInterval, Mathf.Min(sr.scanInterval, textScanInterval), v => sr.scanInterval = v);
                    Set(null, sr.maxReadDistance, Mathf.Max(sr.maxReadDistance, textMaxReadDistance), v => sr.maxReadDistance = v);
                }
                break;

            case OperatingMode.Landmarks:
                QuietSonar(s, true);
                if (df != null)
                {
                    Set(null, df.doorSound, true, v => df.doorSound = v);
                    Set(null, df.doorVoice, true, v => df.doorVoice = v);
                }
                if (dv != null) Set(null, dv.detectDoors, true, v => dv.detectDoors = v);
                break;

            case OperatingMode.Narrow:
            {
                float maxR = Mathf.Max(Mathf.Min(s.maxRange, narrowMaxRange), s.dangerDistance + TuningModel.Gap);
                Set("pathHalfWidth", s.pathHalfWidth, Mathf.Min(s.pathHalfWidth, narrowHalfWidth), v => s.pathHalfWidth = v);
                Set("maxRange", s.maxRange, maxR, v => s.maxRange = v);
                Set("rangeSlow", s.rangeSlow, Mathf.Min(s.rangeSlow, maxR), v => s.rangeSlow = v);
                Set("rangeFast", s.rangeFast, Mathf.Min(s.rangeFast, maxR), v => s.rangeFast = v);
                Set("rangeFixed", s.rangeFixed, Mathf.Min(s.rangeFixed, maxR), v => s.rangeFixed = v);
                Set("sideRange", s.sideRange, Mathf.Min(s.sideRange, maxR), v => s.sideRange = v);
                Set(null, s.sideWarning, true, v => s.sideWarning = v);
                Set(null, s.sideVolume, Mathf.Max(s.sideVolume, narrowSideVolume), v => s.sideVolume = v);
                break;
            }

            case OperatingMode.Silence:
                if (silenceKeepStop)
                {
                    QuietSonar(s, false);
                    Set(null, s.dangerVolume, 0f, v => s.dangerVolume = v);   // «стоп» (contactVolume) и обрыв остаются
                }
                else Set(null, s.muted, true, v => s.SetMuted(v));
                VisionQuiet(sr, df, dv);
                break;

            case OperatingMode.Experiment:
                conditionBefore = AudioSonar.Mode;
                lastCondition = AudioSonar.Mode;
                if (experiment != null) experiment.enabled = true;
                if (experimentManualThresholds)
                {
                    Set(null, s.contactMin, s.contactDistance, v => s.contactMin = v);
                    Set(null, s.dangerMin, s.dangerDistance, v => s.dangerMin = v);
                    float need = s.dangerDistance + TuningModel.Gap;
                    if (s.speedAdaptiveRange && s.rangeSlow < need) Set(null, s.rangeSlow, Mathf.Min(need, s.maxRange), v => s.rangeSlow = v);
                    if (!s.speedAdaptiveRange && s.rangeFixed < need) Set(null, s.rangeFixed, Mathf.Min(need, s.maxRange), v => s.rangeFixed = v);
                }
                break;
        }
    }

    /// <summary>Сонар «только опасность»: дальние писки беззвучны, боковых полос нет, обрыв включён. Опасность и стоп не трогаются.</summary>
    void QuietSonar(AudioSonar s, bool landmarks)
    {
        Set(null, s.nearVolume, 0f, v => s.nearVolume = v);
        Set(null, s.farVolume, 0f, v => s.farVolume = v);
        Set(null, s.minInterval, Mathf.Max(s.minInterval, quietBeepInterval), v => s.minInterval = v);
        Set(null, s.maxInterval, Mathf.Max(s.maxInterval, quietBeepInterval), v => s.maxInterval = v);
        Set(null, s.sideWarning, false, v => s.sideWarning = v);
        Set(null, s.detectDropOff, true, v => s.detectDropOff = v);
        Set(null, s.landmarkCues, landmarks, v => s.landmarkCues = v);
    }

    /// <summary>Двери и таблички молчат и не распознаются в фоне (батарея). Правый хват SignReader остаётся.</summary>
    void VisionQuiet(SignReader sr, DoorFinder df, DoorVision dv)
    {
        if (df != null)
        {
            Set(null, df.doorSound, false, v => df.doorSound = v);
            Set(null, df.doorVoice, false, v => df.doorVoice = v);
        }
        if (dv != null) Set(null, dv.detectDoors, false, v => dv.detectDoors = v);
        if (sr != null)
        {
            Set(null, sr.readSigns, false, v => sr.readSigns = v);
            Set(null, sr.speakConfirmed, false, v => sr.speakConfirmed = v);
        }
    }

    /// <summary>Вне «Тишины» «опасность» и «стоп» должны быть слышны. Возвращает, что пришлось исправить.</summary>
    List<string> VerifySafety(OperatingMode m)
    {
        var fixes = new List<string>();
        if (m == OperatingMode.Silence || sonar == null) return fixes;
        AudioSonar s = sonar;
        if (s.muted) { s.SetMuted(false); fixes.Add("muted"); }
        if (m != OperatingMode.Experiment && AudioSonar.Mode == AudioSonar.FeedbackMode.Off)
        {
            AudioSonar.Mode = AudioSonar.FeedbackMode.Spatial;
            fixes.Add("feedback_off");
        }
        if (s.dangerVolume < 0.05f) { s.dangerVolume = 0.8f; fixes.Add("dangerVolume"); }
        if (s.contactVolume < 0.05f) { s.contactVolume = 0.9f; fixes.Add("contactVolume"); }
        if (s.masterVolume < minMasterVolume) { s.masterVolume = minMasterVolume; fixes.Add("masterVolume"); }
        if (!s.detectDropOff) { s.detectDropOff = true; fixes.Add("detectDropOff"); }
        return fixes;
    }

    // ---------------------------------------------------------------- голос, тон, вибрация
    void Announce(OperatingMode m, List<string> fixes)
    {
        string text = ModeVoice.T("Режим: ", "Mod: ", "Mode: ") + ModeName(m) + ". " + ModeHint(m);
        if (fixes.Count > 0)
            text += " " + ModeVoice.T("Опасность и стоп снова включены.", "Pericol și stop sunt din nou pornite.", "Danger and stop are back on.");
        ModeVoice.Say(text.Trim());
        if (tone && toneSrc != null) toneSrc.PlayOneShot(modeClips[(int)m], toneVolume);
        if (vibrate) StartCoroutine(ModeVoice.Buzz(sonar, m == OperatingMode.Silence ? 2 : 1));
    }

    void Refuse(string message)
    {
        if (tone && toneSrc != null) toneSrc.PlayOneShot(refuseClip, toneVolume);
        if (vibrate) StartCoroutine(ModeVoice.Buzz(sonar, 2, 0.08f));
        if (!string.IsNullOrEmpty(message)) ModeVoice.Say(message);
    }

    void SayStatus()
    {
        ModeVoice.Say((ModeVoice.T("Режим: ", "Mod: ", "Mode: ") + ModeName(current) + ". " + ModeHint(current)).Trim());
    }

    void ModeAction()
    {
        switch (current)
        {
            case OperatingMode.Text:
                if (SignReaderBridge.ReadNow(SignReader.Instance, out string problem))
                {
                    if (tone && toneSrc != null) toneSrc.PlayOneShot(actionClip, toneVolume);
                    ModeVoice.Say(ModeVoice.T("Читаю", "Citesc", "Reading"));
                }
                else Refuse(problem);
                break;
            case OperatingMode.Landmarks:
                SayNearestLandmark();
                break;
            default:
                SayStatus();
                break;
        }
    }

    /// <summary>«Ближайшая дверь справа, 3 метра»: по реестру SonarLandmarks (двери и окна, все направления).</summary>
    void SayNearestLandmark()
    {
        if (head == null) return;
        float now = Time.time;
        float memory = Mathf.Max(SonarLandmarks.ttl, landmarkMemorySec);
        Vector3 hp = head.position;
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
        f.Normalize();

        Landmark best = null;
        float bestD = landmarkQueryRange, bestYaw = 0f;
        foreach (Landmark lm in SonarLandmarks.All)
        {
            if (lm.kind == LandmarkKind.Text || !lm.IsFresh(now, memory)) continue;
            Vector3 rel = lm.worldPos - hp;
            rel.y = 0f;
            float d = rel.magnitude;
            if (d >= bestD) continue;
            best = lm;
            bestD = d;
            bestYaw = Vector3.SignedAngle(f, rel, Vector3.up);   // плюс = справа
        }

        if (best == null)
        {
            ModeVoice.Say(ModeVoice.T("Дверей и окон рядом не найдено", "Nu am găsit uși sau ferestre în apropiere", "No doors or windows nearby"));
            return;
        }
        if (tone && toneSrc != null) toneSrc.PlayOneShot(actionClip, toneVolume);
        string what = best.kind == LandmarkKind.Door
            ? ModeVoice.T("Ближайшая дверь", "Cea mai apropiată ușă", "Nearest door")
            : ModeVoice.T("Ближайшее окно", "Cea mai apropiată fereastră", "Nearest window");
        string dist = bestD < 1f ? ModeVoice.T("меньше метра", "sub un metru", "less than a meter")
                                 : ModeVoice.Meters(Mathf.Round(bestD * 2f) / 2f);
        ModeVoice.Say(what + " " + ModeVoice.Side(bestYaw) + ", " + dist);
    }

    string ModeHint(OperatingMode m)
    {
        switch (m)
        {
            case OperatingMode.DangerOnly:
                return ModeVoice.T("Только опасность, стоп и обрыв.", "Doar pericol, stop și treaptă.", "Only danger, stop and drop.");
            case OperatingMode.Text:
                return ModeVoice.T("Сонар: только опасность. Курок: прочитать текст.", "Sonar: doar pericol. Trăgaci: citește textul.", "Sonar: danger only. Trigger: read text.");
            case OperatingMode.Landmarks:
                return ModeVoice.T("Сонар: только опасность. Курок: ближайшая дверь.", "Sonar: doar pericol. Trăgaci: cea mai apropiată ușă.", "Sonar: danger only. Trigger: nearest door.");
            case OperatingMode.Silence:
                if (silenceKeepStop) return ModeVoice.T("Остаются только стоп и обрыв.", "Rămân doar stop și treaptă.", "Only stop and drop remain.");
                return ModeVoice.T("Внимание: опасность и стоп выключены.", "Atenție: pericol și stop sunt oprite.", "Warning: danger and stop are off.");
            case OperatingMode.Experiment:
                return ConditionText();
            default:
                return "";
        }
    }

    string ConditionText()
    {
        string letter = "ABCD"[Mathf.Clamp((int)AudioSonar.Mode, 0, 3)].ToString();
        string t = ModeVoice.T("Условие ", "Condiția ", "Condition ") + letter;
        if (AudioSonar.Mode == AudioSonar.FeedbackMode.Off)
            t += ModeVoice.T(", сонар выключен, только трость", ", sonar oprit, doar bastonul", ", sonar off, cane only");
        return t + ".";
    }

    /// <summary>Название режима на языке голоса.</summary>
    public static string ModeName(OperatingMode m)
    {
        switch (m)
        {
            case OperatingMode.Navigation: return ModeVoice.T("навигация", "navigare", "navigation");
            case OperatingMode.DangerOnly: return ModeVoice.T("только опасность", "doar pericol", "danger only");
            case OperatingMode.Text: return ModeVoice.T("текст", "text", "text");
            case OperatingMode.Landmarks: return ModeVoice.T("ориентиры", "repere", "landmarks");
            case OperatingMode.Narrow: return ModeVoice.T("узкий проход", "trecere îngustă", "narrow passage");
            case OperatingMode.Silence: return ModeVoice.T("тишина", "liniște", "silence");
            default: return ModeVoice.T("эксперимент", "experiment", "experiment");
        }
    }

    /// <summary>Название режима по-русски (для панели и Inspector).</summary>
    public static string ModeNameRu(OperatingMode m)
    {
        switch (m)
        {
            case OperatingMode.Navigation: return "навигация";
            case OperatingMode.DangerOnly: return "только опасность";
            case OperatingMode.Text: return "текст";
            case OperatingMode.Landmarks: return "ориентиры";
            case OperatingMode.Narrow: return "узкий проход";
            case OperatingMode.Silence: return "тишина";
            default: return "эксперимент";
        }
    }

    /// <summary>Метка ExperimentController (TextMesh у головы) видна только в «Эксперименте». GameObject.Find видит только активные объекты.</summary>
    void SyncExperimentLabel()
    {
        bool show = current == OperatingMode.Experiment;
        if (expLabel == null && !show) expLabel = GameObject.Find("ExperimentLabel");
        if (expLabel != null && expLabel.activeSelf != show) expLabel.SetActive(show);
    }

    void UpdateStatus()
    {
        status = "режим: " + ModeNameRu(current)
               + (current == OperatingMode.Experiment ? ", условие " + AudioSonar.Mode : "")
               + (experiment == null ? ", ExperimentController нет" : "");
    }
}
