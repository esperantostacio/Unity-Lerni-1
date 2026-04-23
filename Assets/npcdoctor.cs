using UnityEngine;
using OpenAI;

public class NPCDoctor : MonoBehaviour
{
    [Header("Animation")]
    [SerializeField] private Animator animator;
    [Tooltip("Name of the bool parameter in Animator for talking animation")]
    [SerializeField] private string talkingParameterName = "talking";
    
    [Header("Head Tracking")]
    [SerializeField] private Transform neckBone;
    [Tooltip("VR camera/head to look at")]
    [SerializeField] private Transform vrHeadTarget;
    [SerializeField] private float headTrackingSpeed = 5f;
    [SerializeField] private float maxHeadRotationAngle = 70f;
    
    private bool _isTalking = false;
    
    private void Start()
    {
        // Validate components
        if (animator == null)
        {
            animator = GetComponent<Animator>();
            if (animator == null)
            {
                Debug.LogError("[NPCDoctor] Animator component not found!");
            }
        }
        
        if (neckBone == null)
        {
            Debug.LogWarning("[NPCDoctor] Neck bone not assigned! Head tracking disabled");
        }
        
        if (vrHeadTarget == null)
        {
            // Try to find VR camera
            vrHeadTarget = Camera.main?.transform;
            if (vrHeadTarget == null)
            {
                Debug.LogWarning("[NPCDoctor] VR head target not found! Assign manually or ensure Camera.main exists");
            }
        }
        
        // Set initial state
        SetTalkingAnimation(false);
        
        Debug.Log("[NPCDoctor] Initialized");
    }
    
    private void Update()
    {
        // Update head tracking to look at VR user
        if (neckBone != null && vrHeadTarget != null)
        {
            UpdateHeadTracking();
        }
    }
    
    private void OnDestroy()
    {
        // Unsubscribe from events
    }
    
    /// <summary>
    /// Called when AI starts/stops speaking
    /// </summary>
    private void HandleAgentSpeaking(bool isSpeaking)
    {
        SetTalkingAnimation(isSpeaking);
        _isTalking = isSpeaking;
        Debug.Log($"[NPCDoctor] AI {(isSpeaking ? "started" : "stopped")} speaking");
    }
    
    /// <summary>
    /// Called when user starts/stops talking
    /// </summary>
    private void HandleUserSpeaking(bool isSpeaking)
    {
        if (isSpeaking)
        {
            // User is talking, doctor listens (idle)
            SetTalkingAnimation(false);
            Debug.Log("[NPCDoctor] User started speaking, switching to idle");
        }
    }
    
    /// <summary>
    /// Sets the talking animation state
    /// </summary>
    private void SetTalkingAnimation(bool isTalking)
    {
        if (animator != null)
        {
            animator.SetBool(talkingParameterName, isTalking);
            Debug.Log($"[NPCDoctor] Animation: {(isTalking ? "Talking" : "Idle")}");
        }
    }
    
    /// <summary>
    /// Smoothly rotates neck bone to look at VR user's head
    /// </summary>
    private void UpdateHeadTracking()
    {
        // Calculate direction to VR head
        Vector3 directionToHead = vrHeadTarget.position - neckBone.position;
        
        // Calculate target rotation
        Quaternion targetRotation = Quaternion.LookRotation(directionToHead);
        
        // Get angle difference
        float angleDifference = Quaternion.Angle(neckBone.rotation, targetRotation);
        
        // Only rotate if within max angle limit
        if (angleDifference <= maxHeadRotationAngle)
        {
            // Smoothly interpolate rotation
            neckBone.rotation = Quaternion.Slerp(
                neckBone.rotation, 
                targetRotation, 
                Time.deltaTime * headTrackingSpeed
            );
        }
        else
        {
            // Clamp to max angle
            Quaternion clampedRotation = Quaternion.RotateTowards(
                neckBone.rotation, 
                targetRotation, 
                maxHeadRotationAngle
            );
            
            neckBone.rotation = Quaternion.Slerp(
                neckBone.rotation, 
                clampedRotation, 
                Time.deltaTime * headTrackingSpeed
            );
        }
    }
    
    /// <summary>
    /// Manually start talking animation (for testing)
    /// </summary>
    [ContextMenu("Test - Start Talking")]
    public void TestStartTalking()
    {
        SetTalkingAnimation(true);
    }
    
    /// <summary>
    /// Manually stop talking animation (for testing)
    /// </summary>
    [ContextMenu("Test - Stop Talking")]
    public void TestStopTalking()
    {
        SetTalkingAnimation(false);
    }
    
    /// <summary>
    /// Test head tracking by looking at specified position
    /// </summary>
    [ContextMenu("Test - Look At Camera")]
    public void TestLookAtCamera()
    {
        if (vrHeadTarget == null)
        {
            vrHeadTarget = Camera.main?.transform;
        }
        Debug.Log($"[NPCDoctor] Looking at: {vrHeadTarget?.name ?? "null"}");
    }
}
