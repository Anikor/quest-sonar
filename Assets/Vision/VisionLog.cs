using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Журнал ветки vision для разбора после прогона на шлеме. Ничего общего с журналами эксперимента (trials.csv) не имеет.
/// Пишет в Application.persistentDataPath/vision-log/&lt;дата-время&gt;/:
///   events.jsonl  - по строке JSON на событие: кадр, результат OCR (все слова до отсева), таблички, двери, карта, речь;
///   frames/*.jpg  - кадры камеры, по которым шло распознавание (серые);
///   map-*.txt     - карта препятствий вокруг пользователя (ASCII, вид сверху) для разбора поиска дверей.
/// Забрать с шлема:
///   adb pull /sdcard/Android/data/com.anikor.questsonar/files/vision-log .
/// Init вызывать из главного потока (persistentDataPath); Event можно звать из любого потока.
/// </summary>
public static class VisionLog
{
    public static bool enabled = true;
    public static string Dir { get; private set; }
    public static int SavedFrames { get; private set; }
    public static int maxFrames = 600;

    static StreamWriter writer;
    static readonly object gate = new object();
    static readonly Stopwatch clock = new Stopwatch();
    static bool failed;
    static byte[] rgb;
    static Texture2D tex;

    /// <summary>Открыть журнал (главный поток). Повторные вызовы ничего не делают.</summary>
    public static void Init()
    {
        if (writer != null || failed || !enabled) return;
        try
        {
            Dir = Path.Combine(Application.persistentDataPath, "vision-log", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(Path.Combine(Dir, "frames"));
            writer = new StreamWriter(Path.Combine(Dir, "events.jsonl"), false, new UTF8Encoding(false)) { AutoFlush = true };
            clock.Start();
            Event("session", $"\"device\":{Str(SystemInfo.deviceModel)},\"os\":{Str(SystemInfo.operatingSystem)},\"app\":{Str(Application.version)},\"started\":{Str(DateTime.Now.ToString("s"))}");
            Debug.Log("VisionLog: " + Dir);
        }
        catch (Exception e)
        {
            failed = true;
            Debug.LogWarning("VisionLog: не удалось открыть журнал: " + e.Message);
        }
    }

    public static float Now => (float)clock.Elapsed.TotalSeconds;

    /// <summary>Записать событие. fields - уже готовые поля JSON без фигурных скобок: "a":1,"b":"x" (см. Str/F/V).</summary>
    public static void Event(string kind, string fields = null)
    {
        if (writer == null) return;
        lock (gate)
        {
            try
            {
                writer.Write("{\"t\":");
                writer.Write(F(Now));
                writer.Write(",\"kind\":");
                writer.Write(Str(kind));
                if (!string.IsNullOrEmpty(fields)) { writer.Write(','); writer.Write(fields); }
                writer.WriteLine("}");
            }
            catch { /* журнал не должен ломать работу */ }
        }
    }

    /// <summary>Сохранить серый кадр (строки сверху вниз) как JPG. Главный поток. Возвращает имя файла или null.</summary>
    public static string SaveFrame(byte[] gray, int w, int h, string name)
    {
        if (writer == null || gray == null || SavedFrames >= maxFrames) return null;
        try
        {
            int n = w * h;
            if (rgb == null || rgb.Length != n * 3) rgb = new byte[n * 3];
            if (tex == null || tex.width != w || tex.height != h)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            }
            // Texture2D хранит строки снизу вверх, кадр у нас сверху вниз
            for (int y = 0; y < h; y++)
            {
                int src = (h - 1 - y) * w;
                int dst = y * w * 3;
                for (int x = 0; x < w; x++)
                {
                    byte g = gray[src + x];
                    rgb[dst] = g; rgb[dst + 1] = g; rgb[dst + 2] = g;
                    dst += 3;
                }
            }
            tex.LoadRawTextureData(rgb);
            tex.Apply(false, false);
            byte[] jpg = tex.EncodeToJPG(80);
            string file = name + ".jpg";
            File.WriteAllBytes(Path.Combine(Dir, "frames", file), jpg);
            SavedFrames++;
            return file;
        }
        catch (Exception e)
        {
            Debug.LogWarning("VisionLog: кадр не сохранён: " + e.Message);
            return null;
        }
    }

    /// <summary>Сохранить текстовый файл (например, ASCII-карту) рядом с журналом. Возвращает имя файла или null.</summary>
    public static string SaveText(string name, string text)
    {
        if (writer == null) return null;
        try
        {
            File.WriteAllText(Path.Combine(Dir, name), text, new UTF8Encoding(false));
            return name;
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- JSON
    public static string Str(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string F(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v)) return "null";
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string V(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";
    public static string V(Vector2 v) => "[" + F(v.x) + "," + F(v.y) + "]";
    public static string Q(Quaternion q) => "[" + F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w) + "]";
    public static string R(RectInt r) => "[" + r.x + "," + r.y + "," + r.width + "," + r.height + "]";
    public static string B(bool b) => b ? "true" : "false";
}
