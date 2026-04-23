using UnityEngine;
using MedicalExam;

/// <summary>
/// Main game manager for the Medical Oral Exam VR Application
/// Integrates with MedicalExamManager for exam flow control
/// </summary>
public class GameManager : MonoBehaviour
{
    [Header("Medical Exam System")]
    [SerializeField] private MedicalExamManager medicalExamManager;
    
    [Header("Application Settings")]
    [SerializeField] private bool autoStartOnAwake = false;
    
    private void Start()
    {
        Debug.Log("[GameManager] Medical Oral Exam Application Starting...");
        
        // Display microphone info
        DisplayMicrophoneInfo();
        
        // Validate medical exam manager
        if (medicalExamManager == null)
        {
            medicalExamManager = FindObjectOfType<MedicalExamManager>();
            if (medicalExamManager == null)
            {
                Debug.LogError("[GameManager] MedicalExamManager not found! Please add it to the scene.");
            }
        }
        
        // Optional: Auto-start logic can be added here
        if (autoStartOnAwake && medicalExamManager != null)
        {
            Debug.Log("[GameManager] Auto-start enabled");
            // medicalExamManager will handle its own startup
        }
        
        Debug.Log("[GameManager] Initialization complete");
    }
    
    private void DisplayMicrophoneInfo()
    {
        string[] devices = Microphone.devices;
        
        Debug.Log("=== MICROPHONE INFORMATION ===");
        Debug.Log($"Total microphones found: {devices.Length}");
        
        if (devices.Length == 0)
        {
            Debug.LogWarning("⚠️ NO MICROPHONES DETECTED! Please check:");
            Debug.LogWarning("  1. Microphone is plugged in");
            Debug.LogWarning("  2. Windows microphone permissions enabled");
            Debug.LogWarning("  3. Device drivers installed");
        }
        else
        {
            for (int i = 0; i < devices.Length; i++)
            {
                string deviceName = devices[i];
                if (string.IsNullOrEmpty(deviceName))
                {
                    Debug.Log($"  [{i}] Default Microphone (System Default)");
                }
                else
                {
                    Debug.Log($"  [{i}] {deviceName}");
                }
                
                // Mark which one will be used (first/default)
                if (i == 0)
                {
                    Debug.Log($"      ✅ This microphone will be used by default");
                }
            }
        }
        
        Debug.Log("==============================");
    }
    
    private void Update()
    {
        // Application-wide update logic
        // Medical exam manager handles its own updates
        
        // Quick restart shortcut (for testing)
        if (Input.GetKeyDown(KeyCode.R))
        {
            RestartApplication();
        }
        
        // Quit application (for testing)
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            QuitApplication();
        }
    }
    
    /// <summary>
    /// Restarts the medical exam application
    /// </summary>
    public void RestartApplication()
    {
        Debug.Log("[GameManager] Restarting application...");
        
        if (medicalExamManager != null)
        {
            medicalExamManager.RestartExam();
        }
    }
    
    /// <summary>
    /// Quits the application
    /// </summary>
    public void QuitApplication()
    {
        Debug.Log("[GameManager] Quitting application...");
        
        #if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
        #else
        Application.Quit();
        #endif
    }
    
    /// <summary>
    /// Manually trigger evaluation from external source
    /// </summary>
    public void TriggerEvaluation()
    {
        if (medicalExamManager != null)
        {
            medicalExamManager.ManualEvaluationTrigger();
        }
    }
}
