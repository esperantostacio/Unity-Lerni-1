using UnityEngine;
using UnityEditor;

namespace MedicalExam
{
#if UNITY_EDITOR
    /// <summary>
    /// Editor utility for quickly creating common medical exam scenarios
    /// </summary>
    public class ScenarioCreator : EditorWindow
    {
        private string scenarioName = "New Medical Scenario";
        private string medicalTopic = "";
        private int examDuration = 5;
        private int difficulty = 3;
        private ExamLanguage language = ExamLanguage.Deutsch;
        
        private string savePath = "Assets/Scenarios/";
        
        [MenuItem("Medical Exam/Scenario Creator")]
        public static void ShowWindow()
        {
            GetWindow<ScenarioCreator>("Scenario Creator");
        }
        
        private void OnGUI()
        {
            GUILayout.Label("Create Medical Exam Scenario", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            scenarioName = EditorGUILayout.TextField("Scenario Name", scenarioName);
            medicalTopic = EditorGUILayout.TextField("Medical Topic", medicalTopic, GUILayout.Height(60));
            examDuration = EditorGUILayout.IntSlider("Duration (minutes)", examDuration, 3, 10);
            difficulty = EditorGUILayout.IntSlider("Difficulty Level", difficulty, 1, 5);
            language = (ExamLanguage)EditorGUILayout.EnumPopup("Language (Deutsch only)", language);
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Save Location:");
            EditorGUILayout.BeginHorizontal();
            savePath = EditorGUILayout.TextField(savePath);
            if (GUILayout.Button("Browse", GUILayout.Width(60)))
            {
                string path = EditorUtility.SaveFolderPanel("Select Save Location", "Assets", "");
                if (!string.IsNullOrEmpty(path))
                {
                    savePath = "Assets" + path.Substring(Application.dataPath.Length) + "/";
                }
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(10);
            
            // Quick preset buttons
            GUILayout.Label("Quick Presets:", EditorStyles.boldLabel);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Cardiology"))
            {
                ApplyCardiologyPreset();
            }
            if (GUILayout.Button("Neurology"))
            {
                ApplyNeurologyPreset();
            }
            if (GUILayout.Button("Pediatrics"))
            {
                ApplyPediatricsPreset();
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Emergency"))
            {
                ApplyEmergencyPreset();
            }
            if (GUILayout.Button("Anamnesis"))
            {
                ApplyAnamnesisPreset();
            }
            if (GUILayout.Button("Surgery"))
            {
                ApplySurgeryPreset();
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(20);
            
            // Create button
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Create Scenario", GUILayout.Height(40)))
            {
                CreateScenario();
            }
            GUI.backgroundColor = Color.white;
        }
        
        private void CreateScenario()
        {
            if (string.IsNullOrWhiteSpace(scenarioName))
            {
                EditorUtility.DisplayDialog("Error", "Please enter a scenario name!", "OK");
                return;
            }
            
            if (string.IsNullOrWhiteSpace(medicalTopic))
            {
                EditorUtility.DisplayDialog("Error", "Please enter a medical topic!", "OK");
                return;
            }
            
            // Create directory if it doesn't exist
            if (!AssetDatabase.IsValidFolder(savePath.TrimEnd('/')))
            {
                string[] folders = savePath.TrimEnd('/').Split('/');
                string currentPath = folders[0];
                
                for (int i = 1; i < folders.Length; i++)
                {
                    string newPath = currentPath + "/" + folders[i];
                    if (!AssetDatabase.IsValidFolder(newPath))
                    {
                        AssetDatabase.CreateFolder(currentPath, folders[i]);
                    }
                    currentPath = newPath;
                }
            }
            
            // Create the scenario asset
            MedicalExamScenario scenario = ScriptableObject.CreateInstance<MedicalExamScenario>();
            scenario.scenarioName = scenarioName;
            scenario.medicalTopic = medicalTopic;
            scenario.examDurationMinutes = examDuration;
            scenario.difficultyLevel = difficulty;
            scenario.language = language;
            
            // Generate filename
            string fileName = scenarioName.Replace(" ", "_");
            string assetPath = savePath + fileName + ".asset";
            
            // Save asset
            AssetDatabase.CreateAsset(scenario, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            // Select the new asset
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = scenario;
            
            EditorUtility.DisplayDialog("Success", $"Scenario '{scenarioName}' created successfully!", "OK");
            
            Debug.Log($"[ScenarioCreator] Created scenario: {assetPath}");
        }
        
        // Preset methods
        private void ApplyCardiologyPreset()
        {
            scenarioName = "Cardiology Basics";
            medicalTopic = "Basic cardiovascular examination, common cardiac conditions including hypertension, coronary artery disease, and heart failure. Discuss diagnostic approaches and treatment options.";
            difficulty = 3;
        }
        
        private void ApplyNeurologyPreset()
        {
            scenarioName = "Neurology Examination";
            medicalTopic = "Neurological examination techniques, common neurological disorders including stroke, epilepsy, and peripheral neuropathy. Assess mental status and motor function.";
            difficulty = 4;
        }
        
        private void ApplyPediatricsPreset()
        {
            scenarioName = "Pediatric Care";
            medicalTopic = "Pediatric examination and assessment, common childhood diseases, growth and development milestones, vaccination schedules, and communication with parents.";
            difficulty = 3;
        }
        
        private void ApplyEmergencyPreset()
        {
            scenarioName = "Emergency Medicine";
            medicalTopic = "Emergency assessment using ABCDE approach, management of acute conditions including trauma, cardiac arrest, respiratory distress, and shock. Prioritize life-threatening conditions.";
            difficulty = 5;
        }
        
        private void ApplyAnamnesisPreset()
        {
            scenarioName = "Patient Anamnesis";
            medicalTopic = "Comprehensive patient history taking including chief complaint, history of present illness, past medical history, medications, allergies, family and social history. Practice active listening and empathy.";
            difficulty = 2;
        }
        
        private void ApplySurgeryPreset()
        {
            scenarioName = "Surgical Consultation";
            medicalTopic = "Pre-operative assessment, surgical indications and contraindications, common surgical procedures, post-operative care, and complications management.";
            difficulty = 4;
        }
    }
#endif
}
