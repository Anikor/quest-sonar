using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Поиск дверей и сообщение о них в SonarLandmarks (звук озвучивает их сам: AudioSonar.UpdateLandmarks).
/// Два источника:
///   1) По карте препятствий (ObstacleMap + NavZones.FindPassages): проём 0.6-1.6 м между двумя высокими препятствиями
///      (стены, косяки), за которым свободно хотя бы 1 м. Проём должен подтвердиться в нескольких сканах подряд
///      (confirmScans), иначе шум карты даст ложную дверь. Такой проём точно открыт: датчик видит сквозь него.
///   2) Из модели комнаты MR Utility Kit (Space Setup на шлеме): якоря DOOR_FRAME и WINDOW_FRAME. Постоянные ориентиры,
///      положение точное, но открыта ли дверь, неизвестно. Если рядом (< 1 м) уже есть проём по карте, второй раз не сообщается.
/// Каждый скан и раз в несколько секунд ASCII-карта вокруг пользователя пишутся в VisionLog для разбора.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class DoorFinder : MonoBehaviour
{
    public static DoorFinder Instance { get; private set; }

    [Header("Проёмы по карте")]
    public bool detectPassages = true;
    [Tooltip("Как часто искать проёмы, сек")]
    public float interval = 0.5f;
    [Tooltip("Полуширина скана, градусы от взгляда в каждую сторону")]
    public float scanYaw = 90f;
    public float scanStepDeg = 2f;
    [Tooltip("Дальше этого проёмы не ищутся, м")]
    public float range = 5f;
    public float minWidth = 0.6f;
    public float maxWidth = 1.6f;
    [Tooltip("За проёмом должно быть свободно хотя бы столько метров (иначе ниша или шкаф)")]
    public float minDepthBeyond = 1f;
    [Tooltip("Проём должен быть найден в стольких сканах подряд, чтобы считаться дверью")]
    public int confirmScans = 2;
    [Tooltip("Низкие препятствия в проёме не закрывают его: порог, низ двери, ошибка высоты пола (по журналу со шлема дверь терялась из-за них)")]
    public bool ignoreLowInDoorway = true;
    [Tooltip("Косяки должны быть частью стены (высокие клетки продолжаются в стороны). По журналу со шлема без этого дверью считался любой зазор между шкафом и монитором")]
    public bool requireWallJambs = true;
    [Tooltip("Проёмы ближе этого расстояния от головы не ищутся, м (в самом проёме его форму не разобрать)")]
    public float minDistance = 0.8f;
    [Tooltip("Проёмы ближе этого расстояния друг к другу считаются одним, м")]
    public float mergeDistance = 1.2f;
    [Tooltip("Проём по карте ближе этого расстояния к двери из модели комнаты считается ею (не дублируется), м")]
    public float sceneMergeDistance = 2f;
    [Tooltip("Проём, не подтверждавшийся столько секунд, забывается")]
    public float forgetAfter = 6f;
    [Tooltip("Подтверждённый проём продолжает сообщаться в реестр, пока с последнего подтверждения прошло не больше, сек (карта шумит, проём виден не каждый скан)")]
    public float holdSeconds = 3f;

    [Header("Модель комнаты (MR Utility Kit)")]
    public bool useSceneModel = true;
    public bool sceneWindows = true;
    public float sceneRange = 6f;
    public float sceneInterval = 3f;

    [Header("Журнал")]
    [Tooltip("Как часто писать ASCII-карту вокруг пользователя в vision-log, сек (0 = не писать)")]
    public float mapDumpInterval = 10f;
    [Tooltip("Размер карты в журнале, м (в каждую сторону от пользователя)")]
    public float mapDumpRadius = 4f;

    [Header("Состояние (только чтение)")]
    public string status = "двери: запуск";
    public int mapDoors;
    public int sceneDoors;

    class Track
    {
        public int id;
        public Vector3 center;
        public float width;
        public int hits;
        public float lastSeen;
        public bool reported;
    }

    ObstacleMap map;
    Transform head;
    NavZones.CellQuery query;
    readonly List<NavZones.Passage> found = new List<NavZones.Passage>();
    readonly List<Track> tracks = new List<Track>();
    readonly List<Vector3> sceneDoorCenters = new List<Vector3>();
    readonly StringBuilder sceneJson = new StringBuilder();
    float tick, sceneTick, dumpTick;
    int scanIndex, mapDumps;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DoorFinder>() == null)
            new GameObject("DoorFinder").AddComponent<DoorFinder>();
    }

    void Awake()
    {
        Instance = this;
        VisionLog.Init();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);
        map = ObstacleMap.Instance;
        head = SonarCommon.FindHead();
        if (map == null || head == null) { status = "двери: нет карты"; yield break; }
        query = map.QueryCell;
        status = "двери: ищу";
        VisionLog.Event("doorfinder", $"\"detectPassages\":{VisionLog.B(detectPassages)},\"range\":{VisionLog.F(range)},\"minWidth\":{VisionLog.F(minWidth)},\"maxWidth\":{VisionLog.F(maxWidth)},\"minDepthBeyond\":{VisionLog.F(minDepthBeyond)},\"confirmScans\":{confirmScans},\"cellSize\":{VisionLog.F(map.cellSize)},\"mapMaxDistance\":{VisionLog.F(map.maxDistance)}");
    }

    void Update()
    {
        if (map == null || head == null) return;
        float now = Time.time;

        tick += Time.deltaTime;
        if (tick >= interval)
        {
            tick = 0f;
            if (detectPassages) ScanMap(now);
            else ForgetAll();
        }

        sceneTick += Time.deltaTime;
        if (sceneTick >= sceneInterval)
        {
            sceneTick = 0f;
            if (useSceneModel) ScanScene();
            else { sceneDoorCenters.Clear(); sceneDoors = 0; }
        }

        if (mapDumpInterval > 0f)
        {
            dumpTick += Time.deltaTime;
            if (dumpTick >= mapDumpInterval)
            {
                dumpTick = 0f;
                DumpMap();
            }
        }

        UpdateStatus();
    }

    // ---------------------------------------------------------------- проёмы по карте
    void ScanMap(float now)
    {
        scanIndex++;
        NavZones.FindPassages(query, map.cellSize, head.position, head.forward, scanYaw, scanStepDeg, range,
                              minWidth, maxWidth, minDepthBeyond, 0.25f, found, ignoreLowInDoorway, minDistance, requireWallJambs);

        foreach (var p in found)
        {
            Track best = null;
            float bd = mergeDistance * mergeDistance;
            foreach (var t in tracks)
            {
                float d2 = Flat2(t.center, p.center);
                if (d2 < bd) { bd = d2; best = t; }
            }
            if (best == null)
            {
                best = new Track { id = SonarLandmarks.MakeId(LandmarkKind.Door, p.center, 1.0f), center = p.center, width = p.width };
                tracks.Add(best);
            }
            else
            {
                best.center = Vector3.Lerp(best.center, p.center, 0.5f);
                best.width = Mathf.Lerp(best.width, p.width, 0.5f);
            }
            best.hits++;
            best.lastSeen = now;
        }

        mapDoors = 0;
        for (int i = tracks.Count - 1; i >= 0; i--)
        {
            var t = tracks[i];
            if (now - t.lastSeen > forgetAfter)
            {
                if (t.reported) SonarLandmarks.Remove(t.id);
                VisionLog.Event("door_forget", $"\"id\":{t.id},\"center\":{VisionLog.V(t.center)},\"hits\":{t.hits},\"reported\":{VisionLog.B(t.reported)}");
                tracks.RemoveAt(i);
                continue;
            }
            if (t.hits < confirmScans || now - t.lastSeen > holdSeconds) continue;
            if (NearSceneDoor(t.center)) continue;   // уже есть точная дверь из модели комнаты
            mapDoors++;
            Vector3 pos = t.center;
            pos.y = map.floorY + 1.0f;
            SonarLandmarks.Report(t.id, LandmarkKind.Door, pos);
            if (!t.reported) VisionLog.Event("door_report", $"\"id\":{t.id},\"center\":{VisionLog.V(pos)},\"width\":{VisionLog.F(t.width)},\"hits\":{t.hits},\"dist\":{VisionLog.F(Mathf.Sqrt(Flat2(pos, head.position)))},\"yaw\":{VisionLog.F(YawTo(pos))}");
            t.reported = true;
        }

        // журнал: что нашёл скан (пишем, когда что-то есть, и раз в 10 сканов для фона)
        if (found.Count > 0 || tracks.Count > 0 || scanIndex % 10 == 0)
        {
            var sb = new StringBuilder(512);
            sb.Append($"\"scan\":{scanIndex},\"headPos\":{VisionLog.V(head.position)},\"headYaw\":{VisionLog.F(head.eulerAngles.y)},\"found\":[");
            for (int i = 0; i < found.Count; i++)
            {
                var p = found[i];
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"c\":{VisionLog.V(p.center)},\"w\":{VisionLog.F(p.width)},\"d\":{VisionLog.F(p.distance)},\"beyond\":{VisionLog.F(p.depthBeyond)},\"yaw\":{VisionLog.F(YawTo(p.center))}}}");
            }
            sb.Append("],\"tracks\":[");
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (i > 0) sb.Append(',');
                sb.Append($"{{\"id\":{t.id},\"c\":{VisionLog.V(t.center)},\"w\":{VisionLog.F(t.width)},\"hits\":{t.hits},\"rep\":{VisionLog.B(t.reported)},\"age\":{VisionLog.F(now - t.lastSeen)}}}");
            }
            sb.Append("],\"landmarks\":").Append(LandmarksJson());
            VisionLog.Event("doors", sb.ToString());
        }
    }

    void ForgetAll()
    {
        foreach (var t in tracks) if (t.reported) SonarLandmarks.Remove(t.id);
        tracks.Clear();
        mapDoors = 0;
    }

    bool NearSceneDoor(Vector3 c)
    {
        foreach (var s in sceneDoorCenters) if (Flat2(s, c) < sceneMergeDistance * sceneMergeDistance) return true;
        return false;
    }

    static float Flat2(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    float YawTo(Vector3 world)
    {
        Vector3 f = head.forward; f.y = 0f;
        Vector3 to = world - head.position; to.y = 0f;
        if (f.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f) return 0f;
        return Vector3.SignedAngle(f, to, Vector3.up);
    }

    /// <summary>Все ориентиры реестра (в том числе чужие) с расстоянием и углом: видно, что звук мог озвучить.</summary>
    string LandmarksJson()
    {
        var sb = new StringBuilder("[");
        bool first = true;
        float now = Time.time;
        foreach (var lm in SonarLandmarks.All)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append($"{{\"kind\":{VisionLog.Str(lm.kind.ToString())},\"d\":{VisionLog.F(Mathf.Sqrt(Flat2(lm.worldPos, head.position)))},\"yaw\":{VisionLog.F(YawTo(lm.worldPos))},\"fresh\":{VisionLog.B(lm.IsFresh(now, SonarLandmarks.ttl))},\"text\":{VisionLog.Str(lm.text)}}}");
        }
        return sb.Append(']').ToString();
    }

    // ---------------------------------------------------------------- модель комнаты
    void ScanScene()
    {
        sceneDoorCenters.Clear();
        sceneDoors = 0;
        sceneJson.Clear();
        var mruk = MRUK.Instance;
        if (mruk == null || !mruk.IsInitialized || mruk.Rooms == null)
        {
            VisionLog.Event("scene", $"\"mruk\":{VisionLog.B(mruk != null)},\"initialized\":{VisionLog.B(mruk != null && mruk.IsInitialized)},\"doors\":[]");
            return;
        }

        Vector3 hp = head.position;
        int rooms = 0, anchors = 0;
        foreach (var room in mruk.Rooms)
        {
            if (room == null || room.Anchors == null) continue;
            rooms++;
            foreach (var a in room.Anchors)
            {
                if (a == null) continue;
                anchors++;
                LandmarkKind kind;
                if ((a.Label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) kind = LandmarkKind.Door;
                else if (sceneWindows && (a.Label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) kind = LandmarkKind.Window;
                else continue;

                Vector3 c = a.PlaneRect.HasValue ? a.transform.TransformPoint(a.PlaneRect.Value.center) : a.transform.position;
                float d = Mathf.Sqrt(Flat2(c, hp));
                if (sceneJson.Length > 0) sceneJson.Append(',');
                sceneJson.Append($"{{\"kind\":{VisionLog.Str(kind.ToString())},\"c\":{VisionLog.V(c)},\"d\":{VisionLog.F(d)},\"yaw\":{VisionLog.F(YawTo(c))},\"w\":{VisionLog.F(a.PlaneRect.HasValue ? a.PlaneRect.Value.width : -1f)}}}");
                if (d > sceneRange) continue;

                // id по типу и положению (якоря комнаты неподвижны); GetInstanceID в Unity 6 устарел, GetEntityId в 2022 нет
                int id = SonarLandmarks.MakeId(kind, c, 0.5f);
                SonarLandmarks.Report(id, kind, c, null, true);
                if (kind == LandmarkKind.Door) { sceneDoorCenters.Add(c); sceneDoors++; }
            }
        }
        VisionLog.Event("scene", $"\"rooms\":{rooms},\"anchors\":{anchors},\"headPos\":{VisionLog.V(hp)},\"doors\":[{sceneJson}]");
    }

    // ---------------------------------------------------------------- ASCII-карта в журнал
    /// <summary>Карта вокруг пользователя: север (+z) вверху, восток (+x) справа. '#' высокое, 'o' низкое, '.' свободно, ' ' неизвестно, '@' голова, '^' взгляд, 'D' проём по карте, 'S' дверь из модели.</summary>
    void DumpMap()
    {
        int r = Mathf.RoundToInt(mapDumpRadius / map.cellSize);
        int n = 2 * r + 1;
        Vector3 hp = head.position;
        Vector2Int hc = map.ToCell(hp);
        var grid = new char[n, n];
        for (int zi = 0; zi < n; zi++)
            for (int xi = 0; xi < n; xi++)
            {
                var key = new Vector2Int(hc.x - r + xi, hc.y + r - zi);   // строка 0 = север
                char ch;
                switch (map.QueryCell(key))
                {
                    case NavZones.Cell.Tall: ch = '#'; break;
                    case NavZones.Cell.Low: ch = 'o'; break;
                    case NavZones.Cell.Free: ch = '.'; break;
                    default: ch = ' '; break;
                }
                grid[zi, xi] = ch;
            }
        void Mark(Vector3 world, char ch)
        {
            var k = map.ToCell(world);
            int xi = k.x - (hc.x - r), zi = (hc.y + r) - k.y;
            if (xi >= 0 && xi < n && zi >= 0 && zi < n) grid[zi, xi] = ch;
        }
        foreach (var t in tracks) Mark(t.center, t.reported ? 'D' : 'd');
        foreach (var s in sceneDoorCenters) Mark(s, 'S');
        Vector3 f = head.forward; f.y = 0f;
        if (f.sqrMagnitude > 1e-6f) { f.Normalize(); for (float d = 0.3f; d <= 1.0f; d += map.cellSize) Mark(hp + f * d, '^'); }
        Mark(hp, '@');

        var sb = new StringBuilder(n * (n + 1) + 256);
        sb.AppendLine($"# t={VisionLog.F(VisionLog.Now)} head=({VisionLog.F(hp.x)},{VisionLog.F(hp.z)}) yaw={head.eulerAngles.y:F0} cell={map.cellSize} радиус={mapDumpRadius} м; север (+z) вверху, восток (+x) справа");
        sb.AppendLine("# '#' высокое (>1.2 м), 'o' низкое, '.' свободно, ' ' неизвестно, '@' голова, '^' взгляд, 'D' проём (сообщён), 'd' проём (не подтверждён), 'S' дверь из модели комнаты");
        for (int zi = 0; zi < n; zi++)
        {
            for (int xi = 0; xi < n; xi++) sb.Append(grid[zi, xi]);
            sb.AppendLine();
        }
        mapDumps++;
        string file = VisionLog.SaveText($"map-{mapDumps:000}.txt", sb.ToString());
        VisionLog.Event("map", $"\"file\":{VisionLog.Str(file)},\"headPos\":{VisionLog.V(hp)},\"headYaw\":{VisionLog.F(head.eulerAngles.y)},\"occupied\":{map.occupiedCount},\"floorY\":{VisionLog.F(map.floorY)}");
    }

    void UpdateStatus()
    {
        Track nearest = null;
        float bd = float.MaxValue;
        Vector3 hp = head.position;
        foreach (var t in tracks)
        {
            if (!t.reported) continue;
            float d2 = Flat2(t.center, hp);
            if (d2 < bd) { bd = d2; nearest = t; }
        }
        if (nearest != null)
        {
            float yaw = YawTo(nearest.center);
            string side = yaw < -10f ? "слева" : yaw > 10f ? "справа" : "прямо";
            status = $"Двери: по карте {mapDoors}, из модели {sceneDoors}; ближайшая {Mathf.Sqrt(bd):F1} м {side}, ширина {nearest.width:F1} м";
        }
        else status = $"Двери: по карте {mapDoors}, из модели {sceneDoors}, кандидатов {tracks.Count}";
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
