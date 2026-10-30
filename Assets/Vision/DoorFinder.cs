using System.Collections;
using System.Collections.Generic;
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
    public int confirmScans = 3;
    [Tooltip("Проёмы ближе этого расстояния друг к другу считаются одним, м")]
    public float mergeDistance = 0.7f;
    [Tooltip("Проём, не подтверждавшийся столько секунд, забывается")]
    public float forgetAfter = 3f;

    [Header("Модель комнаты (MR Utility Kit)")]
    public bool useSceneModel = true;
    public bool sceneWindows = true;
    public float sceneRange = 6f;
    public float sceneInterval = 3f;

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
    float tick, sceneTick;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<DoorFinder>() == null)
            new GameObject("DoorFinder").AddComponent<DoorFinder>();
    }

    void Awake() { Instance = this; }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);
        map = ObstacleMap.Instance;
        head = SonarCommon.FindHead();
        if (map == null || head == null) { status = "двери: нет карты"; yield break; }
        query = map.QueryCell;
        status = "двери: ищу";
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

        UpdateStatus();
    }

    // ---------------------------------------------------------------- проёмы по карте
    void ScanMap(float now)
    {
        NavZones.FindPassages(query, map.cellSize, head.position, head.forward, scanYaw, scanStepDeg, range,
                              minWidth, maxWidth, minDepthBeyond, 0.25f, found);

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
                tracks.RemoveAt(i);
                continue;
            }
            if (t.hits < confirmScans || now - t.lastSeen > interval * 1.5f) continue;
            if (NearSceneDoor(t.center)) continue;   // уже есть точная дверь из модели комнаты
            mapDoors++;
            Vector3 pos = t.center;
            pos.y = map.floorY + 1.0f;
            SonarLandmarks.Report(t.id, LandmarkKind.Door, pos);
            t.reported = true;
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
        foreach (var s in sceneDoorCenters) if (Flat2(s, c) < 1f) return true;
        return false;
    }

    static float Flat2(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    // ---------------------------------------------------------------- модель комнаты
    void ScanScene()
    {
        sceneDoorCenters.Clear();
        sceneDoors = 0;
        var mruk = MRUK.Instance;
        if (mruk == null || !mruk.IsInitialized || mruk.Rooms == null) return;

        Vector3 hp = head.position;
        foreach (var room in mruk.Rooms)
        {
            if (room == null || room.Anchors == null) continue;
            foreach (var a in room.Anchors)
            {
                if (a == null) continue;
                LandmarkKind kind;
                if ((a.Label & MRUKAnchor.SceneLabels.DOOR_FRAME) != 0) kind = LandmarkKind.Door;
                else if (sceneWindows && (a.Label & MRUKAnchor.SceneLabels.WINDOW_FRAME) != 0) kind = LandmarkKind.Window;
                else continue;

                Vector3 c = a.PlaneRect.HasValue ? a.transform.TransformPoint(a.PlaneRect.Value.center) : a.transform.position;
                if (Flat2(c, hp) > sceneRange * sceneRange) continue;

                // id по типу и положению (якоря комнаты неподвижны); GetInstanceID в Unity 6 устарел, GetEntityId в 2022 нет
                int id = SonarLandmarks.MakeId(kind, c, 0.5f);
                SonarLandmarks.Report(id, kind, c, null, true);
                if (kind == LandmarkKind.Door) { sceneDoorCenters.Add(c); sceneDoors++; }
            }
        }
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
            Vector3 f = head.forward; f.y = 0f;
            Vector3 to = nearest.center - hp; to.y = 0f;
            float yaw = (f.sqrMagnitude > 1e-6f && to.sqrMagnitude > 1e-6f) ? Vector3.SignedAngle(f, to, Vector3.up) : 0f;
            string side = yaw < -10f ? "слева" : yaw > 10f ? "справа" : "прямо";
            status = $"Двери: по карте {mapDoors}, из модели {sceneDoors}; ближайшая {Mathf.Sqrt(bd):F1} м {side}, ширина {nearest.width:F1} м";
        }
        else status = $"Двери: по карте {mapDoors}, из модели {sceneDoors}";
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
