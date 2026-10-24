using System.Collections;
using System.Text;
using UnityEngine;

/// <summary>
/// Панель статуса (HUD) для диагностики ObstacleMap. Без MRUK и без своих лучей.
/// Показывает: частоту кадров скана, лучи (попадания/всего), лучей в секунду,
/// статусы лучей (почему нет попаданий), высоту пола, ближайшее препятствие, зоны прохода и ближайший проём.
/// Скрипт создаётся сам при старте сцены.
/// </summary>
public class QuestRoomScanner : MonoBehaviour
{
    public bool showHud = true;
    public Vector3 hudOffset = new Vector3(0f, -0.22f, 1.0f);
    public float characterSize = 0.012f;

    Transform head;
    TextMesh hud;
    float timer;
    int frames;
    float fps;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<QuestRoomScanner>() == null)
            new GameObject("QuestRoomScanner").AddComponent<QuestRoomScanner>();
    }

    IEnumerator Start()
    {
        yield return SonarCommon.WaitUntilReady(() => ObstacleMap.Instance != null);

        head = SonarCommon.FindHead();

        if (showHud && head != null) CreateHud();
    }

    void CreateHud()
    {
        var go = new GameObject("HUD");
        go.transform.SetParent(head, false);
        go.transform.localPosition = hudOffset;
        hud = go.AddComponent<TextMesh>();
        hud.characterSize = characterSize;
        hud.fontSize = 48;
        hud.anchor = TextAnchor.UpperCenter;
        hud.color = Color.white;
    }

    void Update()
    {
        var map = ObstacleMap.Instance;
        if (hud == null || map == null) return;

        frames++;
        timer += Time.unscaledDeltaTime;
        if (timer < 0.2f) return;
        fps = frames / timer;
        frames = 0;
        timer = 0f;

        var sb = new StringBuilder();
        sb.AppendLine(map.nearestDistance >= 0 ? $"Препятствие: {map.nearestDistance:F1} м" : "Препятствие: нет");
        sb.AppendLine($"Пол: y={map.floorY:F2}  клеток: {map.occupiedCount}");
        sb.AppendLine(map.ZonesText());
        sb.Append($"Скан: {fps:F0}/с  лучи {map.lastHits}/{map.lastRays}  {map.raysPerSecond:F0} лучей/с  {map.scanMs:F1} мс  ");
        foreach (var kv in map.statusCounts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
        var ds = DepthSampler.Instance;
        if (ds != null)
            sb.Append($"\nШейдер: {(ds.ready ? ds.validCount + " т." : "не готов")}  {ds.pointsPerSecond:F0}/с  ошибка {(ds.calibErrorMedian >= 0 ? (ds.calibErrorMedian * 100f).ToString("F1") + " см" : "-")}  {ds.status}");
        hud.text = sb.ToString();

        hud.color = map.LevelColor();
    }
}
