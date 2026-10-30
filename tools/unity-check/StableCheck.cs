using UnityEditor;
using UnityEngine;

// Только для временного проекта проверки (ветка main, v4.x). Компиляция всех скриптов проекта с пакетами Meta
// и тесты реестра ориентиров SonarLandmarks (единственная чистая логика в v4, не зависящая от шлема).
// Ошибки C# ломают -executeMethod сами. Результат - строки STABLECHECK:, последняя "OK DONE"; код выхода 3 при провале.
public static class StableCheck
{
    static int fails;

    static void Check(bool ok, string what)
    {
        Debug.Log((ok ? "STABLECHECK: OK   " : "STABLECHECK: FAIL ") + what);
        if (!ok) fails++;
    }

    public static void Run()
    {
        SonarLandmarks.Clear();

        // 1. стабильный id: одна и та же дверь, сдвинутая на 5 см, даёт тот же id; другой тип или другая клетка - другой
        var p = new Vector3(2.03f, 1.0f, -1.47f);
        int idA = SonarLandmarks.MakeId(LandmarkKind.Door, p);
        int idB = SonarLandmarks.MakeId(LandmarkKind.Door, p + new Vector3(0.05f, 0f, 0.05f));
        int idW = SonarLandmarks.MakeId(LandmarkKind.Window, p);
        int idFar = SonarLandmarks.MakeId(LandmarkKind.Door, p + new Vector3(1.5f, 0f, 0f));
        Check(idA == idB, $"id двери не меняется при сдвиге 5 см ({idA} / {idB})");
        Check(idA != idW && idA != idFar, $"id отличает тип и клетку ({idA} / {idW} / {idFar})");

        // 2. Report добавляет и обновляет, не дублируя
        SonarLandmarks.Report(idA, LandmarkKind.Door, p);
        SonarLandmarks.Report(idA, LandmarkKind.Door, p + Vector3.right * 0.02f);
        SonarLandmarks.Report(idW, LandmarkKind.Window, p);
        Check(SonarLandmarks.All.Count == 2, $"после трёх Report в реестре 2 ориентира (есть {SonarLandmarks.All.Count})");

        // 3. текст сохраняется, null не стирает прежний
        int idT = SonarLandmarks.MakeId(LandmarkKind.Text, p);
        SonarLandmarks.Report(idT, LandmarkKind.Text, p, "ВЫХОД");
        SonarLandmarks.Report(idT, LandmarkKind.Text, p, null);
        string txt = null;
        foreach (var lm in SonarLandmarks.All) if (lm.id == idT) txt = lm.text;
        Check(txt == "ВЫХОД", $"текст ориентира сохранён ({txt})");

        // 4. свежесть: непостоянный ориентир устаревает через ttl, постоянный - нет
        float now = Time.time;
        var fresh = new Landmark { lastSeen = now, persistent = false };
        var stale = new Landmark { lastSeen = now - 10f, persistent = false };
        var door = new Landmark { lastSeen = now - 10f, persistent = true };
        Check(fresh.IsFresh(now, SonarLandmarks.ttl) && !stale.IsFresh(now, SonarLandmarks.ttl) && door.IsFresh(now, SonarLandmarks.ttl),
              "IsFresh: свежий да, старый нет, постоянный да");

        // 5. Remove / Clear
        SonarLandmarks.Remove(idW);
        Check(SonarLandmarks.All.Count == 2, $"Remove убирает один ({SonarLandmarks.All.Count})");
        SonarLandmarks.Clear();
        Check(SonarLandmarks.All.Count == 0, "Clear очищает реестр");

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
}
