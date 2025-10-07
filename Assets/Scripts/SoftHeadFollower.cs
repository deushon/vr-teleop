using UnityEngine;

public class SoftHeadFollower : MonoBehaviour
{
    [Header("Head (camera)")]
    public Transform head;

    [Header("Target offset (relative to head)")]
    [Tooltip("Distance along the gaze direction")]
    public float distance = 1.8f;
    [Tooltip("Vertical offset upward from gaze line")]
    public float verticalOffset = -0.05f;
    [Tooltip("Horizontal offset (right +, left -)")]
    public float lateralOffset = 0.0f;

    [Header("Smoothing")]
    [Tooltip("Position damping time (smaller = faster)")]
    public float positionSmoothTime = 0.12f;
    [Tooltip("Max linear speed (m/s), 0 = unlimited")]
    public float maxPositionSpeed = 4.0f;
    [Tooltip("Angular smoothing time (sec). 0.1–0.2 gives a 'soft' feel")]
    public float rotationSmoothTime = 0.10f;

    [Header("Gaze catch-up")]
    [Tooltip("Angle threshold (degrees) beyond which rotation accelerates")]
    public float catchUpAngle = 35f;
    [Tooltip("Rotation acceleration multiplier when exceeding threshold")]
    public float catchUpBoost = 2.0f;

    [Header("Bounds")]
    [Tooltip("Min/max distance from the head")]
    public Vector2 distanceClamp = new Vector2(0.5f, 5.0f);

    // internal state
    Vector3 velocity;                     // for SmoothDamp
    Quaternion rotVel = Quaternion.identity; // pseudo velocity for quaternion smoothing
    float rotLerpVel;                     // helps implement exponential rotation damping

    private bool blockVertical = true;

    void Reset()
    {
        // Try to find the main camera by default
        if (!head && Camera.main) head = Camera.main.transform;
    }

    void LateUpdate()
    {
        if (!head) return;

        // Target position relative to the head
        Vector3 forward = blockVertical ? Flatten(head.forward).normalized : head.forward.normalized; // project onto horizontal plane to prevent flipping when head tilts
        if (forward.sqrMagnitude < 1e-4f) forward = head.forward.normalized;

        Vector3 up = Vector3.up;
        Vector3 right = Vector3.Cross(up, forward).normalized;

        float d = Mathf.Clamp(distance, distanceClamp.x, distanceClamp.y);

        Vector3 targetPos =
            head.position +
            forward * d +
            up * verticalOffset +
            right * lateralOffset;

        // Smoothly move toward the target position
        float maxSpeed = (maxPositionSpeed <= 0f) ? Mathf.Infinity : maxPositionSpeed;
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref velocity, positionSmoothTime, maxSpeed, Time.deltaTime);

        // Target rotation: face the head (so the panel "looks at the user")
        Vector3 toHead = (head.position - transform.position);
        if (toHead.sqrMagnitude < 1e-6f) toHead = forward; // prevent zero vector
        Quaternion targetRot = Quaternion.LookRotation(-toHead.normalized, Vector3.up); // minus = face toward the user

        // Compute angular difference
        float angDelta;
        {
            Quaternion delta = targetRot * Quaternion.Inverse(transform.rotation);
            delta.ToAngleAxis(out var angle, out _);
            angDelta = (angle > 180f) ? 360f - angle : angle;
        }

        // Exponential rotation smoothing (similar to SmoothDamp for angles)
        // Smaller rotationSmoothTime = faster rotation
        float smooth = SmoothFactor(rotationSmoothTime, Time.deltaTime);

        // Speed up catch-up if user turns head sharply (large angle difference)
        if (angDelta > catchUpAngle) smooth = 1f - Mathf.Pow(1f - smooth, catchUpBoost);

        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, smooth);
    }

    /// <summary>Exponential smoothing factor for a given time constant.</summary>
    static float SmoothFactor(float timeConstant, float dt)
    {
        if (timeConstant <= 1e-4f) return 1f; // instant
        // Classic formula: 1 - exp(-dt / tau)
        return 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, timeConstant));
    }

    /// <summary>Removes vertical component to keep panel level with the horizon.</summary>
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
