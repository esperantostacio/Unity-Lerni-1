using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Represents a chunk of medical knowledge with metadata for retrieval.
    /// </summary>
    [Serializable]
    public class KnowledgeChunk
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("document_id")]
        public string DocumentId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("section_title")]
        public string SectionTitle { get; set; }

        [JsonProperty("clinical_context")]
        public string ClinicalContext { get; set; }

        [JsonProperty("scenario_tags")]
        public List<string> ScenarioTags { get; set; } = new List<string>();

        [JsonProperty("embedding")]
        public float[] Embedding { get; set; }

        [JsonProperty("token_count")]
        public int TokenCount { get; set; }

        [JsonProperty("created_at")]
        public string CreatedAt { get; set; }

        [JsonProperty("source_file")]
        public string SourceFile { get; set; }

        /// <summary>
        /// Calculates similarity with another chunk using cosine similarity.
        /// Returns value between 0 and 1 (higher = more similar).
        /// </summary>
        public float CalculateSimilarity(KnowledgeChunk other)
        {
            if (Embedding == null || other.Embedding == null)
                return 0f;

            if (Embedding.Length != other.Embedding.Length)
                return 0f;

            float dotProduct = 0f;
            float magnitudeA = 0f;
            float magnitudeB = 0f;

            for (int i = 0; i < Embedding.Length; i++)
            {
                dotProduct += Embedding[i] * other.Embedding[i];
                magnitudeA += Embedding[i] * Embedding[i];
                magnitudeB += other.Embedding[i] * other.Embedding[i];
            }

            if (magnitudeA == 0f || magnitudeB == 0f)
                return 0f;

            return dotProduct / (float)(Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
        }

        /// <summary>
        /// Checks if this chunk matches any of the given scenario tags.
        /// </summary>
        public bool MatchesScenario(List<string> scenarioTags)
        {
            if (scenarioTags == null || scenarioTags.Count == 0)
                return true;

            foreach (var tag in scenarioTags)
            {
                if (ScenarioTags.Contains(tag.ToLower()))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Creates a display-friendly version for debugging.
        /// </summary>
        public override string ToString()
        {
            return $"[{Category}] {SectionTitle} ({ScenarioTags.Count} tags) - {Content?.Substring(0, Math.Min(50, Content.Length))}...";
        }
    }

    /// <summary>
    /// Request object for knowledge retrieval.
    /// </summary>
    [Serializable]
    public class KnowledgeRetrievalRequest
    {
        [JsonProperty("query_text")]
        public string QueryText { get; set; }

        [JsonProperty("scenario_tags")]
        public List<string> ScenarioTags { get; set; } = new List<string>();

        [JsonProperty("category_filter")]
        public string CategoryFilter { get; set; }

        [JsonProperty("top_k")]
        public int TopK { get; set; } = 5;

        [JsonProperty("similarity_threshold")]
        public float SimilarityThreshold { get; set; } = 0.5f;
    }

    /// <summary>
    /// Response object from knowledge retrieval.
    /// </summary>
    [Serializable]
    public class KnowledgeRetrievalResult
    {
        [JsonProperty("chunks")]
        public List<RetrievedChunk> Chunks { get; set; } = new List<RetrievedChunk>();

        [JsonProperty("total_found")]
        public int TotalFound { get; set; }

        [JsonProperty("query_text")]
        public string QueryText { get; set; }

        [JsonProperty("retrieval_time_ms")]
        public float RetrievalTimeMs { get; set; }

        /// <summary>
        /// Combines all chunk contents into a single context string.
        /// </summary>
        public string GetCombinedContext(string separator = "\n---\n")
        {
            if (Chunks.Count == 0)
                return "";

            var contexts = new List<string>();
            foreach (var chunk in Chunks)
            {
                contexts.Add($"[{chunk.Category}] {chunk.SectionTitle}\n{chunk.Content}");
            }

            return string.Join(separator, contexts);
        }
    }

    /// <summary>
    /// A chunk returned from retrieval with matching score.
    /// </summary>
    [Serializable]
    public class RetrievedChunk
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("document_id")]
        public string DocumentId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("section_title")]
        public string SectionTitle { get; set; }

        [JsonProperty("content")]
        public string Content { get; set; }

        [JsonProperty("similarity_score")]
        public float SimilarityScore { get; set; }

        [JsonProperty("scenario_tags")]
        public List<string> ScenarioTags { get; set; } = new List<string>();

        [JsonProperty("clinical_context")]
        public string ClinicalContext { get; set; }
    }
}
