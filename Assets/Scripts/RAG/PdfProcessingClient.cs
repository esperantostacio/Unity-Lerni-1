using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MedicalExam.RAG
{
    /// <summary>
    /// HTTP client that uploads a PDF to the RAGSYSTEMPDF service
    /// and returns parsed KnowledgeChunks ready for embedding.
    /// </summary>
    public class PdfProcessingClient : MonoBehaviour
    {
        [Header("Service Configuration")]
        [Tooltip("Base URL of the PDF parsing service (e.g. https://ragsystempdf.onrender.com)")]
        [SerializeField] private string serviceUrl = "https://ragsystempdf.onrender.com";

        [Header("Chunking Parameters")]
        [SerializeField] private int chunkSize = 500;
        [SerializeField] private int chunkOverlap = 50;

        /// <summary>
        /// Upload a PDF to the parsing service and receive structured chunks.
        /// </summary>
        /// <param name="pdfBytes">Raw PDF file bytes</param>
        /// <param name="filename">Original filename (e.g. "book.pdf")</param>
        /// <param name="category">Optional category (auto-detected by server if null)</param>
        /// <param name="scenarioTags">Optional comma-separated tags (merged with auto-detected)</param>
        /// <param name="onSuccess">Callback with parsed chunks</param>
        /// <param name="onError">Error callback</param>
        public IEnumerator ParsePdf(
            byte[] pdfBytes,
            string filename,
            string category,
            string scenarioTags,
            Action<List<KnowledgeChunk>, PdfParseMetadata> onSuccess,
            Action<string> onError)
        {
            if (pdfBytes == null || pdfBytes.Length == 0)
            {
                onError?.Invoke("PDF bytes are empty");
                yield break;
            }

            string url = serviceUrl.TrimEnd('/') + "/api/v1/parse-pdf";

            // Build multipart form
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", pdfBytes, filename ?? "upload.pdf", "application/pdf"),
                new MultipartFormDataSection("chunk_size", chunkSize.ToString()),
                new MultipartFormDataSection("chunk_overlap", chunkOverlap.ToString()),
            };

            if (!string.IsNullOrWhiteSpace(category))
                form.Add(new MultipartFormDataSection("category", category));

            if (!string.IsNullOrWhiteSpace(scenarioTags))
                form.Add(new MultipartFormDataSection("scenario_tags", scenarioTags));

            using (var request = UnityWebRequest.Post(url, form))
            {
                request.timeout = 300; // 5 minutes for large PDFs

                Debug.Log($"[PdfProcessingClient] Uploading {pdfBytes.Length / (1024f * 1024f):F1} MB to {url}");

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    string errorMsg = $"PDF service request failed: {request.error}";
                    if (request.downloadHandler != null && !string.IsNullOrEmpty(request.downloadHandler.text))
                        errorMsg += $" — {request.downloadHandler.text}";

                    Debug.LogError($"[PdfProcessingClient] {errorMsg}");
                    onError?.Invoke(errorMsg);
                    yield break;
                }

                // Parse response
                try
                {
                    string responseText = request.downloadHandler.text;
                    var json = JObject.Parse(responseText);

                    if (json["success"]?.Value<bool>() != true)
                    {
                        onError?.Invoke("Server returned success=false");
                        yield break;
                    }

                    var chunks = new List<KnowledgeChunk>();
                    var chunksArray = json["chunks"] as JArray;
                    if (chunksArray != null)
                    {
                        foreach (var item in chunksArray)
                        {
                            var chunk = new KnowledgeChunk
                            {
                                Id = Guid.NewGuid().ToString(),
                                DocumentId = item["document_id"]?.ToString() ?? "",
                                Category = item["category"]?.ToString() ?? "",
                                Content = item["content"]?.ToString() ?? "",
                                SectionTitle = item["section_title"]?.ToString() ?? "",
                                ClinicalContext = item["clinical_context"]?.ToString() ?? "",
                                ScenarioTags = item["scenario_tags"]?.ToObject<List<string>>() ?? new List<string>(),
                                TokenCount = item["token_count"]?.Value<int>() ?? 0,
                                CreatedAt = DateTime.UtcNow.ToString("o"),
                                SourceFile = filename ?? "pdf_upload",
                            };
                            chunks.Add(chunk);
                        }
                    }

                    var metadata = new PdfParseMetadata();
                    var metaJson = json["metadata"];
                    if (metaJson != null)
                    {
                        metadata.Filename = metaJson["filename"]?.ToString() ?? "";
                        metadata.TotalPages = metaJson["total_pages"]?.Value<int>() ?? 0;
                        metadata.TotalChunks = metaJson["total_chunks"]?.Value<int>() ?? 0;
                        metadata.ChaptersDetected = metaJson["chapters_detected"]?.Value<int>() ?? 0;
                        metadata.ProcessingTimeSeconds = metaJson["processing_time_seconds"]?.Value<float>() ?? 0f;
                    }

                    Debug.Log($"[PdfProcessingClient] Received {chunks.Count} chunks from {metadata.TotalPages} pages ({metadata.ChaptersDetected} chapters) in {metadata.ProcessingTimeSeconds:F1}s");
                    onSuccess?.Invoke(chunks, metadata);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Failed to parse service response: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Metadata about a parsed PDF returned by the service.
    /// </summary>
    [Serializable]
    public class PdfParseMetadata
    {
        public string Filename;
        public int TotalPages;
        public int TotalChunks;
        public int ChaptersDetected;
        public float ProcessingTimeSeconds;
    }
}
