using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Разбор журнала со шлема без шлема: берёт ASCII-карты vision-log (map-NNN.txt), восстанавливает сетку и запускает
// NavZones.FindPassages из записанного положения и взгляда с разными наборами правил. Показывает, какое правило
// отсекает проём. Папка с картами: переменная окружения QS_MAPS (по умолчанию - последняя сессия в logs/vision-log).
// Запуск: Unity.exe -batchmode -nographics -projectPath <dir> -executeMethod MapReplay.Run -quit -logFile <log>
public static class MapReplay
{
    const float Cs = 0.1f;

    public static void Run()
    {
        string dir = Environment.GetEnvironmentVariable("QS_MAPS");
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-qsMaps") dir = args[i + 1];
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Debug.LogError("MAPREPLAY: задайте QS_MAPS = папка сессии vision-log с map-*.txt");
            EditorApplication.Exit(2);
            return;
        }
        var files = Directory.GetFiles(dir, "map-*.txt");
        Array.Sort(files, StringComparer.Ordinal);
        Debug.Log($"MAPREPLAY: {files.Length} карт в {dir}");
        foreach (var f in files) Replay(f);
        Debug.Log("MAPREPLAY: DONE");
        EditorApplication.Exit(0);
    }

    static void Replay(string file)
    {
        var lines = File.ReadAllLines(file);
        if (lines.Length < 3) return;
        // # t=317.214 head=(-0.591,0.254) yaw=335 cell=0,1 радиус=4 м; север (+z) вверху, восток (+x) справа
        var m = Regex.Match(lines[0], @"head=\((-?[0-9.]+),(-?[0-9.]+)\) yaw=(-?[0-9.]+)");
        if (!m.Success) { Debug.Log("MAPREPLAY: нет заголовка в " + file); return; }
        float hx = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        float hz = float.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        float yaw = float.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        var rm = Regex.Match(lines[0], @"радиус=([0-9.]+)");
        float radius = rm.Success ? float.Parse(rm.Groups[1].Value, CultureInfo.InvariantCulture) : 4f;
        int r = Mathf.RoundToInt(radius / Cs);
        int n = 2 * r + 1;

        // строка 0 карты = север (+z), столбец 0 = запад (-x); центр = клетка головы
        var head = new Vector3(hx, 0f, hz);
        var hc = NavZones.ToCell(head, Cs);
        var cells = new Dictionary<Vector2Int, NavZones.Cell>();
        for (int zi = 0; zi < n && zi + 2 < lines.Length; zi++)
        {
            string row = lines[zi + 2];
            for (int xi = 0; xi < n && xi < row.Length; xi++)
            {
                NavZones.Cell c;
                switch (row[xi])
                {
                    case '#': c = NavZones.Cell.Tall; break;
                    case 'o': c = NavZones.Cell.Low; break;
                    case '.': case '@': case '^': case 'D': case 'd': case 'S': c = NavZones.Cell.Free; break;
                    default: c = NavZones.Cell.Unknown; break;
                }
                if (c != NavZones.Cell.Unknown) cells[new Vector2Int(hc.x - r + xi, hc.y + r - zi)] = c;
            }
        }
        NavZones.CellQuery q = k => cells.TryGetValue(k, out var c) ? c : NavZones.Cell.Unknown;
        Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

        var res = new List<NavZones.Passage>();
        string Show(List<NavZones.Passage> list)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var p in list)
            {
                Vector3 to = p.center - head; to.y = 0f;
                float py = Vector3.SignedAngle(fwd, to, Vector3.up);
                sb.Append($" [c=({p.center.x:F1},{p.center.z:F1}) w={p.width:F2} d={p.distance:F1} yaw={py:F0} beyond={p.depthBeyond:F1}]");
            }
            return sb.Length > 0 ? sb.ToString() : " нет";
        }

        string name = Path.GetFileName(file);
        int a = NavZones.FindPassages(q, Cs, head, fwd, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true, 0.8f, true);
        string sa = Show(res);
        int b = NavZones.FindPassages(q, Cs, head, fwd, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true, 0.8f, false);
        string sb2 = Show(res);
        int c2 = NavZones.FindPassages(q, Cs, head, fwd, 90f, 2f, 5f, 0.6f, 1.6f, 0.5f, 0.25f, res, true, 0f, false);
        string sc = Show(res);
        int d = NavZones.FindPassages(q, Cs, head, fwd, 180f, 2f, 5f, 0.5f, 2.0f, 0.3f, 0.25f, res, true, 0f, false);
        string sd = Show(res);
        Debug.Log($"MAPREPLAY: {name} клеток={cells.Count} голова=({hx:F1},{hz:F1}) yaw={yaw:F0}\n" +
                  $"  как на шлеме (стена у косяка, >=0.8 м):{sa}\n" +
                  $"  без проверки стены:{sb2}\n" +
                  $"  без стены, глубина за проёмом 0.5 м, любое расстояние:{sc}\n" +
                  $"  круговой обзор, ширина 0.5-2.0 м, глубина 0.3 м:{sd}");
    }
}
