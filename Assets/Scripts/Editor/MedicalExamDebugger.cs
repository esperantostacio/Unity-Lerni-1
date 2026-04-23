using UnityEngine;
using UnityEditor;
using MedicalExam;

namespace MedicalExamEditor
{
    /// <summary>
    /// Editor window for testing Medical Exam functionality in Play Mode
    /// </summary>
    public class MedicalExamDebugger : EditorWindow
    {
        private MedicalExamManager _examManager;
        private WelcomeAudioPlayer _welcomeAudioPlayer;
        private MedicalExamScenario _selectedScenario;
        private Vector2 _scrollPosition;
        
        // Cached scenarios
        private MedicalExamScenario[] _availableScenarios;
        private string[] _scenarioNames;
        private int _selectedScenarioIndex = 0;
        
        // Track Whisper recording state for toggle button
        private bool _whisperRecordingActive = false;
        
        [MenuItem("Medical Exam/Debugger Window")]
        public static void ShowWindow()
        {
            var window = GetWindow<MedicalExamDebugger>("Medical Exam Debugger");
            window.minSize = new Vector2(400, 500);
        }
        
        private void OnEnable()
        {
            RefreshScenarios();
            FindExamManager();
        }
        
        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            
            GUILayout.Space(10);
            EditorGUILayout.LabelField("Medical Exam Debugger", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Use this window to test exam functionality in Play Mode", MessageType.Info);
            
            GUILayout.Space(10);
            DrawManagerSection();
            
            GUILayout.Space(10);
            DrawScenarioSection();
            
            GUILayout.Space(10);
            DrawRoleSelectionSection();
            
            GUILayout.Space(10);
            DrawConversationControlSection();
            
            GUILayout.Space(10);
            
            GUILayout.Space(10);
            DrawEvaluationSection();
            
            GUILayout.Space(10);
            DrawApplicationControlSection();
            
            GUILayout.Space(10);
            DrawInfoSection();
            
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawManagerSection()
        {
            EditorGUILayout.LabelField("Manager Reference", EditorStyles.boldLabel);
            
            EditorGUI.BeginChangeCheck();
            _examManager = (MedicalExamManager)EditorGUILayout.ObjectField(
                "Exam Manager", 
                _examManager, 
                typeof(MedicalExamManager), 
                true
            );
            if (EditorGUI.EndChangeCheck() || _examManager == null)
            {
                if (_examManager == null)
                {
                    FindExamManager();
                }
            }
            
            if (_examManager == null)
            {
                EditorGUILayout.HelpBox("MedicalExamManager not found! Please assign manually or ensure it exists in the scene.", MessageType.Warning);
                
                if (GUILayout.Button("Find Manager in Scene"))
                {
                    FindExamManager();
                }
            }
            else
            {
                EditorGUILayout.HelpBox($"Connected to: {_examManager.gameObject.name}", MessageType.None);
            }
        }
        
        private void DrawScenarioSection()
        {
            EditorGUILayout.LabelField("Scenario Selection", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Scenarios", GUILayout.Width(150)))
            {
                RefreshScenarios();
            }
            if (GUILayout.Button("Create New Scenario"))
            {
                ScenarioCreator.ShowWindow();
            }
            EditorGUILayout.EndHorizontal();
            
            if (_availableScenarios != null && _availableScenarios.Length > 0)
            {
                _selectedScenarioIndex = EditorGUILayout.Popup(
                    "Available Scenarios", 
                    _selectedScenarioIndex, 
                    _scenarioNames
                );
                
                _selectedScenario = _availableScenarios[_selectedScenarioIndex];
                
                if (_selectedScenario != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField($"Topic: {_selectedScenario.medicalTopic}");
                    EditorGUILayout.LabelField($"Duration: {_selectedScenario.examDurationMinutes} minutes");
                    EditorGUILayout.LabelField($"Language: {_selectedScenario.language}");
                    EditorGUI.indentLevel--;
                    
                    GUI.enabled = _examManager != null;
                    if (GUILayout.Button("Apply This Scenario to Manager", GUILayout.Height(30)))
                    {
                        ApplyScenario();
                    }
                    GUI.enabled = true;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("No scenarios found. Create one using the Scenario Creator.", MessageType.Warning);
            }
        }
        
        private void DrawRoleSelectionSection()
        {
            EditorGUILayout.LabelField("Role Selection", EditorStyles.boldLabel);
            
            GUI.enabled = _examManager != null && Application.isPlaying;
            
            EditorGUILayout.BeginHorizontal();
            
            GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
            if (GUILayout.Button("👨‍⚕️ Doctor to Doctor", GUILayout.Height(50)))
            {
                _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToDoctor);
                Debug.Log("[Debugger] Selected Doctor to Doctor role");
            }
            
            GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
            if (GUILayout.Button("🤒 Doctor to Patient", GUILayout.Height(50)))
            {
                _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToPatient);
                Debug.Log("[Debugger] Selected Doctor to Patient role");
            }
            
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();
            
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to select roles", MessageType.Info);
            }
            
            GUI.enabled = true;
        }
        
        private void DrawConversationControlSection()
        {
            EditorGUILayout.LabelField("Conversation Control", EditorStyles.boldLabel);
            
            GUI.enabled = _examManager != null && Application.isPlaying;
            
            EditorGUILayout.BeginHorizontal();
            
            GUI.backgroundColor = new Color(1f, 0.8f, 0.5f);
            if (GUILayout.Button("🩺 Start D2P\n(Doctor → Patient)", GUILayout.Height(50)))
            {
                FindWelcomeAudioPlayer();
                if (_welcomeAudioPlayer != null)
                {
                    _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToPatient);
                    _welcomeAudioPlayer.PlayPatientWelcome();
                    Debug.Log("[Debugger] Started D2P with welcome audio");
                }
                else
                {
                    Debug.LogWarning("[Debugger] WelcomeAudioPlayer not found! Falling back to direct StartConversation.");
                    _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToPatient);
                    _examManager.StartConversation();
                }
            }
            
            GUI.backgroundColor = new Color(0.5f, 0.8f, 1f);
            if (GUILayout.Button("👨‍⚕️ Start D2D\n(Doctor → Doctor)", GUILayout.Height(50)))
            {
                FindWelcomeAudioPlayer();
                if (_welcomeAudioPlayer != null)
                {
                    _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToDoctor);
                    _welcomeAudioPlayer.PlayDoctorWelcome();
                    Debug.Log("[Debugger] Started D2D with welcome audio");
                }
                else
                {
                    Debug.LogWarning("[Debugger] WelcomeAudioPlayer not found! Falling back to direct StartConversation.");
                    _examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToDoctor);
                    _examManager.StartConversation();
                }
            }
            
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();


            // Toggle Whisper recording button
            GUI.backgroundColor = new Color(1f, 0.6f, 0.6f);
            if (GUILayout.Button(_whisperRecordingActive ? "⏹️ Stop Whisper Recording & Send" : "🔴 Start Whisper Recording", GUILayout.Height(40)))
            {
                if (_examManager != null)
                {
                    if (!_whisperRecordingActive)
                    {
                        _whisperRecordingActive = true;
                        _examManager.StartWhisperManualRecording();
                        Debug.Log("[Debugger] Started Whisper manual recording...");
                    }
                    else
                    {
                        _whisperRecordingActive = false;
                        _examManager.StopWhisperManualRecordingAndSend();
                        Debug.Log("[Debugger] Stopped Whisper recording and sent to Whisper/GPT...");
                    }
                }
                else
                {
                    Debug.LogWarning("[Debugger] MedicalExamManager not assigned. Cannot record.");
                }
            }
            GUI.backgroundColor = Color.white;
            
            GUI.backgroundColor = new Color(1f, 0.5f, 0.5f);
            if (GUILayout.Button("⏹️ End Conversation (Trigger Evaluation)", GUILayout.Height(40)))
            {
                _examManager.ManualEvaluationTrigger();
                Debug.Log("[Debugger] Triggered evaluation");
            }
            GUI.backgroundColor = Color.white;
            
            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Enter Play Mode to control conversation", MessageType.Info);
            }
            
            GUI.enabled = true;
        }
        
       
        
        private void DrawEvaluationSection()
        {
            EditorGUILayout.LabelField("Evaluation Testing", EditorStyles.boldLabel);
            
            GUI.enabled = _examManager != null && Application.isPlaying;

            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.65f);
            if (GUILayout.Button("✅ Send Evaluation Demo (Good Conversation)", GUILayout.Height(35)))
            {
                _examManager.Debug_SendEvaluationDemo_GoodConversation();
                Debug.Log("[Debugger] Sent evaluation demo (good conversation)");
            }
            GUI.backgroundColor = Color.white;
            
            if (GUILayout.Button("📊 Simulate Evaluation", GUILayout.Height(35)))
            {
                SimulateEvaluation();
            }
            
            EditorGUILayout.HelpBox("Simulates an AI evaluation response for testing UI", MessageType.Info);
            
            GUI.enabled = true;
        }
        
        private void DrawApplicationControlSection()
        {
            EditorGUILayout.LabelField("Application Control", EditorStyles.boldLabel);
            
            GUI.enabled = _examManager != null && Application.isPlaying;
            
            GUI.backgroundColor = new Color(0.5f, 1f, 0.5f);
            if (GUILayout.Button("🔄 Restart Exam", GUILayout.Height(35)))
            {
                _examManager.RestartExam();
                Debug.Log("[Debugger] Restarted exam");
            }
            GUI.backgroundColor = Color.white;
            
            GUI.enabled = true;
            
            GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
            if (GUILayout.Button("❌ Stop Play Mode", GUILayout.Height(35)))
            {
                EditorApplication.isPlaying = false;
            }
            GUI.backgroundColor = Color.white;
        }
        
        private void DrawInfoSection()
        {
            EditorGUILayout.LabelField("Debug Information", EditorStyles.boldLabel);
            
            EditorGUILayout.LabelField($"Play Mode: {(Application.isPlaying ? "✅ Active" : "❌ Inactive")}");
            
            if (_examManager != null)
            {
                // Use reflection to get private fields for debugging
                var examActiveField = _examManager.GetType().GetField("_examActive", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var selectedRoleField = _examManager.GetType().GetField("selectedRole", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (examActiveField != null)
                {
                    bool examActive = (bool)examActiveField.GetValue(_examManager);
                    EditorGUILayout.LabelField($"Exam Active: {(examActive ? "✅ Yes" : "❌ No")}");
                }
                
                if (selectedRoleField != null)
                {
                    var selectedRole = selectedRoleField.GetValue(_examManager);
                    EditorGUILayout.LabelField($"Selected Role: {selectedRole}");
                }
            }
            
            GUILayout.Space(10);
            
            if (GUILayout.Button("Open Console"))
            {
                EditorWindow.GetWindow(System.Type.GetType("UnityEditor.ConsoleWindow,UnityEditor"));
            }
        }
        
        // Helper Methods
        
        private void FindExamManager()
        {
            _examManager = FindObjectOfType<MedicalExamManager>();
            if (_examManager != null)
            {
                Debug.Log($"[Debugger] Found MedicalExamManager: {_examManager.gameObject.name}");
            }
            FindWelcomeAudioPlayer();
        }
        
        private void FindWelcomeAudioPlayer()
        {
            // Always re-find in case scene changed or object was re-enabled
            _welcomeAudioPlayer = null;
            
            // First try to get it from MedicalExamManager (works even if GameObject is inactive)
            if (_examManager != null)
            {
                var field = _examManager.GetType().GetField("welcomeAudioPlayer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null)
                {
                    _welcomeAudioPlayer = field.GetValue(_examManager) as WelcomeAudioPlayer;
                }
            }
            
            // Fallback: FindObjectOfType (only finds active objects)
            if (_welcomeAudioPlayer == null)
            {
                _welcomeAudioPlayer = FindObjectOfType<WelcomeAudioPlayer>();
            }
            
            if (_welcomeAudioPlayer != null)
            {
                Debug.Log($"[Debugger] Found WelcomeAudioPlayer: {_welcomeAudioPlayer.gameObject.name}");
            }
            else
            {
                Debug.LogWarning("[Debugger] WelcomeAudioPlayer not found! Make sure it exists in the scene and is assigned on MedicalExamManager.");
            }
        }
        
        private void RefreshScenarios()
        {
            string[] guids = AssetDatabase.FindAssets("t:MedicalExamScenario");
            _availableScenarios = new MedicalExamScenario[guids.Length];
            _scenarioNames = new string[guids.Length];
            
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                _availableScenarios[i] = AssetDatabase.LoadAssetAtPath<MedicalExamScenario>(path);
                _scenarioNames[i] = _availableScenarios[i] != null ? _availableScenarios[i].scenarioName : "Unknown";
            }
            
            Debug.Log($"[Debugger] Found {_availableScenarios.Length} scenario(s)");
        }
        
        private void ApplyScenario()
        {
            if (_examManager == null || _selectedScenario == null) return;
            
            var scenarioField = _examManager.GetType().GetField("currentScenario", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (scenarioField != null)
            {
                scenarioField.SetValue(_examManager, _selectedScenario);
                Debug.Log($"[Debugger] Applied scenario: {_selectedScenario.scenarioName}");
                EditorUtility.SetDirty(_examManager);
            }
        }
        
        private void SimulateEvaluation()
        {
            if (_examManager == null) return;
            
            // Create a simulated AI evaluation response (using whole numbers to avoid parsing issues)
            string simulatedResponse = @"Terminologie: 4/5
Verständlichkeit: 4/5
Aussprache: 4/5
Overall Score: 82/100
Feedback: Your medical terminology was excellent and you demonstrated strong knowledge of cardiovascular conditions. Communication was clear but could be more structured. Pronunciation was very good. Consider using more systematic approach when presenting differential diagnoses. Overall, a solid performance that shows good clinical knowledge and communication skills.";
            
            Debug.Log($"[Debugger] Simulating evaluation:\n{simulatedResponse}");
            
            // Trigger the OnAgentSpoke method with this response
            var onAgentSpokeMethod = _examManager.GetType().GetMethod("OnAgentSpoke", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (onAgentSpokeMethod != null)
            {
                onAgentSpokeMethod.Invoke(_examManager, new object[] { simulatedResponse });
                Debug.Log("[Debugger] Simulated evaluation triggered successfully");
            }
            else
            {
                Debug.LogWarning("[Debugger] Could not find OnAgentSpoke method");
            }
        }
        
        private void OnInspectorUpdate()
        {
            // Refresh the window periodically
            Repaint();
        }
    }
}
