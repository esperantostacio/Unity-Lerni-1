using System;
using System.Collections.Generic;
using UnityEngine;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Test and demo script for RAG system.
    /// Shows how to initialize, load, and query the knowledge base.
    /// </summary>
    public class RAGDemo : MonoBehaviour
    {
        [Header("Test Queries")]
        [SerializeField] private bool runDemoOnStart = false;
        [SerializeField] private List<string> testQueries = new List<string>
        {
            "What are the symptoms of stroke?",
            "How do I assess neck pain in a patient?",
            "What is the FAST test?",
            "Explain aphasia symptoms",
            "What are cardiac assessment criteria?"
        };

        [SerializeField] private List<string> testScenarioTags = new List<string>
        {
            "stroke",
            "anatomy_drill",
            "back_pain",
            "emergency_medicine"
        };

        private KnowledgeRetriever _retriever;

        private void Start()
        {
            _retriever = KnowledgeRetriever.Instance;

            if (runDemoOnStart)
            {
                StartCoroutine(RunDemo());
            }
        }

        /// <summary>
        /// Runs a complete demo of RAG functionality.
        /// </summary>
        private System.Collections.IEnumerator RunDemo()
        {
            Debug.Log("===== RAG SYSTEM DEMO =====");

            // Wait for initialization
            yield return new WaitUntil(() => _retriever.IsReady);

            Debug.Log($"Knowledge base ready with {_retriever.KnowledgeChunkCount} chunks");

            // Test 1: Semantic search
            Debug.Log("\n--- TEST 1: Semantic Search ---");
            foreach (var query in testQueries)
            {
                yield return StartCoroutine(TestSemanticSearch(query));
            }

            // Test 2: Scenario-based retrieval
            Debug.Log("\n--- TEST 2: Scenario-Based Retrieval ---");
            foreach (var tag in testScenarioTags)
            {
                yield return StartCoroutine(TestScenarioRetrieval(new List<string> { tag }));
            }

            // Test 3: Combined retrieval
            Debug.Log("\n--- TEST 3: Combined Retrieval ---");
            yield return StartCoroutine(TestCombinedRetrieval("stroke assessment", new List<string> { "stroke", "emergency_medicine" }));

            Debug.Log("\n===== DEMO COMPLETE =====");
        }

        private System.Collections.IEnumerator TestSemanticSearch(string query)
        {
            Debug.Log($"\nQuery: '{query}'");
            yield return new WaitForSeconds(0.1f);

            bool complete = false;
            _retriever.RetrieveKnowledge(
                query,
                topK: 3,
                onSuccess: (result) =>
                {
                    Debug.Log($"Found {result.Chunks.Count} relevant chunks (retrieved in {result.RetrievalTimeMs}ms):");
                    for (int i = 0; i < result.Chunks.Count; i++)
                    {
                        var chunk = result.Chunks[i];
                        Debug.Log($"  {i + 1}. [{chunk.Category}] {chunk.SectionTitle} (Relevance: {chunk.SimilarityScore * 100:F1}%)");
                        Debug.Log($"     {chunk.Content.Substring(0, Math.Min(100, chunk.Content.Length))}...");
                    }
                    complete = true;
                },
                onError: (error) =>
                {
                    Debug.LogError($"Error: {error}");
                    complete = true;
                }
            );

            yield return new WaitUntil(() => complete);
        }

        private System.Collections.IEnumerator TestScenarioRetrieval(List<string> tags)
        {
            Debug.Log($"\nScenario Tags: {string.Join(", ", tags)}");
            yield return new WaitForSeconds(0.1f);

            bool complete = false;
            _retriever.RetrieveByScenario(
                tags,
                topK: 3,
                onSuccess: (result) =>
                {
                    Debug.Log($"Found {result.Chunks.Count} chunks for scenario:");
                    for (int i = 0; i < result.Chunks.Count; i++)
                    {
                        var chunk = result.Chunks[i];
                        Debug.Log($"  {i + 1}. [{chunk.Category}] {chunk.SectionTitle}");
                    }
                    complete = true;
                },
                onError: (error) =>
                {
                    Debug.LogError($"Error: {error}");
                    complete = true;
                }
            );

            yield return new WaitUntil(() => complete);
        }

        private System.Collections.IEnumerator TestCombinedRetrieval(string query, List<string> tags)
        {
            Debug.Log($"\nQuery: '{query}' with tags: {string.Join(", ", tags)}");
            yield return new WaitForSeconds(0.1f);

            bool complete = false;
            _retriever.RetrieveKnowledge(
                query,
                scenarioTags: tags,
                topK: 3,
                onSuccess: (result) =>
                {
                    Debug.Log($"Found {result.Chunks.Count} relevant chunks:");
                    
                    // Format for display
                    string contextString = _retriever.FormatContextForPrompt(result, "COMBINED RETRIEVAL RESULT:");
                    Debug.Log(contextString);
                    
                    complete = true;
                },
                onError: (error) =>
                {
                    Debug.LogError($"Error: {error}");
                    complete = true;
                }
            );

            yield return new WaitUntil(() => complete);
        }

        /// <summary>
        /// Simulates what an exam would do: retrieve knowledge for a specific scenario,
        /// then format it to inject into the AI system prompt.
        /// </summary>
        public void SimulateExamContextInjection(string scenarioName, List<string> scenarioTags, string additionalQuery = null)
        {
            Debug.Log($"=== SIMULATING EXAM CONTEXT FOR: {scenarioName} ===");

            StartCoroutine(SimulateExamCoroutine(scenarioName, scenarioTags, additionalQuery));
        }

        private System.Collections.IEnumerator SimulateExamCoroutine(string scenarioName, List<string> scenarioTags, string additionalQuery)
        {
            // First, get scenario-based knowledge
            bool complete = false;
            KnowledgeRetrievalResult result = null;

            _retriever.RetrieveByScenario(
                scenarioTags,
                topK: 10,
                onSuccess: (r) =>
                {
                    result = r;
                    complete = true;
                },
                onError: (error) =>
                {
                    Debug.LogError($"Failed to retrieve scenario knowledge: {error}");
                    complete = true;
                }
            );

            yield return new WaitUntil(() => complete);

            if (result != null)
            {
                // Format as system prompt context
                string contextString = _retriever.FormatContextForPrompt(result, $"MEDICAL KNOWLEDGE FOR {scenarioName.ToUpper()}:");
                
                Debug.Log("=== FORMATTED FOR SYSTEM PROMPT ===");
                Debug.Log(contextString);
                
                Debug.Log("\n=== USAGE IN CODE ===");
                Debug.Log($@"
// In your MedicalExamManager or similar:
string systemPrompt = $""You are a medical examination AI assistant.

{{contextString}}

Be thorough and clinically accurate in your responses."";

// Then pass this systemPrompt to your Realtime API or GPT call
");
            }
        }
    }
}
