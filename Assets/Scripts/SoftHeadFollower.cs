using UnityEngine;

/// <summary>
/// Плавно держит объект перед головой пользователя: слегка "подвешен" и догоняет при поворотах.
/// Работает и в симуляторе, и на устройстве. Кидай на объект-экран.
/// </summary>
public class SoftHeadFollower : MonoBehaviour
{
    [Header("Head (camera)")]
    public Transform head;                // сюда передай Transform головы/камеры

    [Header("Target offset (relative to head)")]
    [Tooltip("Дистанция по взгляду")]
    public float distance = 1.8f;
    [Tooltip("Вертикальный сдвиг вверх от линии взгляда")]
    public float verticalOffset = -0.05f;
    [Tooltip("Горизонтальный сдвиг (вправо +, влево -)")]
    public float lateralOffset = 0.0f;

    [Header("Smoothing")]
    [Tooltip("Время успокоения позиции (меньше — быстрее)")]
    public float positionSmoothTime = 0.12f;
    [Tooltip("Макс. линейная скорость (м/с), 0 = без ограничения")]
    public float maxPositionSpeed = 4.0f;
    [Tooltip("Угловое сглаживание (сек). 0.1–0.2 даёт «мягко»")]
    public float rotationSmoothTime = 0.10f;

    [Header("Gaze catch-up")]
    [Tooltip("Порог угла (в градусах), после которого усиливаем догон")]
    public float catchUpAngle = 35f;
    [Tooltip("Множитель ускорения поворота при большом расхождении")]
    public float catchUpBoost = 2.0f;

    [Header("Bounds")]
    [Tooltip("Мин/макс дистанция от головы")]
    public Vector2 distanceClamp = new Vector2(0.5f, 5.0f);

    // внутреннее состояние
    Vector3 velocity;         // для SmoothDamp
    Quaternion rotVel = Quaternion.identity; // псевдо-скорость для "сглаживания" кватерниона
    float rotLerpVel;         // помогает делать экспоненциальное сглаживание поворота

    private bool blockVertical = true;

    void Reset()
    {
        // Попытка найти основную камеру по умолчанию
        if (!head && Camera.main) head = Camera.main.transform;
    }

    void LateUpdate()
    {
        if (!head) return;

        // Целевая позиция относительно головы
        Vector3 forward = blockVertical ? Flatten(head.forward).normalized : head.forward.normalized; // проекция на горизонт, чтобы панель не кувыркалась при наклоне головы
        if (forward.sqrMagnitude < 1e-4f) forward = head.forward.normalized;

        Vector3 up = Vector3.up;
        Vector3 right = Vector3.Cross(up, forward).normalized;

        float d = Mathf.Clamp(distance, distanceClamp.x, distanceClamp.y);

        Vector3 targetPos =
            head.position +
            forward * d +
            up * verticalOffset +
            right * lateralOffset;

        // Плавное перемещение к целевой позиции
        float maxSpeed = (maxPositionSpeed <= 0f) ? Mathf.Infinity : maxPositionSpeed;
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref velocity, positionSmoothTime, maxSpeed, Time.deltaTime);

        // Целевой поворот: смотреть на голову (чтобы панель «смотрела на пользователя»)
        Vector3 toHead = (head.position - transform.position);
        if (toHead.sqrMagnitude < 1e-6f) toHead = forward; // защита от нулевого вектора
        Quaternion targetRot = Quaternion.LookRotation(-toHead.normalized, Vector3.up); // минус — лицом к пользователю

        // Оценка рассогласования
        float angDelta;
        {
            Quaternion delta = targetRot * Quaternion.Inverse(transform.rotation);
            delta.ToAngleAxis(out var angle, out _);
            angDelta = (angle > 180f) ? 360f - angle : angle;
        }

        // Экспоненциальное сглаживание поворота (похоже на SmoothDamp для угла)
        // Чем меньше rotationSmoothTime, тем быстрее поворот.
        float smooth = SmoothFactor(rotationSmoothTime, Time.deltaTime);

        // Ускоряем догон, если пользователь резко повернулся (большое рассогласование)
        if (angDelta > catchUpAngle) smooth = 1f - Mathf.Pow(1f - smooth, catchUpBoost);

        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, smooth);
    }

    /// <summary>Экспоненциальный коэффициент сглаживания для заданной константы времени.</summary>
    static float SmoothFactor(float timeConstant, float dt)
    {
        if (timeConstant <= 1e-4f) return 1f; // мгновенно
        // классическая формула 1 - exp(-dt / tau)
        return 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, timeConstant));
    }

    /// <summary>Убираем вертикальную составляющую — чтобы панель не «клевала» при наклоне головы.</summary>
    static Vector3 Flatten(Vector3 v)
    {
        v.y = 0f;
        return v;
    }

    public void SetLateralOffset(float value)
    {
        lateralOffset = value;
    }

    public void SetFlattenState(bool state)
    {
        blockVertical = state;
    }

    public void SetDistance(float value)
    {
        distance = value;
    }
}
