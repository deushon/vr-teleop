using UnityEngine;

public class SoftHeadFollower : MonoBehaviour
{
    [Header("Head (camera)")]
    public Transform head;

    [Header("Follow mode")]
    [Tooltip("If enabled, object position is recalculated from camera gaze direction")]
    public bool followHeadDirection = true;

    [Tooltip("If enabled, object rotates to face the user")]
    public bool followRotation = true;

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

    private Vector3 velocity;
    private bool blockVertical = true;

    // frozen orientation basis for position calculation when followHeadDirection == false
    private Vector3 frozenForward = Vector3.forward;
    private Vector3 frozenRight = Vector3.right;

    private bool lastFollowHeadDirection;

    void Reset()
    {
        if (!head && Camera.main) head = Camera.main.transform;
    }

    void Start()
    {
        if (!head && Camera.main) head = Camera.main.transform;

        lastFollowHeadDirection = followHeadDirection;

        if (head)
            CaptureFrozenBasis();
    }

    void LateUpdate()
    {
        if (!head) return;

        HandleModeSwitch();

        Vector3 targetPos = GetTargetPosition();

        float maxSpeed = (maxPositionSpeed <= 0f) ? Mathf.Infinity : maxPositionSpeed;
        transform.position = Vector3.SmoothDamp(
            transform.position,
            targetPos,
            ref velocity,
            positionSmoothTime,
            maxSpeed,
            Time.deltaTime
        );

        if (followRotation)
        {
            Vector3 toHead = head.position - transform.position;
            if (toHead.sqrMagnitude < 1e-6f)
                toHead = head.forward;

            Quaternion targetRot = Quaternion.LookRotation(-toHead.normalized, Vector3.up);

            Quaternion delta = targetRot * Quaternion.Inverse(transform.rotation);
            delta.ToAngleAxis(out float angle, out _);
            float angDelta = (angle > 180f) ? 360f - angle : angle;

            float smooth = SmoothFactor(rotationSmoothTime, Time.deltaTime);
            if (angDelta > catchUpAngle)
                smooth = 1f - Mathf.Pow(1f - smooth, catchUpBoost);

            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, smooth);
        }
    }

    private void HandleModeSwitch()
    {
        if (followHeadDirection != lastFollowHeadDirection)
        {
            if (!followHeadDirection)
            {
                // Freeze current direction basis so position remains editable
                // by distance / lateralOffset, but no longer depends on camera rotation
                CaptureFrozenBasis();
            }

            lastFollowHeadDirection = followHeadDirection;
        }
    }

    private void CaptureFrozenBasis()
    {
        Vector3 forward = blockVertical
            ? Flatten(head.forward).normalized
            : head.forward.normalized;

        if (forward.sqrMagnitude < 1e-4f)
            forward = head.forward.normalized;

        Vector3 up = Vector3.up;
        Vector3 right = Vector3.Cross(up, forward).normalized;

        if (right.sqrMagnitude < 1e-4f)
            right = head.right.normalized;

        frozenForward = forward;
        frozenRight = right;
    }

    private Vector3 GetTargetPosition()
    {
        float d = Mathf.Clamp(distance, distanceClamp.x, distanceClamp.y);
        Vector3 up = Vector3.up;

        if (followHeadDirection)
        {
            Vector3 forward = blockVertical
                ? Flatten(head.forward).normalized
                : head.forward.normalized;

            if (forward.sqrMagnitude < 1e-4f)
                forward = head.forward.normalized;

            Vector3 right = Vector3.Cross(up, forward).normalized;
            if (right.sqrMagnitude < 1e-4f)
                right = head.right.normalized;

            return head.position
                   + forward * d
                   + up * verticalOffset
                   + right * lateralOffset;
        }
        else
        {
            return head.position
                   + frozenForward * d
                   + up * verticalOffset
                   + frozenRight * lateralOffset;
        }
    }

    static float SmoothFactor(float timeConstant, float dt)
    {
        if (timeConstant <= 1e-4f) return 1f;
        return 1f - Mathf.Exp(-dt / Mathf.Max(1e-4f, timeConstant));
    }

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
        if (blockVertical == state) return;

        blockVertical = state;

        // If direction following is disabled, refresh frozen basis so that
        // the new flatten mode is reflected in subsequent position changes
        if (!followHeadDirection && head != null)
            CaptureFrozenBasis();
    }

    public void SetDistance(float value)
    {
        distance = value;
    }

    public void SetFollowRotation(bool state)
    {
        followRotation = state;
    }

    public void SetFollowHeadDirection(bool state)
    {
        if (followHeadDirection == state) return;

        followHeadDirection = state;

        if (!followHeadDirection && head != null)
            CaptureFrozenBasis();
    }
}