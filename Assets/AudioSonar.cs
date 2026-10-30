using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Звуковой "парктроник" для слепых (версия 4: дальность по скорости, три независимые полосы, без постоянных звуков, без руки и трости).
///
/// Три независимые полосы, у каждой свой источник звука и свой ритм (звуки слева и справа не смешиваются):
///   ЦЕНТР  (коридор перед вами, pathHalfWidth в обе стороны, до текущей дальности): главный сигнал.
///   ЛЕВО / ПРАВО (вне коридора, ближе sideRange): такие же писки класса предмета, но тише и с той стороны.
/// На пути и по бокам чисто = тишина.
///
/// Дальность растёт со скоростью ходьбы (по умолчанию: до 1 км/ч = 0.5 м, 3 км/ч = 2 м, дальше растёт до maxRange).
/// Опасная и контактная границы тоже масштабируются от дальности.
///
/// Три класса предмета по высоте (тембр): НИЗКИЙ "тук", СРЕДНИЙ "щипок", ВЫСОКИЙ "колокольчик".
/// Расстояние: чем ближе, тем чаще писк, громче и выше нота (пентатоника). Высокие частоты бывают только у самого близкого предмета.
///
/// Ничего не звучит непрерывно, всё это короткие импульсы, как у сонара:
///   Опасная близость: частые низкие импульсы (тон по классу), ритм ускоряется к контакту + вибрация контроллеров.
///   Вплотную: самый частый и самый высокий сигнал "стоп" (единственный высокий звук) + сильная вибрация.
/// Обрыв / ступеньки вниз: нисходящий сигнал.
/// Ориентиры (двери, окна, текст): отдельные короткие мелодии, см. SonarLandmarks.cs.
/// Рука и белая трость: точки в их объёме выбрасываются (см. раздел "Рука и трость").
///
/// Положение источника считается в мировых координатах и в системе курса (без наклона и крена головы),
/// по центру масс ближайших слоёв, с мёртвой зоной по центру. Это убирает случайные перескоки влево-вправо.
///
/// Кнопка A или X включает и выключает звук (B/Y остаются для камер).
/// Режимы эксперимента (Mode): Off / Mono / Spatial / SpatialVoice. В Mono звучит ОДИН главный источник, боковых полос нет.
/// Все дальности подобраны под ПОМЕЩЕНИЕ. Скрипт создаётся сам при старте сцены.
/// ВАЖНО: это помощь, а не замена трости или собаки-проводника. Датчик не видит стекло и тонкие предметы.
/// </summary>
public class AudioSonar : MonoBehaviour
{
    [Header("Дальность (зависит от скорости ходьбы)")]
    [Tooltip("Верхний предел дальности, м. Слои считаются до 6.4 м")]
    public float maxRange = 3.0f;
    public float minRange = 0.15f;
    [Tooltip("Дальность растёт со скоростью. Выключите, чтобы использовать rangeFixed")]
    public bool speedAdaptiveRange = true;
    [Tooltip("Дальность при выключенной адаптации, м")]
    public float rangeFixed = 1.8f;
    [Tooltip("До этой скорости (км/ч) дальность равна rangeSlow. Стоя на месте тоже")]
    public float rangeSlowKmh = 1f;
    public float rangeSlow = 0.5f;
    [Tooltip("На этой скорости (км/ч) дальность равна rangeFast, дальше растёт линейно до maxRange")]
    public float rangeFastKmh = 3f;
    public float rangeFast = 2.0f;
    [Tooltip("Как быстро дальность меняется, м/с (чтобы границы не прыгали)")]
    public float rangeSlewPerSec = 2f;
    [Tooltip("Период измерения скорости, сек")]
    public float speedSampleSec = 0.25f;

    [Header("Путь вперёд (центральная полоса)")]
    [Tooltip("Половина ширины коридора перед вами, м")]
    public float pathHalfWidth = 0.4f;
    [Tooltip("Окно накопления точек, сек. Меньше = быстрее реакция, но больше дрожания")]
    public float windowSec = 0.12f;
    [Tooltip("Сколько держать последний сигнал, если точек на миг пропало, сек")]
    public float holdSec = 0.3f;
    [Tooltip("Сглаживание расстояния между окнами (0 = без сглаживания, 0.9 = очень вязко)")]
    [Range(0f, 0.9f)] public float smoothing = 0.4f;
    [Tooltip("Сколько точек в слое 0.1 м должно быть, чтобы это считалось предметом, а не шумом датчика")]
    public int minPointsInBin = 2;

    [Header("Классы предметов по высоте")]
    [Tooltip("Предмет достаёт ниже этого над полом = НИЗКИЙ")]
    public float lowTop = 0.4f;
    [Tooltip("Предмет достаёт выше этого над полом = ВЫСОКИЙ. Между lowTop и этим = СРЕДНИЙ")]
    public float highFrom = 1.15f;
    [Tooltip("Высоту предмета берём по точкам не дальше ближайшей больше чем на столько, м")]
    public float classBand = 0.3f;
    [Tooltip("Сколько окон подряд должен показать другой класс, чтобы тембр переключился")]
    public int timbreSwitchWindows = 2;

    [Header("Ритм и громкость")]
    public float minInterval = 0.15f;   // у самой границы опасности (не короче самого длинного клипа при pitch 1)
    public float maxInterval = 0.55f;   // на дальней границе
    public float nearVolume = 0.9f;
    public float farVolume = 0.3f;
    [Tooltip("На сколько ступеней пентатонической гаммы делится расстояние: ближе = выше нота (у низких предметов диапазон уже)")]
    public int noteBands = 6;
    public float masterVolume = 1f;
    [Tooltip("Источник ставится не дальше этого расстояния от головы, чтобы громкость задавалась нами, а не спадом звука")]
    public float maxSourceDistance = 1.5f;
    [Tooltip("Минимальная пауза между началами писков разных полос, сек (писки не начинаются в один миг)")]
    public float minOnsetGap = 0.06f;

    [Header("Направление (устойчивость)")]
    [Tooltip("Во сколько раз преувеличить угол на препятствие. Встроенные динамики плохо передают направление")]
    public float azimuthGain = 2.0f;
    [Tooltip("Максимальный угол источника от прямо вперёд, градусы")]
    public float maxAzimuth = 80f;
    [Tooltip("Мёртвая зона по центру, градусы: пока предмет в ней, звук строго спереди (убирает перескоки лево-право)")]
    public float azimuthDeadzone = 6f;
    [Tooltip("Сглаживание положения предмета между окнами (0..0.9). Выше, чем у расстояния, чтобы направление не прыгало")]
    [Range(0f, 0.9f)] public float azimuthSmoothing = 0.6f;
    [Tooltip("Положение берём как центр масс точек ближайшего слоя и слоёв до +этого значения, м")]
    public float positionBand = 0.2f;

    [Header("Опасная близость: частые низкие импульсы")]
    [Tooltip("Максимум границы опасности, м. Реально: дальность * dangerFraction, но не меньше dangerMin и не больше этого значения")]
    public float dangerDistance = 0.9f;
    public float dangerFraction = 0.35f;
    public float dangerMin = 0.3f;
    [Tooltip("Гистерезис: выход из опасности только когда препятствие дальше порога на столько, м")]
    public float dangerExitMargin = 0.15f;
    public float dangerVolume = 0.8f;
    [Tooltip("Интервал импульсов у границы контакта, сек")]
    public float dangerMinInterval = 0.10f;
    [Tooltip("Интервал импульсов на границе опасности, сек")]
    public float dangerMaxInterval = 0.24f;

    [Header("Вплотную: сигнал «стоп» (единственный высокий звук)")]
    [Tooltip("Максимум границы контакта, м. Реально: дальность * contactFraction, но не меньше contactMin и не больше этого значения")]
    public float contactDistance = 0.4f;
    public float contactFraction = 0.2f;
    public float contactMin = 0.2f;
    public float contactExitMargin = 0.1f;
    public float contactVolume = 0.9f;
    [Tooltip("Интервал высоких импульсов, сек")]
    public float contactInterval = 0.10f;
    public bool haptics = true;

    [Header("Боковые полосы (лево/право, независимые звуки)")]
    public bool sideWarning = true;
    [Tooltip("Предмет сбоку (вне коридора) ближе этого расстояния звучит с его стороны. Не дальше текущей дальности вперёд")]
    public float sideRange = 1.0f;
    [Tooltip("Точки чуть позади курса тоже считаются боковыми (камера видит шире взгляда), м")]
    public float sideBehind = 0.25f;
    public float sideMinInterval = 0.25f;
    public float sideMaxInterval = 0.7f;
    public float sideVolume = 0.55f;

    [Header("Внешние боковые датчики (железо, необязательно)")]
    [Tooltip("До какого расстояния учитывать внешний датчик, м")]
    public float externalSideRange = 1.5f;
    [Tooltip("Класс, который присваиваем предмету от внешнего датчика (он высоту не знает)")]
    public ObjectKind externalKind = ObjectKind.Tall;
    [Tooltip("Если значение от датчика не обновлялось дольше, сек, считаем, что там чисто")]
    public float externalTimeout = 0.6f;

    [Header("Обрыв / ступеньки вниз")]
    public bool detectDropOff = true;
    [Tooltip("Точка ниже пола на столько метров считается обрывом")]
    public float dropDepth = 0.3f;
    public int dropMinVotes = 3;
    public float dropRepeat = 0.7f;
    public float dropRange = 2f;
    [Tooltip("Сколько держать сигнал обрыва после последнего подтверждения, сек")]
    public float dropHold = 0.5f;

    [Header("Ориентиры: двери, окна, текст (см. SonarLandmarks.cs)")]
    public bool landmarkCues = true;
    [Tooltip("Дальше этого расстояния ориентиры не озвучиваются, м")]
    public float landmarkRange = 3f;
    [Tooltip("Ориентир должен быть в этом секторе перед вами, градусы от курса")]
    public float landmarkFov = 50f;
    [Tooltip("Один и тот же ориентир озвучивается не чаще, чем раз в столько секунд")]
    public float landmarkCooldown = 8f;
    public float landmarkVolume = 0.8f;
    [Tooltip("Дополнительно произносить «дверь», «окно» через SonarLandmarks.SpeakHandler")]
    public bool speakLandmarkNames = false;

    [Header("Рука и трость (исключаются из обнаружения)")]
    public bool excludeHandsAndCane = true;
    [Tooltip("Радиус сферы вокруг каждой кисти или контроллера, м")]
    public float handRadius = 0.22f;
    public enum CaneHand { None, Left, Right, Both }
    [Tooltip("В какой руке трость. Для неё исключается ещё и отрезок вдоль трости")]
    public CaneHand caneHand = CaneHand.Right;
    [Tooltip("Длина трости от кисти, м")]
    public float caneLength = 1.4f;
    [Tooltip("Радиус исключения вокруг трости, м (чем больше, тем надёжнее, но тем больше слепая зона)")]
    public float caneRadius = 0.10f;
    [Tooltip("Куда направлена трость в системе контроллера. Forward = вдоль контроллера; если трость держится под углом, подберите")]
    public Vector3 caneLocalAxis = Vector3.forward;
    [Tooltip("Рисовать зоны исключения линиями (видно в Scene view)")]
    public bool debugDrawExclusion = false;

    [Header("Управление")]
    public bool toggleWithButtons = true;   // A (правый) / X (левый)
    public bool muted;

    [Header("Устарело (оставлено для совместимости, не используется)")]
    public int sectors = 5;
    public int activeSectors = 3;

    /// <summary>Условия эксперимента: A = Off (только трость), B = Mono (сигнал без направления), C = Spatial, D = Spatial + голос.</summary>
    public enum FeedbackMode { Off, Mono, Spatial, SpatialVoice }
    public static FeedbackMode Mode = FeedbackMode.Spatial;

    /// <summary>Класс предмета по высоте.</summary>
    public enum ObjectKind { Low, Middle, Tall }

    // для озвучки голосом, HUD и логов
    public bool HasPathObstacle { get { return laneC.valid; } }
    public float PathDistance { get { return laneC.valid ? laneC.ds : -1f; } }
    public ObjectKind PathKind { get { return laneC.kind; } }
    public bool InDanger { get { return laneC.valid && laneC.inDanger; } }
    public float LeftDistance { get { return laneL.valid ? laneL.ds : -1f; } }
    public float RightDistance { get { return laneR.valid ? laneR.ds : -1f; } }
    /// <summary>Текущая дальность вперёд, м (зависит от скорости).</summary>
    public float CurrentRange { get { return curRange; } }
    public float WalkSpeedKmh { get { return speed * 3.6f; } }

    /// <summary>Одна полоса: накопление точек за окно и опубликованное состояние.</summary>
    sealed class Lane
    {
        public readonly int[] count = new int[MaxBins];
        public readonly float[] maxH = new float[MaxBins];
        public readonly float[] minD = new float[MaxBins];
        public readonly Vector3[] sumW = new Vector3[MaxBins];

        public bool valid, inDanger, inContact, external;
        public ObjectKind kind;
        public int kindVotes;
        public float ds, lastSeen, lastBeep = -10f;
        public Vector3 posW;
        public AudioSource src;

        public void Reset()
        {
            for (int i = 0; i < MaxBins; i++)
            {
                count[i] = 0;
                maxH[i] = -1f;
                minD[i] = float.MaxValue;
                sumW[i] = Vector3.zero;
            }
        }
    }

    ObstacleMap map;
    Transform head;
    OVRCameraRig rig;
    AudioSource dropSrc, landmarkSrc;
    AudioClip clipMiddle, clipTall, clipLow, clipDrop;
    AudioClip clipDangerLow, clipDanger, clipDangerTall, clipContact;
    AudioClip clipDoor, clipWindow, clipText;

    const float BinSize = 0.1f;
    const int MaxBins = 64;
    readonly Lane laneC = new Lane(), laneL = new Lane(), laneR = new Lane();
    int dropVotes;
    Vector3 dropDirAcc;
    float window;

    // скорость и дальность
    Vector3 speedPrevPos;
    float speedTimer, speed;
    float curRange = 0.5f, curSideRange = 0.5f, dangerNow = 0.3f, contactNow = 0.2f;

    // внешние боковые датчики
    float extL = -1f, extR = -1f, extStampL = -100f, extStampR = -100f;

    // обрыв
    Vector3 dropDirW;
    float dropUntil, nextDrop;

    float lastOnset;
    bool hapticOn, hapticStrong;
    Vector3 yawFwd = Vector3.forward, yawRight = Vector3.right;

    // ориентиры
    readonly Dictionary<int, float> lastAnnounce = new Dictionary<int, float>();
    float nextLandmarkCheck;

    // рука и трость (кэш на кадр)
    int exclFrame = -1;
    bool lValid, rValid;
    Vector3 lPos, rPos, lAxis, rAxis;

    // пентатоника (полутоны от основной ноты): любые две ноты звучат созвучно, поэтому писки не режут слух
    static readonly int[] Penta = { 0, 2, 4, 7, 9, 12, 14, 16 };

    const int SampleRate = 48000;

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

        rig = FindFirstObjectByType<OVRCameraRig>();
        if (excludeHandsAndCane && rig == null)
            Debug.LogWarning("AudioSonar: OVRCameraRig не найден, руки и трость будут браться из OVRInput в пространстве отслеживания.");

        speedPrevPos = FlatPos(head.position);
        curRange = speedAdaptiveRange ? rangeSlow : rangeFixed;
        UpdateThresholds();

        ResetWindow();

        // Все частоты низкие или средние. Высокий только сигнал «стоп».
        clipLow = MakeKnock("sonarLow", 140f, 0.11f);                   // глухой "тук": под ногами, ниже стола
        clipMiddle = MakeSoft("sonarMiddle", 220f, false, 0.14f);       // тёплый "щипок": стол, тумбочка
        clipTall = MakeSoft("sonarTall", 320f, true, 0.15f);            // "колокольчик": стена, шкаф, человек
        clipDangerLow = MakeDangerPulse("sonarDangerLow", 180f, 0.08f);
        clipDanger = MakeDangerPulse("sonarDanger", 240f, 0.08f);
        clipDangerTall = MakeDangerPulse("sonarDangerTall", 320f, 0.08f);
        clipContact = MakeContactPing("sonarContact", 1400f, 0.06f);    // единственный высокий звук
        clipDrop = MakeChirp("sonarDrop", 700f, 220f, 0.35f);
        clipDoor = MakeNotes("sonarDoor", new[] { 392f, 294f }, 0.18f, 0.02f);        // два длинных вниз: "динь-дон"
        clipWindow = MakeNotes("sonarWindow", new[] { 294f, 392f }, 0.10f, 0.02f);    // два коротких вверх
        clipText = MakeNotes("sonarText", new[] { 330f, 330f, 330f }, 0.05f, 0.04f);  // три тихих щелчка

        laneC.src = MakeSource("SonarCenter");
        laneL.src = MakeSource("SonarLeft");
        laneR.src = MakeSource("SonarRight");
        landmarkSrc = MakeSource("SonarLandmark");
        dropSrc = MakeSource("SonarDrop");

        map.OnDepthHit += OnHit;
    }

    void OnDestroy()
    {
        if (map != null) map.OnDepthHit -= OnHit;
        SetHaptics(false);
    }

    /// <summary>
    /// Вход для внешних боковых датчиков (например, два ультразвуковых или ToF на очках по BLE/USB).
    /// Передавайте расстояние до ближайшего предмета слева и справа в метрах, 0 или меньше = чисто. Вызывать нужно регулярно (не реже раза в externalTimeout).
    /// </summary>
    public void SetExternalSide(float leftMeters, float rightMeters)
    {
        float t = Time.time;
        extL = leftMeters; extStampL = t;
        extR = rightMeters; extStampR = t;
    }

    // ---------------------------------------------------------------- рука и трость
    static float DistToSegmentSq(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / len2) : 0f;
        return (p - (a + ab * t)).sqrMagnitude;
    }

    bool TryGetHand(bool leftHand, out Vector3 pos, out Vector3 axis)
    {
        pos = Vector3.zero;
        axis = Vector3.forward;

        OVRInput.Controller ctrl = leftHand ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;
        bool tracked = OVRInput.GetControllerPositionTracked(ctrl);

        Transform t = null;
        if (rig != null)
        {
            if (tracked) t = leftHand ? rig.leftControllerAnchor : rig.rightControllerAnchor;
            else t = leftHand ? rig.leftHandAnchor : rig.rightHandAnchor;     // отслеживание рук без контроллеров
            if (t != null && !t.gameObject.activeInHierarchy) t = null;
        }
        if (t != null)
        {
            pos = t.position;
            axis = t.TransformDirection(caneLocalAxis).normalized;
            return true;
        }
        if (tracked && head != null && head.parent != null)
        {
            Transform space = head.parent;     // обычно TrackingSpace
            pos = space.TransformPoint(OVRInput.GetLocalControllerPosition(ctrl));
            axis = (space.rotation * OVRInput.GetLocalControllerRotation(ctrl) * caneLocalAxis).normalized;
            return true;
        }
        return false;
    }

    void RefreshExclusion()
    {
        if (exclFrame == Time.frameCount) return;
        exclFrame = Time.frameCount;
        lValid = TryGetHand(true, out lPos, out lAxis);
        rValid = TryGetHand(false, out rPos, out rAxis);
    }

    bool IsHandOrCane(Vector3 p)
    {
        if (!excludeHandsAndCane) return false;
        RefreshExclusion();

        float hr2 = handRadius * handRadius;
        float cr2 = caneRadius * caneRadius;
        bool caneL = caneHand == CaneHand.Left || caneHand == CaneHand.Both;
        bool caneR = caneHand == CaneHand.Right || caneHand == CaneHand.Both;

        if (lValid)
        {
            if ((p - lPos).sqrMagnitude < hr2) return true;
            if (caneL && DistToSegmentSq(p, lPos, lPos + lAxis * caneLength) < cr2) return true;
        }
        if (rValid)
        {
            if ((p - rPos).sqrMagnitude < hr2) return true;
            if (caneR && DistToSegmentSq(p, rPos, rPos + rAxis * caneLength) < cr2) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- приём точек
    void OnHit(Vector3 p)
    {
        if (head == null) return;

        Vector3 rel = p - head.position;
        float d3 = rel.magnitude;
        if (d3 < minRange) return;

        // рука, контроллер и белая трость не должны считаться препятствиями
        if (IsHandOrCane(p)) return;

        float h = p.y - map.floorY;

        // обрыв: точка заметно ниже пола
        if (detectDropOff && h < -dropDepth)
        {
            if (d3 <= dropRange)
            {
                dropVotes++;
                dropDirAcc += rel.normalized;
            }
            return;
        }

        if (h < map.stepHeight) return;          // пол
        if (h > map.headClearance) return;       // потолок

        // координаты относительно курса (по взгляду, без наклона и крена головы)
        float z = Vector3.Dot(rel, yawFwd);
        float x = Vector3.Dot(rel, yawRight);
        float dh = Mathf.Sqrt(x * x + z * z);

        if (Mathf.Abs(x) <= pathHalfWidth)
        {
            if (z <= 0f) return;
            if (dh > curRange) return;
            Accumulate(laneC, p, dh, h);
        }
        else if (sideWarning && z > -sideBehind && dh <= curSideRange)
        {
            Accumulate(x < 0f ? laneL : laneR, p, dh, h);
        }
    }

    static void Accumulate(Lane L, Vector3 p, float dh, float h)
    {
        int b = Mathf.Clamp((int)(dh / BinSize), 0, MaxBins - 1);
        L.count[b]++;
        L.sumW[b] += p;
        if (h > L.maxH[b]) L.maxH[b] = h;
        if (dh < L.minD[b]) L.minD[b] = dh;
    }

    void ResetWindow()
    {
        laneC.Reset();
        laneL.Reset();
        laneR.Reset();
        dropVotes = 0;
        dropDirAcc = Vector3.zero;
    }

    ObjectKind ClassifyHeight(float maxH)
    {
        return maxH >= highFrom ? ObjectKind.Tall
             : maxH >= lowTop ? ObjectKind.Middle
             : ObjectKind.Low;
    }

    /// <summary>Публикует накопленное за окно для одной полосы: ближайший предмет, класс по высоте, положение (центр масс), сглаживание, удержание.</summary>
    void PublishLane(Lane L, float range, float now)
    {
        float k = 1f - smoothing;
        float kp = 1f - azimuthSmoothing;

        int nb = Mathf.Clamp((int)(range / BinSize) + 1, 1, MaxBins);
        int first = -1;
        for (int i = 0; i < nb; i++)
            if (L.count[i] >= minPointsInBin) { first = i; break; }

        if (first >= 0)
        {
            float near = L.minD[first];

            // положение: центр масс точек ближайших слоёв, он устойчивее одной ближайшей точки
            int lastPos = Mathf.Min(nb - 1, first + Mathf.Max(1, (int)(positionBand / BinSize)));
            Vector3 sum = Vector3.zero;
            int cnt = 0;
            for (int i = first; i <= lastPos; i++) { sum += L.sumW[i]; cnt += L.count[i]; }
            Vector3 pos = sum / Mathf.Max(1, cnt);

            // как высоко достаёт предмет: самая высокая точка в слоях от ближайшего до +classBand
            float maxH = L.maxH[first];
            int lastH = Mathf.Min(nb - 1, first + (int)(classBand / BinSize) + 1);
            for (int i = first + 1; i <= lastH; i++)
                if (L.count[i] > 0 && L.maxH[i] > maxH) maxH = L.maxH[i];
            ObjectKind kindNow = ClassifyHeight(maxH);

            if (!L.valid || L.external)
            {
                L.ds = near;
                L.posW = pos;
                L.kind = kindNow;
                L.kindVotes = 0;
            }
            else
            {
                L.ds = Mathf.Lerp(L.ds, near, k);
                L.posW = Vector3.Lerp(L.posW, pos, kp);
                if (kindNow == L.kind) L.kindVotes = 0;
                else if (++L.kindVotes >= timbreSwitchWindows) { L.kind = kindNow; L.kindVotes = 0; }
            }
            L.valid = true;
            L.external = false;
            L.lastSeen = now;
        }
        else if (L.valid && now - L.lastSeen > holdSec)
        {
            L.valid = false;
            L.external = false;
        }
    }

    void ApplyExternalSide(Lane L, float dist, float stamp, float sign, float now)
    {
        if (dist <= 0f || now - stamp > externalTimeout || dist > externalSideRange) return;
        if (!L.valid || dist < L.ds)
        {
            L.valid = true;
            L.external = true;
            L.ds = dist;
            L.kind = externalKind;
            L.kindVotes = 0;
            L.lastSeen = now;
            L.posW = head.position + yawRight * (sign * dist);
        }
    }

    void Publish(float now)
    {
        PublishLane(laneC, curRange, now);

        // опасная близость и контакт с гистерезисом (только центральная полоса)
        if (laneC.valid)
        {
            if (!laneC.inDanger && laneC.ds < dangerNow) laneC.inDanger = true;
            else if (laneC.inDanger && laneC.ds > dangerNow + dangerExitMargin) laneC.inDanger = false;

            if (!laneC.inContact && laneC.ds < contactNow) laneC.inContact = true;
            else if (laneC.inContact && laneC.ds > contactNow + contactExitMargin) laneC.inContact = false;
        }
        else { laneC.inDanger = false; laneC.inContact = false; }

        if (sideWarning)
        {
            PublishLane(laneL, curSideRange, now);
            PublishLane(laneR, curSideRange, now);
            ApplyExternalSide(laneL, extL, extStampL, -1f, now);
            ApplyExternalSide(laneR, extR, extStampR, 1f, now);
        }
        else { laneL.valid = false; laneR.valid = false; }

        if (dropVotes >= dropMinVotes)
        {
            dropUntil = now + dropHold;
            dropDirW = dropDirAcc.normalized;
        }

        ResetWindow();
    }

    // ---------------------------------------------------------------- скорость и дальность
    static Vector3 FlatPos(Vector3 p) { p.y = 0f; return p; }

    void UpdateSpeedAndRange(float dt)
    {
        speedTimer += dt;
        if (speedTimer >= speedSampleSec)
        {
            float v = (FlatPos(head.position) - speedPrevPos).magnitude / Mathf.Max(0.01f, speedTimer);
            v = Mathf.Min(v, 3f);   // отсекаем скачки при пересчёте координат отслеживания
            speedPrevPos = FlatPos(head.position);
            speedTimer = 0f;
            // быстро реагируем на разгон, медленно на торможение
            speed = Mathf.Lerp(speed, v, v > speed ? 0.6f : 0.25f);
        }

        float target;
        if (speedAdaptiveRange)
        {
            float kmh = speed * 3.6f;
            float t = (kmh - rangeSlowKmh) / Mathf.Max(0.01f, rangeFastKmh - rangeSlowKmh);
            target = rangeSlow + Mathf.Max(0f, t) * (rangeFast - rangeSlow);
            target = Mathf.Clamp(target, rangeSlow, Mathf.Max(rangeSlow, maxRange));
        }
        else target = Mathf.Min(rangeFixed, maxRange);

        curRange = Mathf.MoveTowards(curRange, target, rangeSlewPerSec * dt);
        UpdateThresholds();
    }

    void UpdateThresholds()
    {
        curSideRange = Mathf.Min(sideRange, curRange);
        contactNow = Mathf.Clamp(curRange * contactFraction, contactMin, contactDistance);
        dangerNow = Mathf.Clamp(curRange * dangerFraction, dangerMin, dangerDistance);
        if (dangerNow < contactNow + 0.08f) dangerNow = contactNow + 0.08f;
    }

    // ---------------------------------------------------------------- звук
    void Update()
    {
        if (map == null || head == null || laneC.src == null) return;

        // курс по взгляду без наклона головы
        Vector3 f = head.forward;
        f.y = 0f;
        if (f.sqrMagnitude > 0.05f)
        {
            yawFwd = f.normalized;
            yawRight = new Vector3(yawFwd.z, 0f, -yawFwd.x);
        }

        if (toggleWithButtons &&
            (OVRInput.GetDown(OVRInput.Button.One) || OVRInput.GetDown(OVRInput.Button.Three)))
        {
            // подтверждающий сигнал: при выключении низкий, при включении высокий
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(muted ? clipTall : clipMiddle);
            muted = !muted;
            if (muted) { SetHaptics(false); StopAllCues(); }
        }

        float now = Time.time;

        UpdateSpeedAndRange(Time.deltaTime);
        if (debugDrawExclusion) DrawExclusion();

        window += Time.deltaTime;
        if (window >= windowSec)
        {
            window = 0f;
            Publish(now);
        }

        if (muted || Mode == FeedbackMode.Off) { SetHaptics(false); return; }

        bool spatial = Mode != FeedbackMode.Mono;

        // 1. центральная полоса: препятствие на пути (писки, импульсы опасности или «стоп»)
        UpdateCenter(now, spatial);

        // 2. боковые полосы: независимо слева и справа, не мешают, пока впереди опасность
        bool centerCritical = laneC.valid && laneC.inDanger;
        if (spatial && sideWarning && !centerCritical)
        {
            UpdateSide(laneL, now);
            UpdateSide(laneR, now);
        }

        // 3. ориентиры: двери, окна, текст
        if (landmarkCues && !centerCritical) UpdateLandmarks(now, spatial);

        // 4. обрыв / ступеньки вниз
        if (now < dropUntil && now >= nextDrop)
        {
            dropSrc.transform.position = head.position + dropDirW;
            dropSrc.volume = masterVolume;
            dropSrc.PlayOneShot(clipDrop);
            nextDrop = now + dropRepeat;
        }

        SetHaptics(haptics && laneC.valid && laneC.inDanger, laneC.inContact);
    }

    void UpdateCenter(float now, bool spatial)
    {
        Lane c = laneC;
        if (!c.valid) return;

        AudioClip clip;
        float interval, volume, pitch = 1f;

        if (c.inContact)
        {
            // вплотную: самые частые высокие импульсы
            clip = clipContact;
            interval = contactInterval;
            volume = contactVolume;
        }
        else if (c.inDanger)
        {
            // опасно близко: частые низкие импульсы, тон по классу, ритм ускоряется к контакту
            clip = c.kind == ObjectKind.Tall ? clipDangerTall
                 : c.kind == ObjectKind.Low ? clipDangerLow
                 : clipDanger;
            float u = Mathf.Clamp01((c.ds - contactNow) / Mathf.Max(0.01f, dangerNow - contactNow));
            interval = Mathf.Lerp(dangerMinInterval, dangerMaxInterval, u);
            volume = dangerVolume;
        }
        else
        {
            clip = c.kind == ObjectKind.Tall ? clipTall
                 : c.kind == ObjectKind.Middle ? clipMiddle
                 : clipLow;
            float k = Mathf.Clamp01((c.ds - dangerNow) / Mathf.Max(0.01f, curRange - dangerNow));
            interval = Mathf.Lerp(minInterval, maxInterval, k);
            volume = Mathf.Lerp(nearVolume, farVolume, k);
            // у низких предметов диапазон нот уже, чтобы "тук" не превращался в щипок
            int bands = c.kind == ObjectKind.Low ? Mathf.Min(3, noteBands) : noteBands;
            int band = Mathf.Clamp((int)((1f - k) * bands), 0, Mathf.Min(bands, Penta.Length) - 1);
            pitch = Mathf.Pow(2f, Penta[band] / 12f);   // ближе = выше нота
        }

        if (now - c.lastBeep >= interval && now - lastOnset >= minOnsetGap)
        {
            PlaceSource(c.src, c.posW, c.ds, spatial);
            c.src.clip = clip;
            c.src.volume = volume * masterVolume;
            c.src.pitch = pitch;
            c.src.Play();
            c.lastBeep = now;
            lastOnset = now;
        }
    }

    void UpdateSide(Lane L, float now)
    {
        if (!L.valid) return;

        float rangeS = L.external ? externalSideRange : Mathf.Max(0.01f, curSideRange);
        float u = Mathf.Clamp01(L.ds / rangeS);
        float interval = Mathf.Lerp(sideMinInterval, sideMaxInterval, u);
        if (now - L.lastBeep < interval || now - lastOnset < minOnsetGap) return;

        AudioClip clip = L.kind == ObjectKind.Tall ? clipTall
                       : L.kind == ObjectKind.Middle ? clipMiddle
                       : clipLow;
        PlaceSource(L.src, L.posW, L.ds, true);
        L.src.clip = clip;
        L.src.volume = Mathf.Lerp(sideVolume, sideVolume * 0.4f, u) * masterVolume;
        L.src.pitch = 1f;
        L.src.Play();
        L.lastBeep = now;
        lastOnset = now;
    }

    void UpdateLandmarks(float now, bool spatial)
    {
        if (now < nextLandmarkCheck) return;
        nextLandmarkCheck = now + 0.25f;
        if (now - lastOnset < 0.35f) return;    // не накладываемся на писки

        Landmark best = null;
        float bestD = float.MaxValue;
        foreach (Landmark lm in SonarLandmarks.All)
        {
            if (!lm.IsFresh(now, SonarLandmarks.ttl)) continue;

            Vector3 rel = lm.worldPos - head.position;
            float z = Vector3.Dot(rel, yawFwd);
            float x = Vector3.Dot(rel, yawRight);
            if (z <= 0.1f) continue;
            float d = Mathf.Sqrt(x * x + z * z);
            if (d > landmarkRange) continue;
            if (Mathf.Abs(Mathf.Atan2(x, z)) * Mathf.Rad2Deg > landmarkFov) continue;

            float t;
            if (lastAnnounce.TryGetValue(lm.id, out t) && now - t < landmarkCooldown) continue;

            if (d < bestD) { bestD = d; best = lm; }
        }
        if (best == null) return;

        lastAnnounce[best.id] = now;
        AudioClip clip = best.kind == LandmarkKind.Door ? clipDoor
                       : best.kind == LandmarkKind.Window ? clipWindow
                       : clipText;
        PlaceSource(landmarkSrc, best.worldPos, bestD, spatial);
        landmarkSrc.volume = landmarkVolume * masterVolume;
        landmarkSrc.pitch = 1f;
        landmarkSrc.PlayOneShot(clip);
        lastOnset = now;

        if (SonarLandmarks.SpeakHandler != null)
        {
            if (best.kind == LandmarkKind.Text && !string.IsNullOrEmpty(best.text)) SonarLandmarks.SpeakHandler(best.text);
            else if (speakLandmarkNames) SonarLandmarks.SpeakHandler(best.kind == LandmarkKind.Door ? "дверь" : "окно");
        }
    }

    /// <summary>
    /// Ставит источник в сторону цели. Считается в мировых координатах по курсу (без наклона и крена головы),
    /// угол преувеличен, у центра есть мёртвая зона. В моно источник по центру.
    /// </summary>
    void PlaceSource(AudioSource s, Vector3 worldPos, float dist, bool spatial)
    {
        s.spatialBlend = spatial ? 1f : 0f;
        s.spatialize = spatial;
        if (!spatial)
        {
            s.transform.position = head.position + yawFwd * 0.5f;
            return;
        }

        Vector3 rel = worldPos - head.position;
        float x = Vector3.Dot(rel, yawRight);
        float z = Vector3.Dot(rel, yawFwd);
        float az = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
        float a = Mathf.Max(0f, Mathf.Abs(az) - azimuthDeadzone) * Mathf.Sign(az);
        a = Mathf.Clamp(a * azimuthGain, -maxAzimuth, maxAzimuth) * Mathf.Deg2Rad;
        float r = Mathf.Clamp(dist, 0.35f, maxSourceDistance);
        Vector3 dir = yawFwd * Mathf.Cos(a) + yawRight * Mathf.Sin(a);
        s.transform.position = head.position + dir * r;
    }

    void DrawExclusion()
    {
        if (!excludeHandsAndCane) return;
        RefreshExclusion();
        if (lValid)
        {
            Debug.DrawLine(lPos, lPos + Vector3.up * handRadius, Color.yellow);
            if (caneHand == CaneHand.Left || caneHand == CaneHand.Both) Debug.DrawLine(lPos, lPos + lAxis * caneLength, Color.red);
        }
        if (rValid)
        {
            Debug.DrawLine(rPos, rPos + Vector3.up * handRadius, Color.yellow);
            if (caneHand == CaneHand.Right || caneHand == CaneHand.Both) Debug.DrawLine(rPos, rPos + rAxis * caneLength, Color.red);
        }
    }

    /// <summary>Включает или выключает звук из кода (например, TrialLogger по условию прохода). Кнопки A/X работают как раньше.</summary>
    public void SetMuted(bool value)
    {
        if (value == muted) return;
        muted = value;
        if (muted) { SetHaptics(false); StopAllCues(); }
    }

    void StopAllCues()
    {
        if (laneC.src != null) laneC.src.Stop();
        if (laneL.src != null) laneL.src.Stop();
        if (laneR.src != null) laneR.src.Stop();
        if (landmarkSrc != null) landmarkSrc.Stop();
    }

    /// <summary>Главное препятствие для голосовых подсказок: расстояние и направление в системе головы.
    /// Сначала препятствие на пути, если его нет, то ближайший предмет сбоку.</summary>
    public bool TryGetNearest(out float dist, out Vector3 localDir)
    {
        Lane best = null;
        if (laneC.valid) best = laneC;
        else
        {
            if (laneL.valid) best = laneL;
            if (laneR.valid && (best == null || laneR.ds < best.ds)) best = laneR;
        }

        if (best != null && head != null)
        {
            dist = best.ds;
            Vector3 local = head.InverseTransformPoint(best.posW);
            localDir = local.sqrMagnitude > 1e-6f ? local.normalized : Vector3.forward;
            return true;
        }
        dist = float.MaxValue;
        localDir = Vector3.forward;
        return false;
    }

    void SetHaptics(bool on, bool strong = false)
    {
        if (on == hapticOn && strong == hapticStrong) return;
        hapticOn = on;
        hapticStrong = on && strong;
        float frequency = on ? (strong ? 1f : 0.5f) : 0f;
        float amplitude = on ? (strong ? 1f : 0.6f) : 0f;
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.RTouch);
        OVRInput.SetControllerVibration(frequency, amplitude, OVRInput.Controller.LTouch);
    }

    // ---------------------------------------------------------------- создание источников и клипов
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

    // Мягкий "щипковый" звук: плавная атака, экспоненциальное затухание, чуть шума (~5%, он помогает локализации).
    // У "колокольчика" только один неплотный обертон (2.76x), чтобы звук не звенел на высоких частотах.
    static AudioClip MakeSoft(string name, float freq, bool bell, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        var rnd = new System.Random(1234);
        float tau = len * 0.35f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float a = Mathf.Clamp01(t / 0.012f);
            a = a * a * (3f - 2f * a);                       // плавная атака без щелчка
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.02f);   // гарантированный ноль в конце
            float w = 2f * Mathf.PI * freq * t;
            float s;
            if (bell)
                s = Mathf.Sin(w) + 0.35f * Mathf.Sin(2.76f * w) * Mathf.Exp(-t / (tau * 0.5f));
            else
                s = Mathf.Sin(w) + 0.35f * Mathf.Sin(2f * w) * Mathf.Exp(-t / (tau * 0.5f));
            float noise = ((float)rnd.NextDouble() * 2f - 1f) * 0.05f;
            data[i] = (s * 0.5f + noise) * a * decay * tail * 0.8f;
        }
        return MakeClip(name, data);
    }

    // Глухой короткий "тук": низкая частота слегка падает, быстрое затухание, едва заметный щелчок в начале.
    static AudioClip MakeKnock(string name, float freq, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        var rnd = new System.Random(4321);
        float tau = len * 0.3f;
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float u = i / (float)n;
            float f = freq * (1f - 0.25f * u);               // частота чуть падает: звук "оседает"
            phase += 2f * Mathf.PI * f / sr;
            float a = Mathf.Clamp01(t / 0.004f);             // быстрая атака
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.015f);
            float s = Mathf.Sin(phase) + 0.3f * Mathf.Sin(2f * phase) * Mathf.Exp(-t / (tau * 0.5f));
            float click = ((float)rnd.NextDouble() * 2f - 1f) * 0.15f * Mathf.Exp(-t / 0.003f);
            data[i] = (s * 0.6f + click) * a * decay * tail * 0.9f;
        }
        return MakeClip(name, data);
    }

    // Короткий жёсткий низкий импульс "опасно": основной тон и нечётные гармоники (звук "полый", заметный, но не высокий).
    // Повторяется планировщиком, в непрерывный звук не склеивается (длина клипа меньше dangerMinInterval).
    static AudioClip MakeDangerPulse(string name, float freq, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        float tau = len * 0.45f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float a = Mathf.Clamp01(t / 0.003f);
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.01f);
            float w = 2f * Mathf.PI * freq * t;
            float s = Mathf.Sin(w) + 0.5f * Mathf.Sin(3f * w) + 0.25f * Mathf.Sin(5f * w);
            data[i] = s * 0.5f * a * decay * tail;
        }
        return MakeClip(name, data);
    }

    // Сигнал «стоп»: один короткий высокий писк. Единственный по-настоящему высокий звук, повторяется только у самого контакта.
    static AudioClip MakeContactPing(string name, float freq, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        float tau = len * 0.5f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)sr;
            float a = Mathf.Clamp01(t / 0.002f);
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.008f);
            float w = 2f * Mathf.PI * freq * t;
            data[i] = (Mathf.Sin(w) + 0.3f * Mathf.Sin(2f * w)) * 0.55f * a * decay * tail;
        }
        return MakeClip(name, data);
    }

    // Последовательность мягких нот (для ориентиров): каждая нота затухает, между нотами пауза gap.
    static AudioClip MakeNotes(string name, float[] freqs, float noteLen, float gap)
    {
        const int sr = SampleRate;
        int noteN = (int)(sr * noteLen);
        int per = (int)(sr * (noteLen + gap));
        var data = new float[per * freqs.Length];
        float tau = noteLen * 0.4f;
        for (int j = 0; j < freqs.Length; j++)
        {
            for (int i = 0; i < noteN; i++)
            {
                float t = i / (float)sr;
                float a = Mathf.Clamp01(t / 0.006f);
                a = a * a * (3f - 2f * a);
                float decay = Mathf.Exp(-t / tau);
                float tail = Mathf.Clamp01((noteLen - t) / 0.015f);
                float w = 2f * Mathf.PI * freqs[j] * t;
                float s = Mathf.Sin(w) + 0.3f * Mathf.Sin(2f * w);
                data[j * per + i] = s * 0.5f * a * decay * tail * 0.8f;
            }
        }
        return MakeClip(name, data);
    }

    static AudioClip MakeChirp(string name, float f0, float f1, float len)
    {
        const int sr = SampleRate;
        int n = (int)(sr * len);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n;
            float f = Mathf.Lerp(f0, f1, u);
            phase += 2f * Mathf.PI * f / sr;
            float env = Mathf.Min(1f, i / (0.01f * sr)) * (1f - u);
            data[i] = (Mathf.Sin(phase) + 0.4f * Mathf.Sin(2f * phase)) * 0.6f * env;
        }
        return MakeClip(name, data);
    }

    static AudioClip MakeClip(string name, float[] data)
    {
        var c = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        c.SetData(data, 0);
        return c;
    }
}
