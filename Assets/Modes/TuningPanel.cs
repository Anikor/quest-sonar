using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Окно ручной настройки порогов AudioSonar: панель в пространстве (TextMesh, ~1 м перед головой, в мировых координатах,
/// догоняет взгляд при повороте больше followAngle). Для зрячего помощника и для самого пользователя: каждое действие
/// произносится голосом (SpeechOutput), экран не обязателен.
///
/// Управление (только контроллеры):
///   X (левый контроллер)        - открыть / закрыть панель (в режиме «Эксперимент» закрыта: пороги фиксированы);
///   правый стик вверх / вниз     - выбрать параметр (произносится название и значение);
///   правый стик влево / вправо   - уменьшить / увеличить (удержание = автоповтор); переключатели: вправо = вкл, влево = выкл;
///   правый стик нажать           - значение по умолчанию для выбранного параметра;
///   «Сбросить всё»               - вправо два раза подряд (за 3 с).
/// Значения прижимаются к границам и связям (TuningModel), при нарушении произносится предупреждение.
/// Сохраняются в PlayerPrefs (ключи quest-sonar.tune.*), применяются при старте, записываются в thresholds.csv (ThresholdLog).
/// Параметры, которые сейчас задаёт режим (OperatingModeController.IsOverridden), не меняются: панель говорит «задано режимом».
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class TuningPanel : MonoBehaviour
{
    public static TuningPanel Instance { get; private set; }
    public const string PrefsPrefix = "quest-sonar.tune.";

    [Header("Управление")]
    [Tooltip("Кнопка X (OVRInput.Button.Three) открывает и закрывает панель")]
    public bool openWithX = true;
    public float stickThreshold = 0.7f;
    public float stickRelease = 0.35f;
    [Tooltip("Удержание стика влево/вправо: первый повтор через, сек")]
    public float repeatDelay = 0.5f;
    public float repeatInterval = 0.15f;

    [Header("Вид")]
    public float distance = 1.0f;
    public float heightOffset = 0.15f;
    [Tooltip("Панель переезжает перед взгляд, если голова повернулась дальше, градусы")]
    public float followAngle = 35f;
    public float characterSize = 0.0065f;

    [Header("Состояние (только чтение)")]
    public bool isOpen;
    public string selected = "";
    public string lastWarning = "";

    public TuningModel Model { get; private set; }
    public bool IsOpen { get { return isOpen; } }
    public bool ManualThresholds { get { return Model != null && Model.On(TuningModel.ManualKey); } }

    AudioSonar sonar;
    Transform head;
    ControllerVibrationOff vibOff;
    bool vibOffAtBoot;
    float autoContactMin, autoDangerMin;

    GameObject root;
    TextMesh text;
    int sel;
    bool yArmed = true;
    int heldDir;
    float nextRepeat, resetArmedUntil, lastChange, redrawTimer;
    bool dirty;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<TuningPanel>() == null)
            new GameObject("TuningPanel").AddComponent<TuningPanel>();
    }

    void Awake() { Instance = this; }

    void OnDestroy() { if (Instance == this) Instance = null; }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => FindFirstObjectByType<AudioSonar>() != null);
        sonar = FindFirstObjectByType<AudioSonar>();
        head = SonarCommon.FindHead();
        if (sonar == null) { Debug.LogError("TuningPanel: нет AudioSonar."); yield break; }

        // ControllerVibrationOff (просьба пользователя) каждые 2 с ставит haptics = false. Вибрация по умолчанию = как он решил;
        // если в панели включить вибрацию явно, его флажок снимается (иначе он выключал бы её снова).
        vibOff = FindFirstObjectByType<ControllerVibrationOff>();
        vibOffAtBoot = vibOff != null && vibOff.disableVibration;
        autoContactMin = sonar.contactMin;
        autoDangerMin = sonar.dangerMin;

        var m = new TuningModel();
        m.SetDefaultsFrom(sonar, sonar.haptics && !vibOffAtBoot);
        Model = m;
        Load();
        ApplyAll();
        ThresholdLog.Write(sonar, "boot");
        CreateView();
    }

    // ---------------------------------------------------------------- применение и хранение
    /// <summary>Записать базовые значения в AudioSonar (кроме тех, что сейчас задаёт режим).</summary>
    public void ApplyAll()
    {
        if (Model == null || sonar == null) return;
        var mc = OperatingModeController.Instance;
        System.Func<string, bool> skip = k => mc != null && mc.IsOverridden(k);
        Model.ApplyTo(sonar, skip, autoContactMin, autoDangerMin);
        if (vibOff != null && !skip("haptics"))
            vibOff.disableVibration = Model.On("haptics") ? false : vibOffAtBoot;
    }

    void Load()
    {
        foreach (var p in Model.Params)
        {
            if (p.kind == TuneKind.Action) continue;
            string k = PrefsPrefix + p.key;
            if (PlayerPrefs.HasKey(k)) Model.RawSet(p.key, PlayerPrefs.GetFloat(k));
        }
        List<string> fixedKeys = Model.Sanitize();
        if (fixedKeys.Count > 0) Debug.LogWarning("TuningPanel: сохранённые значения исправлены по ограничениям: " + string.Join(", ", fixedKeys.ToArray()));
    }

    void SaveAll()
    {
        foreach (var p in Model.Params)
            if (p.kind != TuneKind.Action) PlayerPrefs.SetFloat(PrefsPrefix + p.key, Model[p.key]);
    }

    void OnApplicationPause(bool paused) { if (paused && Model != null) { SaveAll(); PlayerPrefs.Save(); } }

    // ---------------------------------------------------------------- кнопки
    void Update()
    {
        if (Model == null || sonar == null) return;

        if (openWithX && OVRInput.GetDown(OVRInput.Button.Three)) Toggle();

        if (dirty && (!isOpen || Time.unscaledTime - lastChange > 3f))
        {
            dirty = false;
            ThresholdLog.Write(sonar, "tune");
        }
        if (!isOpen) return;

        HandleStick();
        Follow(false);
        redrawTimer -= Time.unscaledDeltaTime;
        if (redrawTimer <= 0f) Redraw();
    }

    /// <summary>Открыть или закрыть панель (тот же эффект, что кнопка X).</summary>
    public void Toggle()
    {
        if (!isOpen)
        {
            var mc = OperatingModeController.Instance;
            if (mc != null && mc.PanelLocked)
            {
                ModeVoice.Say(ModeVoice.T("Настройки закрыты в режиме эксперимент", "Setările sunt blocate în modul experiment", "Settings are locked in experiment mode"));
                return;
            }
            isOpen = true;
            if (root != null) root.SetActive(true);
            Follow(true);
            sel = Mathf.Clamp(sel, 0, Mathf.Max(0, VisibleParams().Count - 1));
            resetArmedUntil = 0f;
            ModeVoice.Say(ModeVoice.T("Настройки. ", "Setări. ", "Settings. ") + Describe(Current()));
            Redraw();
        }
        else
        {
            isOpen = false;
            if (root != null) root.SetActive(false);
            SaveAll();
            PlayerPrefs.Save();
            ModeVoice.Say(ModeVoice.T("Настройки закрыты", "Setări închise", "Settings closed"));
        }
    }

    void HandleStick()
    {
        Vector2 a = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
        float now = Time.unscaledTime;

        // вверх / вниз: один шаг на одно отклонение
        if (yArmed && Mathf.Abs(a.y) > stickThreshold && Mathf.Abs(a.y) > Mathf.Abs(a.x))
        {
            yArmed = false;
            Move(a.y > 0f ? -1 : 1);
        }
        else if (Mathf.Abs(a.y) < stickRelease) yArmed = true;

        // влево / вправо: с автоповтором для чисел
        int dir = Mathf.Abs(a.x) > stickThreshold && Mathf.Abs(a.x) > Mathf.Abs(a.y) ? (a.x > 0f ? 1 : -1) : 0;
        if (dir != 0 && dir != heldDir)
        {
            heldDir = dir;
            nextRepeat = now + repeatDelay;
            Change(dir, false);
        }
        else if (dir != 0 && now >= nextRepeat)
        {
            nextRepeat = now + repeatInterval;
            Change(dir, true);
        }
        else if (dir == 0 && Mathf.Abs(a.x) < stickRelease) heldDir = 0;

        if (OVRInput.GetDown(OVRInput.Button.SecondaryThumbstick)) ResetCurrent();
    }

    List<TuneParam> VisibleParams()
    {
        var list = new List<TuneParam>();
        foreach (var p in Model.Params) if (Model.Visible(p)) list.Add(p);
        return list;
    }

    TuneParam Current()
    {
        var list = VisibleParams();
        if (list.Count == 0) return null;
        sel = Mathf.Clamp(sel, 0, list.Count - 1);
        return list[sel];
    }

    void Move(int d)
    {
        var list = VisibleParams();
        if (list.Count == 0) return;
        sel = ((sel + d) % list.Count + list.Count) % list.Count;
        resetArmedUntil = 0f;
        ModeVoice.Say(Describe(list[sel]));
        Redraw();
    }

    string LockedBy(TuneParam p)
    {
        var mc = OperatingModeController.Instance;
        if (p == null || mc == null || !mc.IsOverridden(p.key)) return null;
        return ModeVoice.T("Сейчас задано режимом ", "Acum este setat de modul ", "Now set by mode ") + OperatingModeController.ModeName(mc.Current) + ".";
    }

    string Describe(TuneParam p)
    {
        if (p == null) return "";
        if (p.kind == TuneKind.Action) return p.Name + ". " + ModeVoice.T("Вправо два раза", "Dreapta de două ori", "Right twice");
        string lockMsg = LockedBy(p);
        float shown = lockMsg != null ? TuningModel.Read(sonar, p.key) : Model[p.key];
        return p.Name + ", " + TuningModel.ValueText(p, shown) + (lockMsg != null ? ". " + lockMsg : "");
    }

    void Change(int dir, bool repeat)
    {
        TuneParam p = Current();
        if (p == null) return;
        if (repeat && p.kind != TuneKind.Meters && p.kind != TuneKind.Percent) return;

        if (p.kind == TuneKind.Action)
        {
            if (dir < 0)
            {
                if (resetArmedUntil > 0f) ModeVoice.Say(ModeVoice.T("Отмена", "Anulat", "Cancelled"));
                resetArmedUntil = 0f;
                return;
            }
            if (Time.unscaledTime < resetArmedUntil) { ResetAll(); return; }
            resetArmedUntil = Time.unscaledTime + 3f;
            ModeVoice.Say(ModeVoice.T("Сбросить всё? Ещё раз вправо", "Resetez tot? Încă o dată dreapta", "Reset everything? Right once more"));
            return;
        }

        string lockMsg = LockedBy(p);
        if (lockMsg != null) { if (!repeat) ModeVoice.Say(lockMsg); return; }

        float before = Model[p.key];
        float wanted = p.kind == TuneKind.Toggle ? (dir > 0 ? 1f : 0f) : before + dir * p.step;
        Commit(p, wanted, null);
    }

    void ResetCurrent()
    {
        TuneParam p = Current();
        if (p == null || p.kind == TuneKind.Action) return;
        string lockMsg = LockedBy(p);
        if (lockMsg != null) { ModeVoice.Say(lockMsg); return; }
        Commit(p, p.def, ModeVoice.T("По умолчанию, ", "Implicit, ", "Default, "));
    }

    /// <summary>Записать значение через ограничения, применить к AudioSonar, сохранить и произнести.</summary>
    void Commit(TuneParam p, float wanted, string prefix)
    {
        float got = Model.Set(p.key, wanted, out string warning);
        if (p.key == "maxRange" && ObstacleMap.Instance != null && got > ObstacleMap.Instance.maxDistance + 1e-3f)
        {
            string w = ModeVoice.T("Карта видит только до ", "Harta vede doar până la ", "The map only sees up to ")
                     + ModeVoice.Meters(ObstacleMap.Instance.maxDistance) + ".";
            warning = warning == null ? w : warning + " " + w;
        }
        ApplyAll();
        SaveAll();
        dirty = true;
        lastChange = Time.unscaledTime;
        lastWarning = warning ?? "";
        ModeVoice.Say((prefix ?? "") + TuningModel.ValueText(p, got) + (warning != null ? ". " + warning : ""));
        Redraw();
    }

    /// <summary>Все параметры к значениям по умолчанию, сохранённые значения удаляются.</summary>
    public void ResetAll()
    {
        if (Model == null) return;
        resetArmedUntil = 0f;
        Model.ResetAllToDefaults();
        foreach (var p in Model.Params) PlayerPrefs.DeleteKey(PrefsPrefix + p.key);
        PlayerPrefs.Save();
        ApplyAll();
        lastWarning = "";
        dirty = false;
        ThresholdLog.Write(sonar, "reset_all");
        ModeVoice.Say(ModeVoice.T("Все параметры сброшены", "Toți parametrii au fost resetați", "All settings reset"));
        Redraw();
    }

    // ---------------------------------------------------------------- вид
    void CreateView()
    {
        root = new GameObject("TuningPanelView");
        var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "TuningPanelBackground";
        var col = bg.GetComponent<Collider>();
        if (col != null) Destroy(col);
        bg.transform.SetParent(root.transform, false);
        bg.transform.localPosition = new Vector3(0f, -0.31f, 0.01f);
        bg.transform.localScale = new Vector3(0.95f, 0.68f, 1f);
        var mat = SonarCommon.NewSpriteMaterial();
        mat.color = new Color(0f, 0f, 0f, 0.75f);
        bg.GetComponent<Renderer>().sharedMaterial = mat;

        var tg = new GameObject("TuningPanelText");
        tg.transform.SetParent(root.transform, false);
        text = tg.AddComponent<TextMesh>();
        text.characterSize = characterSize;
        text.fontSize = 48;
        text.anchor = TextAnchor.UpperCenter;
        text.alignment = TextAlignment.Left;
        text.richText = true;
        text.color = Color.white;
        root.SetActive(isOpen);
    }

    /// <summary>Ставит панель перед взглядом (по курсу, без наклона головы); force = сразу, иначе плавно, если голова отвернулась.</summary>
    void Follow(bool force)
    {
        if (root == null || head == null) return;
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-4f) return;
        f.Normalize();
        Vector3 target = head.position + f * distance + Vector3.up * heightOffset;
        Vector3 cur = root.transform.position - head.position;
        cur.y = 0f;
        if (!force && cur.sqrMagnitude > 1e-4f && Vector3.Angle(cur, f) < followAngle) return;
        float k = force ? 1f : Mathf.Clamp01(Time.unscaledDeltaTime * 4f);
        root.transform.position = Vector3.Lerp(root.transform.position, target, k);
        root.transform.rotation = Quaternion.Slerp(root.transform.rotation, Quaternion.LookRotation(f), k);
    }

    void Redraw()
    {
        redrawTimer = 0.25f;
        if (text == null || Model == null) return;
        var mc = OperatingModeController.Instance;
        var sb = new StringBuilder(1024);
        sb.Append("<b>Пороги сонара</b>   режим: ").Append(mc != null ? OperatingModeController.ModeNameRu(mc.Current) : "?").Append('\n');
        var list = VisibleParams();
        TuneParam cur = Current();
        selected = cur != null ? cur.key : "";
        foreach (var p in list)
        {
            bool isSel = p == cur;
            bool locked = mc != null && mc.IsOverridden(p.key);
            string line = p.ru;
            if (p.kind != TuneKind.Action)
            {
                float v = locked ? TuningModel.Read(sonar, p.key) : Model[p.key];
                line += ": " + TuningModel.DisplayText(p, v);
                if (locked) line += " (режим)";
                else if (Mathf.Abs(Model[p.key] - p.def) > 1e-3f) line += " *";
            }
            if (isSel) sb.Append("<color=yellow>> ").Append(line).Append("</color>\n");
            else if (locked) sb.Append("<color=#888888>   ").Append(line).Append("</color>\n");
            else sb.Append("   ").Append(line).Append('\n');
        }
        if (lastWarning.Length > 0) sb.Append("<color=orange>! ").Append(lastWarning).Append("</color>\n");
        sb.Append("<size=36>Правый стик: вверх/вниз выбор, влево/вправо значение,\nнажать = по умолчанию.  X = закрыть.  * = изменено</size>");
        text.text = sb.ToString();
    }
}
