using UnityEngine;

namespace MedicalExam
{
    /// <summary>
    /// Defines a medical exam scenario with topic and role-specific prompts
    /// </summary>
    [CreateAssetMenu(fileName = "NewMedicalScenario", menuName = "Medical Exam/Scenario", order = 1)]
    public class MedicalExamScenario : ScriptableObject
    {
        [Header("Scenario Information")]
        [Tooltip("Name of the medical topic (e.g., 'Cardiology Basics', 'Patient Anamnesis')")]
        public string scenarioName = "Cardiology Basics";
        
        [Tooltip("Medical subject/topic to discuss")]
        [TextArea(2, 4)]
        public string medicalTopic = "Basic cardiovascular examination and common cardiac conditions";

        [Tooltip("Short context label (e.g., 'Kardiologie', 'Pneumologie', 'Psychiatrie', 'Physiotherapie'). Used for remote scenario overrides and planning prompts.")]
        public string scenarioContext = "Allgemeinmedizin";

        [Header("Role-Specific Topics")]
        [Tooltip("Topic / case description when used in Doctor-to-Doctor (Fallvorstellung) mode.")]
        [TextArea(2, 6)]
        public string topicD2D = "";

        [Tooltip("Topic / case description when used in Doctor-to-Patient (Anamnese) mode.")]
        [TextArea(2, 6)]
        public string topicD2P = "";

        [Header("RAG")]
        [Tooltip("Comma-separated RAG tags (e.g., 'cardiology,hypertension'). Overridden by remote CSV rag_tags column.")]
        public string ragTags = "";

        [Tooltip("Duration of the exam in minutes")]
        [Range(3, 10)]
        public int examDurationMinutes = 5;
        
        [Header("Difficulty")]
        [Range(1, 5)]
        public int difficultyLevel = 3;
        
        [Header("Language")]
        [Tooltip("Language of the exam (Deutsch only)")]
        public ExamLanguage language = ExamLanguage.Deutsch;
    }
    
    public enum ExamLanguage
    {
        English,
        Deutsch
    }
}
