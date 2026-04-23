using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

namespace MedicalExam.RAG
{
    /// <summary>
    /// Service for converting text to embeddings using OpenAI API.
    /// Supports batch embedding requests for efficiency.
    /// </summary>
    public class EmbeddingService : MonoBehaviour
    {
        [SerializeField] private string apiKey;
        [SerializeField] private string embeddingModel = "text-embedding-3-small";
        [SerializeField] private bool useLocalMockEmbeddings = true; // For testing without API calls

        private const string OPENAI_EMBEDDING_URL = "https://api.openai.com/v1/embeddings";

        public event System.Action<string> OnEmbeddingError;

        /// <summary>
        /// Requests an embedding for a single text string.
        /// </summary>
        public IEnumerator GetEmbedding(string text, System.Action<float[]> onSuccess, System.Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                onError?.Invoke("Text is empty");
                yield break;
            }

            if (useLocalMockEmbeddings)
            {
                // For testing: generate deterministic mock embedding
                yield return new WaitForEndOfFrame();
                onSuccess?.Invoke(GenerateMockEmbedding(text));
                yield break;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var errorMsg = "OpenAI API key not configured";
                onError?.Invoke(errorMsg);
                OnEmbeddingError?.Invoke(errorMsg);
                yield break;
            }

            var requestBody = new
            {
                input = text,
                model = embeddingModel
            };

            var jsonBody = JsonConvert.SerializeObject(requestBody);
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (UnityWebRequest request = new UnityWebRequest(OPENAI_EMBEDDING_URL, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var response = JsonConvert.DeserializeObject<EmbeddingResponse>(request.downloadHandler.text);
                        if (response?.data != null && response.data.Length > 0)
                        {
                            onSuccess?.Invoke(response.data[0].embedding);
                        }
                        else
                        {
                            onError?.Invoke("No embedding data in response");
                        }
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke($"Failed to parse embedding response: {ex.Message}");
                    }
                }
                else
                {
                    var errorMsg = $"Embedding request failed: {request.error}";
                    onError?.Invoke(errorMsg);
                    OnEmbeddingError?.Invoke(errorMsg);
                }
            }
        }

        /// <summary>
        /// Requests embeddings for multiple texts (batch mode).
        /// More efficient than individual requests.
        /// </summary>
        public IEnumerator GetEmbeddingsBatch(List<string> texts, System.Action<List<float[]>> onSuccess, System.Action<string> onError)
        {
            if (texts == null || texts.Count == 0)
            {
                onError?.Invoke("Text list is empty");
                yield break;
            }

            if (useLocalMockEmbeddings)
            {
                yield return new WaitForEndOfFrame();
                var mockEmbeddings = new List<float[]>();
                foreach (var text in texts)
                {
                    mockEmbeddings.Add(GenerateMockEmbedding(text));
                }
                onSuccess?.Invoke(mockEmbeddings);
                yield break;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                var errorMsg = "OpenAI API key not configured";
                onError?.Invoke(errorMsg);
                OnEmbeddingError?.Invoke(errorMsg);
                yield break;
            }

            var requestBody = new
            {
                input = texts,
                model = embeddingModel
            };

            var jsonBody = JsonConvert.SerializeObject(requestBody);
            var bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (UnityWebRequest request = new UnityWebRequest(OPENAI_EMBEDDING_URL, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var response = JsonConvert.DeserializeObject<EmbeddingResponse>(request.downloadHandler.text);
                        if (response?.data != null && response.data.Length > 0)
                        {
                            var embeddings = new List<float[]>();
                            foreach (var item in response.data)
                            {
                                embeddings.Add(item.embedding);
                            }
                            onSuccess?.Invoke(embeddings);
                        }
                        else
                        {
                            onError?.Invoke("No embedding data in batch response");
                        }
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke($"Failed to parse batch embedding response: {ex.Message}");
                    }
                }
                else
                {
                    var errorMsg = $"Batch embedding request failed: {request.error}";
                    onError?.Invoke(errorMsg);
                    OnEmbeddingError?.Invoke(errorMsg);
                }
            }
        }

        /// <summary>
        /// Generates a mock embedding for testing (deterministic based on text).
        /// </summary>
        private float[] GenerateMockEmbedding(string text)
        {
            // Create a small but meaningful mock embedding (1536 dimensions like real embeddings)
            var embedding = new float[1536];
            
            int hash = text.GetHashCode();
            var random = new System.Random(hash);
            
            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] = (float)random.NextGaussian();
            }

            // Normalize
            float magnitude = 0f;
            for (int i = 0; i < embedding.Length; i++)
            {
                magnitude += embedding[i] * embedding[i];
            }
            magnitude = (float)Math.Sqrt(magnitude);

            for (int i = 0; i < embedding.Length; i++)
            {
                embedding[i] /= magnitude;
            }

            return embedding;
        }

        [System.Serializable]
        private class EmbeddingResponse
        {
            [JsonProperty("object")]
            public string @object { get; set; }

            [JsonProperty("data")]
            public EmbeddingData[] data { get; set; }

            [JsonProperty("model")]
            public string model { get; set; }

            [JsonProperty("usage")]
            public EmbeddingUsage usage { get; set; }
        }

        [System.Serializable]
        private class EmbeddingData
        {
            [JsonProperty("object")]
            public string @object { get; set; }

            [JsonProperty("index")]
            public int index { get; set; }

            [JsonProperty("embedding")]
            public float[] embedding { get; set; }
        }

        [System.Serializable]
        private class EmbeddingUsage
        {
            [JsonProperty("prompt_tokens")]
            public int prompt_tokens { get; set; }

            [JsonProperty("total_tokens")]
            public int total_tokens { get; set; }
        }

        public void SetAPIKey(string key)
        {
            apiKey = key;
        }

        public void SetMockMode(bool useMock)
        {
            useLocalMockEmbeddings = useMock;
        }
    }

    /// <summary>
    /// Extension for Random class to generate Gaussian distribution
    /// </summary>
    public static class RandomExtensions
    {
        public static double NextGaussian(this System.Random random, double mean = 0.0, double stdDev = 1.0)
        {
            double u1 = random.NextDouble();
            double u2 = random.NextDouble();
            double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            return mean + stdDev * randStdNormal;
        }
    }
}
