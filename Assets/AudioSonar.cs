using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// Звуковой сонар для слепых: пространственные звуки (Meta XR Audio SDK, HRTF) из направления объекта.
/// Словарь звуков (все короткие, учатся за минуту):
///  - ПИСК (щипок / колокольчик) - препятствие в зоне. Чем ближе, тем чаще и выше нота, у самого
///    препятствия почти сплошной тон и вибрация контроллеров. Низкий тембр = предмет ниже пояса,
///    высокий звонкий = на уровне корпуса или головы (трость такое не находит). Зон три: слева / прямо / справа.
///  - ДВЕ ВОСХОДЯЩИЕ НОТЫ «путь свободен» - раз в pathRepeat секунд из направления, куда можно идти
///    (по зонам ObstacleMap: свободно хотя бы clearDistance; если прямо свободно, зовёт прямо).
///  - ДВА НИЗКИХ УДАРА «тупик» - все зоны перекрыты ближе blockedDistance: остановиться, повернуться.
///  - СТУК «дверь» - из места проёма. Два удара = проход виден насквозь по карте (можно идти),
///    один удар = дверной проём из модели комнаты (дверь может быть закрыта).
///  - НИСХОДЯЩИЙ СВИСТ - обрыв / ступеньки вниз.
///  - КУРОК (любой контроллер) - обзор по запросу: слева, прямо, справа (свободно / писк / тупик),
///    затем ближайшая дверь. Работает и при выключенном звуке.
///  - A или X - звук вкл/выкл (B/Y остаются для камер).
/// Скрипт создаётся сам при старте сцены.
/// ВАЖНО: это помощь, а не замена трости или собаки-проводника. Датчик не видит стекло и тонкие предметы.
/// </summary>
public class AudioSonar : MonoBehaviour
{
    [Header("Зоны писков (совпадают с зонами карты)")]
    [Tooltip("3 = слева / прямо / справа. Меняйте вместе с zoneCount в ObstacleMap")]
    public int sectors = 3;
    [Tooltip("Дальше этого расстояния зона молчит (не больше maxDistance в ObstacleMap)")]
    public float maxRange = 3f;
    public float minRange = 0.2f;
    [Tooltip("Окно накопления ближайшей точки, сек. Меньше = быстрее реакция, но больше дрожания")]
    public float windowSec = 0.12f;

    [Header("Ритм и громкость писков")]
    public float minInterval = 0.09f;   // у самого препятствия
    public float maxInterval = 0.8f;    // на дальней границе
    public float nearVolume = 0.9f;
    public float farVolume = 0.25f;
    [Tooltip("Одновременно звучат только столько ближайших зон")]
    public int activeSectors = 3;
    [Tooltip("Минимальная пауза между началами любых звуков, сек (звуки не накладываются)")]
    public float minOnsetGap = 0.06f;
    [Tooltip("На сколько ступеней пентатонической гаммы делится расстояние: ближе = выше нота")]
    public int noteBands = 6;
    public float masterVolume = 1f;
    [Tooltip("Источник ставится не дальше этого расстояния от головы, чтобы громкость задавалась нами, а не спадом звука")]
    public float maxSourceDistance = 1.5f;

    [Header("Тембр по высоте")]
    [Tooltip("Точка выше этого над полом = высокий резкий тон")]
    public float highFrom = 0.9f;

    [Header("Путь свободен / тупик (по зонам ObstacleMap)")]
    [Tooltip("Две восходящие ноты из направления, куда можно идти")]
    public bool pathCue = true;
    public float pathRepeat = 1.6f;
    public float pathVolume = 0.55f;
    [Tooltip("На каком расстоянии от головы ставить источник сигнала «путь свободен»")]
    public float pathSourceDistance = 1.2f;
    [Tooltip("Два низких удара, когда все зоны перекрыты")]
    public bool blockedCue = true;
    public float blockedRepeat = 1.5f;
    public float blockedVolume = 0.7f;

    [Header("Двери и проёмы")]
    [Tooltip("Стук из места проёма: два удара = виден насквозь, один = дверь из модели комнаты")]
    public bool doorCue = true;
    public float doorRepeat = 2.2f;
    [Tooltip("Дальше этого расстояния двери не озвучиваются")]
    public float doorRange = 5f;
    [Tooltip("Двери дальше этого угла от взгляда (в каждую сторону) не озвучиваются")]
    public float doorYaw = 80f;
    public float doorVolume = 0.8f;
    public float doorSourceDistance = 2.5f;

    [Header("Обзор по запросу (курок любого контроллера)")]
    public bool queryWithTrigger = true;
    [Tooltip("Пауза между звуками обзора: слева, прямо, справа, дверь")]
    public float queryStep = 0.45f;

    [Header("Опасность (порог берётся из ObstacleMap.dangerDistance)")]
    public bool haptics = true;

    [Header("Обрыв / ступеньки вниз")]
    public bool detectDropOff = true;
    [Tooltip("Точка ниже пола на столько метров считается обрывом")]
    public float dropDepth = 0.3f;
    public int dropMinVotes = 3;
    public float dropRepeat = 0.7f;

    [Header("Управление")]
    public bool toggleWithButtons = true;   // A (правый) / X (левый)
    public bool muted;

    ObstacleMap map;
    Transform head;
    AudioSource[] src;
    AudioSource dropSrc, pathSrc, doorSrc, querySrc;
    AudioClip clipLow, clipHigh, clipDrop, clipClear, clipBlocked, clipKnock1, clipKnock2;

    float[] acc, pub;
    Vector3[] accDir, pubDir;      // направление в локальных координатах головы
    int[] accHigh, pubHigh;        // 1 = высокий тон
    float[] nextBeep;
    float window;
    int dropVotes;
    Vector3 dropDirAcc, dropDir;
    bool dropActive;
    float nextDrop, nextPath, nextBlocked, nextDoor;
    bool hapticOn;
    float half;
    int[] order;
    float lastOnset;
    int queryIndex = -1;           // -1 = обзор не идёт, иначе номер следующего шага
    float queryNext;
    // пентатоника (полутоны от основной ноты): любые две ноты звучат созвучно, поэтому писки не режут слух
    static readonly int[] Penta = { 0, 2, 4, 7, 9, 12, 14, 16 };

    const float RampStart = 0.3f;     // до этого расстояния звук считается "вплотную" (интервал и громкость на максимуме)

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<AudioSonar>() == null)
            new GameObject("AudioSonar").AddComponent<AudioSonar>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);

        head = SonarCommon.FindHead();
        map = ObstacleMap.Instance;
        if (head == null || map == null) { Debug.LogError("AudioSonar: нет головы или ObstacleMap."); yield break; }

        half = map.yawRange * 0.5f;
        sectors = Mathf.Max(1, sectors);
        acc = new float[sectors]; pub = new float[sectors];
        accDir = new Vector3[sectors]; pubDir = new Vector3[sectors];
        accHigh = new int[sectors]; pubHigh = new int[sectors];
        nextBeep = new float[sectors];
        order = new int[sectors];
        ResetWindow();
        for (int i = 0; i < sectors; i++) pub[i] = float.MaxValue;

        clipLow = SonarSounds.Clip("sonarLow", SonarSounds.Soft(330f, false, 0.14f));    // тёплый "щипок" (предмет ниже)
        clipHigh = SonarSounds.Clip("sonarHigh", SonarSounds.Soft(523f, true, 0.18f));   // "колокольчик" (предмет выше)
        clipDrop = SonarSounds.Clip("sonarDrop", SonarSounds.Chirp(900f, 300f, 0.35f));
        clipClear = SonarSounds.Clip("sonarClear", SonarSounds.Clear());
        clipBlocked = SonarSounds.Clip("sonarBlocked", SonarSounds.Blocked());
        clipKnock1 = SonarSounds.Clip("sonarKnock1", SonarSounds.Knock(1));
        clipKnock2 = SonarSounds.Clip("sonarKnock2", SonarSounds.Knock(2));

        src = new AudioSource[sectors];
        for (int i = 0; i < sectors; i++) src[i] = MakeSource("Sonar" + i);
        dropSrc = MakeSource("SonarDrop");
        pathSrc = MakeSource("SonarPath");
        doorSrc = MakeSource("SonarDoor");
        querySrc = MakeSource("SonarQuery");

        map.OnDepthHit += OnHit;
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
        SetHaptics(false);
    }

    // ---------------------------------------------------------------- приём точек
    void OnHit(Vector3 p)
    {
        if (head == null || acc == null) return;

        Vector3 local = head.InverseTransformPoint(p);
        float d = local.magnitude;
        if (d < minRange || d > maxRange) return;

        float h = p.y - map.floorY;

        // обрыв: точка заметно ниже пола
        if (detectDropOff && h < -dropDepth)
        {
            dropVotes++;
            dropDirAcc += local.normalized;
            return;
        }

        if (h < map.stepHeight) return;          // пол
        if (h > map.headClearance) return;       // потолок

        float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        if (Mathf.Abs(yaw) > half) return;

        int s = Mathf.Clamp((int)((yaw + half) / (half * 2f) * sectors), 0, sectors - 1);
        if (d < acc[s])
        {
            acc[s] = d;
            accDir[s] = local / d;
            accHigh[s] = h >= highFrom ? 1 : 0;
        }
    }

    void ResetWindow()
    {
        for (int i = 0; i < sectors; i++) acc[i] = float.MaxValue;
        dropVotes = 0;
        dropDirAcc = Vector3.zero;
    }

    // ---------------------------------------------------------------- главный цикл
    void Update()
    {
        if (map == null || head == null || src == null) return;

        HandleButtons();

        // публикуем накопленное за окно
        window += Time.deltaTime;
        if (window >= windowSec)
        {
            window = 0f;
            Array.Copy(acc, pub, sectors);
            Array.Copy(accDir, pubDir, sectors);
            Array.Copy(accHigh, pubHigh, sectors);
            dropActive = dropVotes >= dropMinVotes;
            dropDir = dropActive ? dropDirAcc.normalized : Vector3.forward;
            ResetWindow();
        }

        float now = Time.time;

        if (queryIndex >= 0)
        {
            StepQuery(now);               // во время обзора по запросу остальные сигналы молчат
            return;
        }
        if (muted) return;

        PlayPings(now);
        PlayPathCue(now);
        PlayDoorCue(now);

        if (dropActive && now >= nextDrop)
        {
            dropSrc.transform.localPosition = dropDir;
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(clipDrop);
            nextDrop = now + dropRepeat;
        }

        SetHaptics(haptics && NearestPublished() < map.dangerDistance);
    }

    void HandleButtons()
    {
        if (toggleWithButtons &&
            (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three)))
        {
            // подтверждающий сигнал: при выключении низкий, при включении высокий
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(muted ? clipHigh : clipLow);
            muted = !muted;
            if (muted) { SetHaptics(false); foreach (var s in src) s.Stop(); }
        }

        if (queryWithTrigger && queryIndex < 0 &&
            (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger) || OVRInput.GetDown(OVRInput.Button.SecondaryIndexTrigger)))
        {
            queryIndex = 0;
            queryNext = Time.time;
            foreach (var s in src) s.Stop();
        }
    }

    // ---------------------------------------------------------------- писки препятствий
    void PlayPings(float now)
    {
        SortSectorsByDistance();
        float danger = map.dangerDistance;

        for (int n = 0; n < sectors && n < activeSectors; n++)
        {
            int i = order[n];
            float d = pub[i];
            if (d >= maxRange) break;

            if (now < nextBeep[i] || now - lastOnset < minOnsetGap) continue;

            bool isDanger = d < danger;
            float k = Closeness(d);
            float interval = isDanger ? minInterval : Mathf.Lerp(minInterval, maxInterval, k);
            float vol = isDanger ? nearVolume : Mathf.Lerp(nearVolume, farVolume, k);

            var s = src[i];
            s.transform.localPosition = pubDir[i] * Mathf.Min(d, maxSourceDistance);
            Play(s, pubHigh[i] == 1 ? clipHigh : clipLow, vol, PitchFor(k));
            nextBeep[i] = now + interval;
        }
    }

    /// <summary>0 = вплотную, 1 = на дальней границе.</summary>
    float Closeness(float d)
    {
        return Mathf.Clamp01((d - RampStart) / Mathf.Max(0.01f, maxRange - RampStart));
    }

    float PitchFor(float k)
    {
        int band = Mathf.Clamp((int)((1f - k) * noteBands), 0, Mathf.Min(noteBands, Penta.Length) - 1);
        return Mathf.Pow(2f, Penta[band] / 12f);   // ближе = выше нота
    }

    float NearestPublished()
    {
        float best = float.MaxValue;
        for (int i = 0; i < sectors; i++) if (pub[i] < best) best = pub[i];
        return best;
    }

    /// <summary>Заполняет order индексами секторов по возрастанию расстояния (секторов мало, хватает вставок).</summary>
    void SortSectorsByDistance()
    {
        for (int i = 0; i < sectors; i++)
        {
            int j = i;
            while (j > 0 && pub[order[j - 1]] > pub[i]) { order[j] = order[j - 1]; j--; }
            order[j] = i;
        }
    }

    // ---------------------------------------------------------------- путь свободен / тупик
    void PlayPathCue(float now)
    {
        var zones = map.Zones;
        if (zones == null) return;

        if (pathCue && map.bestZone >= 0 && map.bestZone < zones.Length)
        {
            if (now < nextPath || now - lastOnset < minOnsetGap) return;
            pathSrc.transform.localPosition = head.InverseTransformDirection(zones[map.bestZone].freeDir) * pathSourceDistance;
            Play(pathSrc, clipClear, pathVolume, 1f);
            nextPath = now + pathRepeat;
            return;
        }

        if (blockedCue && map.allBlocked)
        {
            if (now < nextBlocked || now - lastOnset < minOnsetGap) return;
            pathSrc.transform.localPosition = Vector3.forward * 0.5f;
            Play(pathSrc, clipBlocked, blockedVolume, 1f);
            nextBlocked = now + blockedRepeat;
        }
    }

    // ---------------------------------------------------------------- двери
    void PlayDoorCue(float now)
    {
        if (!doorCue || now < nextDoor || now - lastOnset < minOnsetGap) return;
        if (!TryNearestDoor(out var p, out Vector3 local)) return;

        doorSrc.transform.localPosition = local;
        float far = Mathf.Clamp01(p.distance / Mathf.Max(0.1f, doorRange));
        Play(doorSrc, p.open ? clipKnock2 : clipKnock1, Mathf.Lerp(doorVolume, doorVolume * 0.4f, far), 1f);
        nextDoor = now + doorRepeat;
    }

    /// <summary>Ближайший проём в пределах doorRange и doorYaw; local = позиция источника относительно головы.</summary>
    bool TryNearestDoor(out NavZones.Passage best, out Vector3 local)
    {
        best = default;
        local = Vector3.forward;
        float bd = float.MaxValue;
        foreach (var p in map.passages)
        {
            if (p.distance > doorRange || p.distance >= bd) continue;
            if (Mathf.Abs(map.YawTo(p.center)) > doorYaw) continue;
            bd = p.distance;
            best = p;
        }
        if (bd == float.MaxValue) return false;

        Vector3 to = best.center - head.position;
        to.y = 0f;                                   // проём на уровне головы, а не пола: так HRTF звучит ровнее
        if (to.sqrMagnitude < 1e-6f) to = Vector3.forward;
        local = head.InverseTransformDirection(to.normalized) * Mathf.Min(to.magnitude, doorSourceDistance);
        return true;
    }

    // ---------------------------------------------------------------- обзор по запросу
    void StepQuery(float now)
    {
        if (now < queryNext) return;

        var zones = map.Zones;
        int n = zones != null ? zones.Length : 0;

        if (queryIndex < n)
        {
            var z = zones[queryIndex];
            Vector3 fwd = head.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            Vector3 dir = Quaternion.Euler(0f, z.CenterYaw, 0f) * fwd.normalized;
            querySrc.transform.localPosition = head.InverseTransformDirection(dir) * pathSourceDistance;

            if (z.free >= map.clearDistance && z.seen >= map.zoneMinSeen)
                Play(querySrc, clipClear, pathVolume, 1f);
            else if (z.free < map.blockedDistance)
                Play(querySrc, clipBlocked, blockedVolume, 1f);
            else if (z.free < map.clearDistance)
            {
                float k = Closeness(z.free);
                Play(querySrc, clipLow, Mathf.Lerp(nearVolume, farVolume, k), PitchFor(k));
            }
            // иначе зона не просмотрена датчиком: пауза без звука

            queryIndex++;
            queryNext = now + queryStep;
            return;
        }

        if (queryIndex == n)
        {
            if (TryNearestDoor(out var p, out Vector3 local))
            {
                doorSrc.transform.localPosition = local;
                Play(doorSrc, p.open ? clipKnock2 : clipKnock1, doorVolume, 1f);
            }
            queryIndex++;
            queryNext = now + queryStep;
            return;
        }

        queryIndex = -1;    // обзор закончен
    }

    // ---------------------------------------------------------------- общее
    void Play(AudioSource s, AudioClip clip, float volume, float pitch)
    {
        s.clip = clip;
        s.volume = volume * masterVolume;
        s.pitch = pitch;
        s.Play();
        lastOnset = Time.time;
    }

    void SetHaptics(bool on)
    {
        if (on == hapticOn) return;
        hapticOn = on;
        float frequency = on ? 0.5f : 0f;
        float amplitude = on ? 0.6f : 0f;
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.LTouch);
    }

    // ---------------------------------------------------------------- создание источников
    static Type metaAudioType;
    static bool metaSearched;

    static void TryAddMetaAudio(GameObject go)
    {
        if (!metaSearched)
        {
            metaSearched = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                        if (t.Name == "MetaXRAudioSource" && typeof(Component).IsAssignableFrom(t)) { metaAudioType = t; break; }
                }
                catch { /* сборка не читается, пропускаем */ }
                if (metaAudioType != null) break;
            }
            Debug.Log(metaAudioType != null
                ? "AudioSonar: Meta XR Audio SDK найден."
                : "AudioSonar: Meta XR Audio SDK НЕ найден, будет обычная панорама Unity.");
        }
        if (metaAudioType != null && go.GetComponent(metaAudioType) == null) go.AddComponent(metaAudioType);
    }

    AudioSource MakeSource(string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(head, false);
        var a = go.AddComponent<AudioSource>();
        a.playOnAwake = false;
        a.loop = false;
        a.spatialBlend = 1f;
        a.spatialize = true;            // включает плагин-спатиализатор (Meta XR Audio), если выбран в Project Settings
        a.dopplerLevel = 0f;
        a.rolloffMode = AudioRolloffMode.Linear;
        a.minDistance = 0.3f;
        a.maxDistance = 6f;
        TryAddMetaAudio(go);
        return a;
    }
}
