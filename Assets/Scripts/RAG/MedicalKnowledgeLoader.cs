using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using Newtonsoft.Json;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Loads and processes medical knowledge from text files.
    /// Chunks content into manageable pieces and prepares for embedding.
    /// </summary>
    public class MedicalKnowledgeLoader
    {
        private const int DEFAULT_CHUNK_SIZE = 500; // tokens approximately
        private const int DEFAULT_CHUNK_OVERLAP = 50; // tokens

        /// <summary>
        /// Loads and parses a medical knowledge text file.
        /// </summary>
        public static List<KnowledgeChunk> LoadFromTextFile(string filePath, string documentId = null)
        {
            if (!File.Exists(filePath))
            {
                Debug.LogError($"[MedicalKnowledgeLoader] File not found: {filePath}");
                return new List<KnowledgeChunk>();
            }

            try
            {
                string content = File.ReadAllText(filePath);
                return ParseMedicalKnowledge(content, documentId ?? Path.GetFileNameWithoutExtension(filePath), filePath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MedicalKnowledgeLoader] Error loading file {filePath}: {ex.Message}");
                return new List<KnowledgeChunk>();
            }
        }

        /// <summary>
        /// Loads knowledge from the bundled sample file in Resources.
        /// </summary>
        public static List<KnowledgeChunk> LoadSampleKnowledge()
        {
            try
            {
                TextAsset sampleFile = Resources.Load<TextAsset>("MedicalKnowledge/sample_medical_knowledge");
                if (sampleFile == null)
                {
                    Debug.LogError("[MedicalKnowledgeLoader] Sample knowledge file not found in Resources");
                    return new List<KnowledgeChunk>();
                }

                return ParseMedicalKnowledge(sampleFile.text, "sample_knowledge", "Resources/MedicalKnowledge/sample_medical_knowledge.txt");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MedicalKnowledgeLoader] Error loading sample knowledge: {ex.Message}");
                return new List<KnowledgeChunk>();
            }
        }

        /// <summary>
        /// Parses medical knowledge content using the document format structure.
        /// </summary>
        private static List<KnowledgeChunk> ParseMedicalKnowledge(string content, string documentId, string sourceFile)
        {
            var chunks = new List<KnowledgeChunk>();

            // Split by document sections (DOCUMENT ID markers)
            string[] documents = Regex.Split(content, @"(?=DOCUMENT ID:)");

            foreach (var docSection in documents)
            {
                if (string.IsNullOrWhiteSpace(docSection))
                    continue;

                try
                {
                    var docChunks = ParseDocument(docSection, documentId, sourceFile);
                    chunks.AddRange(docChunks);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MedicalKnowledgeLoader] Error parsing document section: {ex.Message}");
                }
            }

            Debug.Log($"[MedicalKnowledgeLoader] Parsed {chunks.Count} chunks from {documentId}");
            return chunks;
        }

        /// <summary>
        /// Parses a single document section.
        /// </summary>
        private static List<KnowledgeChunk> ParseDocument(string docContent, string baseDocId, string sourceFile)
        {
            var chunks = new List<KnowledgeChunk>();

            // Extract metadata
            var docIdMatch = Regex.Match(docContent, @"DOCUMENT ID:\s*([^\n]+)");
            var categoryMatch = Regex.Match(docContent, @"CATEGORY:\s*([^\n]+)");
            var tagsMatch = Regex.Match(docContent, @"SCENARIO_TAGS:\s*([^\n]+)");
            var contextMatch = Regex.Match(docContent, @"CLINICAL_CONTEXT:\s*([^\n]+)");

            string docId = docIdMatch.Success ? docIdMatch.Groups[1].Value.Trim() : baseDocId;
            string category = categoryMatch.Success ? categoryMatch.Groups[1].Value.Trim() : "General";
            List<string> tags = ParseTags(tagsMatch.Success ? tagsMatch.Groups[1].Value : "");
            string context = contextMatch.Success ? contextMatch.Groups[1].Value.Trim() : "";

            // Extract content after the "---" separator
            int contentStart = docContent.IndexOf("---");
            if (contentStart < 0)
                return chunks;

            string contentOnly = docContent.Substring(contentStart + 3).Trim();

            // Split into sections (marked by numbered headings)
            string[] sections = Regex.Split(contentOnly, @"\n(?=\d+\.\s+[A-Z])");

            foreach (var section in sections)
            {
                if (string.IsNullOrWhiteSpace(section))
                    continue;

                // Extract section title
                var sectionTitleMatch = Regex.Match(section, @"(\d+\.\s+[^\n]+)");
                string sectionTitle = sectionTitleMatch.Success ? sectionTitleMatch.Groups[1].Value.Trim() : "Content";

                // Split large sections into smaller chunks
                var smallChunks = ChunkText(section.Trim(), DEFAULT_CHUNK_SIZE, DEFAULT_CHUNK_OVERLAP);

                foreach (var chunk in smallChunks)
                {
                    var knowledgeChunk = new KnowledgeChunk
                    {
                        Id = Guid.NewGuid().ToString(),
                        DocumentId = docId,
                        Category = category,
                        SectionTitle = sectionTitle,
                        Content = chunk,
                        ClinicalContext = context,
                        ScenarioTags = new List<string>(tags),
                        TokenCount = EstimateTokenCount(chunk),
                        SourceFile = sourceFile,
                        CreatedAt = DateTime.UtcNow.ToString("O")
                    };

                    chunks.Add(knowledgeChunk);
                }
            }

            return chunks;
        }

        /// <summary>
        /// Splits large text into smaller chunks with optional overlap.
        /// </summary>
        private static List<string> ChunkText(string text, int chunkSizeTokens, int overlapTokens)
        {
            var chunks = new List<string>();
            var sentences = Regex.Split(text, @"(?<=[.!?])\s+");

            var currentChunk = new List<string>();
            int currentTokenCount = 0;

            foreach (var sentence in sentences)
            {
                if (string.IsNullOrWhiteSpace(sentence))
                    continue;

                int sentenceTokens = EstimateTokenCount(sentence);

                if (currentTokenCount + sentenceTokens > chunkSizeTokens && currentChunk.Count > 0)
                {
                    // Save current chunk
                    string chunkText = string.Join(" ", currentChunk).Trim();
                    if (!string.IsNullOrWhiteSpace(chunkText))
                    {
                        chunks.Add(chunkText);
                    }

                    // Keep overlap
                    int overlapSentences = Mathf.CeilToInt((float)overlapTokens / (sentenceTokens + 1));
                    currentChunk = currentChunk.GetRange(Mathf.Max(0, currentChunk.Count - overlapSentences), 
                                                        Mathf.Min(overlapSentences, currentChunk.Count));
                    currentTokenCount = currentChunk.Count * (sentenceTokens + 1);
                }

                currentChunk.Add(sentence);
                currentTokenCount += sentenceTokens;
            }

            // Add final chunk
            if (currentChunk.Count > 0)
            {
                string finalChunk = string.Join(" ", currentChunk).Trim();
                if (!string.IsNullOrWhiteSpace(finalChunk))
                {
                    chunks.Add(finalChunk);
                }
            }

            return chunks.Count > 0 ? chunks : new List<string> { text };
        }

        /// <summary>
        /// Estimates token count (approximately 4 chars per token).
        /// </summary>
        private static int EstimateTokenCount(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            return Mathf.CeilToInt(text.Length / 4f);
        }

        /// <summary>
        /// Parses comma-separated tags.
        /// </summary>
        private static List<string> ParseTags(string tagString)
        {
            var tags = new List<string>();
            if (string.IsNullOrWhiteSpace(tagString))
                return tags;

            foreach (var tag in tagString.Split(','))
            {
                string trimmedTag = tag.Trim().ToLower();
                if (!string.IsNullOrWhiteSpace(trimmedTag))
                {
                    tags.Add(trimmedTag);
                }
            }

            return tags;
        }
    }
}
