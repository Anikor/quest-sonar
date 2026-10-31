using UnityEditor;
using UnityEngine;

// Проверка без шлема (временный проект, см. tools/unity-check/README.md): чистая логика панели порогов (TuningModel).
// Запуск: Unity.exe -batchmode -nographics -projectPath <dir> -executeMethod ModesCheck.Run -quit -logFile <log>
// Результат - строки MODESCHECK:, последняя "OK DONE"; код выхода 3 при провале.
// Кнопки, голос, вибрация, панель и сами режимы здесь НЕ проверяются (нужен шлем).
public static class ModesCheck
{
    static int fails;
    const float E = 1e-3f;

    static void Check(bool ok, string what)
    {
        Debug.Log((ok ? "MODESCHECK: OK   " : "MODESCHECK: FAIL ") + what);
        if (!ok) fails++;
    }

    // Копия формулы AudioSonar.UpdateThresholds() (метод закрытый): проверяем, что ручные пороги дают фиксированные значения.
    static void Thresholds(AudioSonar s, float range, out float contactNow, out float dangerNow)
    {
        contactNow = Mathf.Clamp(range * s.contactFraction, s.contactMin, s.contactDistance);
        dangerNow = Mathf.Clamp(range * s.dangerFraction, s.dangerMin, s.dangerDistance);
        if (dangerNow < contactNow + 0.08f) dangerNow = contactNow + 0.08f;
    }

    public static void Run()
    {
        fails = 0;
        var go = new GameObject("ModesCheckSonar");
        var s = go.AddComponent<AudioSonar>();
        float autoContactMin = s.contactMin, autoDangerMin = s.dangerMin;

        var m = new TuningModel();
        m.SetDefaultsFrom(s, true);
        Check(Mathf.Abs(m["dangerDistance"] - s.dangerDistance) < E && Mathf.Abs(m["maxRange"] - s.maxRange) < E,
              $"значения по умолчанию взяты из AudioSonar (danger {m["dangerDistance"]}, maxRange {m["maxRange"]})");
        Check(ModeVoice.T("ru", "ro", "en") == "en", "без SpeechOutput фразы английские");

        // 1. стоп не ближе чем на Gap к опасности
        float got = m.Set("contactDistance", 0.95f, out string warn);
        Check(got <= m["dangerDistance"] - TuningModel.Gap + E && warn != null, $"стоп прижат: {got} при опасности {m["dangerDistance"]}, «{warn}»");

        // 2. опасность не ниже стоп + Gap
        got = m.Set("dangerDistance", 0.1f, out warn);
        Check(got >= m["contactDistance"] + TuningModel.Gap - E && warn != null, $"опасность прижата снизу: {got}, «{warn}»");

        // 3. опасность не выше maxRange − Gap
        m.Set("maxRange", 1.0f, out warn);   // упрётся в rangeFast 2.0
        Check(Mathf.Abs(m["maxRange"] - m["rangeFast"]) < E && warn != null, $"дальность не ниже дальности в ходьбе: {m["maxRange"]}, «{warn}»");
        m.Set("rangeFast", 0.6f, out warn);
        m.Set("sideRange", 0.6f, out warn);
        m.Set("contactDistance", 0.2f, out warn);
        m.Set("maxRange", 1.0f, out warn);
        got = m.Set("dangerDistance", 2.0f, out warn);
        Check(got <= m["maxRange"] - TuningModel.Gap + E && warn != null, $"опасность ≤ maxRange − 0.1: {got} при {m["maxRange"]}");

        // 4. боковая дальность не больше maxRange
        got = m.Set("sideRange", 3.0f, out warn);
        Check(got <= m["maxRange"] + E && warn != null, $"боковая ≤ maxRange: {got}");

        // 5. громкость не ниже 20 %
        got = m.Set("masterVolume", 0f, out warn);
        Check(got >= 0.2f - E, $"громкость не ниже 20 %: {got}");

        // 6. ручные пороги поднимают дальность стоя до опасность + Gap
        m.ResetAllToDefaults();
        m.Set(TuningModel.ManualKey, 1f, out warn);
        Check(m["rangeSlow"] >= m["dangerDistance"] + TuningModel.Gap - E && warn != null,
              $"ручные пороги: дальность стоя {m["rangeSlow"]} ≥ опасность {m["dangerDistance"]} + 0.1, «{warn}»");
        got = m.Set("rangeSlow", 0.3f, out warn);
        Check(got >= m["dangerDistance"] + TuningModel.Gap - E && warn != null, $"ручные пороги: дальность стоя не опускается ниже: {got}");

        // 7. ApplyTo: ручные пороги = фиксированные значения при любой дальности
        m.ApplyTo(s, null, autoContactMin, autoDangerMin);
        Thresholds(s, 0.5f, out float c1, out float d1);
        Thresholds(s, 3.0f, out float c2, out float d2);
        Check(Mathf.Abs(c1 - s.contactDistance) < E && Mathf.Abs(c2 - s.contactDistance) < E &&
              Mathf.Abs(d1 - s.dangerDistance) < E && Mathf.Abs(d2 - s.dangerDistance) < E,
              $"ручные пороги: стоп {c1}/{c2}, опасность {d1}/{d2} при дальности 0.5 и 3 м");

        // 8. выключение ручных порогов возвращает масштабирование
        m.Set(TuningModel.ManualKey, 0f, out warn);
        m.ApplyTo(s, null, autoContactMin, autoDangerMin);
        Thresholds(s, 0.5f, out c1, out d1);
        Check(Mathf.Abs(s.contactMin - autoContactMin) < E && d1 < s.dangerDistance - E, $"авто пороги: при дальности 0.5 опасность {d1} < {s.dangerDistance}");

        // 9. пропуск параметров, которые задаёт режим
        s.pathHalfWidth = 0.25f;
        m.ApplyTo(s, k => k == "pathHalfWidth", autoContactMin, autoDangerMin);
        Check(Mathf.Abs(s.pathHalfWidth - 0.25f) < E, "ApplyTo не трогает параметр режима");

        // 10. испорченные сохранённые значения исправляются
        m.RawSet("contactDistance", 1.5f);
        m.RawSet("dangerDistance", 0.5f);
        m.RawSet("maxRange", 0.2f);
        m.RawSet("sideRange", float.NaN);
        m.Sanitize();
        Check(m["contactDistance"] + TuningModel.Gap <= m["dangerDistance"] + E && m["dangerDistance"] + TuningModel.Gap <= m["maxRange"] + E &&
              m["sideRange"] <= m["maxRange"] + E && !float.IsNaN(m["sideRange"]),
              $"Sanitize: стоп {m["contactDistance"]}, опасность {m["dangerDistance"]}, дальность {m["maxRange"]}, бок {m["sideRange"]}");

        // 11. сброс
        m.ResetAllToDefaults();
        bool allDef = true;
        foreach (var p in m.Params) if (p.kind != TuneKind.Action && Mathf.Abs(m[p.key] - p.def) > E) allDef = false;
        Check(allDef, "сброс возвращает значения по умолчанию");

        Object.DestroyImmediate(go);

        if (fails > 0)
        {
            Debug.LogError($"MODESCHECK: FAILED ({fails})");
            EditorApplication.Exit(3);
        }
        else
        {
            Debug.Log("MODESCHECK: OK DONE");
            EditorApplication.Exit(0);
        }
    }
}
