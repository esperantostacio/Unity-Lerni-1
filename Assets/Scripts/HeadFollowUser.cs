using UnityEngine;

/// <summary>
/// Makes an NPC head/neck bone look at the user's head with clamped rotation.
/// Assign neckBone to the head/neck transform, target to the VR camera (auto-finds Camera.main if empty).
/// </summary>
public class HeadFollowUser : MonoBehaviour
{
    [SerializeField] private Transform neckBone;
    [SerializeField] private Transform target; // typically the VR camera
    [SerializeField] private float followSpeed = 5f;
    [SerializeField] private float maxAngle = 70f;
    [SerializeField] private Vector3 targetOffset = Vector3.zero; // local offset from target

    private void Start()
    {
        if (target == null)
        {
            target = Camera.main != null ? Camera.main.transform : null;
        }

        if (neckBone == null)
        {
            Debug.LogWarning("[HeadFollowUser] Neck bone not assigned; head tracking disabled.");
        }
        if (target == null)
        {
            Debug.LogWarning("[HeadFollowUser] Target not found; head tracking disabled.");
        }
    }

    private void LateUpdate()
    {
        if (neckBone == null || target == null) return;

        Vector3 targetPosition = target.TransformPoint(targetOffset);
        Vector3 direction = targetPosition - neckBone.position;
        Quaternion lookRot = Quaternion.LookRotation(direction);

        float angle = Quaternion.Angle(neckBone.rotation, lookRot);
        if (angle > maxAngle)
        {
            lookRot = Quaternion.RotateTowards(neckBone.rotation, lookRot, maxAngle);
        }

        neckBone.rotation = Quaternion.Slerp(neckBone.rotation, lookRot, Time.deltaTime * followSpeed);
    }
}