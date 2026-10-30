using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>Одно слово из OCR: текст, уверенность 0..100, прямоугольник в пикселях кадра (y вниз от верхнего края), номер строки, проход (1 = весь кадр, 2 = увеличенный вырез).</summary>
public struct OcrWord
{
    public string text;
    public float confidence;
    public RectInt rect;
    public int line;
    public int pass;
    /// <summary>Найдено на инвертированном кадре: светлый текст на тёмном фоне (номера кабинетов на тёмной полосе и т.п.).</summary>
    public bool inverted;
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
/// Обработка изображения перед OCR и вырезы для второго прохода (чистые функции, проверяются тестом).
/// </summary>
public static class OcrImage
{
    /// <summary>Растяжение контраста по перцентилям (2% и 98%): камера даёт тусклый серый, Tesseract любит чёрное на белом.</summary>
    public static void NormalizeContrast(byte[] src, byte[] dst, int n)
    {
        var hist = new int[256];
        for (int i = 0; i < n; i++) hist[src[i]]++;
        int lo = 0, hi = 255, acc = 0;
        int loTarget = (int)(n * 0.02f), hiTarget = (int)(n * 0.98f);
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= loTarget) { lo = v; break; } }
        acc = 0;
        for (int v = 0; v < 256; v++) { acc += hist[v]; if (acc >= hiTarget) { hi = v; break; } }
        if (hi - lo < 16) { Buffer.BlockCopy(src, 0, dst, 0, n); return; }
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)Mathf.Clamp(Mathf.RoundToInt((v - lo) * 255f / (hi - lo)), 0, 255);
        for (int i = 0; i < n; i++) dst[i] = lut[src[i]];
    }

    /// <summary>Вырез rect из кадра w×h, увеличенный в scale раз (билинейно). Размер результата rect.width*scale × rect.height*scale.</summary>
    public static byte[] CropScale(byte[] img, int w, int h, RectInt rect, int scale)
    {
        int cw = rect.width * scale, ch = rect.height * scale;
        var dst = new byte[cw * ch];
        float inv = 1f / scale;
        for (int y = 0; y < ch; y++)
        {
            float sy = rect.y + (y + 0.5f) * inv - 0.5f;
            int y0 = Mathf.Clamp(Mathf.FloorToInt(sy), 0, h - 1);
            int y1 = Mathf.Min(y0 + 1, h - 1);
            float fy = Mathf.Clamp01(sy - y0);
            int row = y * cw;
            for (int x = 0; x < cw; x++)
            {
                float sx = rect.x + (x + 0.5f) * inv - 0.5f;
                int x0 = Mathf.Clamp(Mathf.FloorToInt(sx), 0, w - 1);
                int x1 = Mathf.Min(x0 + 1, w - 1);
                float fx = Mathf.Clamp01(sx - x0);
                float a = img[y0 * w + x0] * (1f - fx) + img[y0 * w + x1] * fx;
                float b = img[y1 * w + x0] * (1f - fx) + img[y1 * w + x1] * fx;
                dst[row + x] = (byte)(a * (1f - fy) + b * fy + 0.5f);
            }
        }
        return dst;
    }

    /// <summary>Область для второго прохода: несколько слов рядом.</summary>
    public sealed class Region
    {
        public RectInt rect;
        public readonly List<OcrWord> words = new List<OcrWord>();
        public float medianHeight;
    }

    /// <summary>
    /// Группирует слова первого прохода (даже неуверенные) в области: соседние по вертикали (зазор ≤ 1.2 высоты)
    /// и по горизонтали (зазор ≤ 2 высоты). Возвращает не больше maxRegions самых крупных.
    /// </summary>
    public static List<Region> FindRegions(List<OcrWord> words, int w, int h, float minConf, int maxRegions)
    {
        var cand = new List<OcrWord>();
        foreach (var o in words)
        {
            if (o.confidence < minConf) continue;
            if (o.rect.height < 6 || o.rect.height > h / 3 || o.rect.width < 4) continue;
            if (string.IsNullOrWhiteSpace(o.text)) continue;
            bool alnum = false;
            foreach (char c in o.text) if (char.IsLetterOrDigit(c)) { alnum = true; break; }
            if (!alnum) continue;
            cand.Add(o);
        }
        int n = cand.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                RectInt a = cand[i].rect, b = cand[j].rect;
                float hh = Math.Max(a.height, b.height);
                int vGap = Math.Max(0, Math.Max(a.yMin, b.yMin) - Math.Min(a.yMax, b.yMax));
                int hGap = Math.Max(0, Math.Max(a.xMin, b.xMin) - Math.Min(a.xMax, b.xMax));
                if (vGap <= 1.2f * hh && hGap <= 2f * hh)
                {
                    int ra = Find(i), rb = Find(j);
                    if (ra != rb) parent[ra] = rb;
                }
            }
        var map = new Dictionary<int, Region>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!map.TryGetValue(r, out var reg)) { reg = new Region { rect = cand[i].rect }; map[r] = reg; }
            else reg.rect = Union(reg.rect, cand[i].rect);
            reg.words.Add(cand[i]);
        }
        var list = new List<Region>(map.Values);
        foreach (var reg in list)
        {
            var hs = new List<int>();
            foreach (var o in reg.words) hs.Add(o.rect.height);
            hs.Sort();
            reg.medianHeight = hs[hs.Count / 2];
        }
        list.Sort((a, b) => (b.rect.width * b.rect.height).CompareTo(a.rect.width * a.rect.height));
        if (list.Count > maxRegions) list.RemoveRange(maxRegions, list.Count - maxRegions);
        return list;
    }

    public static RectInt Union(RectInt a, RectInt b)
    {
        int x0 = Math.Min(a.xMin, b.xMin), y0 = Math.Min(a.yMin, b.yMin);
        int x1 = Math.Max(a.xMax, b.xMax), y1 = Math.Max(a.yMax, b.yMax);
        return new RectInt(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>Расширить прямоугольник на margin и обрезать по кадру.</summary>
    public static RectInt Expand(RectInt r, int margin, int w, int h) { return Expand(r, margin, margin, w, h); }

    public static RectInt Expand(RectInt r, int marginX, int marginY, int w, int h)
    {
        int x0 = Math.Max(0, r.xMin - marginX), y0 = Math.Max(0, r.yMin - marginY);
        int x1 = Math.Min(w, r.xMax + marginX), y1 = Math.Min(h, r.yMax + marginY);
        return new RectInt(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    /// <summary>Качество набора слов: средняя уверенность, взвешенная по длине (−1, если слов нет).</summary>
    public static float Score(List<OcrWord> words)
    {
        float sum = 0f, wsum = 0f;
        foreach (var o in words)
        {
            int len = 0;
            foreach (char c in o.text) if (char.IsLetterOrDigit(c)) len++;
            if (len == 0) continue;
            sum += o.confidence * len;
            wsum += len;
        }
        return wsum > 0f ? sum / wsum : -1f;
    }
}

/// <summary>
/// Tesseract OCR через библиотеку Tesseract4Android (Assets/Plugins/Android/tesseract4android-4.8.0.aar, Tesseract 5.5, только arm64).
/// Языковые данные: Assets/StreamingAssets/tessdata/rus.traineddata, eng.traineddata (tessdata_fast); SignReader копирует их
/// в Application.persistentDataPath/tesseract/tessdata при первом запуске.
///
/// Два прохода: (1) весь кадр после растяжения контраста, режим «разрежённый текст» (PSM 11) - находит, где текст;
/// (2) каждая область текста вырезается с полем, увеличивается так, чтобы высота строки стала ~targetTextHeight пикселей,
/// и распознаётся как один блок (PSM 6). Берётся тот результат, у которого выше взвешенная уверенность.
/// Мелкий текст табличек в 1280×960 имеет высоту 10-25 px, а Tesseract уверенно читает от ~30 px, поэтому второй проход важен.
/// </summary>
public sealed class TesseractRecognizer : ITextRecognizer
{
    const int OemLstmOnly = 1;
    const int PsmSingleBlock = 6;
    const int PsmSparseText = 11;
    const int RilTextline = 2, RilWord = 3;

    public bool twoPass = true;
    public bool normalizeContrast = true;
    public bool useWhitelist = true;
    /// <summary>Искать и светлый текст на тёмном фоне: первый проход повторяется на инвертированном кадре (+0.5-1 с).</summary>
    public bool invertedPass = true;
    public int targetTextHeight = 32;
    public int maxScale = 4;
    public int maxRegions = 4;   // по журналу: 6 областей × увеличение до ×4 давали до 4.6 с на кадр
    /// <summary>Слова первого прохода с уверенностью ниже этой не образуют областей для второго (0 = любые слова с буквами).</summary>
    public float regionMinConf = 0f;

    /// <summary>Статистика последнего кадра для журнала.</summary>
    public int lastPass1Words, lastRegions, lastPass2Words;
    public float lastPass1Ms, lastPass2Ms;

    AndroidJavaObject api;
    byte[] work, workInv;
    sbyte[] signed;
    public bool Ready { get; private set; }
    public string Status { get; private set; } = "не запущен";
    public string Version { get; private set; } = "?";

    // кириллица, латиница, румынские буквы (ăâîșț и варианты с седилью şţ), цифры и немного знаков
    const string Whitelist = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя" +
                             "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz" +
                             "ĂÂÎȘȚăâîșțŞŢşţ0123456789 -.,:/№()";

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
            api.Call<bool>("setVariable", "tessedit_do_invert", "1");   // пробовать инверсию строки при низкой уверенности
            if (useWhitelist) api.Call<bool>("setVariable", "tessedit_char_whitelist", Whitelist);
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
        lastPass1Words = lastRegions = lastPass2Words = 0;
        lastPass1Ms = lastPass2Ms = 0f;
        if (!Ready || api == null || gray == null || gray.Length < width * height) return;

        int n = width * height;
        byte[] img = gray;
        if (normalizeContrast)
        {
            if (work == null || work.Length != n) work = new byte[n];
            OcrImage.NormalizeContrast(gray, work, n);
            img = work;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pass1 = new List<OcrWord>();
        RunPass(img, width, height, PsmSparseText, pass1, 0, 0, 1, 0, 1, false);

        // светлый текст на тёмном фоне (номер кабинета на тёмной полосе): тот же проход по инвертированному кадру
        byte[] inv = null;
        if (invertedPass)
        {
            if (workInv == null || workInv.Length != n) workInv = new byte[n];
            inv = workInv;
            for (int i = 0; i < n; i++) inv[i] = (byte)(255 - img[i]);
            RunPass(inv, width, height, PsmSparseText, pass1, 0, 0, 1, 1000, 1, true);
        }
        lastPass1Ms = (float)sw.Elapsed.TotalMilliseconds;
        lastPass1Words = pass1.Count;

        if (!twoPass || pass1.Count == 0)
        {
            result.AddRange(pass1);
            return;
        }

        sw.Restart();
        var regions = OcrImage.FindRegions(pass1, width, height, regionMinConf, maxRegions);
        lastRegions = regions.Count;
        int lineBase = 2000;
        var pass2 = new List<OcrWord>();
        foreach (var reg in regions)
        {
            int scale = Mathf.Clamp(Mathf.RoundToInt(targetTextHeight / Mathf.Max(4f, reg.medianHeight)), 1, maxScale);
            // поле вокруг области: по горизонтали щедрое (первый проход часто теряет начало строки), по вертикали - одна строка
            int marginX = Mathf.Max(24, Mathf.RoundToInt(reg.medianHeight * 4f));
            int marginY = Mathf.Max(10, Mathf.RoundToInt(reg.medianHeight * 1.2f));
            RectInt crop = OcrImage.Expand(reg.rect, marginX, marginY, width, height);
            long pixels = (long)crop.width * scale * crop.height * scale;
            if (pixels > 4_000_000L) scale = Mathf.Max(1, (int)Math.Sqrt(4_000_000.0 / ((long)crop.width * crop.height)));

            // область читаем в той полярности, в которой её нашёл первый проход (большинство слов)
            int invCount = 0;
            foreach (var o in reg.words) if (o.inverted) invCount++;
            bool useInv = inv != null && invCount * 2 > reg.words.Count;

            pass2.Clear();
            byte[] sub = OcrImage.CropScale(useInv ? inv : img, width, height, crop, scale);
            RunPass(sub, crop.width * scale, crop.height * scale, PsmSingleBlock, pass2, crop.x, crop.y, scale, lineBase, 2, useInv);

            if (OcrImage.Score(pass2) >= OcrImage.Score(reg.words))
            {
                result.AddRange(pass2);
                lastPass2Words += pass2.Count;
            }
            else
            {
                foreach (var o in reg.words)
                {
                    var c = o;
                    c.line = lineBase + o.line;
                    result.Add(c);
                }
            }
            lineBase += 100;
        }
        lastPass2Ms = (float)sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Один вызов Tesseract: картинка img (w×h, серый, сверху вниз). Рамки слов переводятся в координаты исходного кадра.</summary>
    void RunPass(byte[] img, int w, int h, int psm, List<OcrWord> outWords, int offX, int offY, int scale, int lineBase, int pass, bool inverted)
    {
        int n = w * h;
        if (signed == null || signed.Length < n) signed = new sbyte[n];
        Buffer.BlockCopy(img, 0, signed, 0, n);
        sbyte[] exact = signed;
        if (signed.Length != n) { exact = new sbyte[n]; Buffer.BlockCopy(img, 0, exact, 0, n); }

        AndroidJavaObject it = null;
        try
        {
            api.Call("setPageSegMode", psm);
            api.Call("setImage", exact, w, h, 1, w);
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
                int x0 = offX + box[0] / scale, y0 = offY + box[1] / scale;
                int x1 = offX + (box[2] + scale - 1) / scale, y1 = offY + (box[3] + scale - 1) / scale;
                outWords.Add(new OcrWord
                {
                    text = t,
                    confidence = conf,
                    line = lineBase + Math.Max(0, line),
                    pass = pass,
                    inverted = inverted,
                    rect = new RectInt(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0))
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
            ln.rect = ln.words.Count == 1 ? w.rect : OcrImage.Union(ln.rect, w.rect);
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
                rect = OcrImage.Union(rect, ln.rect);
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
    public float forgetAfter = 20f;
    public int needed = 2;
    [Tooltip("Надёжная табличка подтверждается с первого скана: уверенность не ниже этой и букв/цифр не меньше strongAlnum (на ходу вторая встреча часто не случается)")]
    public float strongConfidence = 85f;
    public int strongAlnum = 8;

    public bool IsStrong(SignCandidate s)
    {
        if (s == null || s.confidence < strongConfidence) return false;
        int alnum = 0, longest = 0, run = 0;
        foreach (char c in s.text)
        {
            if (char.IsLetterOrDigit(c)) { alnum++; run++; if (run > longest) longest = run; }
            else run = 0;
        }
        return alnum >= strongAlnum && longest >= 4;
    }

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
            if (IsStrong(s) && best.seen < needed) best.seen = needed;   // надёжный текст не ждёт второго скана
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
