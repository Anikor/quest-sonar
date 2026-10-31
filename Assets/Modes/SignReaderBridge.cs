using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// «Прочитать то, что перед тобой» по курку в режиме «Текст» без правки SignReader.cs.
/// У SignReader чтение по запросу есть только на правом хвате, а флаги, которые ставит хват (forceScan, speakAfterScan,
/// forceSince), закрытые. Поэтому:
///   1) если в SignReader появится публичный метод ReadNow() (предложение в docs/MODES_AND_TUNING.md), зовём его;
///   2) иначе ставим те же закрытые поля через отражение (ровно то, что делает обработчик хвата в SignReader.Update).
/// НЕ ПРОВЕРЕНО на шлеме: отражение закрытых полей под IL2CPP работает, пока поля не вырезаны при стрипинге
/// (поля MonoBehaviour обычно сохраняются). Если поле не найдено, говорим «используйте правый хват».
/// </summary>
public static class SignReaderBridge
{
    const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;
    static bool searched;
    static MethodInfo readNow;
    static FieldInfo fForce, fSpeak, fSince, fOcrReady;

    static void Find()
    {
        if (searched) return;
        searched = true;
        Type t = typeof(SignReader);
        try
        {
            readNow = t.GetMethod("ReadNow", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            fForce = t.GetField("forceScan", Priv);
            fSpeak = t.GetField("speakAfterScan", Priv);
            fSince = t.GetField("forceSince", Priv);
            fOcrReady = t.GetField("ocrReady", Priv);
        }
        catch (Exception e) { Debug.LogWarning("SignReaderBridge: " + e.Message); }
    }

    /// <summary>Запросить чтение вслух. false + problem (фраза для голоса), если читать нечем.</summary>
    public static bool ReadNow(SignReader r, out string problem)
    {
        problem = null;
        if (r == null)
        {
            problem = ModeVoice.T("Чтение текста не запущено", "Citirea textului nu rulează", "Text reading is not running");
            return false;
        }
        Find();
        try
        {
            if (readNow != null)
            {
                readNow.Invoke(r, null);
                VisionLog.Event("trigger_read", "\"via\":\"ReadNow\"");
                return true;
            }
            var feed = PassthroughCameraFeed.Instance;
            if (feed == null || !feed.IsReady)
            {
                problem = ModeVoice.T("Камера не готова", "Camera nu este gata", "Camera not ready");
                return false;
            }
            if (fOcrReady != null && fOcrReady.FieldType == typeof(bool) && !(bool)fOcrReady.GetValue(r))
            {
                problem = ModeVoice.T("Распознавание не готово", "Recunoașterea nu este gata", "Recognition not ready");
                return false;
            }
            if (fForce == null || fSpeak == null || fSince == null)
            {
                problem = ModeVoice.T("Используйте правый хват", "Folosiți butonul lateral drept", "Use the right grip");
                return false;
            }
            fForce.SetValue(r, true);
            fSpeak.SetValue(r, true);
            fSince.SetValue(r, Time.time);
            VisionLog.Event("trigger_read", "\"via\":\"reflection\"");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("SignReaderBridge: " + e.Message);
            problem = ModeVoice.T("Используйте правый хват", "Folosiți butonul lateral drept", "Use the right grip");
            return false;
        }
    }
}
