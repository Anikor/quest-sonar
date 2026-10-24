using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Геометрия для звуковой навигации по карте препятствий: зоны прохода (слева / прямо / справа)
/// и поиск проёмов (дверей, проходов) в стенах.
/// Не зависит от сцены: карта задаётся функцией «что в клетке», поэтому логика проверяется
/// тестом без шлема (tools/unity-check/StableCheck.cs). Всё в мировых координатах, высота (y) не используется.
/// </summary>
public static class NavZones
{
    /// <summary>Что известно о клетке карты.</summary>
    public enum Cell : byte
    {
        Unknown = 0,   // датчик туда не смотрел
        Free = 1,      // видели пол, свободно
        Low = 2,       // препятствие ниже 1.2 м (мебель)
        Tall = 3       // препятствие выше 1.2 м (стены, косяки, шкафы)
    }

    public delegate Cell CellQuery(Vector2Int key);

    /// <summary>Состояние одной зоны по горизонтали.</summary>
    public struct Zone
    {
        public float yawFrom, yawTo;    // границы зоны, градусы относительно взгляда (минус = влево)
        public float free;              // сколько метров можно пройти по лучшему направлению зоны
        public Vector3 freeDir;         // это направление (мир, горизонталь)
        public float nearest;           // ближайшее препятствие в зоне (= maxRange, если нет)
        public Vector3 nearestDir;
        public float seen;              // доля просмотренных клеток вдоль лучшего направления (0..1)

        public float CenterYaw => (yawFrom + yawTo) * 0.5f;
    }

    /// <summary>Проём в стене: дверь или проход.</summary>
    public struct Passage
    {
        public Vector3 center;      // середина проёма (мир)
        public float width;         // ширина, м
        public float distance;      // по горизонтали от наблюдателя
        public float depthBeyond;   // сколько свободно за проёмом, м
        public bool open;           // виден сквозной проход по карте (иначе только дверь из модели комнаты, может быть закрыта)
        public bool fromScene;      // есть в модели комнаты (MR Utility Kit)
    }

    public static Vector2Int ToCell(Vector3 p, float cellSize)
    {
        return new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));
    }

    static bool Blocked(Cell c) { return c == Cell.Low || c == Cell.Tall; }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0f;
        return v.sqrMagnitude < 1e-8f ? Vector3.forward : v.normalized;
    }

    /// <summary>
    /// Коридор шириной width вдоль dir: сколько метров свободно (от start до max) до первой занятой клетки.
    /// Неизвестные клетки не останавливают; seenFrac = доля известных клеток по осевой линии.
    /// </summary>
    public static float Corridor(CellQuery cell, float cellSize, Vector3 origin, Vector3 dir, float width,
                                 float max, float start, out float seenFrac)
    {
        dir = Flat(dir);
        Vector3 side = new Vector3(dir.z, 0f, -dir.x) * (width * 0.5f);
        float step = cellSize * 0.5f;
        int checks = 0, seen = 0;
        for (float d = start; d <= max; d += step)
        {
            Vector3 p = origin + dir * d;
            Cell c0 = cell(ToCell(p, cellSize));
            checks++;
            if (c0 != Cell.Unknown) seen++;
            bool blocked = Blocked(c0);
            if (!blocked && width > 0f)
                blocked = Blocked(cell(ToCell(p + side, cellSize))) || Blocked(cell(ToCell(p - side, cellSize)));
            if (blocked)
            {
                seenFrac = checks > 0 ? seen / (float)checks : 0f;
                return d;
            }
        }
        seenFrac = checks > 0 ? seen / (float)checks : 0f;
        return max;
    }

    /// <summary>
    /// Делит обзор yawRange на zones.Length зон и в каждой ищет лучшее направление (самый длинный коридор)
    /// и ближайшее препятствие.
    /// </summary>
    public static void EvaluateZones(CellQuery cell, float cellSize, Vector3 origin, Vector3 forward, float yawRange,
                                     int raysPerZone, float bodyWidth, float maxRange, float start, Zone[] zones)
    {
        forward = Flat(forward);
        int count = zones.Length;
        raysPerZone = Mathf.Max(1, raysPerZone);
        float zoneWidth = yawRange / count;
        for (int z = 0; z < count; z++)
        {
            var zone = new Zone
            {
                yawFrom = -yawRange * 0.5f + z * zoneWidth,
                yawTo = -yawRange * 0.5f + (z + 1) * zoneWidth,
                free = -1f,
                nearest = maxRange,
                freeDir = forward,
                nearestDir = forward,
                seen = 0f
            };
            for (int k = 0; k < raysPerZone; k++)
            {
                float yaw = zone.yawFrom + (k + 0.5f) * zoneWidth / raysPerZone;
                Vector3 dir = Quaternion.Euler(0f, yaw, 0f) * forward;
                float f = Corridor(cell, cellSize, origin, dir, bodyWidth, maxRange, start, out float seen);
                if (f > zone.free || (Mathf.Approximately(f, zone.free) && seen > zone.seen))
                {
                    zone.free = f;
                    zone.freeDir = dir;
                    zone.seen = seen;
                }
                if (f < zone.nearest)
                {
                    zone.nearest = f;
                    zone.nearestDir = dir;
                }
            }
            if (zone.free < 0f) zone.free = 0f;
            zones[z] = zone;
        }
    }

    /// <summary>
    /// Лучшая зона для движения: свободно хотя бы clearDistance и зона просмотрена (seen >= minSeen).
    /// При равных условиях предпочитается центральная зона (идти прямо), затем самая длинная.
    /// Возвращает -1, если такой зоны нет.
    /// </summary>
    public static int BestZone(Zone[] zones, float clearDistance, float minSeen)
    {
        if (zones == null || zones.Length == 0) return -1;
        int center = zones.Length / 2;
        if (zones.Length % 2 == 1 && zones[center].free >= clearDistance && zones[center].seen >= minSeen) return center;
        int best = -1;
        for (int i = 0; i < zones.Length; i++)
        {
            if (zones[i].free < clearDistance || zones[i].seen < minSeen) continue;
            if (best < 0 || zones[i].free > zones[best].free + 0.3f ||
                (Mathf.Abs(zones[i].free - zones[best].free) <= 0.3f && Mathf.Abs(i - center) < Mathf.Abs(best - center)))
                best = i;
        }
        return best;
    }

    /// <summary>
    /// Поиск проёмов полярным сканом карты: пара высоких препятствий на одной стене, между которыми лучи
    /// уходят заметно дальше, ширина между ними в [minWidth, maxWidth], за серединой свободно >= minDepthBeyond.
    /// </summary>
    public static int FindPassages(CellQuery cell, float cellSize, Vector3 origin, Vector3 forward, float scanYaw,
                                   float stepDeg, float maxRange, float minWidth, float maxWidth, float minDepthBeyond,
                                   float start, List<Passage> result)
    {
        result.Clear();
        forward = Flat(forward);
        stepDeg = Mathf.Max(0.5f, stepDeg);
        int n = Mathf.Max(3, Mathf.RoundToInt(2f * scanYaw / stepDeg) + 1);
        float angleStep = 2f * scanYaw / (n - 1);

        var dist = new float[n];
        var tall = new bool[n];
        var pts = new Vector3[n];
        float step = cellSize * 0.5f;
        for (int i = 0; i < n; i++)
        {
            Vector3 dir = Quaternion.Euler(0f, -scanYaw + i * angleStep, 0f) * forward;
            dist[i] = float.PositiveInfinity;
            for (float d = start; d <= maxRange; d += step)
            {
                Vector3 p = origin + dir * d;
                Cell c = cell(ToCell(p, cellSize));
                if (Blocked(c))
                {
                    dist[i] = d;
                    tall[i] = c == Cell.Tall;
                    pts[i] = p;
                    break;
                }
            }
        }

        const float GapTol = 0.4f;                               // луч «внутри проёма», если идёт дальше стены хотя бы на столько
        float sameWallTol = Mathf.Max(0.8f, maxWidth * 0.6f);     // границы проёма лежат примерно на одном расстоянии
        int i0 = 0;
        while (i0 < n - 1)
        {
            if (!tall[i0] || !(dist[i0 + 1] > dist[i0] + GapTol)) { i0++; continue; }
            float dL = dist[i0];
            int j = i0 + 1;
            while (j < n && dist[j] > dL + GapTol) j++;
            if (j >= n) break;                                   // проём не закрыт справа в пределах скана
            if (!tall[j] || Mathf.Abs(dist[j] - dL) > sameWallTol) { i0 = j; continue; }

            Vector3 a = pts[i0], b = pts[j];
            float width = Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));
            if (width >= minWidth && width <= maxWidth)
            {
                Vector3 center = (a + b) * 0.5f;
                Vector3 toC = center - origin;
                toC.y = 0f;
                float depth = Corridor(cell, cellSize, center, toC, 0f, 3f, cellSize, out _);
                if (depth >= minDepthBeyond)
                {
                    result.Add(new Passage
                    {
                        center = center,
                        width = width,
                        distance = toC.magnitude,
                        depthBeyond = depth,
                        open = true,
                        fromScene = false
                    });
                }
            }
            i0 = j;
        }
        return result.Count;
    }
}
