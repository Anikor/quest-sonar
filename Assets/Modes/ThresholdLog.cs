using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Пороги сонара рядом с журналом эксперимента: persistentDataPath/thresholds.csv (тот же каталог, что trials.csv
/// у ExperimentController; его код не трогается). Строка пишется при старте, после правки в панели настройки
/// (при закрытии панели или через 3 с после последней правки), при сбросе, при каждой смене режима, при смене условия
/// эксперимента и в момент конца попытки (правый курок в режиме «Эксперимент» - тот же кадр, в котором
/// ExperimentController пишет строку в trials.csv). Связь с trials.csv: по participant и timestamp (одинаковый формат "s").
/// Пишутся ФАКТИЧЕСКИЕ значения полей AudioSonar (с учётом режима), поэтому замер можно повторить.
/// </summary>
public static class ThresholdLog
{
    public const string FileName = "thresholds.csv";
    public static bool enabled = true;

    public const string Header =
        "timestamp,participant,event,op_mode,feedback_mode,manual_thresholds,speed_adaptive," +
        "danger_distance,contact_distance,danger_min,contact_min,danger_fraction,contact_fraction," +
        "max_range,range_slow,range_fast,range_fixed,current_range,side_range,path_half_width," +
        "drop_depth,detect_drop,landmark_range,landmark_cues,master_volume,haptics,muted," +
        "near_volume,far_volume,danger_volume,contact_volume,side_warning,side_volume";

    static bool headerChecked;

    public static string CsvPath { get { return Path.Combine(Application.persistentDataPath, FileName); } }

    /// <summary>Записать строку с текущими значениями AudioSonar. evt: boot, tune, reset_all, mode, condition, trial_end.</summary>
    public static void Write(AudioSonar s, string evt)
    {
        if (!enabled || s == null) return;
        try
        {
            string path = CsvPath;
            EnsureHeader(path);
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(400);
            string mode = OperatingModeController.Instance != null ? OperatingModeController.Instance.Current.ToString() : "";
            // ручные пороги по факту: Clamp(..., min, max) с min = max даёт фиксированное значение
            bool manual = s.dangerMin >= s.dangerDistance - 1e-4f && s.contactMin >= s.contactDistance - 1e-4f;

            sb.Append(DateTime.Now.ToString("s", ci)).Append(',');
            sb.Append(Participant()).Append(',');
            sb.Append(evt).Append(',');
            sb.Append(mode).Append(',');
            sb.Append(AudioSonar.Mode).Append(',');
            sb.Append(B(manual)).Append(',');
            sb.Append(B(s.speedAdaptiveRange)).Append(',');
            sb.Append(F(s.dangerDistance)).Append(',').Append(F(s.contactDistance)).Append(',');
            sb.Append(F(s.dangerMin)).Append(',').Append(F(s.contactMin)).Append(',');
            sb.Append(F(s.dangerFraction)).Append(',').Append(F(s.contactFraction)).Append(',');
            sb.Append(F(s.maxRange)).Append(',').Append(F(s.rangeSlow)).Append(',').Append(F(s.rangeFast)).Append(',');
            sb.Append(F(s.rangeFixed)).Append(',').Append(F(s.CurrentRange)).Append(',');
            sb.Append(F(s.sideRange)).Append(',').Append(F(s.pathHalfWidth)).Append(',');
            sb.Append(F(s.dropDepth)).Append(',').Append(B(s.detectDropOff)).Append(',');
            sb.Append(F(s.landmarkRange)).Append(',').Append(B(s.landmarkCues)).Append(',');
            sb.Append(F(s.masterVolume)).Append(',').Append(B(s.haptics)).Append(',').Append(B(s.muted)).Append(',');
            sb.Append(F(s.nearVolume)).Append(',').Append(F(s.farVolume)).Append(',');
            sb.Append(F(s.dangerVolume)).Append(',').Append(F(s.contactVolume)).Append(',');
            sb.Append(B(s.sideWarning)).Append(',').Append(F(s.sideVolume));
            sb.Append('\n');
            File.AppendAllText(path, sb.ToString());
        }
        catch (Exception e) { Debug.LogWarning("ThresholdLog: " + e.Message); }
    }

    static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    static string B(bool b) { return b ? "1" : "0"; }

    /// <summary>Номер участника из participant.txt (как в ExperimentController), иначе P00.</summary>
    static string Participant()
    {
        try
        {
            string p = Path.Combine(Application.persistentDataPath, "participant.txt");
            if (File.Exists(p))
            {
                string t = File.ReadAllText(p).Trim().Replace(",", " ").Replace("\n", " ").Replace("\r", "");
                if (t.Length > 0) return t;
            }
        }
        catch { /* не важно */ }
        return "P00";
    }

    /// <summary>Новый файл - с заголовком. Если заголовок старый (набор столбцов поменялся), старый файл переименовывается.</summary>
    static void EnsureHeader(string path)
    {
        if (!File.Exists(path))
        {
            File.WriteAllText(path, Header + "\n");
            headerChecked = true;
            Debug.Log("ThresholdLog: CSV " + path);
            return;
        }
        if (headerChecked) return;
        headerChecked = true;
        string first;
        using (var r = new StreamReader(path)) first = r.ReadLine();
        if (first == Header) return;
        string old = Path.Combine(Path.GetDirectoryName(path), "thresholds-old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".csv");
        File.Move(path, old);
        File.WriteAllText(path, Header + "\n");
    }
}
