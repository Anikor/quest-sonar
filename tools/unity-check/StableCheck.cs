using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// Только для временного проекта проверки (ветка main, v3.x). Проверяет логику звуковой навигации без шлема:
//  1) NavZones.FindPassages находит дверь 0.9 м в стене синтетической комнаты (и когда за дверью ничего не видно),
//     не принимает за дверь широкий проём 2.5 м, зазор между низкой мебелью и нишу глубиной 0.5 м;
//  2) NavZones.EvaluateZones / BestZone: стена прямо на 1.5 м, слева свободно -> лучшая зона левая;
//  3) NavZones.Corridor учитывает ширину тела: щель 0.4 м между высокими препятствиями не проходима;
//  4) SonarSounds: все клипы конечны, пик <= 1, не тишина.
// Ошибки C# ломают -executeMethod сами. Результат - строки STABLECHECK:, последняя "OK DONE"; код выхода 3 при провале.
public static class StableCheck
{
    const float Cs = 0.1f;
    static int fails;

    static void Check(bool ok, string what)
    {
        Debug.Log((ok ? "STABLECHECK: OK   " : "STABLECHECK: FAIL ") + what);
        if (!ok) fails++;
    }

    class Grid
    {
        public readonly Dictionary<Vector2Int, NavZones.Cell> cells = new();

        public NavZones.Cell Query(Vector2Int k) { return cells.TryGetValue(k, out var c) ? c : NavZones.Cell.Unknown; }

        /// <summary>Прямоугольник клеток (включительно).</summary>
        public void Fill(float x0, float z0, float x1, float z1, NavZones.Cell kind)
        {
            for (float x = x0; x <= x1 + 1e-4f; x += Cs * 0.5f)
                for (float z = z0; z <= z1 + 1e-4f; z += Cs * 0.5f)
                    cells[NavZones.ToCell(new Vector3(x, 0f, z), Cs)] = kind;
        }

        /// <summary>Отрезок стены.</summary>
        public void Wall(float x0, float z0, float x1, float z1, NavZones.Cell kind = NavZones.Cell.Tall)
        {
            float len = Vector2.Distance(new Vector2(x0, z0), new Vector2(x1, z1));
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / (Cs * 0.5f)));
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                cells[NavZones.ToCell(new Vector3(Mathf.Lerp(x0, x1, t), 0f, Mathf.Lerp(z0, z1, t)), Cs)] = kind;
            }
        }
    }

    /// <summary>
    /// Комната 6x6 м (стены x = ±3, z = ±3), наблюдатель в (0,0,0) смотрит на +z.
    /// В передней стене (z = 3) проём шириной w с центром x = cx; за ним коридор (или ничего: Unknown).
    /// </summary>
    static Grid Room(float cx, float w, NavZones.Cell jambKind = NavZones.Cell.Tall, bool corridor = true)
    {
        var g = new Grid();
        g.Fill(-2.95f, -2.95f, 2.95f, 2.95f, NavZones.Cell.Free);
        g.Wall(-3f, -3f, 3f, -3f);
        g.Wall(-3f, -3f, -3f, 3f);
        g.Wall(3f, -3f, 3f, 3f);
        g.Wall(-3f, 3f, cx - w / 2f, 3f, jambKind);
        g.Wall(cx + w / 2f, 3f, 3f, 3f, jambKind);
        if (corridor)
        {
            g.Fill(cx - w / 2f + Cs, 3.05f, cx + w / 2f - Cs, 6f, NavZones.Cell.Free);
            g.Wall(cx - w / 2f - 0.3f, 3.1f, cx - w / 2f - 0.3f, 6f);
            g.Wall(cx + w / 2f + 0.3f, 3.1f, cx + w / 2f + 0.3f, 6f);
        }
        return g;
    }

    static int Passages(Grid g, List<NavZones.Passage> result)
    {
        return NavZones.FindPassages(g.Query, Cs, Vector3.zero, Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, result);
    }

    public static void Run()
    {
        var res = new List<NavZones.Passage>();

        // 1. дверь 0.9 м, центр x = 1.0, за ней коридор
        int n = Passages(Room(1f, 0.9f), res);
        Check(n == 1, $"дверь 0.9 м найдена ровно один раз (найдено {n})");
        if (n >= 1)
        {
            var p = res[0];
            Check(p.width >= 0.7f && p.width <= 1.25f, $"ширина двери {p.width:F2} м (ожидается ~0.9)");
            Check(Mathf.Abs(p.center.x - 1f) < 0.2f && Mathf.Abs(p.center.z - 3f) < 0.2f, $"центр двери ({p.center.x:F2}, {p.center.z:F2}) (ожидается (1, 3))");
            Check(Mathf.Abs(p.distance - 3.16f) < 0.3f, $"расстояние до двери {p.distance:F2} м (ожидается ~3.16)");
            Check(p.depthBeyond >= 1f && p.open && !p.fromScene, $"глубина за дверью {p.depthBeyond:F1} м, open={p.open}, fromScene={p.fromScene}");
        }

        // 1б. за дверью ничего не видно (Unknown) - дверь всё равно находится
        n = Passages(Room(1f, 0.9f, NavZones.Cell.Tall, false), res);
        Check(n == 1, $"дверь найдена, когда за ней неизвестно (найдено {n})");

        // 1в. дверь слева от взгляда
        n = Passages(Room(-1.5f, 0.8f), res);
        Check(n == 1 && res.Count > 0 && res[0].center.x < -1f, $"дверь слева найдена (найдено {n}, x={(res.Count > 0 ? res[0].center.x : 0f):F2})");

        // 2. широкий проём 2.5 м - не дверь
        n = Passages(Room(1f, 2.5f), res);
        Check(n == 0, $"проём 2.5 м не считается дверью (найдено {n})");

        // 3. зазор 0.9 м между низкой мебелью - не дверь
        n = Passages(Room(1f, 0.9f, NavZones.Cell.Low), res);
        Check(n == 0, $"зазор между низкой мебелью не считается дверью (найдено {n})");

        // 4. ниша глубиной 0.5 м - не дверь
        var niche = Room(1f, 0.9f, NavZones.Cell.Tall, false);
        niche.Fill(0.6f, 3.05f, 1.4f, 3.45f, NavZones.Cell.Free);
        niche.Wall(0.5f, 3.5f, 1.5f, 3.5f);
        n = Passages(niche, res);
        Check(n == 0, $"ниша 0.5 м не считается дверью (найдено {n})");

        // 5. зоны: стена прямо на 1.5 м (x от -0.8 до 3), слева свободно
        var zRoom = Room(1f, 0.9f);
        zRoom.Wall(-0.8f, 1.5f, 3f, 1.5f);
        var zones = new NavZones.Zone[3];
        NavZones.EvaluateZones(zRoom.Query, Cs, Vector3.zero, Vector3.forward, 120f, 5, 0.6f, 4f, 0.25f, zones);
        int best = NavZones.BestZone(zones, 2f, 0.25f);
        Check(best == 0, $"лучшая зона - левая (получено {best}; free: {zones[0].free:F1} / {zones[1].free:F1} / {zones[2].free:F1})");
        Check(zones[1].free >= 1.2f && zones[1].free <= 1.8f, $"прямо свободно {zones[1].free:F2} м (ожидается ~1.5)");
        Check(zones[0].free >= 3.5f && zones[0].seen >= 0.5f, $"слева свободно {zones[0].free:F1} м, просмотрено {zones[0].seen:F2}");
        Check(zones[0].yawFrom < zones[0].yawTo && zones[0].yawFrom == -60f && zones[2].yawTo == 60f, $"границы зон {zones[0].yawFrom}..{zones[2].yawTo}");

        // 5б. пустая комната: лучшая зона - прямо; все перекрыто -> -1
        var empty = Room(1f, 0.9f);
        NavZones.EvaluateZones(empty.Query, Cs, Vector3.zero, Vector3.forward, 120f, 5, 0.6f, 4f, 0.25f, zones);
        Check(NavZones.BestZone(zones, 2f, 0.25f) == 1, "в пустой комнате лучшая зона - прямо");
        var box = new Grid();
        box.Fill(-3f, -3f, 3f, 3f, NavZones.Cell.Free);
        box.Fill(-3f, 0.7f, 3f, 0.8f, NavZones.Cell.Tall);
        NavZones.EvaluateZones(box.Query, Cs, Vector3.zero, Vector3.forward, 120f, 5, 0.6f, 4f, 0.25f, zones);
        Check(NavZones.BestZone(zones, 2f, 0.25f) == -1 && zones[0].free < 1f && zones[1].free < 1f && zones[2].free < 1f,
              $"стена поперёк на 0.7 м: свободной зоны нет (free {zones[0].free:F1} / {zones[1].free:F1} / {zones[2].free:F1})");

        // 6. коридор: щель 0.4 м между высокими препятствиями
        var slot = new Grid();
        slot.Fill(-3f, -3f, 3f, 3f, NavZones.Cell.Free);
        slot.Wall(-1f, 2f, -0.2f, 2f);
        slot.Wall(0.2f, 2f, 1f, 2f);
        float wide = NavZones.Corridor(slot.Query, Cs, Vector3.zero, Vector3.forward, 0.6f, 4f, 0.25f, out float seenW);
        float thin = NavZones.Corridor(slot.Query, Cs, Vector3.zero, Vector3.forward, 0f, 4f, 0.25f, out _);
        Check(Mathf.Abs(wide - 2f) < 0.15f, $"коридор 0.6 м упирается в щель на {wide:F2} м (ожидается 2.0)");
        Check(thin >= 3.9f, $"осевая линия проходит сквозь щель: {thin:F1} м (ожидается 4.0)");
        Check(seenW > 0.9f, $"доля просмотренных клеток {seenW:F2}");

        // 7. звуки
        CheckSound("Soft low", SonarSounds.Soft(330f, false, 0.14f));
        CheckSound("Soft bell", SonarSounds.Soft(523f, true, 0.18f));
        CheckSound("Chirp", SonarSounds.Chirp(900f, 300f, 0.35f));
        CheckSound("Clear", SonarSounds.Clear());
        CheckSound("Blocked", SonarSounds.Blocked());
        CheckSound("Knock1", SonarSounds.Knock(1));
        CheckSound("Knock2", SonarSounds.Knock(2));

        if (fails > 0)
        {
            Debug.LogError($"STABLECHECK: FAILED ({fails})");
            EditorApplication.Exit(3);
        }
        else
        {
            Debug.Log("STABLECHECK: OK DONE");
            EditorApplication.Exit(0);
        }
    }

    static void CheckSound(string name, float[] d)
    {
        float peak = 0f, sum = 0f;
        bool finite = true;
        for (int i = 0; i < d.Length; i++)
        {
            if (float.IsNaN(d[i]) || float.IsInfinity(d[i])) finite = false;
            peak = Mathf.Max(peak, Mathf.Abs(d[i]));
            sum += d[i] * d[i];
        }
        float rms = d.Length > 0 ? Mathf.Sqrt(sum / d.Length) : 0f;
        Check(finite && peak <= 1f && rms > 0.01f && d.Length > 1000 && Mathf.Abs(d[d.Length - 1]) < 0.01f,
              $"звук {name}: {d.Length} сэмплов, пик {peak:F2}, RMS {rms:F3}, конец {d[d.Length - 1]:F3}");
    }
}
