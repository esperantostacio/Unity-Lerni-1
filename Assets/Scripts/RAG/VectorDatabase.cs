using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Helper class for deserializing database JSON.
    /// </summary>
    [Serializable]
    internal class DatabaseData
    {
        public int version;
        public string created_at;
        public int chunk_count;
        public List<KnowledgeChunk> chunks;
    }

    /// <summary>
    /// Local vector database for storing and retrieving knowledge chunks.
    /// Persists data to JSON files and supports fast similarity-based retrieval.
    /// </summary>
    public class VectorDatabase
    {
        private List<KnowledgeChunk> _allChunks = new List<KnowledgeChunk>();
        private string _databasePath;
        private const string DATABASE_FILENAME = "medical_knowledge_db.json";

        public int ChunkCount => _allChunks.Count;

        public VectorDatabase(string dataPath = null)
        {
            if (string.IsNullOrWhiteSpace(dataPath))
            {
                dataPath = Path.Combine(Application.persistentDataPath, "RAG");
            }

            _databasePath = Path.Combine(dataPath, DATABASE_FILENAME);

            // Ensure directory exists
            Directory.CreateDirectory(dataPath);

            Debug.Log($"[VectorDatabase] Initialized at: {_databasePath}");
        }

        /// <summary>
        /// Adds a chunk to the database.
        /// </summary>
        public void AddChunk(KnowledgeChunk chunk)
        {
            if (chunk == null)
                return;

            if (string.IsNullOrWhiteSpace(chunk.Id))
            {
                chunk.Id = Guid.NewGuid().ToString();
            }

            chunk.CreatedAt = DateTime.UtcNow.ToString("O");

            // Remove if already exists (update)
            _allChunks.RemoveAll(c => c.Id == chunk.Id);
            _allChunks.Add(chunk);

            Debug.Log($"[VectorDatabase] Added chunk: {chunk.Id}");
        }

        /// <summary>
        /// Adds multiple chunks to the database.
        /// </summary>
        public void AddChunks(List<KnowledgeChunk> chunks)
        {
            if (chunks == null)
                return;

            foreach (var chunk in chunks)
            {
                AddChunk(chunk);
            }

            Debug.Log($"[VectorDatabase] Added {chunks.Count} chunks. Total: {_allChunks.Count}");
        }

        /// <summary>
        /// Retrieves the most similar chunks for a query.
        /// Uses cosine similarity on embeddings.
        /// </summary>
        public KnowledgeRetrievalResult RetrieveSimilar(
            KnowledgeChunk queryEmbedding,
            int topK = 5,
            float similarityThreshold = 0.3f,
            List<string> scenarioTagFilter = null,
            string categoryFilter = null)
        {
            var result = new KnowledgeRetrievalResult
            {
                QueryText = queryEmbedding?.SectionTitle ?? "Unknown",
                Chunks = new List<RetrievedChunk>()
            };

            if (_allChunks.Count == 0)
            {
                Debug.LogWarning("[VectorDatabase] No chunks in database");
                return result;
            }

            if (queryEmbedding?.Embedding == null)
            {
                Debug.LogWarning("[VectorDatabase] Query has no embedding");
                return result;
            }

            var startTime = DateTime.UtcNow;
            var candidates = new List<(KnowledgeChunk chunk, float similarity)>();

            // Calculate similarities
            foreach (var chunk in _allChunks)
            {
                if (chunk.Embedding == null)
                    continue;

                // Check category filter
                if (!string.IsNullOrWhiteSpace(categoryFilter) && chunk.Category != categoryFilter)
                    continue;

                // Check scenario tag filter
                if (!chunk.MatchesScenario(scenarioTagFilter))
                    continue;

                float similarity = queryEmbedding.CalculateSimilarity(chunk);

                if (similarity >= similarityThreshold)
                {
                    candidates.Add((chunk, similarity));
                }
            }

            // Sort by similarity (highest first)
            candidates.Sort((a, b) => b.similarity.CompareTo(a.similarity));

            // Take top K
            int taken = 0;
            foreach (var (chunk, similarity) in candidates)
            {
                if (taken >= topK)
                    break;

                result.Chunks.Add(new RetrievedChunk
                {
                    Id = chunk.Id,
                    DocumentId = chunk.DocumentId,
                    Category = chunk.Category,
                    SectionTitle = chunk.SectionTitle,
                    Content = chunk.Content,
                    SimilarityScore = similarity,
                    ScenarioTags = new List<string>(chunk.ScenarioTags),
                    ClinicalContext = chunk.ClinicalContext
                });

                taken++;
            }

            var endTime = DateTime.UtcNow;
            result.TotalFound = candidates.Count;
            result.RetrievalTimeMs = (float)(endTime - startTime).TotalMilliseconds;

            Debug.Log($"[VectorDatabase] Retrieved {result.Chunks.Count}/{candidates.Count} chunks in {result.RetrievalTimeMs}ms");

            return result;
        }

        /// <summary>
        /// Retrieves chunks by scenario tags alone (no embedding similarity).
        /// Useful for tag-based filtering.
        /// </summary>
        public KnowledgeRetrievalResult RetrieveByScenario(
            List<string> scenarioTags,
            int topK = 10,
            string categoryFilter = null)
        {
            var result = new KnowledgeRetrievalResult
            {
                QueryText = $"Tags: {string.Join(",", scenarioTags ?? new List<string>())}",
                Chunks = new List<RetrievedChunk>()
            };

            if (_allChunks.Count == 0)
                return result;

            var startTime = DateTime.UtcNow;
            var candidates = new List<KnowledgeChunk>();

            foreach (var chunk in _allChunks)
            {
                // Check category filter
                if (!string.IsNullOrWhiteSpace(categoryFilter) && chunk.Category != categoryFilter)
                    continue;

                // Check if matches scenario
                if (chunk.MatchesScenario(scenarioTags))
                {
                    candidates.Add(chunk);
                }
            }

            // Take top K (order by category relevance)
            int taken = 0;
            foreach (var chunk in candidates)
            {
                if (taken >= topK)
                    break;

                result.Chunks.Add(new RetrievedChunk
                {
                    Id = chunk.Id,
                    DocumentId = chunk.DocumentId,
                    Category = chunk.Category,
                    SectionTitle = chunk.SectionTitle,
                    Content = chunk.Content,
                    SimilarityScore = 1.0f, // Tag matches are considered perfect for this query
                    ScenarioTags = new List<string>(chunk.ScenarioTags),
                    ClinicalContext = chunk.ClinicalContext
                });

                taken++;
            }

            var endTime = DateTime.UtcNow;
            result.TotalFound = candidates.Count;
            result.RetrievalTimeMs = (float)(endTime - startTime).TotalMilliseconds;

            return result;
        }

        /// <summary>
        /// Saves all chunks to persistent storage.
        /// </summary>
        public void Save()
        {
            try
            {
                var jsonSettings = new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore
                };

                var dbData = new
                {
                    version = 1,
                    created_at = DateTime.UtcNow.ToString("O"),
                    chunk_count = _allChunks.Count,
                    chunks = _allChunks
                };

                string json = JsonConvert.SerializeObject(dbData, jsonSettings);
                File.WriteAllText(_databasePath, json);

                Debug.Log($"[VectorDatabase] Saved {_allChunks.Count} chunks to {_databasePath}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VectorDatabase] Failed to save: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads chunks from persistent storage.
        /// </summary>
        public bool Load()
        {
            try
            {
                if (!File.Exists(_databasePath))
                {
                    Debug.Log($"[VectorDatabase] No existing database found at {_databasePath}");
                    return false;
                }

                string json = File.ReadAllText(_databasePath);
                var dbData = JsonConvert.DeserializeObject<DatabaseData>(json);

                _allChunks = dbData?.chunks ?? new List<KnowledgeChunk>();

                Debug.Log($"[VectorDatabase] Loaded {_allChunks.Count} chunks from {_databasePath}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[VectorDatabase] Failed to load: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clears all data from the database.
        /// </summary>
        public void Clear()
        {
            _allChunks.Clear();
            Debug.Log("[VectorDatabase] Cleared all chunks");
        }

        /// <summary>
        /// Gets all chunks in database.
        /// </summary>
        public List<KnowledgeChunk> GetAllChunks()
        {
            return new List<KnowledgeChunk>(_allChunks);
        }

        /// <summary>
        /// Gets statistics about the database.
        /// </summary>
        public void PrintStatistics()
        {
            var categories = new Dictionary<string, int>();
            var tags = new Dictionary<string, int>();

            foreach (var chunk in _allChunks)
            {
                if (!categories.ContainsKey(chunk.Category))
                    categories[chunk.Category] = 0;
                categories[chunk.Category]++;

                foreach (var tag in chunk.ScenarioTags)
                {
                    if (!tags.ContainsKey(tag))
                        tags[tag] = 0;
                    tags[tag]++;
                }
            }

            Debug.Log("=== VectorDatabase Statistics ===");
            Debug.Log($"Total Chunks: {_allChunks.Count}");
            Debug.Log("Categories:");
            foreach (var kvp in categories)
            {
                Debug.Log($"  - {kvp.Key}: {kvp.Value}");
            }
            Debug.Log("Top Tags:");
            foreach (var kvp in tags)
            {
                Debug.Log($"  - {kvp.Key}: {kvp.Value}");
            }
        }
    }
}
