using UnityEngine;

namespace OpenAI
{
    [CreateAssetMenu(menuName = "OpenAI/Config", fileName = "OpenAIConfig")]
    public class OpenAIConfig : ScriptableObject
    {
        [Header("API Configuration")]
        public string apiKey;
        public string realtimeConvWebsocketUrl = "wss://api.openai.com/v1/realtime";

        [Header("Chat / Fallback Model")]
        [Tooltip("Base (non fine-tuned) model id used for chat/completions requests when fine-tuned model is disabled or unavailable. Set this to a model that exists on your account.")]
        public string chatModelId = "gpt-4o-mini";
        
        [Header("Fine-Tuned Model")]
        [Tooltip("Fine-tuned GPT-4 model ID for medical evaluation. Example: ft:gpt-4-turbo-2024-04-09:default")]
        public string fineTunedModelId = "ft:gpt-4-turbo-2024-04-09:default";
    }
}
