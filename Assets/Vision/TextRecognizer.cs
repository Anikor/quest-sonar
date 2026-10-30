using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>Одно слово из OCR: текст, уверенность 0..100, прямоугольник в пикселях кадра (y вниз от верхнего края), номер строки.</summary>
public struct OcrWord
{
    public string text;
    public float confidence;
    public RectInt rect;
    public int line;
}

/// <summary>Движок распознавания текста. Init и Recognize вызываются из рабочего потока (не главного).</summary>
public interface ITextRecognizer : IDisposable
{
    bool Ready { get; }
    string Status { get; }
    /// <param name="dataDir">папка, в которой лежит подпапка tessdata/ с *.traineddata</param>
    /// <param name="languages">языки через плюс, например "rus+eng"</param>
    bool Init(string dataDir, string languages);
    /// <summary>Распознать серый кадр (1 байт на пиксель, строки сверху вниз).</summary>
    void Recognize(byte[] gray, int width, int height, List<OcrWord> result);
}

/// <summary>Заглушка для редактора и не-Android платформ: ничего не распознаёт.</summary>
public sealed class NullRecognizer : ITextRecognizer
{
    public bool Ready => false;
    public string Status => "OCR работает только на Android (Tesseract)";
    public bool Init(string dataDir, string languages) { return false; }
    public void Recognize(byte[] gray, int width, int height, List<OcrWord> result) { result.Clear(); }
    public void Dispose() { }
}

/// <summary>
/// Tesseract OCR через библиотеку Tesseract4Android (Assets/Plugins/Android/tesseract4android-4.8.0.aar, Tesseract 5.5, только arm64).
/// Языковые данные: Assets/StreamingAssets/tessdata/rus.traineddata, eng.traineddata (tessdata_fast); SignReader копирует их
/// в Application.persistentDataPath/tesseract/tessdata при первом запуске.
/// Режим: только LSTM, разрежённый текст (PSM 11): ищет отдельные надписи по всему кадру, а не страницу текста.
/// </summary>
public sealed class TesseractRecognizer : ITextRecognizer
{
    const int OemLstmOnly = 1;
    const int PsmSparseText = 11;
    const int RilTextline = 2, RilWord = 3;

    AndroidJavaObject api;
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "не запущен";
    public string Version { get; private set; } = "?";

    public bool Init(string dataDir, string languages)
    {
        try
        {
            api = new AndroidJavaObject("com.googlecode.tesseract.android.TessBaseAPI");
            if (!dataDir.EndsWith("/")) dataDir += "/";
            bool ok = api.Call<bool>("init", dataDir, languages, OemLstmOnly);
            if (!ok)
            {
                Status = "Tesseract: init не удался (нет " + dataDir + "tessdata/{" + languages + "}.traineddata?)";
                return false;
            }
            api.Call("setPageSegMode", PsmSparseText);
            api.Call<bool>("setVariable", "preserve_interword_spaces", "1");
            try { Version = api.Call<string>("getVersion"); } catch { Version = "?"; }
            Ready = true;
            Status = "Tesseract " + Version + " (" + languages + ")";
            return true;
        }
        catch (Exception e)
        {
            Status = "Tesseract: " + e.Message;
            Ready = false;
            return false;
        }
    }

    public void Recognize(byte[] gray, int width, int height, List<OcrWord> result)
    {
        result.Clear();
        if (!Ready || api == null || gray == null || gray.Length < width * height) return;

        // Java byte[] = sbyte[] в Unity. 1 байт на пиксель, bytes per line = width.
        var signed = new sbyte[width * height];
        Buffer.BlockCopy(gray, 0, signed, 0, width * height);

        AndroidJavaObject it = null;
        try
        {
            api.Call("setImage", signed, width, height, 1, width);
            string all = api.Call<string>("getUTF8Text");   // здесь выполняется само распознавание
            if (string.IsNullOrWhiteSpace(all)) return;

            it = api.Call<AndroidJavaObject>("getResultIterator");
            if (it == null) return;
            it.Call("begin");
            int line = -1;
            do
            {
                if (it.Call<bool>("isAtBeginningOf", RilTextline)) line++;
                string t = it.Call<string>("getUTF8Text", RilWord);
                float conf = it.Call<float>("confidence", RilWord);
                int[] box = it.Call<int[]>("getBoundingBox", RilWord);   // left, top, right, bottom
                if (string.IsNullOrEmpty(t) || box == null || box.Length < 4) continue;
                result.Add(new OcrWord
                {
                    text = t,
                    confidence = conf,
                    line = Math.Max(0, line),
                    rect = new RectInt(box[0], box[1], Math.Max(1, box[2] - box[0]), Math.Max(1, box[3] - box[1]))
                });
            } while (it.Call<bool>("next", RilWord));
        }
        catch (Exception e)
        {
            Status = "Tesseract: " + e.Message;
        }
        finally
        {
            if (it != null)
            {
                try { it.Call("delete"); } catch { /* не важно */ }
                it.Dispose();
            }
            try { api.Call("clear"); } catch { /* не важно */ }
        }
    }

    public void Dispose()
    {
        if (api == null) return;
        try { api.Call("recycle"); } catch { /* не важно */ }
        api.Dispose();
        api = null;
        Ready = false;
    }
}

/// <summary>Надпись (табличка): одна или несколько строк текста рядом друг с другом.</summary>
public sealed class SignCandidate
{
    public string text;
    public RectInt rect;
    public float confidence;
    public int words;
    public Vector2 Center => new Vector2(rect.x + rect.width * 0.5f, rect.y + rect.height * 0.5f);
    public int Area => rect.width * rect.height;
}

/// <summary>
/// Чистая логика обработки результатов OCR (без сцены, проверяется тестом tools/unity-check/VisionCheck.cs):
/// нормализация слов, отсев мусора, сборка строк в таблички, похожесть строк.
/// </summary>
public static class SignText
{
    /// <summary>Верхний регистр, только буквы, цифры, пробел и немного знаков; лишние пробелы схлопнуты.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        bool space = true;
        foreach (char ch in s)
        {
            char c = ch;
            if (char.IsLetterOrDigit(c) || c == '-' || c == '/' || c == '№' || c == '.' || c == ',' || c == ':')
            {
                sb.Append(char.ToUpperInvariant(c));
                space = false;
            }
            else if (char.IsWhiteSpace(c) || c == '_' || c == '|')
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            // остальное (кавычки, скобки, мусорные символы) выбрасывается
        }
        return sb.ToString().Trim();
    }

    static int AlnumCount(string s)
    {
        int n = 0;
        foreach (char c in s) if (char.IsLetterOrDigit(c)) n++;
        return n;
    }

    static bool HasTripleRepeat(string s)
    {
        for (int i = 2; i < s.Length; i++)
            if (s[i] == s[i - 1] && s[i] == s[i - 2] && !char.IsDigit(s[i])) return true;
        return false;
    }

    /// <summary>Слово похоже на настоящее: достаточная уверенность, хотя бы две буквы или цифры, мало знаков, без «ааа».</summary>
    public static bool IsPlausibleWord(string normalized, float confidence, float minConfidence)
    {
        if (string.IsNullOrEmpty(normalized)) return false;
        if (confidence < minConfidence) return false;
        int alnum = AlnumCount(normalized);
        if (alnum < 2) return false;
        if (alnum < normalized.Length * 0.6f) return false;
        if (normalized.Length == 2 && confidence < minConfidence + 15f) return false;   // короткие слова чаще шум
        if (HasTripleRepeat(normalized)) return false;
        return true;
    }

    public static int Levenshtein(string a, string b)
    {
        if (string.IsNullOrEmpty(a)) return b?.Length ?? 0;
        if (string.IsNullOrEmpty(b)) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            var t = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }

    /// <summary>1 = одинаковые строки, 0 = совсем разные.</summary>
    public static float Similarity(string a, string b)
    {
        int max = Math.Max(a?.Length ?? 0, b?.Length ?? 0);
        if (max == 0) return 1f;
        return 1f - Levenshtein(a ?? "", b ?? "") / (float)max;
    }

    sealed class Line
    {
        public int index;
        public readonly List<OcrWord> words = new List<OcrWord>();
        public RectInt rect;
        public string text;
        public float confSum;
    }

    static RectInt Union(RectInt a, RectInt b)
    {
        int x0 = Math.Min(a.xMin, b.xMin), y0 = Math.Min(a.yMin, b.yMin);
        int x1 = Math.Max(a.xMax, b.xMax), y1 = Math.Max(a.yMax, b.yMax);
        return new RectInt(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Отсеивает мусорные слова, собирает слова в строки (по номеру строки OCR), строки, лежащие близко друг под другом,
    /// объединяет в одну табличку. Текст таблички - строки сверху вниз через пробел.
    /// </summary>
    public static List<SignCandidate> GroupIntoSigns(List<OcrWord> words, float minConfidence, float lineGapFactor = 1.0f)
    {
        var lines = new Dictionary<int, Line>();
        foreach (var w in words)
        {
            string t = Normalize(w.text);
            if (!IsPlausibleWord(t, w.confidence, minConfidence)) continue;
            if (!lines.TryGetValue(w.line, out var ln))
            {
                ln = new Line { index = w.line, rect = w.rect };
                lines[w.line] = ln;
            }
            var nw = w; nw.text = t;
            ln.words.Add(nw);
            ln.rect = ln.words.Count == 1 ? w.rect : Union(ln.rect, w.rect);
            ln.confSum += w.confidence;
        }

        var list = new List<Line>(lines.Values);
        foreach (var ln in list)
        {
            ln.words.Sort((a, b) => a.rect.xMin.CompareTo(b.rect.xMin));
            var sb = new StringBuilder();
            foreach (var w in ln.words) { if (sb.Length > 0) sb.Append(' '); sb.Append(w.text); }
            ln.text = sb.ToString();
        }

        // объединение строк (union-find)
        int n = list.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                RectInt a = list[i].rect, b = list[j].rect;
                float h = Math.Max(a.height, b.height);
                int vGap = Math.Max(0, Math.Max(a.yMin, b.yMin) - Math.Min(a.yMax, b.yMax));
                int hGap = Math.Max(0, Math.Max(a.xMin, b.xMin) - Math.Min(a.xMax, b.xMax));
                if (vGap <= lineGapFactor * h && hGap <= 1.5f * h)
                {
                    int ra = Find(i), rb = Find(j);
                    if (ra != rb) parent[ra] = rb;
                }
            }

        var groups = new Dictionary<int, List<Line>>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!groups.TryGetValue(r, out var g)) { g = new List<Line>(); groups[r] = g; }
            g.Add(list[i]);
        }

        var result = new List<SignCandidate>();
        foreach (var g in groups.Values)
        {
            g.Sort((a, b) => a.rect.yMin.CompareTo(b.rect.yMin));
            var sb = new StringBuilder();
            RectInt rect = g[0].rect;
            float conf = 0f; int wc = 0;
            foreach (var ln in g)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(ln.text);
                rect = Union(rect, ln.rect);
                conf += ln.confSum;
                wc += ln.words.Count;
            }
            result.Add(new SignCandidate { text = sb.ToString(), rect = rect, confidence = wc > 0 ? conf / wc : 0f, words = wc });
        }
        result.Sort((a, b) => b.Area.CompareTo(a.Area));   // крупные (ближние) первыми
        return result;
    }
}

/// <summary>
/// Подтверждение текста по времени: табличка считается настоящей, когда похожий текст виден в нескольких сканах подряд.
/// Так случайный мусор одного кадра не читается вслух. Чистая логика, проверяется тестом.
/// </summary>
public sealed class TextTracker
{
    public sealed class Track
    {
        public string text;
        public Vector2 pixel;
        public int seen;
        public float lastSeen;
        public float bestConf;
        public bool announced;
    }

    [Tooltip("Похожесть текстов (0..1), чтобы считать их одной табличкой")]
    public float similarity = 0.75f;
    public float forgetAfter = 10f;
    public int needed = 2;

    readonly List<Track> tracks = new List<Track>();
    public IReadOnlyList<Track> Tracks => tracks;

    /// <summary>Учитывает новые кандидаты. В confirmed складываются треки, увиденные >= needed раз (среди обновлённых сейчас); в updated - все обновлённые.</summary>
    public void Observe(List<SignCandidate> signs, float now, List<Track> confirmed, List<Track> updated = null)
    {
        confirmed.Clear();
        updated?.Clear();
        var used = new HashSet<Track>();
        foreach (var s in signs)
        {
            Track best = null;
            float bestSim = 0f;
            foreach (var t in tracks)
            {
                if (used.Contains(t)) continue;
                float sim = SignText.Similarity(s.text, t.text);
                if (sim > bestSim) { bestSim = sim; best = t; }
            }
            if (best == null || bestSim < similarity)
            {
                best = new Track { text = s.text, bestConf = s.confidence };
                tracks.Add(best);
            }
            else if (s.confidence > best.bestConf || (s.text.Length > best.text.Length && s.confidence >= best.bestConf - 5f))
            {
                best.text = s.text;
                best.bestConf = s.confidence;
            }
            best.seen++;
            best.lastSeen = now;
            best.pixel = s.Center;
            used.Add(best);
            updated?.Add(best);
            if (best.seen >= needed) confirmed.Add(best);
        }
        tracks.RemoveAll(t => now - t.lastSeen > forgetAfter);
    }

    public void Clear() { tracks.Clear(); }
}
