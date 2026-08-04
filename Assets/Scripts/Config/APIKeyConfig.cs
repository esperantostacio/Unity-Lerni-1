using UnityEngine;

namespace MedicalExam
{
    /// <summary>
    /// Single place to store third-party API keys for this project.
    /// Loaded from Assets/Resources/APIKeys.asset — fill in the keys there via the Inspector.
    /// Do not hardcode keys in any other script; read them from Instance instead.
    /// Keep APIKeys.asset out of version control (it holds live secrets).
    /// </summary>
    [CreateAssetMenu(fileName = "APIKeys", menuName = "MedicalExam/API Keys")]
    public class APIKeyConfig : ScriptableObject
    {
        [Header("OpenAI — embeddings, realtime voice, Whisper, TTS, GPT-4 evaluation")]
        [SerializeField] private string openAIApiKey = "";

        [Header("Anthropic — Claude evaluation")]
        [SerializeField] private string anthropicApiKey = "";

        [Header("ElevenLabs — streaming TTS")]
        [SerializeField] private string elevenLabsApiKey = "";

        [Header("Murf.ai — premium TTS")]
        [SerializeField] private string murfAiApiKey = "";

        [Header("Wit.ai — fallback TTS")]
        [SerializeField] private string witAiToken = "";

        public string OpenAIApiKey => openAIApiKey;
        public string AnthropicApiKey => anthropicApiKey;
        public string ElevenLabsApiKey => elevenLabsApiKey;
        public string MurfAiApiKey => murfAiApiKey;
        public string WitAiToken => witAiToken;

        private static APIKeyConfig _instance;

        /// <summary>Lazily loads Assets/Resources/APIKeys.asset. Null if it hasn't been created yet.</summary>
        public static APIKeyConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Resources.Load<APIKeyConfig>("APIKeys");
                return _instance;
            }
        }
    }
}
