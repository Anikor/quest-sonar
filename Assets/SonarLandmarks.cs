using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Тип ориентира, который озвучивает AudioSonar отдельным сигналом.</summary>
public enum LandmarkKind { Door, Window, Text }

/// <summary>Один найденный ориентир в мировых координатах.</summary>
public sealed class Landmark
{
    public int id;
    public LandmarkKind kind;
    public Vector3 worldPos;
    /// <summary>Распознанный текст (только для Text). Если задан SonarLandmarks.SpeakHandler, текст будет произнесён.</summary>
    public string text;
    /// <summary>true = не устаревает (например, дверь из скана комнаты). false = живёт SonarLandmarks.ttl секунд после последнего Report.</summary>
    public bool persistent;
    public float lastSeen;

    public bool IsFresh(float now, float ttl)
    {
        return persistent || now - lastSeen <= ttl;
    }
}

/// <summary>
/// Общий реестр ориентиров: двери, окна, текст.
/// AudioSonar сам НИЧЕГО не распознаёт по картинке. Он только озвучивает то, что сюда сообщили.
/// Источники, которые могут вызывать Report:
///   1) MrukLandmarkSource (ниже): двери и окна из скана комнаты Meta (Space Setup), работает без нейросети.
///   2) Ваш детектор по камере (Passthrough Camera API + модель в Unity Inference Engine / Sentis): двери, окна, OCR текста.
///      Он переводит найденный прямоугольник в мировую точку (луч от камеры до глубины) и вызывает Report.
/// </summary>
public static class SonarLandmarks
{
    /// <summary>Сколько секунд живёт непостоянный ориентир без обновления.</summary>
    public static float ttl = 2f;

    /// <summary>Подключите сюда свой TTS: SonarLandmarks.SpeakHandler = MyTts.Speak;</summary>
    public static Action<string> SpeakHandler;

    static readonly Dictionary<int, Landmark> items = new Dictionary<int, Landmark>();

    public static Dictionary<int, Landmark>.ValueCollection All { get { return items.Values; } }

    /// <summary>Добавить или обновить ориентир. Для детекторов без своих id используйте MakeId.</summary>
    public static void Report(int id, LandmarkKind kind, Vector3 worldPos, string text = null, bool persistent = false)
    {
        Landmark lm;
        if (!items.TryGetValue(id, out lm))
        {
            lm = new Landmark { id = id };
            items[id] = lm;
        }
        lm.kind = kind;
        lm.worldPos = worldPos;
        if (text != null) lm.text = text;
        lm.persistent = persistent;
        lm.lastSeen = Time.time;
    }

    public static void Remove(int id) { items.Remove(id); }
    public static void Clear() { items.Clear(); }

    /// <summary>Стабильный id по типу и позиции (сетка cell м), чтобы один и тот же объект не озвучивался каждый кадр как новый.</summary>
    public static int MakeId(LandmarkKind kind, Vector3 worldPos, float cell = 0.5f)
    {
        int x = Mathf.RoundToInt(worldPos.x / cell);
        int y = Mathf.RoundToInt(worldPos.y / cell);
        int z = Mathf.RoundToInt(worldPos.z / cell);
        unchecked
        {
            int h = (int)kind + 17;
            h = h * 31 + x;
            h = h * 31 + y;
            h = h * 31 + z;
            return h;
        }
    }
}

#if USE_MRUK
// Включается, если в Project Settings > Player > Scripting Define Symbols добавить USE_MRUK
// и в проекте стоит Meta MR Utility Kit. Берёт двери и окна из скана комнаты (Space Setup).
// Названия MRUK API проверялись по документации, но не собирались против вашей версии пакета.
// Если компилятор ругнётся на имя, сверьте его с версией MRUK.
using Meta.XR.MRUtilityKit;

public class MrukLandmarkSource : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<MrukLandmarkSource>() == null)
            new GameObject("MrukLandmarkSource").AddComponent<MrukLandmarkSource>();
    }

    IEnumerator Start()
    {
        while (MRUK.Instance == null) yield return null;
        while (true)
        {
            Refresh();
            yield return new WaitForSeconds(3f);   // сцена может загрузиться позже старта
        }
    }

    void Refresh()
    {
        foreach (var room in MRUK.Instance.Rooms)
        {
            foreach (var anchor in room.Anchors)
            {
                LandmarkKind kind;
                if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.DOOR_FRAME)) kind = LandmarkKind.Door;
                else if (anchor.HasAnyLabel(MRUKAnchor.SceneLabels.WINDOW_FRAME)) kind = LandmarkKind.Window;
                else continue;

                int id = anchor.GetInstanceID();
                SonarLandmarks.Report(id, kind, anchor.transform.position, null, true);
            }
        }
    }
}
#endif
