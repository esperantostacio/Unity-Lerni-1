using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MedicalExam.RAG;

namespace MedicalExam
{
    /// <summary>
    /// Example integration showing how to add RAG to your existing MedicalExamManager.
    /// 
    /// INTEGRATION STEPS:
    /// 1. Add RAGIntegrationHelper component to your scene
    /// 2. Add KnowledgeRetriever component to your scene
    /// 3. Call EnhanceWithRAG() before starting Realtime conversation
    /// 4. Your AI will now have access to medical knowledge from your books!
    /// </summary>
    public class RAGExampleIntegration : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private RAGIntegrationHelper ragHelper;
        [SerializeField] private OpenAIRealtimeClient realtimeClient;

        /// <summary>
        /// Example: How to enhance a system prompt with RAG before starting conversation.
        /// Call this instead of directly calling StartRealtimeSession().
        /// </summary>
        public void StartConversationWithRAG(string baseSystemPrompt, MedicalExamScenario scenario)
        {
            StartCoroutine(StartConversationWithRAGCoroutine(baseSystemPrompt, scenario));
        }

        private IEnumerator StartConversationWithRAGCoroutine(string baseSystemPrompt, MedicalExamScenario scenario)
        {
            Debug.Log("[RAGExample] Starting conversation with RAG enhancement...");

            // Get scenario tags (you can customize this based on your scenario structure)
            List<string> scenarioTags = GetScenarioTags(scenario);
            Debug.Log($"[RAGExample] Using scenario tags: {string.Join(", ", scenarioTags)}");

            // Wait for RAG helper to be ready
            yield return new WaitUntil(() => ragHelper != null && ragHelper.IsReady);

            bool promptReady = false;
            string enhancedPrompt = baseSystemPrompt;

            // Enhance the prompt with medical knowledge
            ragHelper.EnhanceSystemPrompt(
                baseSystemPrompt,
                scenarioTags,
                onEnhancedPromptReady: (enhanced) =>
                {
                    enhancedPrompt = enhanced;
                    promptReady = true;
                    Debug.Log("[RAGExample] System prompt enhanced with medical knowledge");
                },
                onError: (error) =>
                {
                    Debug.LogWarning($"[RAGExample] RAG enhancement failed: {error}");
                    promptReady = true; // Continue with base prompt
                }
            );

            yield return new WaitUntil(() => promptReady);

            // Now start the Realtime session with enhanced prompt
            Debug.Log($"[RAGExample] Starting Realtime session with prompt length: {enhancedPrompt.Length}");
            
            // This is where you'd call your existing StartRealtimeSession method
            // StartRealtimeSession(enhancedPrompt);
            
            // For demonstration, just log the enhanced prompt
            Debug.Log($"[RAGExample] Enhanced prompt:\n{enhancedPrompt.Substring(0, Mathf.Min(500, enhancedPrompt.Length))}...");
        }

        /// <summary>
        /// Helper to extract scenario tags from your scenario object.
        /// Customize this based on your MedicalExamScenario structure.
        /// </summary>
        private List<string> GetScenarioTags(MedicalExamScenario scenario)
        {
            var tags = new List<string>();

            if (scenario == null)
            {
                Debug.LogWarning("[RAGExample] No scenario provided, using default tags");
                return new List<string> { "general_medical" };
            }

            // Derive tags from scenario context (e.g., "Kardiologie")
            if (!string.IsNullOrEmpty(scenario.scenarioContext))
            {
                string contextLower = scenario.scenarioContext.ToLower();
                tags.Add(contextLower);
                
                // Map common German medical terms to English tags
                if (contextLower.Contains("kardiologie")) tags.Add("cardiology");
                else if (contextLower.Contains("pneumologie")) tags.Add("pulmonology");
                else if (contextLower.Contains("neurologie")) tags.Add("neurology");
                else if (contextLower.Contains("notfall")) tags.Add("emergency_medicine");
            }

            // Also derive tags from scenario name and topic
            string scenarioName = scenario.scenarioName?.ToLower() ?? "";
            string medicalTopic = scenario.medicalTopic?.ToLower() ?? "";

            // Add common medical domain tags
            if (scenarioName.Contains("stroke") || medicalTopic.Contains("stroke"))
                tags.Add("stroke");
            if (scenarioName.Contains("cardiac") || medicalTopic.Contains("heart"))
                tags.Add("cardiology");
            if (scenarioName.Contains("back pain") || scenarioName.Contains("lumbar"))
                tags.Add("back_pain");
            if (scenarioName.Contains("emergency"))
                tags.Add("emergency_medicine");
            if (scenarioName.Contains("trauma"))
                tags.Add("trauma");
            
            // Add role-based tags
            // You can get this from your exam manager's selectedRole
            // Example:
            // tags.AddRange(ragHelper.GetScenarioTagsFromRole(examManager.selectedRole));

            if (tags.Count == 0)
            {
                Debug.LogWarning("[RAGExample] No specific tags matched, using general");
                tags.Add("general_medical");
            }

            return tags;
        }

        /// <summary>
        /// Example: Retrieve additional knowledge mid-conversation based on user question.
        /// </summary>
        public void HandleUserQuestion(string userQuestion, List<string> scenarioTags)
        {
            if (ragHelper == null || !ragHelper.IsReady)
                return;

            ragHelper.RetrieveForQuery(
                userQuestion,
                scenarioTags,
                onContextReady: (context) =>
                {
                    if (!string.IsNullOrEmpty(context))
                    {
                        Debug.Log($"[RAGExample] Retrieved additional context for question: {userQuestion}");
                        // You could inject this context as a user message or system message update
                        // Example: realtimeClient.SendText("Context: " + context);
                    }
                },
                onError: (error) =>
                {
                    Debug.LogWarning($"[RAGExample] Failed to retrieve context: {error}");
                }
            );
        }
    }
}
