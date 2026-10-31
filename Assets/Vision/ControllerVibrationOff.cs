using System.Collections;
using UnityEngine;

/// <summary>
/// Выключает вибрацию контроллеров (просьба пользователя 2026-10-31): вибрацию даёт AudioSonar при опасной близости
/// (поле AudioSonar.haptics). Файл AudioSonar.cs (ветка sound) не трогаем: флажок снимается отсюда во время игры.
/// Чтобы вернуть вибрацию, снять галочку disableVibration у объекта ControllerVibrationOff в Inspector
/// или удалить этот файл.
/// </summary>
public class ControllerVibrationOff : MonoBehaviour
{
    public bool disableVibration = true;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (FindFirstObjectByType<ControllerVibrationOff>() == null)
            new GameObject("ControllerVibrationOff").AddComponent<ControllerVibrationOff>();
    }

    IEnumerator Start()
    {
        while (true)
        {
            if (disableVibration)
            {
                var sonar = FindFirstObjectByType<AudioSonar>();
                if (sonar != null && sonar.haptics) sonar.haptics = false;
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
                OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
            }
            yield return new WaitForSeconds(2f);
        }
    }
}
