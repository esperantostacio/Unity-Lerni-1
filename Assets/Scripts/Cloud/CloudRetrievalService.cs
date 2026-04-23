using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace MedicalExam.Cloud
{
    [Serializable]
    public class RetrievalRequest
    {
        public string query;
        public int topK = 3;
        public float threshold = 0.5f;
    }

    [Serializable]
    public class SearchResult
    {
        public string documentId;
        public string documentName;
        public string fileName;
        public int chunkIndex;
        public int? pageFrom;
        public int? pageTo;
        public string content;
        public float similarity;
    }

    [Serializable]
    public class RetrievalResponse
    {
        public SearchResult[] results;
        public string query;
        public int count;
    }

    /// <summary>
    /// Cloud-based RAG retrieval service for medical evaluation
    /// Calls Next.js API to retrieve relevant PDF chunks for exam context
    /// </summary>
    public class CloudRetrievalService : MonoBehaviour
    {
        [Header("API Configuration")]
        [SerializeField] private string apiBaseUrl = "https://your-app.vercel.app";
        [SerializeField] private float timeoutSeconds = 5f;
        
        [Header("Retrieval Settings")]
        [SerializeField] private int defaultTopK = 3;
        [SerializeField] private float defaultThreshold = 0.5f;
        
        [Header("Debug")]
        [SerializeField] private bool enableLogging = true;

        private const string RETRIEVE_ENDPOINT = "/api/rag/retrieve";

        /// <summary>
        /// Retrieve relevant knowledge chunks for a query
        /// </summary>
        public async Task<RetrievalResponse> RetrieveContextAsync(
            string query, 
            int? topK = null, 
            float? threshold = null)
        {
            if (string.IsNullOrEmpty(query))
            {
                LogWarning("Empty query, skipping retrieval");
                return CreateEmptyResponse(query);
            }

            var request = new RetrievalRequest
            {
                query = query,
                topK = topK ?? defaultTopK,
                threshold = threshold ?? defaultThreshold
            };

            string jsonBody = JsonUtility.ToJson(request);
            string url = apiBaseUrl + RETRIEVE_ENDPOINT;

            using (UnityWebRequest webRequest = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
                webRequest.uploadHandler = new UploadHandlerRaw(bodyRaw);
                webRequest.downloadHandler = new DownloadHandlerBuffer();
                webRequest.SetRequestHeader("Content-Type", "application/json");
                webRequest.timeout = (int)timeoutSeconds;

                var operation = webRequest.SendWebRequest();

                // Wait for completion with timeout
                float startTime = Time.time;
                while (!operation.isDone)
                {
                    if (Time.time - startTime > timeoutSeconds)
                    {
                        LogWarning($"Request timed out after {timeoutSeconds}s");
                        return CreateEmptyResponse(query);
                    }
                    await Task.Yield();
                }

                if (webRequest.result == UnityWebRequest.Result.Success)
                {
                    string responseJson = webRequest.downloadHandler.text;
                    var response = JsonUtility.FromJson<RetrievalResponse>(responseJson);
                    
                    Log($"Successfully retrieved {response.count} chunks for query: {TruncateQuery(query)}");
                    
                    return response;
                }
                else
                {
                    LogWarning($"Request failed: {webRequest.error}");
                    return CreateEmptyResponse(query);
                }
            }
        }

        /// <summary>
        /// Format retrieved chunks as context string for prompt injection
        /// </summary>
        public string FormatContextForPrompt(RetrievalResponse response, bool includeCitations = true)
        {
            if (response == null || response.count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("[RETRIEVED KNOWLEDGE FROM MEDICAL GUIDELINES]");
            sb.AppendLine();

            foreach (var result in response.results)
            {
                if (includeCitations)
                {
                    string citation = result.pageFrom.HasValue
                        ? $"{result.documentName} (page {result.pageFrom})"
                        : result.documentName;

                    sb.AppendLine($"Source: {citation} [Relevance: {result.similarity:F2}]");
                }
                
                sb.AppendLine(result.content);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Get a summary of retrieved sources for UI display
        /// </summary>
        public string GetSourcesSummary(RetrievalResponse response)
        {
            if (response == null || response.count == 0)
                return "No sources";

            var sources = new System.Collections.Generic.HashSet<string>();
            foreach (var result in response.results)
            {
                sources.Add(result.documentName);
            }

            return $"{response.count} chunks from {sources.Count} document(s): {string.Join(", ", sources)}";
        }

        private RetrievalResponse CreateEmptyResponse(string query)
        {
            return new RetrievalResponse
            {
                results = new SearchResult[0],
                query = query ?? "",
                count = 0
            };
        }

        private string TruncateQuery(string query, int maxLength = 100)
        {
            if (string.IsNullOrEmpty(query) || query.Length <= maxLength)
                return query;
            
            return query.Substring(0, maxLength) + "...";
        }

        private void Log(string message)
        {
            if (enableLogging)
                Debug.Log($"[CloudRetrieval] {message}");
        }

        private void LogWarning(string message)
        {
            if (enableLogging)
                Debug.LogWarning($"[CloudRetrieval] {message}");
        }

        // Validate configuration on start
        private void Start()
        {
            if (string.IsNullOrEmpty(apiBaseUrl) || apiBaseUrl.Contains("your-app"))
            {
                Debug.LogError("[CloudRetrieval] API Base URL not configured! Set it in the Inspector.");
            }
            else
            {
                Log($"Initialized with API: {apiBaseUrl}");
            }
        }
    }
}
