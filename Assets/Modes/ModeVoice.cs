using System.Collections;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Общие мелочи режимов и панели настройки: фразы на языке голоса (ro / ru / en, как DoorFinder.DoorWord),
/// числа для речи, короткие тоны и вибрация-подтверждение. Сам по себе ничего не делает и в сцене не создаётся.
/// Язык берётся из SpeechOutput.Instance.language (первый язык, который принял движок TTS); пока голос не готов, фразы английские.
/// </summary>
public static class ModeVoice
{
    public static string Lang { get { return SpeechOutput.Instance != null ? SpeechOutput.Instance.language ?? "" : ""; } }

    /// <summary>Фраза на языке голоса.</summary>
    public static string T(string ru, string ro, string en)
    {
        string l = Lang;
        if (l.StartsWith("ro")) return ro;
        if (l.StartsWith("ru")) return ru;
        return en;
    }

    /// <summary>Голос готов произносить (на Android запустился хоть один движок; в редакторе всегда true).</summary>
    public static bool VoiceReady { get { return SpeechOutput.Instance != null && SpeechOutput.Instance.ready; } }

    /// <summary>Произнести сразу, оборвав текущую фразу (служебные сообщения должны звучать немедленно).</summary>
    public static void Say(string text) { SpeechOutput.Speak(text, true); }

    /// <summary>Число для речи: 0.9 → «0,9» (ru, ro) или «0.9» (en), без лишних нулей.</summary>
    public static string Num(float v)
    {
        string s = (Mathf.Round(v * 100f) / 100f).ToString("0.##", CultureInfo.InvariantCulture);
        return Lang.StartsWith("en") || Lang.Length == 0 ? s : s.Replace('.', ',');
    }

    /// <summary>«0,9 метра», «1 метр», «5 метров» / «0,9 metri» / «0.9 meters».</summary>
    public static string Meters(float v)
    {
        string n = Num(v);
        bool whole = Mathf.Abs(v - Mathf.Round(v)) < 0.005f;
        int i = Mathf.RoundToInt(v);
        string ru;
        if (!whole) ru = "метра";
        else if (i % 10 == 1 && i % 100 != 11) ru = "метр";
        else if (i % 10 >= 2 && i % 10 <= 4 && (i % 100 < 12 || i % 100 > 14)) ru = "метра";
        else ru = "метров";
        return T(n + " " + ru, n + (whole && i == 1 ? " metru" : " metri"), n + (whole && i == 1 ? " meter" : " meters"));
    }

    public static string Percent(float v01)
    {
        int p = Mathf.RoundToInt(v01 * 100f);
        return T(p + " процентов", p + " la sută", p + " percent");
    }

    public static string OnOff(bool on) { return on ? T("включено", "pornit", "on") : T("выключено", "oprit", "off"); }

    /// <summary>Сторона по углу от курса, градусы (минус = слева).</summary>
    public static string Side(float yawDeg)
    {
        float a = Mathf.Abs(yawDeg);
        if (a > 120f) return T("сзади", "în spate", "behind");
        if (a <= 20f) return T("впереди", "în față", "ahead");
        return yawDeg < 0f ? T("слева", "la stânga", "on the left") : T("справа", "la dreapta", "on the right");
    }

    // ---------------------------------------------------------------- тоны и вибрация
    const int SampleRate = 48000;

    /// <summary>Серия коротких одинаковых нот: count штук по len с паузой gap. Средние частоты, ничего общего со «стоп» 1400 Гц.</summary>
    public static AudioClip MakePips(string name, float freq, int count, float len = 0.05f, float gap = 0.07f)
    {
        int noteN = (int)(SampleRate * len);
        int per = (int)(SampleRate * (len + gap));
        var data = new float[per * Mathf.Max(1, count)];
        for (int j = 0; j < count; j++)
        {
            for (int i = 0; i < noteN; i++)
            {
                float t = i / (float)SampleRate;
                float a = Mathf.Clamp01(t / 0.004f);
                float tail = Mathf.Clamp01((len - t) / 0.012f);
                float w = 2f * Mathf.PI * freq * t;
                data[j * per + i] = (Mathf.Sin(w) + 0.25f * Mathf.Sin(2f * w)) * 0.5f * a * tail;
            }
        }
        var c = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        c.SetData(data, 0);
        return c;
    }

    /// <summary>Обычный (не пространственный) источник для подтверждающих тонов.</summary>
    public static AudioSource MakeUiSource(string name)
    {
        var go = new GameObject(name);
        var a = go.AddComponent<AudioSource>();
        a.playOnAwake = false;
        a.loop = false;
        a.spatialBlend = 0f;
        a.spatialize = false;
        a.dopplerLevel = 0f;
        return a;
    }

    /// <summary>
    /// Короткая вибрация обоих контроллеров: pulses импульсов по pulseSec. Если AudioSonar уже вибрирует из-за опасности,
    /// не вмешивается (иначе выключение импульса погасило бы его вибрацию: AudioSonar шлёт команду только при смене состояния).
    /// </summary>
    public static IEnumerator Buzz(AudioSonar sonar, int pulses, float pulseSec = 0.12f, float amplitude = 0.6f)
    {
        for (int i = 0; i < pulses; i++)
        {
            if (sonar != null && sonar.haptics && sonar.InDanger) yield break;
            OVRInput.SetControllerVibration(0.5f, amplitude, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0.5f, amplitude, OVRInput.Controller.RTouch);
            yield return new WaitForSecondsRealtime(pulseSec);
            if (sonar != null && sonar.haptics && sonar.InDanger) yield break;
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            if (i + 1 < pulses) yield return new WaitForSecondsRealtime(pulseSec);
        }
    }
}
