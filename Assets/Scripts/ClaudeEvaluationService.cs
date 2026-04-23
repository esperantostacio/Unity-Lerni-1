using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace MedicalExam
{
    /// <summary>
    /// Calls the Anthropic Claude API for final exam evaluation.
    /// Drop this component on the same GameObject as FineTunedGPT4EvaluationService,
    /// then assign it there and enable useClaudeForEvaluation.
    /// </summary>
    public class ClaudeEvaluationService : MonoBehaviour
    {
        private const string API_URL          = "https://api.anthropic.com/v1/messages";
        private const string ANTHROPIC_VERSION = "2023-06-01";

        [Header("Claude API")]
        [Tooltip("Your Anthropic API key (sk-ant-...).")]
        [SerializeField] private string apiKey = "";

        [Tooltip("Model ID. Default: claude-sonnet-4-6 (best rubric adherence + JSON reliability).")]
        [SerializeField] private string model = "claude-sonnet-4-6";

        [Tooltip("Max output tokens. 2048 is enough for the 5-criteria JSON block.")]
        [SerializeField] private int maxTokens = 2048;

        [Tooltip("HTTP timeout seconds.")]
        [SerializeField] private int timeoutSeconds = 60;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(apiKey);

        /// <summary>
        /// Sends a system + user prompt to Claude and returns the raw text response.
        /// The caller (FineTunedGPT4EvaluationService) owns JSON parsing.
        /// </summary>
        public void Evaluate(
            string systemPrompt,
            string userPrompt,
            Action<string> onSuccess,
            Action<string> onError)
        {
            if (!IsConfigured)
            {
                onError?.Invoke("[Claude] API key is not set. Assign it in ClaudeEvaluationService.");
                return;
            }

            StartCoroutine(SendRequest(systemPrompt, userPrompt, onSuccess, onError));
        }

        private IEnumerator SendRequest(
            string systemPrompt,
            string userPrompt,
            Action<string> onSuccess,
            Action<string> onError)
        {
            var body = new JObject
            {
                ["model"]      = model,
                ["max_tokens"] = maxTokens,
                ["system"]     = systemPrompt,
                ["messages"]   = new JArray
                {
                    new JObject { ["role"] = "user", ["content"] = userPrompt }
                }
            };

            byte[] raw = Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));

            using var req = new UnityWebRequest(API_URL, "POST");
            req.uploadHandler   = new UploadHandlerRaw(raw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",      "application/json");
            req.SetRequestHeader("x-api-key",         apiKey);
            req.SetRequestHeader("anthropic-version", ANTHROPIC_VERSION);
            req.timeout = Mathf.Clamp(timeoutSeconds, 10, 120);

            Debug.Log($"[Claude] Sending evaluation request | model={model} | maxTokens={maxTokens}");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                string errBody = req.downloadHandler?.text ?? "";
                string msg = $"[Claude] HTTP {req.responseCode}: {req.error}. {errBody}";
                Debug.LogError(msg);
                onError?.Invoke(msg);
                yield break;
            }

            try
            {
                var resp = JObject.Parse(req.downloadHandler.text);
                string text = resp["content"]?[0]?["text"]?.ToString()?.Trim();

                if (string.IsNullOrWhiteSpace(text))
                {
                    onError?.Invoke("[Claude] Response had no content text.");
                    yield break;
                }

                Debug.Log($"[Claude] Response received ({text.Length} chars).");
                onSuccess?.Invoke(text);
            }
            catch (Exception e)
            {
                onError?.Invoke($"[Claude] Parse error: {e.Message}");
            }
        }
    }
}
