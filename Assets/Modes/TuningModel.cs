using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Вид параметра панели: расстояние в метрах, доля 0..1 (громкость), переключатель, действие.</summary>
public enum TuneKind { Meters, Percent, Toggle, Action }

/// <summary>Один параметр панели настройки: границы, шаг, значение по умолчанию и названия (ru для экрана, ro/ru/en для голоса).</summary>
public sealed class TuneParam
{
    public string key;          // имя поля AudioSonar (или служебное: manualThresholds, resetAll)
    public string ru, ro, en;
    public TuneKind kind;
    public float min, max, step, def;
    /// <summary>0 = виден всегда, 1 = только при автодальности (speedAdaptiveRange), 2 = только без неё.</summary>
    public int showIf;

    public string Name { get { return ModeVoice.T(ru, ro, en); } }
}

/// <summary>
/// Чистая логика панели порогов (без кнопок и экрана, поэтому проверяется тестом Assets/Modes/Editor/ModesCheck.cs).
/// Хранит «базовые» значения параметров AudioSonar, прижимает их к границам и к связям между ними:
///   contactDistance + Gap ≤ dangerDistance ≤ maxRange − Gap,   sideRange ≤ maxRange,
///   rangeSlow ≤ rangeFast ≤ maxRange,   rangeFixed ≤ maxRange,
///   при ручных порогах ещё: rangeSlow (или rangeFixed без автодальности) ≥ dangerDistance + Gap,
///   иначе стоя на месте дальность меньше порога опасности и «опасность» на этом пороге не включится
///   (AudioSonar.OnHit отбрасывает точки дальше текущей дальности).
/// Ручные пороги не требуют правки AudioSonar: UpdateThresholds() считает
///   contactNow = Clamp(дальность × contactFraction, contactMin, contactDistance),
///   dangerNow  = Clamp(дальность × dangerFraction,  dangerMin,  dangerDistance);
/// если contactMin = contactDistance и dangerMin = dangerDistance, оба Clamp дают ровно эти значения при любой дальности.
/// </summary>
public sealed class TuningModel
{
    public const float Gap = 0.1f;
    public const string ManualKey = "manualThresholds";
    public const string ResetKey = "resetAll";
    const float Eps = 1e-4f;

    public readonly List<TuneParam> Params = new List<TuneParam>();
    readonly Dictionary<string, TuneParam> byKey = new Dictionary<string, TuneParam>();
    readonly Dictionary<string, float> val = new Dictionary<string, float>();

    public TuningModel()
    {
        // Значения по умолчанию здесь = инициализаторы полей AudioSonar (ветка sound, 2026-10-30). На шлеме они
        // заменяются фактическими значениями объекта при старте (SetDefaultsFrom), так что правки коллеги не теряются.
        Add(ManualKey, "Ручные пороги", "Praguri manuale", "Manual thresholds", TuneKind.Toggle, 0f, 1f, 1f, 0f);
        Add("dangerDistance", "Опасность", "Pericol", "Danger", TuneKind.Meters, 0.3f, 2.0f, 0.05f, 0.9f);
        Add("contactDistance", "Стоп", "Stop", "Stop", TuneKind.Meters, 0.2f, 1.0f, 0.05f, 0.4f);
        Add("speedAdaptiveRange", "Автодальность", "Rază automată", "Auto range", TuneKind.Toggle, 0f, 1f, 1f, 1f);
        Add("maxRange", "Дальность макс.", "Rază maximă", "Max range", TuneKind.Meters, 1.0f, 6.0f, 0.25f, 3.0f);
        Add("rangeSlow", "Дальность стоя", "Rază pe loc", "Range standing", TuneKind.Meters, 0.3f, 3.0f, 0.1f, 0.5f, 1);
        Add("rangeFast", "Дальность в ходьбе", "Rază în mers", "Range walking", TuneKind.Meters, 0.5f, 6.0f, 0.25f, 2.0f, 1);
        Add("rangeFixed", "Дальность фикс.", "Rază fixă", "Fixed range", TuneKind.Meters, 0.5f, 6.0f, 0.25f, 1.8f, 2);
        Add("sideRange", "Боковая дальность", "Rază laterală", "Side range", TuneKind.Meters, 0.3f, 3.0f, 0.1f, 1.0f);
        Add("pathHalfWidth", "Полуширина пути", "Semilățimea căii", "Path half width", TuneKind.Meters, 0.2f, 0.8f, 0.05f, 0.4f);
        Add("dropDepth", "Глубина обрыва", "Adâncime treaptă", "Drop depth", TuneKind.Meters, 0.1f, 0.6f, 0.05f, 0.3f);
        Add("landmarkRange", "Дальность ориентиров", "Rază repere", "Landmark range", TuneKind.Meters, 1.0f, 6.0f, 0.5f, 3.0f);
        // громкость не ниже 20 %: при 0 молчат и «опасность», и «стоп» (masterVolume умножает все звуки AudioSonar)
        Add("masterVolume", "Громкость", "Volum", "Volume", TuneKind.Percent, 0.2f, 1.0f, 0.1f, 1.0f);
        Add("haptics", "Вибрация", "Vibrație", "Vibration", TuneKind.Toggle, 0f, 1f, 1f, 1f);
        Add(ResetKey, "Сбросить всё", "Resetează tot", "Reset all", TuneKind.Action, 0f, 0f, 0f, 0f);
    }

    void Add(string key, string ru, string ro, string en, TuneKind kind, float min, float max, float step, float def, int showIf = 0)
    {
        var p = new TuneParam { key = key, ru = ru, ro = ro, en = en, kind = kind, min = min, max = max, step = step, def = def, showIf = showIf };
        Params.Add(p);
        byKey[key] = p;
        val[key] = def;
    }

    public TuneParam Find(string key) { return key != null && byKey.TryGetValue(key, out TuneParam p) ? p : null; }

    public float this[string key] { get { return key != null && val.TryGetValue(key, out float v) ? v : 0f; } }

    public bool On(string key) { return this[key] > 0.5f; }

    public bool Visible(TuneParam p)
    {
        if (p.showIf == 1) return On("speedAdaptiveRange");
        if (p.showIf == 2) return !On("speedAdaptiveRange");
        return true;
    }

    /// <summary>Значения по умолчанию = текущие значения полей AudioSonar (до применения сохранённых). Границы расширяются, если значение за ними.</summary>
    public void SetDefaultsFrom(AudioSonar s, bool hapticsDefault)
    {
        foreach (var p in Params)
        {
            if (p.kind == TuneKind.Action || p.key == ManualKey) continue;
            float v = p.key == "haptics" ? (hapticsDefault ? 1f : 0f) : Read(s, p.key);
            if (float.IsNaN(v)) continue;
            p.def = v;
            if (p.kind != TuneKind.Toggle)
            {
                p.min = Mathf.Min(p.min, v);
                p.max = Mathf.Max(p.max, v);
            }
        }
        ResetAllToDefaults();
    }

    public void ResetAllToDefaults()
    {
        foreach (var p in Params) val[p.key] = p.def;
        Sanitize();
    }

    /// <summary>Записать значение без проверок (загрузка из PlayerPrefs). После серии RawSet вызвать Sanitize.</summary>
    public void RawSet(string key, float v) { if (key != null && byKey.ContainsKey(key)) val[key] = v; }

    static float R(float v) { return Mathf.Round(v * 100f) / 100f; }

    /// <summary>
    /// Пользователь меняет параметр: значение прижимается к собственным границам и к связям с другими параметрами
    /// (другие параметры не трогаются, кроме переключателей «ручные пороги» и «автодальность»: там зависимые
    /// дальности поднимаются автоматически, иначе переключатель нельзя было бы включить). warning = что ограничило
    /// (на языке голоса) или null. Возвращает записанное значение.
    /// </summary>
    public float Set(string key, float wanted, out string warning)
    {
        warning = null;
        TuneParam p = Find(key);
        if (p == null || p.kind == TuneKind.Action) return 0f;

        if (p.kind == TuneKind.Toggle)
        {
            val[key] = wanted > 0.5f ? 1f : 0f;
            List<string> changed = Sanitize();
            changed.Remove(key);
            if (changed.Count > 0)
            {
                var parts = new List<string>();
                foreach (string k in changed)
                {
                    TuneParam c = Find(k);
                    parts.Add(c.Name + " " + ValueText(c, val[k]));
                }
                warning = ModeVoice.T("Изменено: ", "Modificat: ", "Changed: ") + string.Join(", ", parts.ToArray()) + ".";
            }
            return val[key];
        }

        float v = R(Mathf.Clamp(wanted, p.min, p.max));
        bool ownLimit = Mathf.Abs(v - wanted) > 0.005f;
        Bounds(key, out float lo, out string loWhy, out float hi, out string hiWhy);
        if (v < lo - Eps)
        {
            v = R(lo);
            warning = LimitText(true, p, v, loWhy);
        }
        else if (v > hi + Eps)
        {
            v = R(hi);
            warning = LimitText(false, p, v, hiWhy);
        }
        else if (ownLimit)
            warning = ModeVoice.T("Это предел.", "Aceasta este limita.", "That is the limit.");
        val[key] = v;
        return v;
    }

    string LimitText(bool lower, TuneParam p, float v, string whyKey)
    {
        TuneParam why = Find(whyKey);
        string vt = ValueText(p, v);
        if (why == null)
            return lower ? ModeVoice.T("Не меньше " + vt + ".", "Cel puțin " + vt + ".", "At least " + vt + ".")
                         : ModeVoice.T("Не больше " + vt + ".", "Cel mult " + vt + ".", "At most " + vt + ".");
        string w = why.Name;
        return lower ? ModeVoice.T("Ограничение: не меньше " + vt + " из-за параметра " + w + ".",
                                   "Limită: cel puțin " + vt + " din cauza parametrului " + w + ".",
                                   "Limit: at least " + vt + " because of " + w + ".")
                     : ModeVoice.T("Ограничение: не больше " + vt + " из-за параметра " + w + ".",
                                   "Limită: cel mult " + vt + " din cauza parametrului " + w + ".",
                                   "Limit: at most " + vt + " because of " + w + ".");
    }

    /// <summary>Допустимый диапазон параметра при текущих значениях остальных; loWhy/hiWhy = какой параметр задал границу (null = собственная граница).</summary>
    public void Bounds(string key, out float lo, out string loWhy, out float hi, out string hiWhy)
    {
        TuneParam p = Find(key);
        lo = p != null ? p.min : float.MinValue;
        hi = p != null ? p.max : float.MaxValue;
        loWhy = null; hiWhy = null;
        bool manual = On(ManualKey), adaptive = On("speedAdaptiveRange");
        float danger = this["dangerDistance"], contact = this["contactDistance"], maxR = this["maxRange"];

        switch (key)
        {
            case "contactDistance":
                Lower(ref hi, ref hiWhy, danger - Gap, "dangerDistance");
                break;
            case "dangerDistance":
                Raise(ref lo, ref loWhy, contact + Gap, "contactDistance");
                Lower(ref hi, ref hiWhy, maxR - Gap, "maxRange");
                if (manual)
                {
                    if (adaptive) Lower(ref hi, ref hiWhy, this["rangeSlow"] - Gap, "rangeSlow");
                    else Lower(ref hi, ref hiWhy, this["rangeFixed"] - Gap, "rangeFixed");
                }
                break;
            case "maxRange":
                Raise(ref lo, ref loWhy, danger + Gap, "dangerDistance");
                Raise(ref lo, ref loWhy, this["sideRange"], "sideRange");
                if (adaptive)
                {
                    Raise(ref lo, ref loWhy, this["rangeFast"], "rangeFast");
                    Raise(ref lo, ref loWhy, this["rangeSlow"], "rangeSlow");
                }
                else Raise(ref lo, ref loWhy, this["rangeFixed"], "rangeFixed");
                break;
            case "rangeSlow":
                if (manual && adaptive) Raise(ref lo, ref loWhy, danger + Gap, "dangerDistance");
                Lower(ref hi, ref hiWhy, this["rangeFast"], "rangeFast");
                break;
            case "rangeFast":
                Raise(ref lo, ref loWhy, this["rangeSlow"], "rangeSlow");
                Lower(ref hi, ref hiWhy, maxR, "maxRange");
                break;
            case "rangeFixed":
                if (manual && !adaptive) Raise(ref lo, ref loWhy, danger + Gap, "dangerDistance");
                Lower(ref hi, ref hiWhy, maxR, "maxRange");
                break;
            case "sideRange":
                Lower(ref hi, ref hiWhy, maxR, "maxRange");
                break;
        }
    }

    static void Raise(ref float lo, ref string why, float v, string key) { if (v > lo + Eps) { lo = v; why = key; } }
    static void Lower(ref float hi, ref string why, float v, string key) { if (v < hi - Eps) { hi = v; why = key; } }

    /// <summary>
    /// Приводит все значения к границам и связям (после загрузки, сброса, переключателей). Порядок: собственные границы,
    /// дальности под maxRange, опасность и стоп, затем подъём дальностей под ручные пороги. Возвращает ключи, которые изменились.
    /// </summary>
    public List<string> Sanitize()
    {
        var before = new Dictionary<string, float>(val);
        foreach (var p in Params)
        {
            if (p.kind == TuneKind.Action) continue;
            float v = val[p.key];
            if (float.IsNaN(v) || float.IsInfinity(v)) v = p.def;
            val[p.key] = p.kind == TuneKind.Toggle ? (v > 0.5f ? 1f : 0f) : R(Mathf.Clamp(v, p.min, p.max));
        }

        float maxR = val["maxRange"];
        val["rangeFast"] = Mathf.Min(val["rangeFast"], maxR);
        val["rangeSlow"] = Mathf.Min(val["rangeSlow"], val["rangeFast"]);
        val["rangeFixed"] = Mathf.Min(val["rangeFixed"], maxR);
        val["sideRange"] = Mathf.Min(val["sideRange"], maxR);

        TuneParam pd = byKey["dangerDistance"], pc = byKey["contactDistance"];
        float danger = Mathf.Clamp(val["dangerDistance"], Mathf.Max(pd.min, pc.min + Gap), maxR - Gap);
        float contact = Mathf.Clamp(val["contactDistance"], pc.min, Mathf.Max(pc.min, danger - Gap));
        if (danger < contact + Gap) danger = contact + Gap;
        val["dangerDistance"] = R(danger);
        val["contactDistance"] = R(contact);

        if (On(ManualKey))
        {
            float need = R(val["dangerDistance"] + Gap);
            if (On("speedAdaptiveRange"))
            {
                val["rangeSlow"] = Mathf.Max(val["rangeSlow"], need);
                val["rangeFast"] = Mathf.Max(val["rangeFast"], val["rangeSlow"]);
            }
            else val["rangeFixed"] = Mathf.Max(val["rangeFixed"], need);
        }

        var changed = new List<string>();
        foreach (var p in Params)
            if (p.kind != TuneKind.Action && Mathf.Abs(before[p.key] - val[p.key]) > Eps) changed.Add(p.key);
        return changed;
    }

    /// <summary>Значение для голоса: «0,9 метра», «80 процентов», «включено».</summary>
    public static string ValueText(TuneParam p, float v)
    {
        switch (p.kind)
        {
            case TuneKind.Meters: return ModeVoice.Meters(v);
            case TuneKind.Percent: return ModeVoice.Percent(v);
            case TuneKind.Toggle: return ModeVoice.OnOff(v > 0.5f);
            default: return "";
        }
    }

    /// <summary>Значение для панели (по-русски, коротко).</summary>
    public static string DisplayText(TuneParam p, float v)
    {
        switch (p.kind)
        {
            case TuneKind.Meters: return v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " м";
            case TuneKind.Percent: return Mathf.RoundToInt(v * 100f) + " %";
            case TuneKind.Toggle: return v > 0.5f ? "вкл" : "выкл";
            default: return "";
        }
    }

    // ---------------------------------------------------------------- связь с AudioSonar (только публичные поля)
    /// <summary>Текущее значение поля AudioSonar по ключу; NaN для служебных ключей.</summary>
    public static float Read(AudioSonar s, string key)
    {
        switch (key)
        {
            case "dangerDistance": return s.dangerDistance;
            case "contactDistance": return s.contactDistance;
            case "speedAdaptiveRange": return s.speedAdaptiveRange ? 1f : 0f;
            case "maxRange": return s.maxRange;
            case "rangeSlow": return s.rangeSlow;
            case "rangeFast": return s.rangeFast;
            case "rangeFixed": return s.rangeFixed;
            case "sideRange": return s.sideRange;
            case "pathHalfWidth": return s.pathHalfWidth;
            case "dropDepth": return s.dropDepth;
            case "landmarkRange": return s.landmarkRange;
            case "masterVolume": return s.masterVolume;
            case "haptics": return s.haptics ? 1f : 0f;
            default: return float.NaN;
        }
    }

    public static void Write(AudioSonar s, string key, float v)
    {
        switch (key)
        {
            case "dangerDistance": s.dangerDistance = v; break;
            case "contactDistance": s.contactDistance = v; break;
            case "speedAdaptiveRange": s.speedAdaptiveRange = v > 0.5f; break;
            case "maxRange": s.maxRange = v; break;
            case "rangeSlow": s.rangeSlow = v; break;
            case "rangeFast": s.rangeFast = v; break;
            case "rangeFixed": s.rangeFixed = v; break;
            case "sideRange": s.sideRange = v; break;
            case "pathHalfWidth": s.pathHalfWidth = v; break;
            case "dropDepth": s.dropDepth = v; break;
            case "landmarkRange": s.landmarkRange = v; break;
            case "masterVolume": s.masterVolume = v; break;
            case "haptics": s.haptics = v > 0.5f; break;
        }
    }

    /// <summary>
    /// Записывает базовые значения в AudioSonar. skip(key) = true для параметров, которые сейчас задаёт режим
    /// (OperatingModeController). Ручные пороги: contactMin/dangerMin = contactDistance/dangerDistance, иначе
    /// исходные autoContactMin/autoDangerMin (запомненные при старте). AudioSonar подхватывает всё в следующем кадре:
    /// UpdateThresholds() вызывается каждый кадр из UpdateSpeedAndRange.
    /// </summary>
    public void ApplyTo(AudioSonar s, Func<string, bool> skip, float autoContactMin, float autoDangerMin)
    {
        if (s == null) return;
        foreach (var p in Params)
        {
            if (p.kind == TuneKind.Action || p.key == ManualKey) continue;
            if (skip != null && skip(p.key)) continue;
            Write(s, p.key, val[p.key]);
        }
        if (skip != null && skip(ManualKey)) return;
        if (On(ManualKey))
        {
            s.contactMin = s.contactDistance;
            s.dangerMin = s.dangerDistance;
        }
        else
        {
            s.contactMin = Mathf.Min(autoContactMin, s.contactDistance);
            s.dangerMin = Mathf.Min(autoDangerMin, s.dangerDistance);
        }
    }
}
