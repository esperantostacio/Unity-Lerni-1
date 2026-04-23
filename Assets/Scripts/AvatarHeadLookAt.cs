using UnityEngine;

/// <summary>
/// Attaches to an avatar GameObject. Finds the Head bone automatically (or use manual assignment)
/// and makes it smoothly look at the XR rig camera (player's eyes).
/// Uses LateUpdate so it runs after the Animator and properly overrides the head pose.
/// </summary>
public class AvatarHeadLookAt : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The head bone of the avatar. If left empty, auto-detected from Animator.")]
    [SerializeField] private Transform headBone;

    [Tooltip("The target to look at (e.g. CenterEyeAnchor or Main Camera). Auto-finds Camera.main if empty.")]
    [SerializeField] private Transform lookTarget;

    [Header("Tracking Settings")]
    [Tooltip("How fast the head rotates toward the target.")]
    [SerializeField] private float trackingSpeed = 5f;

    [Tooltip("Maximum angle the head can rotate from its rest pose.")]
    [SerializeField] private float maxAngle = 75f;

    [Tooltip("Blend weight 0-1. 0 = animation only, 1 = full look-at.")]
    [Range(0f, 1f)]
    [SerializeField] private float weight = 1f;

    [Tooltip("Only track when the target is within this distance (0 = unlimited).")]
    [SerializeField] private float maxDistance = 0f;

    [Header("Bone Axis Correction")]
    [Tooltip("Manual euler offset to fix the head bone orientation. Adjust until the head looks straight when the target is directly in front.")]
    [SerializeField] private Vector3 rotationOffset = new Vector3(9f, 105f, -2f);

    // Cached state
    private Quaternion _animatedRotation;
    private bool _initialized;
    private Quaternion _offsetQuat;

    private void Start()
    {
        TryInitialize();
    }

    private void TryInitialize()
    {
        // --- Head bone ---
        if (headBone == null)
        {
            Animator animator = GetComponentInChildren<Animator>();
            if (animator != null && animator.isHuman)
            {
                headBone = animator.GetBoneTransform(HumanBodyBones.Head);
            }

            if (headBone == null)
            {
                // Fallback: search hierarchy for common head bone names
                headBone = FindBoneByName("Head");
            }
        }

        // --- Look target ---
        if (lookTarget == null)
        {
            // Try Meta XR CenterEyeAnchor first
            GameObject centerEye = GameObject.Find("CenterEyeAnchor");
            if (centerEye != null)
            {
                lookTarget = centerEye.transform;
            }
            else
            {
                // Fallback to main camera
                lookTarget = Camera.main != null ? Camera.main.transform : null;
            }
        }

        if (headBone == null)
        {
            Debug.LogWarning($"[AvatarHeadLookAt] No head bone found on {gameObject.name}. " +
                             "Assign it manually in the Inspector.");
            return;
        }

        if (lookTarget == null)
        {
            Debug.LogWarning("[AvatarHeadLookAt] No look target found. " +
                             "Assign it manually or ensure Camera.main / CenterEyeAnchor exists.");
            return;
        }

        _initialized = true;
        _offsetQuat = Quaternion.Euler(rotationOffset);
        Debug.Log($"[AvatarHeadLookAt] Ready — head: {headBone.name}, target: {lookTarget.name}, offset: {rotationOffset}");
    }

    private void LateUpdate()
    {
        // Lazy init in case target wasn't available at Start (e.g. XR rig spawns later)
        if (!_initialized)
        {
            TryInitialize();
            if (!_initialized) return;
        }

        if (headBone == null || lookTarget == null) return;

        // Store the rotation the Animator produced this frame (before we override it)
        _animatedRotation = headBone.rotation;

        // Recalculate in case user tweaks offset in Inspector at runtime
        _offsetQuat = Quaternion.Euler(rotationOffset);

        if (weight <= 0f) return;

        // Distance check
        if (maxDistance > 0f)
        {
            float dist = Vector3.Distance(headBone.position, lookTarget.position);
            if (dist > maxDistance) return;
        }

        // Direction from head to target
        Vector3 direction = lookTarget.position - headBone.position;

        if (direction.sqrMagnitude < 0.001f) return;

        // Build the desired look rotation, then apply the manual offset
        // to correct for the bone's non-standard axis orientation
        Quaternion desiredRotation = Quaternion.LookRotation(direction, Vector3.up) * _offsetQuat;

        // Clamp angle relative to the animated rest pose
        float angle = Quaternion.Angle(_animatedRotation, desiredRotation);
        if (angle > maxAngle)
        {
            desiredRotation = Quaternion.RotateTowards(_animatedRotation, desiredRotation, maxAngle);
        }

        // Blend between animated rotation and look-at rotation
        Quaternion blended = Quaternion.Slerp(_animatedRotation, desiredRotation, weight);

        // Smooth interpolation
        headBone.rotation = Quaternion.Slerp(headBone.rotation, blended, Time.deltaTime * trackingSpeed);
    }

    /// <summary>
    /// Search the hierarchy for a bone with a matching name (case-insensitive).
    /// </summary>
    private Transform FindBoneByName(string boneName)
    {
        Transform[] children = GetComponentsInChildren<Transform>();
        foreach (Transform t in children)
        {
            if (t.name.IndexOf(boneName, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return t;
            }
        }
        return null;
    }

    // ─────────────────────────── Public API ───────────────────────────

    /// <summary>Set the look-at weight at runtime (0 = off, 1 = full).</summary>
    public void SetWeight(float w) => weight = Mathf.Clamp01(w);

    /// <summary>Change the target at runtime.</summary>
    public void SetTarget(Transform newTarget)
    {
        lookTarget = newTarget;
        _initialized = lookTarget != null && headBone != null;
    }

    // ─────────────────────────── Editor helpers ───────────────────────

    [ContextMenu("Auto-find Head Bone")]
    private void EditorFindHead()
    {
        headBone = null;
        TryInitialize();
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (headBone == null || lookTarget == null) return;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(headBone.position, lookTarget.position);
        Gizmos.DrawWireSphere(headBone.position, 0.05f);
    }
#endif
}
