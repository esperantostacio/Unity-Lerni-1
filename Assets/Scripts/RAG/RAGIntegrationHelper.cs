using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Helper component that integrates RAG (Retrieval Augmented Generation) into the medical exam flow.
    /// Retrieves relevant medical knowledge and injects it into AI system prompts.
    /// </summary>
    public class RAGIntegrationHelper : MonoBehaviour
    {
        [Header("RAG Configuration")]
        [SerializeField] private bool enableRAG = true;
        [Tooltip("Number of knowledge chunks to retrieve for each exam")]
        [SerializeField] private int topKChunks = 8;
        [Tooltip("Minimum similarity score (0-1) to include a chunk")]
        [SerializeField] private float similarityThreshold = 0.3f;
        
        [Header("Context Formatting")]
        [SerializeField] private string contextHeader = "MEDICAL KNOWLEDGE CONTEXT:";
        [Tooltip("If true, adds instructions to AI to reference the provided knowledge")]
        [SerializeField] private bool addReferenceInstructions = true;
        
        [Header("Debug")]
        [SerializeField] private bool logRetrievedKnowledge = true;

        private KnowledgeRetriever _knowledgeRetriever;
        private bool _isInitialized = false;

        public bool IsReady => _isInitialized && _knowledgeRetriever != null && _knowledgeRetriever.IsReady;

        private void Start()
        {
            if (!enableRAG)
            {
                Debug.Log("[RAGIntegrationHelper] RAG is disabled in settings");
                return;
            }

            _knowledgeRetriever = KnowledgeRetriever.Instance;
            StartCoroutine(WaitForInitialization());
        }

        private IEnumerator WaitForInitialization()
        {
            yield return new WaitUntil(() => _knowledgeRetriever != null && _knowledgeRetriever.IsReady);
            _isInitialized = true;
            Debug.Log($"[RAGIntegrationHelper] Ready! Knowledge base has {_knowledgeRetriever.KnowledgeChunkCount} chunks");
        }

        /// <summary>
        /// Retrieves medical knowledge for a scenario and enhances the system prompt.
        /// This is the main method to call before starting a conversation.
        /// </summary>
        public void EnhanceSystemPrompt(
            string baseSystemPrompt,
            List<string> scenarioTags,
            System.Action<string> onEnhancedPromptReady,
            System.Action<string> onError = null)
        {
            if (!enableRAG)
            {
                Debug.Log("[RAGIntegrationHelper] RAG disabled, returning base prompt");
                onEnhancedPromptReady?.Invoke(baseSystemPrompt);
                return;
            }

            if (!IsReady)
            {
                Debug.LogWarning("[RAGIntegrationHelper] Not ready yet, returning base prompt");
                onEnhancedPromptReady?.Invoke(baseSystemPrompt);
                return;
            }

            StartCoroutine(EnhanceSystemPromptCoroutine(baseSystemPrompt, scenarioTags, onEnhancedPromptReady, onError));
        }

        private IEnumerator EnhanceSystemPromptCoroutine(
            string baseSystemPrompt,
            List<string> scenarioTags,
            System.Action<string> onEnhancedPromptReady,
            System.Action<string> onError)
        {
            bool retrievalComplete = false;
            KnowledgeRetrievalResult retrievalResult = null;
            string errorMessage = null;

            // Retrieve knowledge by scenario tags
                // Build a semantic query from scenario tags and retrieve by embedding similarity.
                // scenarioTags:null disables tag filtering so all DB chunks are candidates,
                // ranked by cosine similarity to the query. This is needed because the prebuilt
                // DB only carries generic tags ('fsp','medical_german') that would never match
                // specialty-specific tags produced by BuildRagTagsForCurrentScenario.
                string ragQueryText = (scenarioTags != null && scenarioTags.Count > 0)
                    ? string.Join(" ", scenarioTags)
                    : "FSP Fachsprachprüfung Arzt Anamnese";
                _knowledgeRetriever.RetrieveKnowledge(
                    ragQueryText,
                    scenarioTags: null,
                    topK: topKChunks,
                    onSuccess: (result) =>
                    {
                        retrievalResult = result;
                        retrievalComplete = true;
                    },
                    onError: (error) =>
                    {
                        errorMessage = error;
                        retrievalComplete = true;
                    }
                );

            yield return new WaitUntil(() => retrievalComplete);

            if (!string.IsNullOrEmpty(errorMessage))
            {
                Debug.LogWarning($"[RAGIntegrationHelper] Retrieval failed: {errorMessage}");
                onError?.Invoke(errorMessage);
                onEnhancedPromptReady?.Invoke(baseSystemPrompt); // Fallback to base prompt
                yield break;
            }

            if (retrievalResult == null || retrievalResult.Chunks.Count == 0)
            {
                Debug.LogWarning("[RAGIntegrationHelper] No knowledge retrieved for scenario");
                onEnhancedPromptReady?.Invoke(baseSystemPrompt);
                yield break;
            }

            // Format knowledge as context
            string medicalContext = _knowledgeRetriever.FormatContextForPrompt(retrievalResult, contextHeader);

            if (logRetrievedKnowledge)
            {
                Debug.Log($"[RAGIntegrationHelper] Retrieved {retrievalResult.Chunks.Count} knowledge chunks:");
                foreach (var chunk in retrievalResult.Chunks)
                {
                    Debug.Log($"  - [{chunk.Category}] {chunk.SectionTitle} (Relevance: {chunk.SimilarityScore * 100:F1}%)");
                }
            }

            // Build reference instructions
            string referenceInstructions = "";
            if (addReferenceInstructions)
            {
                referenceInstructions = "\n\nIMPORTANT INSTRUCTIONS:\n" +
                    "- Reference the medical knowledge provided above when answering questions\n" +
                    "- Be specific and cite relevant clinical information from the context\n" +
                    "- If the student asks about topics covered in the medical knowledge, use that information\n" +
                    "- Remain clinically accurate and evidence-based in all responses";
            }

            // Combine base prompt + medical context + instructions
            string enhancedPrompt = $"{baseSystemPrompt}\n\n{medicalContext}{referenceInstructions}";

            Debug.Log($"[RAGIntegrationHelper] Enhanced system prompt (length: {enhancedPrompt.Length} chars)");
            onEnhancedPromptReady?.Invoke(enhancedPrompt);
        }

        /// <summary>
        /// Retrieves knowledge for a specific query (useful for mid-conversation questions).
        /// </summary>
        public void RetrieveForQuery(
            string queryText,
            List<string> scenarioTags,
            System.Action<string> onContextReady,
            System.Action<string> onError = null)
        {
            if (!enableRAG || !IsReady)
            {
                onContextReady?.Invoke("");
                return;
            }

            StartCoroutine(RetrieveForQueryCoroutine(queryText, scenarioTags, onContextReady, onError));
        }

        private IEnumerator RetrieveForQueryCoroutine(
            string queryText,
            List<string> scenarioTags,
            System.Action<string> onContextReady,
            System.Action<string> onError)
        {
            bool retrievalComplete = false;
            KnowledgeRetrievalResult retrievalResult = null;
            string errorMessage = null;

            _knowledgeRetriever.RetrieveKnowledge(
                queryText,
                scenarioTags: scenarioTags,
                topK: 3, // Fewer chunks for specific queries
                onSuccess: (result) =>
                {
                    retrievalResult = result;
                    retrievalComplete = true;
                },
                onError: (error) =>
                {
                    errorMessage = error;
                    retrievalComplete = true;
                }
            );

            yield return new WaitUntil(() => retrievalComplete);

            if (!string.IsNullOrEmpty(errorMessage))
            {
                onError?.Invoke(errorMessage);
                onContextReady?.Invoke("");
                yield break;
            }

            if (retrievalResult == null || retrievalResult.Chunks.Count == 0)
            {
                onContextReady?.Invoke("");
                yield break;
            }

            string context = _knowledgeRetriever.FormatContextForPrompt(retrievalResult, "ADDITIONAL CONTEXT:");
            onContextReady?.Invoke(context);
        }

        /// <summary>
        /// Gets scenario tags from exam configuration.
        /// Derives tags from scenario context and topic.
        /// </summary>
        public List<string> GetScenarioTags(MedicalExamScenario scenario)
        {
            var tags = new List<string>();

            if (scenario == null)
                return tags;

            // Derive tags from scenario context (e.g., "Kardiologie" -> "cardiology")
            if (!string.IsNullOrEmpty(scenario.scenarioContext))
            {
                string contextLower = scenario.scenarioContext.ToLower();
                tags.Add(contextLower);
                
                // Map common German medical terms to English tags
                if (contextLower.Contains("kardiologie")) tags.Add("cardiology");
                else if (contextLower.Contains("pneumologie")) tags.Add("pulmonology");
                else if (contextLower.Contains("neurologie")) tags.Add("neurology");
                else if (contextLower.Contains("orthopädie")) tags.Add("orthopedics");
                else if (contextLower.Contains("psychiatrie")) tags.Add("psychiatry");
                else if (contextLower.Contains("pädiatrie")) tags.Add("pediatrics");
                else if (contextLower.Contains("notfall")) tags.Add("emergency_medicine");
            }

            // Also check scenario name for keywords
            if (!string.IsNullOrEmpty(scenario.scenarioName))
            {
                string nameLower = scenario.scenarioName.ToLower();
                if (nameLower.Contains("stroke")) tags.Add("stroke");
                if (nameLower.Contains("trauma")) tags.Add("trauma");
                if (nameLower.Contains("emergency")) tags.Add("emergency_medicine");
            }

            return tags;
        }

        /// <summary>
        /// Gets scenario tags based on role type.
        /// This is a fallback method when scenario tags aren't explicitly defined.
        /// </summary>
        public List<string> GetScenarioTagsFromRole(MedicalExamManager.RoleType roleType)
        {
            var tags = new List<string>();

            switch (roleType)
            {
                case MedicalExamManager.RoleType.DoctorToPatient:
                    tags.AddRange(new[] { "patient_communication", "clinical_assessment", "diagnosis" });
                    break;
                case MedicalExamManager.RoleType.DoctorToDoctor:
                    tags.AddRange(new[] { "professional_handover", "clinical_decision", "case_discussion" });
                    break;
                case MedicalExamManager.RoleType.None:
                default:
                    // Add general medical tags when role is not specified
                    tags.AddRange(new[] { "general_medicine", "clinical_knowledge" });
                    break;
            }

            return tags;
        }

        /// <summary>
        /// Enable or disable RAG at runtime.
        /// </summary>
        public void SetRAGEnabled(bool enabled)
        {
            enableRAG = enabled;
            Debug.Log($"[RAGIntegrationHelper] RAG {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>
        /// Adjusts the number of knowledge chunks to retrieve.
        /// </summary>
        public void SetTopK(int k)
        {
            topKChunks = Mathf.Clamp(k, 1, 20);
        }

        /// <summary>
        /// Prints statistics about the knowledge base.
        /// </summary>
        public void LogKnowledgeBaseStats()
        {
            if (_knowledgeRetriever != null)
            {
                _knowledgeRetriever.PrintStatistics();
            }
        }
    }
}
