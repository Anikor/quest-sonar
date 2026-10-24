using UnityEngine;

/// <summary>
/// Синтез коротких звуков сонара (без файлов, всё считается при старте).
/// Словарь звуков:
///  - Soft (щипок / колокольчик) - писк препятствия: низкий = предмет ниже пояса, высокий = на уровне корпуса;
///  - Clear - «путь свободен»: две восходящие ноты из направления, куда можно идти;
///  - Blocked - «тупик»: два низких глухих удара (все зоны перекрыты);
///  - Knock - «дверь / проход»: стук по дереву (два удара = проход виден насквозь, один = дверь из модели комнаты);
///  - Chirp - обрыв / ступеньки вниз: нисходящий свист.
/// Генераторы возвращают массив сэмплов, чтобы их можно было проверить тестом без аудиосистемы.
/// </summary>
public static class SonarSounds
{
    public const int SampleRate = 48000;

    public static AudioClip Clip(string name, float[] data)
    {
        var c = AudioClip.Create(name, data.Length, 1, SampleRate, false);
        c.SetData(data, 0);
        return c;
    }

    /// <summary>Мягкий «щипковый» звук: плавная атака, экспоненциальное затухание, чуть шума (~5%, он помогает локализации).</summary>
    public static float[] Soft(float freq, bool bell, float len)
    {
        int n = (int)(SampleRate * len);
        var data = new float[n];
        var rnd = new System.Random(1234);
        float tau = len * 0.35f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float a = Attack(t, 0.012f);
            float decay = Mathf.Exp(-t / tau);
            float tail = Mathf.Clamp01((len - t) / 0.02f);   // гарантированный ноль в конце
            float w = 2f * Mathf.PI * freq * t;
            float s;
            if (bell)
                s = Mathf.Sin(w)
                  + 0.5f * Mathf.Sin(2.76f * w) * Mathf.Exp(-t / (tau * 0.5f))
                  + 0.25f * Mathf.Sin(5.4f * w) * Mathf.Exp(-t / (tau * 0.3f));
            else
                s = Mathf.Sin(w) + 0.35f * Mathf.Sin(2f * w) * Mathf.Exp(-t / (tau * 0.5f));
            float noise = ((float)rnd.NextDouble() * 2f - 1f) * 0.05f;
            data[i] = (s * 0.5f + noise) * a * decay * tail * 0.8f;
        }
        return data;
    }

    /// <summary>Свист от f0 к f1 (обрыв: сверху вниз).</summary>
    public static float[] Chirp(float f0, float f1, float len)
    {
        int n = (int)(SampleRate * len);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float u = i / (float)n;
            float f = Mathf.Lerp(f0, f1, u);
            phase += 2f * Mathf.PI * f / SampleRate;
            float env = Mathf.Min(1f, i / (0.01f * SampleRate)) * (1f - u);
            data[i] = (Mathf.Sin(phase) + 0.4f * Mathf.Sin(2f * phase)) * 0.6f * env;
        }
        return data;
    }

    /// <summary>«Путь свободен»: две мягкие восходящие ноты (до5 - соль5), 0.36 с.</summary>
    public static float[] Clear()
    {
        const float len = 0.36f, split = 0.16f;
        int n = (int)(SampleRate * len);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            data[i] = Note(t, 0f, split, 523.25f) + Note(t, split, len, 783.99f);
            data[i] *= Mathf.Clamp01((len - t) / 0.02f);
        }
        return Normalize(data, 0.8f);
    }

    /// <summary>«Тупик»: два низких глухих удара, 0.42 с.</summary>
    public static float[] Blocked()
    {
        const float len = 0.42f;
        int n = (int)(SampleRate * len);
        var data = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            data[i] = Thud(t) + Thud(t - 0.2f);
            data[i] *= Mathf.Clamp01((len - t) / 0.02f);
        }
        return Normalize(data, 0.85f);
    }

    /// <summary>Стук по дереву: count ударов через 0.14 с.</summary>
    public static float[] Knock(int count)
    {
        count = Mathf.Clamp(count, 1, 3);
        const float gap = 0.14f;
        float len = gap * (count - 1) + 0.18f;
        int n = (int)(SampleRate * len);
        var data = new float[n];
        var rnd = new System.Random(777);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)SampleRate;
            float noise = (float)rnd.NextDouble() * 2f - 1f;
            float s = 0f;
            for (int k = 0; k < count; k++) s += KnockHit(t - k * gap, noise);
            data[i] = s * Mathf.Clamp01((len - t) / 0.02f);
        }
        return Normalize(data, 0.85f);
    }

    // ---------------------------------------------------------------- кирпичики
    static float Attack(float t, float dur)
    {
        float a = Mathf.Clamp01(t / dur);
        return a * a * (3f - 2f * a);   // плавная атака без щелчка
    }

    static float Note(float t, float from, float to, float freq)
    {
        if (t < from || t >= to) return 0f;
        float u = t - from;
        float env = Attack(u, 0.012f) * Mathf.Exp(-u / 0.12f);
        float w = 2f * Mathf.PI * freq * u;
        return (Mathf.Sin(w) + 0.3f * Mathf.Sin(2f * w)) * env;
    }

    static float Thud(float t)
    {
        if (t < 0f || t > 0.19f) return 0f;
        float f = Mathf.Lerp(130f, 85f, Mathf.Clamp01(t / 0.06f));   // удар с проседанием высоты
        float env = Attack(t, 0.004f) * Mathf.Exp(-t / 0.07f);
        float w = 2f * Mathf.PI * f * t;
        return (Mathf.Sin(w) + 0.2f * Mathf.Sin(3f * w)) * env;
    }

    static float KnockHit(float t, float noise)
    {
        if (t < 0f || t > 0.17f) return 0f;
        float click = noise * Mathf.Exp(-t / 0.012f) * 0.7f;                               // щелчок
        float body = Mathf.Sin(2f * Mathf.PI * 210f * t) * Mathf.Exp(-t / 0.04f) * 0.6f;    // корпус двери
        float wood = Mathf.Sin(2f * Mathf.PI * 1150f * t) * Mathf.Exp(-t / 0.01f) * 0.3f;   // «деревянный» призвук
        return (click + body + wood) * Attack(t, 0.002f);
    }

    static float[] Normalize(float[] d, float peak)
    {
        float m = 0f;
        for (int i = 0; i < d.Length; i++) m = Mathf.Max(m, Mathf.Abs(d[i]));
        if (m > 1e-6f)
        {
            float k = peak / m;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }
        return d;
    }
}
