using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

// Только для временного проекта проверки (ветка vision). Проверяет без шлема чистую логику ветки зрения:
//  1) NavZones.FindPassages / EvaluateZones / Corridor на синтетических комнатах (двери по карте для DoorFinder);
//  2) SignText: нормализация, отсев мусора OCR, сборка слов в строки и строк в таблички, похожесть;
//  3) TextTracker: текст читается только после подтверждения в нескольких сканах, похожие тексты сливаются;
//  4) PassthroughCameraFeed.ToGray: яркость и переворот строк (строка 0 результата = верх картинки).
// Компиляция всего остального (Tesseract через JNI, камера passthrough, TTS) проверяется самим запуском.
// Результат - строки VISIONCHECK:, последняя "OK DONE"; код выхода 3 при провале.
public static class VisionCheck
{
    const float Cs = 0.1f;
    static int fails;

    static void Check(bool ok, string what)
    {
        Debug.Log((ok ? "VISIONCHECK: OK   " : "VISIONCHECK: FAIL ") + what);
        if (!ok) fails++;
    }

    class Grid
    {
        public readonly Dictionary<Vector2Int, NavZones.Cell> cells = new();
        public NavZones.Cell Query(Vector2Int k) { return cells.TryGetValue(k, out var c) ? c : NavZones.Cell.Unknown; }

        public void Fill(float x0, float z0, float x1, float z1, NavZones.Cell kind)
        {
            for (float x = x0; x <= x1 + 1e-4f; x += Cs * 0.5f)
                for (float z = z0; z <= z1 + 1e-4f; z += Cs * 0.5f)
                    cells[NavZones.ToCell(new Vector3(x, 0f, z), Cs)] = kind;
        }

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

    /// <summary>Комната 6x6 м, наблюдатель в (0,0,0) смотрит на +z; в передней стене проём шириной w с центром x = cx.</summary>
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

    static OcrWord W(string text, float conf, int x, int y, int w, int h, int line)
    {
        return new OcrWord { text = text, confidence = conf, rect = new RectInt(x, y, w, h), line = line };
    }

    public static void Run()
    {
        // ---------------- 1. двери по карте
        var res = new List<NavZones.Passage>();
        int n = Passages(Room(1f, 0.9f), res);
        Check(n == 1, $"дверь 0.9 м найдена ровно один раз (найдено {n})");
        if (n >= 1)
        {
            var p = res[0];
            Check(p.width >= 0.7f && p.width <= 1.25f, $"ширина двери {p.width:F2} м (ожидается ~0.9)");
            Check(Mathf.Abs(p.center.x - 1f) < 0.2f && Mathf.Abs(p.center.z - 3f) < 0.2f, $"центр двери ({p.center.x:F2}, {p.center.z:F2}) (ожидается (1, 3))");
            Check(p.depthBeyond >= 1f && p.open, $"глубина за дверью {p.depthBeyond:F1} м, open={p.open}");
        }
        n = Passages(Room(1f, 0.9f, NavZones.Cell.Tall, false), res);
        Check(n == 1, $"дверь найдена, когда за ней неизвестно (найдено {n})");
        n = Passages(Room(-1.5f, 0.8f), res);
        Check(n == 1 && res.Count > 0 && res[0].center.x < -1f, $"дверь слева найдена (найдено {n})");
        n = Passages(Room(1f, 2.5f), res);
        Check(n == 0, $"проём 2.5 м не считается дверью (найдено {n})");
        n = Passages(Room(1f, 0.9f, NavZones.Cell.Low), res);
        Check(n == 0, $"зазор между низкой мебелью не считается дверью (найдено {n})");
        var niche = Room(1f, 0.9f, NavZones.Cell.Tall, false);
        niche.Fill(0.6f, 3.05f, 1.4f, 3.45f, NavZones.Cell.Free);
        niche.Wall(0.5f, 3.5f, 1.5f, 3.5f);
        n = Passages(niche, res);
        Check(n == 0, $"ниша 0.5 м не считается дверью (найдено {n})");

        // дверь с «низкими» клетками в проёме (порог / ошибка пола): без tallOnly не находится, с tallOnly находится
        var sill = Room(1f, 0.9f);
        sill.Fill(0.6f, 2.95f, 1.4f, 3.15f, NavZones.Cell.Low);
        int nPlain = Passages(sill, res);
        int nTall = NavZones.FindPassages(sill.Query, Cs, Vector3.zero, Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true);
        Check(nPlain == 0 && nTall == 1, $"дверь с порогом из низких клеток: обычный поиск {nPlain}, только по высоким {nTall} (ожидается 0 и 1)");
        n = NavZones.FindPassages(Room(1f, 0.9f, NavZones.Cell.Low).Query, Cs, Vector3.zero, Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true);
        Check(n == 0, $"tallOnly: зазор между низкой мебелью по-прежнему не дверь ({n})");

        // косяки должны быть в стене: два отдельно стоящих высоких предмета (шкаф и монитор) с зазором 0.9 м - не дверь,
        // а настоящая дверь в стене с тем же требованием находится; проём ближе 0.8 м от головы не ищется
        var pillars = new Grid();
        pillars.Fill(-3f, -3f, 3f, 3f, NavZones.Cell.Free);
        pillars.Fill(0.3f, 2.0f, 0.5f, 2.2f, NavZones.Cell.Tall);
        pillars.Fill(1.4f, 2.0f, 1.6f, 2.2f, NavZones.Cell.Tall);
        int nPillarsPlain = Passages(pillars, res);
        int nPillarsWall = NavZones.FindPassages(pillars.Query, Cs, Vector3.zero, Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true, 0.8f, true);
        int nDoorWall = NavZones.FindPassages(Room(1f, 0.9f).Query, Cs, Vector3.zero, Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true, 0.8f, true);
        Check(nPillarsPlain >= 1 && nPillarsWall == 0 && nDoorWall == 1,
              $"зазор между двумя столбами: без проверки стены {nPillarsPlain}, с проверкой {nPillarsWall}; дверь в стене с проверкой {nDoorWall} (ожидается >=1, 0, 1)");
        int nNear = NavZones.FindPassages(Room(1f, 0.9f).Query, Cs, new Vector3(1f, 0f, 2.6f), Vector3.forward, 90f, 2f, 5f, 0.6f, 1.6f, 1f, 0.25f, res, true, 0.8f, true);
        Check(nNear == 0, $"дверь в 0.4 м от головы не считается ({nNear})");

        var slot = new Grid();
        slot.Fill(-3f, -3f, 3f, 3f, NavZones.Cell.Free);
        slot.Wall(-1f, 2f, -0.2f, 2f);
        slot.Wall(0.2f, 2f, 1f, 2f);
        float wide = NavZones.Corridor(slot.Query, Cs, Vector3.zero, Vector3.forward, 0.6f, 4f, 0.25f, out _);
        Check(Mathf.Abs(wide - 2f) < 0.15f, $"коридор 0.6 м упирается в щель 0.4 м на {wide:F2} м (ожидается 2.0)");

        // ---------------- 2. SignText
        Check(SignText.Normalize("  выход  ") == "ВЫХОД", $"Normalize: '{SignText.Normalize("  выход  ")}'");
        Check(SignText.Normalize("Каб.|12\"") == "КАБ. 12", $"Normalize знаки: '{SignText.Normalize("Каб.|12\"")}'");
        Check(SignText.Normalize("a  b\tc") == "A B C", "Normalize схлопывает пробелы");
        Check(SignText.IsPlausibleWord("ВЫХОД", 85f, 60f), "ВЫХОД 85% принимается");
        Check(!SignText.IsPlausibleWord("ВЫХОД", 40f, 60f), "ВЫХОД 40% отклоняется (низкая уверенность)");
        Check(!SignText.IsPlausibleWord("|||", 95f, 60f), "'|||' отклоняется");
        Check(!SignText.IsPlausibleWord("А", 95f, 60f), "одна буква отклоняется");
        Check(!SignText.IsPlausibleWord("НА", 65f, 60f) && SignText.IsPlausibleWord("НА", 80f, 60f), "двухбуквенное слово требует уверенности повыше");
        Check(!SignText.IsPlausibleWord("ШШШШ", 90f, 60f), "'ШШШШ' отклоняется (повтор)");
        Check(SignText.IsPlausibleWord("12", 80f, 60f) && SignText.IsPlausibleWord("Б-2", 80f, 60f), "номера принимаются");
        Check(SignText.Similarity("ВЫХОД", "ВЫХОД") == 1f && SignText.Similarity("ВЫХОД", "ВЫХ0Д") >= 0.75f && SignText.Similarity("ВЫХОД", "ТУАЛЕТ") < 0.5f,
              $"похожесть: {SignText.Similarity("ВЫХОД", "ВЫХ0Д"):F2} / {SignText.Similarity("ВЫХОД", "ТУАЛЕТ"):F2}");

        // слова одной строки -> одна табличка; две строки рядом -> одна табличка; далёкая строка -> отдельная; мусор выброшен
        var words = new List<OcrWord>
        {
            W("Кабинет", 88f, 100, 100, 140, 30, 0),
            W("12", 90f, 250, 102, 40, 28, 0),
            W("Директор", 82f, 110, 140, 160, 30, 1),      // строкой ниже, зазор 10 px
            W("~~", 95f, 300, 140, 20, 30, 1),             // мусор
            W("ВЫХОД", 91f, 600, 700, 150, 40, 2),         // далеко
            W("x", 30f, 10, 10, 10, 10, 3)                 // мусор
        };
        var signs = SignText.GroupIntoSigns(words, 60f);
        Check(signs.Count == 2, $"из 6 слов получилось 2 таблички (получено {signs.Count})");
        if (signs.Count == 2)
        {
            Check(signs[0].text == "КАБИНЕТ 12, ДИРЕКТОР", $"табличка 1: '{signs[0].text}' (ожидается 'КАБИНЕТ 12, ДИРЕКТОР': строки через запятую = пауза голоса)");
            Check(signs[1].text == "ВЫХОД", $"табличка 2: '{signs[1].text}'");
            Check(signs[0].rect.xMin == 100 && signs[0].rect.yMin == 100 && signs[0].rect.xMax == 290 && signs[0].rect.yMax == 170,
                  $"прямоугольник таблички 1: {signs[0].rect}");
            Check(signs[0].words == 3 && signs[0].confidence > 85f, $"слов {signs[0].words}, уверенность {signs[0].confidence:F0}");
        }

        // одна строка кусками из разных проходов (разные номера строк) склеивается без запятой; слово у края кадра отбрасывается
        var pieces = new List<OcrWord>
        {
            W("ACEST", 90f, 100, 300, 80, 24, 0), W("SPATIU", 88f, 190, 301, 90, 23, 7), W("FOST", 85f, 290, 299, 60, 24, 12),
            W("DOTAT", 86f, 100, 340, 80, 24, 20),
            W("MWORK", 95f, 1200, 500, 78, 30, 30)   // упирается в правый край кадра 1280
        };
        var ps = SignText.GroupIntoSigns(pieces, 60f, 1.0f, 1280, 960);
        Check(ps.Count == 1 && ps[0].text == "ACEST SPATIU FOST, DOTAT", $"куски строки склеены: '{(ps.Count > 0 ? ps[0].text : "-")}' (табличек {ps.Count}; ожидается 'ACEST SPATIU FOST, DOTAT' и без MWORK)");

        // номер кабинета в тексте таблички
        Check(SignText.ExtractRoomNumber("3-212, DEPARTAMENT INFORMATICĂ") == "3-212" && SignText.ExtractRoomNumber("LABORATOR 212") == "212"
              && SignText.ExtractRoomNumber("CAB. 101A") == "101A" && SignText.ExtractRoomNumber("TEAMWORK") == null
              && SignText.ExtractRoomNumber("PROIECT 2024") == null && SignText.ExtractRoomNumber("01001001 01000001") == null,
              "номер кабинета: 3-212 / 212 / 101A находятся, TEAMWORK / 2024 / двоичный код - нет");

        // ---------------- 3. TextTracker
        var tracker = new TextTracker { needed = 2, similarity = 0.75f, forgetAfter = 10f };
        var confirmed = new List<TextTracker.Track>();
        var updated = new List<TextTracker.Track>();
        var s1 = new List<SignCandidate> { new SignCandidate { text = "ВЫХОД", rect = new RectInt(0, 0, 100, 40), confidence = 80f, words = 1 } };
        tracker.Observe(s1, 0f, confirmed, updated);
        Check(confirmed.Count == 0 && updated.Count == 1, $"первый скан: не подтверждено ({confirmed.Count}), обновлено {updated.Count}");
        var s2 = new List<SignCandidate> { new SignCandidate { text = "ВЫХ0Д", rect = new RectInt(5, 0, 100, 40), confidence = 70f, words = 1 } };
        tracker.Observe(s2, 2.5f, confirmed, updated);
        Check(confirmed.Count == 1 && confirmed[0].text == "ВЫХОД" && tracker.Tracks.Count == 1,
              $"второй скан с похожим текстом подтверждает (треков {tracker.Tracks.Count}, текст '{(confirmed.Count > 0 ? confirmed[0].text : "-")}')");
        var s3 = new List<SignCandidate> { new SignCandidate { text = "ТУАЛЕТ", rect = new RectInt(0, 0, 100, 40), confidence = 80f, words = 1 } };
        tracker.Observe(s3, 5f, confirmed, updated);
        Check(confirmed.Count == 0 && tracker.Tracks.Count == 2, $"другой текст: новый трек, не подтверждён (треков {tracker.Tracks.Count})");
        tracker.Observe(new List<SignCandidate>(), 40f, confirmed, updated);
        Check(tracker.Tracks.Count == 0, $"через 35 с без наблюдений треки забыты ({tracker.Tracks.Count})");
        // надёжная табличка подтверждается с первого скана, слабая - нет
        var strong = new List<SignCandidate> { new SignCandidate { text = "DEPARTAMENT INFORMATICĂ", rect = new RectInt(0, 0, 300, 40), confidence = 92f, words = 2 } };
        tracker.Observe(strong, 50f, confirmed, updated);
        Check(confirmed.Count == 1, $"надёжный текст (92%, 22 буквы) подтверждён с первого скана ({confirmed.Count})");
        var weak = new List<SignCandidate> { new SignCandidate { text = "2/09 2/09", rect = new RectInt(0, 0, 100, 40), confidence = 94f, words = 2 } };
        tracker.Observe(weak, 51f, confirmed, updated);
        Check(confirmed.Count == 0, $"короткий текст '2/09 2/09' с первого скана не подтверждён ({confirmed.Count})");

        // ---------------- 3б. OcrImage: контраст, вырез с увеличением, области
        var dim = new byte[1000];
        for (int i = 0; i < dim.Length; i++) dim[i] = (byte)(100 + (i % 50));   // тусклый кадр 100..149
        var norm = new byte[1000];
        OcrImage.NormalizeContrast(dim, norm, dim.Length);
        int lo = 255, hi = 0;
        foreach (var b in norm) { if (b < lo) lo = b; if (b > hi) hi = b; }
        Check(lo <= 5 && hi >= 250, $"контраст растянут: {lo}..{hi} (ожидается ~0..255)");

        var img = new byte[8 * 8];
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) img[y * 8 + x] = (byte)(x < 4 ? 0 : 200);   // левая половина чёрная
        var crop = OcrImage.CropScale(img, 8, 8, new RectInt(2, 2, 4, 4), 3);
        Check(crop.Length == 144 && crop[0] == 0 && crop[11] == 200 && crop[5] > 40 && crop[5] < 160,
              $"вырез 4x4 ×3 = 12x12: {crop.Length} байт, левый {crop[0]}, правый {crop[11]}, переход {crop[5]}");

        var raw = new List<OcrWord>
        {
            W("Каб", 40f, 100, 100, 60, 20, 0), W("12", 35f, 170, 101, 30, 19, 0),   // неуверенные, но рядом -> одна область
            W("ВЫХОД", 80f, 600, 500, 150, 40, 1),                                     // отдельная область
            W("~", 90f, 300, 300, 10, 10, 2)                                           // без букв - не область
        };
        var regs = OcrImage.FindRegions(raw, 1280, 960, 10f, 6);
        Check(regs.Count == 2 && regs[0].words.Count == 1 && regs[1].words.Count == 2,
              $"областей {regs.Count} (ожидается 2: ВЫХОД крупнее, затем Каб+12)");
        if (regs.Count == 2)
            Check(regs[1].rect.xMin == 100 && regs[1].rect.xMax == 200 && regs[1].medianHeight >= 19f && regs[1].medianHeight <= 20f,
                  $"область Каб 12: {regs[1].rect}, медианная высота {regs[1].medianHeight}");
        var dup = new List<OcrWord>
        {
            W("LA", 70f, 100, 100, 40, 20, 0), W("LA", 85f, 102, 101, 40, 19, 5),   // одно слово из двух проходов
            W("LA", 80f, 300, 100, 40, 20, 0)                                       // другое место - остаётся
        };
        OcrImage.Dedupe(dup);
        Check(dup.Count == 2 && dup[0].confidence == 85f, $"дубли слов убраны: осталось {dup.Count}, уверенность первого {dup[0].confidence}");
        var ex = OcrImage.Expand(new RectInt(5, 5, 10, 10), 8, 100, 100);
        Check(ex.xMin == 0 && ex.yMin == 0 && ex.xMax == 23 && ex.yMax == 23, $"Expand обрезает по кадру: {ex}");
        Check(OcrImage.Score(new List<OcrWord> { W("АБВ", 90f, 0, 0, 1, 1, 0), W("Г", 30f, 0, 0, 1, 1, 0) }) == 75f, "Score взвешен по длине (90*3+30*1)/4 = 75");

        // ---------------- 4. ToGray с переворотом
        var src = new NativeArray<Color32>(4, Allocator.Temp);
        src[0] = new Color32(255, 0, 0, 255);     // низ слева  -> серый 76
        src[1] = new Color32(0, 255, 0, 255);     // низ справа -> 149
        src[2] = new Color32(0, 0, 255, 255);     // верх слева -> 28
        src[3] = new Color32(255, 255, 255, 255); // верх справа -> 255
        var dst = new byte[4];
        PassthroughCameraFeed.ToGray(src, dst, 2, 2, true);
        Check(dst[0] == 28 && dst[1] == 255 && dst[2] == 76 && dst[3] == 149, $"ToGray flip: {dst[0]} {dst[1]} {dst[2]} {dst[3]} (ожидается 28 255 76 149)");
        PassthroughCameraFeed.ToGray(src, dst, 2, 2, false);
        Check(dst[0] == 76 && dst[3] == 255, $"ToGray без flip: {dst[0]} {dst[3]}");
        src.Dispose();

        if (fails > 0)
        {
            Debug.LogError($"VISIONCHECK: FAILED ({fails})");
            EditorApplication.Exit(3);
        }
        else
        {
            Debug.Log("VISIONCHECK: OK DONE");
            EditorApplication.Exit(0);
        }
    }
}
