using System.Collections;
using System.Collections.Generic;
using System.Text;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Карта препятствий в реальном времени (2D occupancy grid, вид сверху, мировые координаты).
/// Только Depth API (EnvironmentRaycastManager), без привязки к разметке комнаты. Подходит для ходьбы.
///
///  1) Каждый кадр пускается пачка лучей из головы в поле зрения (последовательность Хэлтона):
///     столько, сколько влезает в scanBudgetMs, либо фиксированные raysPerFrame (useTimeBudget = false).
///  2) Попадание на высоте [stepHeight .. headClearance] над полом = препятствие (+log-odds).
///     Попадание в пол = проходимо. Потолок игнорируется.
///  3) Вдоль луча до точки попадания клетки "очищаются" (-log-odds): исчезнувшие предметы стираются.
///  4) Высота разбита на 3 слоя, чтобы луч над низким предметом не стирал его.
///  5) Высота пола учится по лучам вниз.
///  6) Минимап (вверх = направление взгляда) + плитки на полу + API для звука/вибрации.
///  7) Зоны прохода (слева / прямо / справа): по карте считается, сколько метров можно пройти в каждой зоне,
///     и ищутся проёмы (двери, проходы) в стенах; этим пользуется AudioSonar. Дверные проёмы берутся также
///     из модели комнаты (MR Utility Kit), если комната размечена в настройках шлема.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class ObstacleMap : MonoBehaviour
{
    public static ObstacleMap Instance { get; private set; }

    [Header("Сетка")]
    public float cellSize = 0.1f;
    [Tooltip("Клетки дальше от игрока удаляются")]
    public float keepRadius = 40f;

    [Header("Высотное окно препятствий (над полом)")]
    public float stepHeight = 0.15f;
    public float headClearance = 1.9f;
    [Tooltip("Рост до глаз: используется, пока пол ещё не определён по лучам")]
    public float fallbackEyeHeight = 1.6f;

    [Header("Лучи")]
    public float maxDistance = 4f;
    [Tooltip("Если падает FPS - уменьшайте")]
    public int raysPerFrame = 64;
    public float yawRange = 110f;
    [Tooltip("Угол относительно взгляда: минус = ВВЕРХ, плюс = ВНИЗ (так работает Quaternion.Euler)")]
    public float pitchMin = -25f;
    public float pitchMax = 45f;
    [Header("Зона под ноги (ступеньки, пороги)")]
    [Tooltip("Доля лучей, направленных в узкую зону впереди-внизу. 0 = выключено")]
    [Range(0f, 0.8f)] public float footShare = 0.35f;
    public float footYaw = 35f;
    public float footPitchMin = 10f;
    public float footPitchMax = 50f;
    [Header("Память")]
    [Tooltip("За сколько секунд карта забывает клетки, которые не подтверждаются лучами. 0 = не забывать")]
    public float memorySeconds = 4f;

    [Header("Скорость лучей")]
    [Tooltip("Вкл: за кадр пускается столько лучей, сколько влезает в бюджет времени (авто-подбор под FPS). Выкл: фиксированные raysPerFrame")]
    public bool useTimeBudget = true;
    [Tooltip("Сколько миллисекунд за кадр можно тратить на лучи. 72 Гц = 13.9 мс на кадр, 90 Гц = 11 мс, 120 Гц = 8.3 мс")]
    public float scanBudgetMs = 5f;
    public int minRaysPerFrame = 32;
    public int maxRaysPerFrame = 512;

    [Header("Вероятности (log-odds)")]
    public float hitAdd = 0.9f;
    public float missAdd = -0.35f;
    public float minL = -2f;
    public float maxL = 5f;
    public float occThreshold = 1.0f;

    [Header("Визуализация")]
    public bool showMinimap = true;
    public int minimapPixels = 100;  // 100 * 0.1 м = 10 м (радиус 5 м)
    public float minimapSize = 0.38f;
    public Vector3 minimapOffset = new Vector3(-0.40f, -0.04f, 1.0f);
    public bool show3DTiles = true;
    public int max3DTiles = 250;
    public float tiles3DRadius = 3f;
    public float warnDistance = 1.2f;
    public float dangerDistance = 0.6f;

    [Header("Зоны прохода (для звука)")]
    [Tooltip("На сколько зон по горизонтали делится обзор: 3 = слева / прямо / справа, 2 = слева / справа")]
    public int zoneCount = 3;
    [Tooltip("Полный угол обзора зон, градусы")]
    public float zoneYawRange = 120f;
    [Tooltip("Ширина «коридора» при проверке, пройдёт ли человек, м")]
    public float bodyWidth = 0.6f;
    [Tooltip("Свободно хотя бы столько метров = «путь свободен»")]
    public float clearDistance = 2f;
    [Tooltip("Свободно меньше этого = зона перекрыта")]
    public float blockedDistance = 1f;
    [Tooltip("Доля просмотренных датчиком клеток вдоль пути, чтобы верить, что зона свободна")]
    [Range(0f, 1f)] public float zoneMinSeen = 0.25f;
    [Tooltip("Дальше этого зоны не проверяются, м")]
    public float zoneRange = 4f;
    public int zoneRaysPerZone = 5;

    [Header("Проёмы: двери и проходы")]
    [Tooltip("Искать проёмы в стенах по карте: пара высоких препятствий, между которыми видно насквозь")]
    public bool detectPassages = true;
    [Tooltip("Брать дверные проёмы из модели комнаты (MR Utility Kit), если комната размечена в настройках шлема")]
    public bool useScenePassages = true;
    public float passageMinWidth = 0.6f;
    public float passageMaxWidth = 1.6f;
    [Tooltip("За проёмом должно быть свободно хотя бы столько метров (иначе это ниша или шкаф)")]
    public float passageMinDepth = 1f;
    [Tooltip("Дальше этого проёмы не ищутся, м")]
    public float passageRange = 5f;
    [Tooltip("Полуширина скана проёмов, градусы от взгляда в каждую сторону")]
    public float passageScanYaw = 90f;

    [Header("Состояние (только чтение)")]
    public float floorY;
    public int occupiedCount;
    public float nearestDistance = -1f;
    public Vector3 nearestPoint;
    public int lastRays, lastHits;
    public float raysPerSecond;
    [Tooltip("Сколько мс ушло на лучи в последнем кадре")]
    public float scanMs;
    [Tooltip("Зона, куда можно идти (-1 = свободной нет)")]
    public int bestZone = -1;
    [Tooltip("Все зоны перекрыты ближе blockedDistance")]
    public bool allBlocked;
    public int passageCount;

    /// <summary>Статусы лучей за последний кадр (для диагностики: почему нет попаданий).</summary>
    public readonly Dictionary<EnvironmentRaycastHitStatus, int> statusCounts = new();

    /// <summary>Каждая точка попадания луча в мировых координатах (до фильтра по высоте). Нужна для облака точек.</summary>
    public event System.Action<Vector3> OnDepthHit;

    /// <summary>Зоны прохода (обновляются StatsInterval раз в секунду). null, пока карта не готова.</summary>
    public NavZones.Zone[] Zones { get; private set; }

    /// <summary>Проёмы (двери, проходы) в радиусе passageRange: по карте и из модели комнаты.</summary>
    public readonly List<NavZones.Passage> passages = new();

    const int Layers = 3;
    static readonly float[] LayerTop = { 0.6f, 1.2f, float.MaxValue };

    const float MinHitDistance = 0.2f;    // ближе - считаем помехой (руки, шлем), не точкой
    const float CarveStart = 0.25f;       // с какого расстояния от головы начинаем "очищать" клетки вдоль луча
    const float SeenEps = 0.05f;          // |log-odds| меньше этого = клетка пустая, ничего не знаем
    const float StatsInterval = 0.1f;     // как часто обновлять статистику, минимап, плитки и зоны, сек
    const float PassageInterval = 0.5f;   // как часто искать проёмы, сек
    const float PruneInterval = 2f;       // как часто удалять пустые и далёкие клетки, сек
    const float PassageScanStepDeg = 2f;  // шаг полярного скана проёмов

    class Cell { public float[] l = new float[Layers]; }

    readonly Dictionary<Vector2Int, Cell> cells = new();
    float learnedFloorY = float.NaN;
    float floorCandidate = float.MaxValue;
    bool floorInit;
    readonly List<Vector2Int> tmpKeys = new();
    readonly List<Transform> tiles = new();

    Transform head;
    EnvironmentRaycastManager raycaster;
    Material matGreen, matYellow, matRed, mapMat;
    Texture2D mapTex;
    Color32[] mapPixels;
    TextMesh mapLabel;
    Vector3 lastFwd = Vector3.forward;
    int sampleIndex;
    float tick, pruneTick, passageTick;
    int raysThisSecondCounter;
    float rayTimer;
    NavZones.CellQuery cellQuery;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ObstacleMap>() == null)
            new GameObject("ObstacleMap").AddComponent<ObstacleMap>();
    }

    // ---------------------------------------------------------------- инициализация
    IEnumerator Start()
    {
        Instance = this;
        cellQuery = QueryCell;
        matGreen = MakeMat(Color.green);
        matYellow = MakeMat(Color.yellow);
        matRed = MakeMat(Color.red);

        yield return SonarCommon.WaitUntilReady(() => FindFirstObjectByType<EnvironmentRaycastManager>() != null);

        head = SonarCommon.FindHead();
        raycaster = FindFirstObjectByType<EnvironmentRaycastManager>();
        if (head == null || raycaster == null)
        {
            Debug.LogError("ObstacleMap: нет головы или EnvironmentRaycastManager.");
            yield break;
        }

        if (showMinimap) CreateMinimap();
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        Destroy(matGreen);
        Destroy(matYellow);
        Destroy(matRed);
        Destroy(mapMat);
        Destroy(mapTex);
    }

    static Material MakeMat(Color c)
    {
        var m = SonarCommon.NewSpriteMaterial();
        m.color = c;
        return m;
    }

    // ---------------------------------------------------------------- главный цикл
    void Update()
    {
        if (head == null || raycaster == null) return;

        UpdateFloor();
        Scan();

        rayTimer += Time.deltaTime;
        if (rayTimer >= 1f)
        {
            raysPerSecond = raysThisSecondCounter / rayTimer;
            raysThisSecondCounter = 0;
            rayTimer = 0f;
        }

        tick += Time.deltaTime;
        if (tick >= StatsInterval)
        {
            if (memorySeconds > 0f) DecayMap(tick);
            tick = 0f;
            UpdateStats();
            UpdateZones();
            if (showMinimap) DrawMinimap();
            if (show3DTiles) Update3DTiles();
        }

        passageTick += Time.deltaTime;
        if (passageTick >= PassageInterval)
        {
            passageTick = 0f;
            UpdatePassages();
        }

        pruneTick += Time.deltaTime;
        if (pruneTick >= PruneInterval)
        {
            pruneTick = 0f;
            Prune();
        }
    }

    void UpdateFloor()
    {
        float target = !float.IsNaN(learnedFloorY)
            ? learnedFloorY
            : head.position.y - fallbackEyeHeight;

        if (!floorInit) { floorY = target; floorInit = true; }
        else floorY = Mathf.Lerp(floorY, target, 0.1f);
    }

    // ---------------------------------------------------------------- сканирование
    void Scan()
    {
        Vector3 origin = head.position;
        int hits = 0;
        floorCandidate = float.MaxValue;
        statusCounts.Clear();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int limit = useTimeBudget ? maxRaysPerFrame : raysPerFrame;
        int fired = 0;
        Quaternion headRot = head.rotation;

        while (fired < limit)
        {
            sampleIndex = sampleIndex % 100000 + 1;
            float yaw, pitch;
            if (footShare > 0f && Halton(sampleIndex, 5) < footShare)
            {
                yaw = Mathf.Lerp(-footYaw, footYaw, Halton(sampleIndex, 2));
                pitch = Mathf.Lerp(footPitchMin, footPitchMax, Halton(sampleIndex, 3));
            }
            else
            {
                yaw = Mathf.Lerp(-yawRange * 0.5f, yawRange * 0.5f, Halton(sampleIndex, 2));
                pitch = Mathf.Lerp(pitchMin, pitchMax, Halton(sampleIndex, 3));
            }
            Vector3 dir = headRot * (Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward);

            if (ProcessRay(origin, dir)) hits++;
            fired++;

            if (useTimeBudget && fired >= minRaysPerFrame && sw.Elapsed.TotalMilliseconds >= scanBudgetMs) break;
        }
        scanMs = (float)sw.Elapsed.TotalMilliseconds;

        // обучение высоты пола по лучам вниз
        if (floorCandidate < float.MaxValue)
        {
            float eye = head.position.y - floorCandidate;
            if (eye > 0.9f && eye < 2.2f)
                learnedFloorY = float.IsNaN(learnedFloorY) ? floorCandidate : Mathf.Lerp(learnedFloorY, floorCandidate, 0.05f);
        }

        lastRays = fired;
        lastHits = hits;
        raysThisSecondCounter += fired;
    }

    bool ProcessRay(Vector3 origin, Vector3 dir)
    {
        bool ok = raycaster.Raycast(new Ray(origin, dir), out EnvironmentRaycastHit hit, maxDistance);
        statusCounts.TryGetValue(hit.status, out int sc);
        statusCounts[hit.status] = sc + 1;

        if (!ok || hit.status != EnvironmentRaycastHitStatus.Hit) return false;

        float dist = Vector3.Distance(origin, hit.point);
        if (dist < MinHitDistance) return false;

        OnDepthHit?.Invoke(hit.point);

        if (dir.y < -0.55f && dist < 2.5f && hit.point.y < floorCandidate) floorCandidate = hit.point.y;

        float h = hit.point.y - floorY;

        if (h < stepHeight)
        {
            // пол: путь свободен на всю длину луча
            CarveFree(origin, dir, dist);
            return true;
        }

        if (h > headClearance)
        {
            // потолок / что-то выше головы: свободно до него, препятствием не считаем
            CarveFree(origin, dir, dist);
            return true;
        }

        // препятствие в рабочем диапазоне высот
        CarveFree(origin, dir, dist - cellSize * 1.2f);
        Vector3 p = hit.point + dir * 0.03f;
        AddLog(ToCell(p), LayerOf(h), hitAdd);
        return true;
    }

    void CarveFree(Vector3 origin, Vector3 dir, float until)
    {
        var last = new Vector2Int(int.MinValue, int.MinValue);
        float step = cellSize * 0.8f;
        for (float d = CarveStart; d < until; d += step)
        {
            Vector3 p = origin + dir * d;
            float h = p.y - floorY;
            if (h < stepHeight || h > headClearance) continue;
            var key = ToCell(p);
            if (key == last) continue;
            last = key;
            AddLog(key, LayerOf(h), missAdd);
        }
    }

    static int LayerOf(float h)
    {
        for (int i = 0; i < Layers; i++) if (h < LayerTop[i]) return i;
        return Layers - 1;
    }

    void AddLog(Vector2Int key, int layer, float delta)
    {
        if (!cells.TryGetValue(key, out var c))
        {
            if (delta < 0f) return; // "свободно" для неизвестной клетки не создаём
            c = new Cell();
            cells[key] = c;
        }
        c.l[layer] = Mathf.Clamp(c.l[layer] + delta, minL, maxL);
    }

    static float Halton(int index, int b)
    {
        float f = 1f, r = 0f;
        while (index > 0)
        {
            f /= b;
            r += f * (index % b);
            index /= b;
        }
        return r;
    }

    // ---------------------------------------------------------------- зоны прохода и проёмы
    /// <summary>Что карта знает о клетке: для NavZones.</summary>
    NavZones.Cell QueryCell(Vector2Int key)
    {
        if (!cells.TryGetValue(key, out var c)) return NavZones.Cell.Unknown;
        if (c.l[Layers - 1] >= occThreshold) return NavZones.Cell.Tall;          // выше 1.2 м: стены, косяки, шкафы
        for (int i = 0; i < Layers - 1; i++) if (c.l[i] >= occThreshold) return NavZones.Cell.Low;
        return IsCellSeen(c) ? NavZones.Cell.Free : NavZones.Cell.Unknown;
    }

    void UpdateZones()
    {
        int n = Mathf.Max(1, zoneCount);
        if (Zones == null || Zones.Length != n) Zones = new NavZones.Zone[n];
        NavZones.EvaluateZones(cellQuery, cellSize, head.position, head.forward, zoneYawRange, zoneRaysPerZone,
                               bodyWidth, zoneRange, CarveStart, Zones);
        bestZone = NavZones.BestZone(Zones, clearDistance, zoneMinSeen);
        allBlocked = true;
        for (int i = 0; i < n; i++)
            if (Zones[i].free >= blockedDistance) { allBlocked = false; break; }
    }

    void UpdatePassages()
    {
        passages.Clear();
        if (detectPassages)
            NavZones.FindPassages(cellQuery, cellSize, head.position, head.forward, passageScanYaw, PassageScanStepDeg,
                                  passageRange, passageMinWidth, passageMaxWidth, passageMinDepth, CarveStart, passages);
        if (useScenePassages) AddScenePassages();
        passageCount = passages.Count;
    }

    /// <summary>Дверные проёмы из модели комнаты (MR Utility Kit). Если проём уже найден по карте, он только помечается.</summary>
    void AddScenePassages()
    {
        var mruk = MRUK.Instance;
        if (mruk == null || !mruk.IsInitialized || mruk.Rooms == null) return;

        Vector3 hp = head.position;
        foreach (var room in mruk.Rooms)
        {
            if (room == null) continue;
            foreach (var a in room.Anchors)
            {
                if (a == null || (a.Label & MRUKAnchor.SceneLabels.DOOR_FRAME) == 0) continue;

                Vector3 c = a.PlaneRect.HasValue ? a.transform.TransformPoint(a.PlaneRect.Value.center) : a.transform.position;
                c.y = floorY;
                float d = Mathf.Sqrt(HorizSqrDistance(c, hp));
                if (d > passageRange) continue;

                int near = -1;
                for (int i = 0; i < passages.Count; i++)
                    if (HorizSqrDistance(passages[i].center, c) < 1f) { near = i; break; }

                if (near >= 0)
                {
                    var p = passages[near];
                    p.fromScene = true;
                    passages[near] = p;
                }
                else
                {
                    passages.Add(new NavZones.Passage
                    {
                        center = c,
                        width = a.PlaneRect.HasValue ? a.PlaneRect.Value.width : 0.9f,
                        distance = d,
                        depthBeyond = 0f,
                        open = false,
                        fromScene = true
                    });
                }
            }
        }
    }

    /// <summary>Ближайший проём (false, если проёмов нет).</summary>
    public bool TryGetNearestPassage(out NavZones.Passage best)
    {
        best = default;
        float bd = float.MaxValue;
        foreach (var p in passages)
            if (p.distance < bd) { bd = p.distance; best = p; }
        return bd < float.MaxValue;
    }

    /// <summary>Угол на точку относительно взгляда по горизонтали, градусы: минус = слева, плюс = справа.</summary>
    public float YawTo(Vector3 world)
    {
        if (head == null) return 0f;
        Vector3 f = head.forward;
        f.y = 0f;
        Vector3 to = world - head.position;
        to.y = 0f;
        if (f.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f) return 0f;
        return Vector3.SignedAngle(f, to, Vector3.up);
    }

    public string ZoneName(int i)
    {
        if (Zones == null || i < 0 || i >= Zones.Length) return "?";
        if (Zones.Length == 3) return i == 0 ? "слева" : i == 1 ? "прямо" : "справа";
        if (Zones.Length == 2) return i == 0 ? "слева" : "справа";
        return $"{Zones[i].CenterYaw:F0}°";
    }

    /// <summary>Строка для HUD: состояние зон и ближайший проём.</summary>
    public string ZonesText()
    {
        if (Zones == null) return "Зоны: карта не готова";
        var sb = new StringBuilder("Зоны: ");
        for (int i = 0; i < Zones.Length; i++)
        {
            var z = Zones[i];
            string state;
            if (z.free >= clearDistance) state = z.seen >= zoneMinSeen ? "свободно" : "не видно";
            else if (z.free < blockedDistance) state = $"ПЕРЕКРЫТО {z.free:F1}";
            else state = $"{z.free:F1} м";
            if (i > 0) sb.Append(" | ");
            sb.Append(ZoneName(i)).Append(' ').Append(state);
        }
        if (bestZone >= 0) sb.Append("  -> ").Append(ZoneName(bestZone));
        else if (allBlocked) sb.Append("  -> ТУПИК");

        if (TryGetNearestPassage(out var p))
        {
            float yaw = YawTo(p.center);
            sb.Append($"\nПроём: {p.distance:F1} м, {Mathf.Abs(yaw):F0}° {(yaw < -5f ? "влево" : yaw > 5f ? "вправо" : "прямо")}, ширина {p.width:F1} м");
            sb.Append(p.open ? " (виден насквозь)" : " (дверь из модели комнаты)");
            if (p.open && p.fromScene) sb.Append(" [и в модели]");
            if (passages.Count > 1) sb.Append($"  всего {passages.Count}");
        }
        else sb.Append("\nПроёмов не видно");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- публичный API
    public Vector2Int ToCell(Vector3 p)
    {
        return new Vector2Int(Mathf.FloorToInt(p.x / cellSize), Mathf.FloorToInt(p.z / cellSize));
    }

    public Vector3 CellCenter(Vector2Int k)
    {
        return new Vector3((k.x + 0.5f) * cellSize, floorY, (k.y + 0.5f) * cellSize);
    }

    public bool IsOccupiedCell(Vector2Int key)
    {
        return cells.TryGetValue(key, out var c) && IsCellOcc(c);
    }

    public bool IsOccupied(Vector3 world) { return IsOccupiedCell(ToCell(world)); }

    /// <summary>Расстояние по горизонтали до ближайшего препятствия вдоль направления (-1, если нет).</summary>
    public float DistanceAlong(Vector3 dir, float max)
    {
        if (head == null) return -1f;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-6f) return -1f;
        dir.Normalize();
        Vector3 o = head.position;
        for (float d = cellSize; d <= max; d += cellSize * 0.5f)
            if (IsOccupied(o + dir * d)) return d;
        return -1f;
    }

    public void ClearMap() { cells.Clear(); }

    // ---------------------------------------------------------------- обслуживание
    void UpdateStats()
    {
        int occ = 0;
        float best = float.MaxValue;
        Vector3 bestP = Vector3.zero;
        Vector3 hp = head.position;

        foreach (var kv in cells)
        {
            if (!IsCellOcc(kv.Value)) continue;
            occ++;
            Vector3 c = CellCenter(kv.Key);
            float d2 = HorizSqrDistance(c, hp);
            if (d2 < best) { best = d2; bestP = c; }
        }

        occupiedCount = occ;
        nearestDistance = best < float.MaxValue ? Mathf.Sqrt(best) : -1f;
        nearestPoint = bestP;
    }

    static float HorizSqrDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dz = a.z - b.z;
        return dx * dx + dz * dz;
    }

    /// <summary>Цвет по расстоянию до ближайшего препятствия: красный (опасно), жёлтый (близко), иначе белый.</summary>
    public Color LevelColor()
    {
        if (nearestDistance < 0f) return Color.white;
        if (nearestDistance < dangerDistance) return Color.red;
        return nearestDistance < warnDistance ? Color.yellow : Color.white;
    }

    /// <summary>Плавное забывание: все значения log-odds стремятся к нулю с постоянной времени memorySeconds.</summary>
    void DecayMap(float dt)
    {
        float f = Mathf.Exp(-dt / memorySeconds);
        foreach (var kv in cells)
        {
            var l = kv.Value.l;
            for (int i = 0; i < Layers; i++) l[i] *= f;
        }
    }

    bool IsCellOcc(Cell c)
    {
        for (int i = 0; i < Layers; i++) if (c.l[i] >= occThreshold) return true;
        return false;
    }

    /// <summary>Клетка хоть что-то "знает" (свободно или занято), а не пустая.</summary>
    static bool IsCellSeen(Cell c)
    {
        for (int i = 0; i < Layers; i++) if (Mathf.Abs(c.l[i]) > SeenEps) return true;
        return false;
    }

    void Prune()
    {
        tmpKeys.Clear();
        Vector3 hp = head.position;
        float r2 = keepRadius * keepRadius;
        foreach (var kv in cells)
        {
            bool far = HorizSqrDistance(CellCenter(kv.Key), hp) > r2;
            if (far || !IsCellSeen(kv.Value)) tmpKeys.Add(kv.Key);
        }
        foreach (var k in tmpKeys) cells.Remove(k);
    }

    // ---------------------------------------------------------------- минимап
    void CreateMinimap()
    {
        mapTex = new Texture2D(minimapPixels, minimapPixels, TextureFormat.RGBA32, false);
        mapTex.filterMode = FilterMode.Point;
        mapTex.wrapMode = TextureWrapMode.Clamp;
        mapPixels = new Color32[minimapPixels * minimapPixels];

        var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(quad.GetComponent<Collider>());
        quad.name = "ObstacleMinimap";
        quad.transform.SetParent(head, false);
        quad.transform.localPosition = minimapOffset;
        quad.transform.localScale = Vector3.one * minimapSize;
        mapMat = SonarCommon.NewSpriteMaterial();
        mapMat.mainTexture = mapTex;
        quad.GetComponent<Renderer>().sharedMaterial = mapMat;

        var lg = new GameObject("MinimapLabel");
        lg.transform.SetParent(quad.transform, false);
        lg.transform.localPosition = new Vector3(0f, -0.56f, 0f);
        lg.transform.localScale = Vector3.one * (1f / minimapSize);
        mapLabel = lg.AddComponent<TextMesh>();
        mapLabel.characterSize = 0.008f;
        mapLabel.fontSize = 48;
        mapLabel.anchor = TextAnchor.UpperCenter;
        mapLabel.color = Color.white;
    }

    void DrawMinimap()
    {
        if (mapTex == null) return;

        Vector3 fwd = head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude > 1e-4f) lastFwd = fwd.normalized;
        fwd = lastFwd;
        Vector3 right = new Vector3(fwd.z, 0f, -fwd.x);
        Vector3 hp = head.position;
        int n = minimapPixels;
        float half = n * 0.5f;

        var unknown = new Color32(0, 0, 0, 90);
        var freeC = new Color32(0, 150, 0, 120);
        var occC = new Color32(255, 30, 30, 240);

        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                Vector3 off = right * ((x + 0.5f - half) * cellSize) + fwd * ((y + 0.5f - half) * cellSize);
                var key = ToCell(hp + off);

                Color32 col = unknown;
                if (cells.TryGetValue(key, out var c))
                    col = IsCellOcc(c) ? occC : IsCellSeen(c) ? freeC : unknown;
                mapPixels[y * n + x] = col;
            }
        }

        // свободный путь: зелёный пунктир из центра; проёмы: голубые квадраты
        if (bestZone >= 0 && Zones != null && bestZone < Zones.Length)
        {
            var z = Zones[bestZone];
            var pathC = new Color32(80, 255, 80, 255);
            for (float d = 0.5f; d <= z.free; d += 0.3f) PaintMark(hp + z.freeDir * d, hp, fwd, right, n, half, pathC, 0);
        }
        var passC = new Color32(0, 220, 255, 255);
        foreach (var p in passages) PaintMark(p.center, hp, fwd, right, n, half, passC, 1);

        // игрок в центре + метка направления
        int c0 = n / 2;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                mapPixels[(c0 + dy) * n + (c0 + dx)] = new Color32(255, 255, 255, 255);
        for (int k = 2; k <= 4; k++) mapPixels[(c0 + k) * n + c0] = new Color32(255, 255, 255, 255);

        mapTex.SetPixels32(mapPixels);
        mapTex.Apply(false);

        if (mapLabel != null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(nearestDistance >= 0 ? $"Ближайшее: {nearestDistance:F1} м" : "Ближайшее: -");
            sb.Append($"Клеток: {occupiedCount}  лучи {lastHits}/{lastRays}  {raysPerSecond:F0}/с  проёмов {passageCount}");
            mapLabel.text = sb.ToString();
            mapLabel.color = LevelColor();
        }
    }

    /// <summary>Закрашивает на минимапе квадрат (2r+1) пикселей вокруг мировой точки.</summary>
    void PaintMark(Vector3 world, Vector3 hp, Vector3 fwd, Vector3 right, int n, float half, Color32 col, int r)
    {
        Vector3 off = world - hp;
        int x = Mathf.FloorToInt(Vector3.Dot(off, right) / cellSize + half);
        int y = Mathf.FloorToInt(Vector3.Dot(off, fwd) / cellSize + half);
        for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                int px = x + dx, py = y + dy;
                if (px < 0 || py < 0 || px >= n || py >= n) continue;
                mapPixels[py * n + px] = col;
            }
    }

    // ---------------------------------------------------------------- плитки на полу
    void Update3DTiles()
    {
        int used = 0;
        Vector3 hp = head.position;
        float r2 = tiles3DRadius * tiles3DRadius;

        foreach (var kv in cells)
        {
            if (used >= max3DTiles) break;
            if (!IsCellOcc(kv.Value)) continue;
            Vector3 c = CellCenter(kv.Key);
            float d2 = HorizSqrDistance(c, hp);
            if (d2 > r2) continue;
            PlaceTile(used++, new Vector3(c.x, floorY + 0.015f, c.z), Mathf.Sqrt(d2));
        }
        for (int i = used; i < tiles.Count; i++) tiles[i].gameObject.SetActive(false);
    }

    void PlaceTile(int index, Vector3 pos, float dist)
    {
        while (tiles.Count <= index)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(transform, false);
            g.transform.localScale = new Vector3(cellSize * 0.9f, 0.02f, cellSize * 0.9f);
            tiles.Add(g.transform);
        }
        var t = tiles[index];
        t.gameObject.SetActive(true);
        t.position = pos;
        t.GetComponent<Renderer>().sharedMaterial =
            dist < dangerDistance ? matRed : dist < warnDistance ? matYellow : matGreen;
    }
}
