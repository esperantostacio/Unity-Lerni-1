using UnityEngine;
using MedicalExam;

public class VoiceScore : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Reference to the MedicalExamManager to access the evaluation data")]
    [SerializeField] private MedicalExamManager medicalExamManager;
    
    [Tooltip("AudioSource that plays the evaluation speech")]
    [SerializeField] private AudioSource evaluationAudioSource;

    [Header("Mute")]
    [Tooltip("GameObject that acts as the mute/unmute button")]
    [SerializeField] private GameObject muteButton;

    [Tooltip("Icon shown while audio is playing (click will mute/pause)")]
    [SerializeField] private GameObject muteIcon;

    [Tooltip("Icon shown while audio is paused (click will unmute/resume)")]
    [SerializeField] private GameObject unmuteIcon;
    
    private bool isPlaying = false;
    private bool isMuted = false;
    private bool isPausedByUser = false;
    
    void Start()
    {
        // Auto-find references if not assigned
        if (medicalExamManager == null)
        {
            medicalExamManager = FindFirstObjectByType<MedicalExamManager>();
            if (medicalExamManager == null)
            {
                Debug.LogError("[VoiceScore] MedicalExamManager not found in scene!");
            }
        }

        SetMuteButtonVisible(false);
        RefreshMuteIconState();
    }
    
    /// <summary>
    /// Call this method when the button is clicked
    /// Toggles between playing and stopping the evaluation audio
    /// </summary>
    public void OnButtonClick()
    {
        if (isPlaying)
        {
            StopEvaluationPlayback();
        }
        else
        {
            PlayEvaluationAudio();
        }
    }
    
    /// <summary>
    /// Plays the evaluation audio (scores and feedback)
    /// </summary>
    private void PlayEvaluationAudio()
    {
        if (medicalExamManager == null)
        {
            Debug.LogError("[VoiceScore] Cannot play evaluation - MedicalExamManager is not assigned!");
            return;
        }

        if (evaluationAudioSource != null && evaluationAudioSource.isPlaying)
        {
            evaluationAudioSource.Stop();
        }

        // Request the evaluation to be spoken again
        isPlaying = true;
        isMuted = false;
        isPausedByUser = false;
        SetMuteButtonVisible(true);
        RefreshMuteIconState();

        Debug.Log("[VoiceScore] Starting evaluation playback...");
        
        // Trigger the MedicalExamManager to replay the evaluation
        medicalExamManager.ReplayEvaluationAudio();
    }
    
    /// <summary>
    /// Stops the currently playing evaluation audio
    /// </summary>
    private void StopEvaluationPlayback()
    {
        isPlaying = false;
        isMuted = false;
        isPausedByUser = false;
        SetMuteButtonVisible(false);
        RefreshMuteIconState();
        
        // Tell MedicalExamManager to stop the audio
        if (medicalExamManager != null)
        {
            medicalExamManager.StopEvaluationAudio();
        }
        
        if (evaluationAudioSource != null && evaluationAudioSource.isPlaying)
        {
            evaluationAudioSource.Stop();
            Debug.Log("[VoiceScore] Stopped evaluation playback");
        }
    }

    /// <summary>
    /// Call this from the mute button's OnClick event.
    /// Toggles mute/unmute on the evaluation audio while it keeps playing.
    /// </summary>
    public void OnMuteButtonClick()
    {
        if (!isPlaying || evaluationAudioSource == null) return;

        isMuted = !isMuted;
        isPausedByUser = isMuted;

        if (isMuted)
        {
            evaluationAudioSource.Pause();
        }
        else
        {
            evaluationAudioSource.UnPause();
        }

        RefreshMuteIconState();
        Debug.Log($"[VoiceScore] Audio {(isMuted ? "muted" : "unmuted")}");
    }

    private void SetMuteButtonVisible(bool isVisible)
    {
        if (muteButton != null)
        {
            muteButton.SetActive(isVisible);
        }
    }

    private void RefreshMuteIconState()
    {
        if (muteIcon != null)
        {
            muteIcon.SetActive(!isMuted);
        }

        if (unmuteIcon != null)
        {
            unmuteIcon.SetActive(isMuted);
        }
    }
    
    void Update()
    {
        // Ignore paused state; only auto-stop when playback actually ends.
        if (isPlaying && !isPausedByUser && evaluationAudioSource != null && !evaluationAudioSource.isPlaying)
        {
            isPlaying = false;
            isMuted = false;
            SetMuteButtonVisible(false);
            RefreshMuteIconState();
        }
    }
}
