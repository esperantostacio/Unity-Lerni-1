using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json.Linq;

namespace MedicalExam
{
    /// <summary>
    /// Handles sending Whisper transcripts to GPT-4 and processing the evaluation response.
    /// Also integrates with ElevenLabsTTS for text-to-speech voice responses.
    /// </summary>
    public class GptAndWhisper : MonoBehaviour
    {
        [Tooltip("API key for GPT-4 or OpenAI service.")]
        [SerializeField] private string apiKey;

        [Tooltip("Model to use for the conversation loop (e.g. gpt-4o, gpt-4o-mini).")]
        [SerializeField] private string chatModel = "gpt-4o";

        [Tooltip("Model to use for the final evaluation (e.g. gpt-4, gpt-4o).")]
        [SerializeField] private string evaluationModel = "gpt-4o";

        [Tooltip("Prompt to prepend to the transcript for evaluation.")]
        [TextArea(3, 12)]
        [SerializeField] private string evaluationPrompt;

        [Header("TTS Integration")]
        [Tooltip("ElevenLabsTTS component for voice synthesis")]
        [SerializeField] private ElevenLabsTTS ttsComponent;

        [Tooltip("Enable TTS for AI responses")]
        [SerializeField] private bool enableTTS = true;

        [Header("Per-turn Pronunciation Tracking")]
        [Tooltip("If enabled, the model returns JSON with spokenText + pronunciation meta. The app speaks ONLY spokenText and stores the meta.")]
        [SerializeField] private bool structuredRoleplayResponses = false;

        [Tooltip("If true, we still ask the model to produce pronunciation notes even though it only has the transcript (best-effort).")]
        [SerializeField] private bool enablePronunciationMeta = true;

        public string LastPronunciationSummary { get; private set; }
        public string[] LastPronunciationMistakes { get; private set; } = Array.Empty<string>();
        public float LastPronunciationScore { get; private set; }

        [Serializable]
        public class PronunciationTurnFeedback
        {
            public float score;
            public string[] mistakes;
            public string feedback;
            public string[] grammarIssues;
            public string[] wordChoiceIssues;
            public string[] fluencyIssues;
            public string summary;
            public float confidence;
        }

        [Header("Roleplay Conversation Memory")]
        [Tooltip("If enabled, we keep a running message history so the role prompt is not forgotten.")]
        [SerializeField] private bool useConversationHistory = true;

        [Tooltip("How many recent non-system messages to keep (user+assistant). Higher = more stable roleplay but more tokens.")]
        [SerializeField, Range(2, 40)] private int maxHistoryMessages = 16;

        [Tooltip("Optional short reminder appended as an additional system message each turn to reduce role drift.")]
        [TextArea(2, 8)]
        [SerializeField] private string perTurnRoleReminder =
            "Bleibe strikt in deiner Rolle. Antworte als Patientin/Patient in Alltagssprache, kurz (1–3 Sätze). Stelle keine Gegenfragen außer bei Unverständnis (\"Was bedeutet das?\").";

        private readonly List<ChatMessage> _conversationHistory = new List<ChatMessage>();
        private string _baseSystemPrompt;

        private const string StructuredResponseInstruction =
            "OUTPUT FORMAT (IMPORTANT):\n" +
            "- Respond with ONLY a single JSON object (no markdown, no extra text).\n" +
            "- The app will speak ONLY the 'spokenText' field.\n" +
            "- Do NOT mention pronunciation feedback in spokenText.\n" +
            "\n" +
            "Schema:\n" +
            "{\n" +
            "  \"spokenText\": \"<what you would normally say next, in character>\",\n" +
            "  \"nextQuestion\": \"<optional next question to continue the exam naturally; can be empty>\",\n" +
            "  \"pronunciation\": {\n" +
            "    \"score\": <0-5 number>,\n" +
            "    \"mistakes\": [\"word/phrase\", ...],\n" +
            "    \"summary\": \"<short private notes>\"\n" +
            "  }\n" +
            "}";

        public void ResetConversation(string baseSystemPrompt)
        {
            if (structuredRoleplayResponses)
                _baseSystemPrompt = (baseSystemPrompt ?? string.Empty) + "\n\n" + StructuredResponseInstruction;
            else
                _baseSystemPrompt = baseSystemPrompt;

            _conversationHistory.Clear();
            ClearLastPronunciationMeta();
        }

        private void ClearLastPronunciationMeta()
        {
            LastPronunciationSummary = null;
            LastPronunciationMistakes = Array.Empty<string>();
            LastPronunciationScore = 0f;
        }

        public IEnumerator EvaluateTranscript(string transcript, Action<string> onResult, Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("API key is missing.");
                yield break;
            }
            if (string.IsNullOrWhiteSpace(transcript))
            {
                onError?.Invoke("Transcript is empty.");
                yield break;
            }

            string prompt = string.IsNullOrWhiteSpace(evaluationPrompt)
                ? "Du bist ein erfahrener medizinischer Prüfer. Analysiere das folgende Gespräch und liefere eine strenge, ehrliche Bewertung als reines JSON-Objekt mit genau diesen Schlüsseln: 'terminologie' (0-5), 'verstaendlichkeit' (0-5), 'aussprache' (0-5), 'overallScore' (0-100), 'feedback' (ausführlicher Text). Antworte AUSSCHLIESSLICH mit dem JSON-Objekt in deutscher Sprache.\n\nTRANSKRIPT:\n" + transcript
                : evaluationPrompt + "\n\nTRANSKRIPT:\n" + transcript;

            string url = "https://api.openai.com/v1/chat/completions";

            var chatRequest = new ChatRequest
            {
                // Default to gpt-4o if not set, for speed.
                model = !string.IsNullOrWhiteSpace(evaluationModel) ? evaluationModel : "gpt-4o",
                messages = new ChatMessage[]
                {
                    new ChatMessage { role = "system", content = "You are a medical exam evaluator. Provide honest, strict evaluation scores in JSON format." },
                    new ChatMessage { role = "user", content = prompt }
                },
                temperature = 0.3f,
                max_tokens = 1000
            };

            string jsonBody = JsonUtility.ToJson(chatRequest);
            Debug.Log("[GptAndWhisper] [SEND EVAL] " + jsonBody);

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            using (var request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    string responseText = request.downloadHandler.text;
                    Debug.Log("[GptAndWhisper] [RESPONSE RAW] " + responseText);
                    try
                    {
                        var respObj = JsonUtility.FromJson<OpenAIResponse>(responseText);
                        if (respObj != null && respObj.choices != null && respObj.choices.Length > 0 && respObj.choices[0].message != null)
                        {
                            string assistantContent = respObj.choices[0].message.content;
                            Debug.Log("[GptAndWhisper] [RESPONSE] Assistant: " + assistantContent);
                            onResult?.Invoke(assistantContent);
                        }
                        else
                        {
                            Debug.LogWarning("[GptAndWhisper] Unexpected response shape, passing raw text.");
                            onResult?.Invoke(responseText);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[WhisperGptEvaluator] Failed to parse response JSON: " + ex.Message + ". Passing raw response.");
                        onResult?.Invoke(responseText);
                    }
                }
                else
                {
                    string errorMsg = $"Error: {request.error}\nResponse: {request.downloadHandler.text}";
                    onError?.Invoke(errorMsg);
                }
            }
        }

        [Serializable]
        private class ChatMessage
        {
            public string role;
            public string content;
        }

        [Serializable]
        private class ChatRequest
        {
            public string model;
            public ChatMessage[] messages;
            public float temperature;
            public int max_tokens;
        }

        [Serializable]
        private class OpenAIResponse
        {
            public Choice[] choices;
        }

        [Serializable]
        private class Choice
        {
            public Message message;
        }

        [Serializable]
        private class Message
        {
            public string role;
            public string content;
        }



        /// <summary>
        /// Sends a lightweight chat prompt (not an evaluation) to the model. Used for start confirmation / simple checks.
        /// </summary>
        public IEnumerator SendPromptToGpt(string prompt, Action<string> onResult, Action<string> onError, string systemMessage = null, bool usePromptAsSystemOnly = false)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("API key is missing.");
                yield break;
            }

            if (usePromptAsSystemOnly)
            {
                // Treat this as the start of a new roleplay run. Persist it so later turns can include it.
                if (!string.IsNullOrWhiteSpace(prompt))
                    ResetConversation(prompt);

                // Send only the provided editor prompt as the system message (no helper system/user message).
                // UPDATED: We use recordInHistory=true so the INITIAL AI greeting is stored in history.
                // However, we pass userContent=null so no user message is added yet.
                yield return SendChatRequest(null, prompt, 0.0f, 60, onResult, onError, "[SEND PROMPT]", "[RESPONSE PROMPT]", recordInHistory: true);
            }
            else
            {
                string system = string.IsNullOrWhiteSpace(systemMessage)
                    ? "You are a helpful assistant. If you understand the instructions, reply with a single word: 'yes' or 'no'."
                    : systemMessage;

                yield return SendChatRequest(prompt, system, 0.0f, 60, onResult, onError, "[SEND PROMPT]", "[RESPONSE PROMPT]", recordInHistory: false);
            }
        }

        /// <summary>
        /// Sends a user transcript for normal conversational reply (not final evaluation).
        /// </summary>
        public IEnumerator SendConversationMessage(string userMessage, Action<string> onResult, Action<string> onError, string systemMessage = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("API key is missing.");
                yield break;
            }

            // System prompt precedence:
            // 1) Explicit systemMessage parameter (if provided)
            // 2) Persisted base system prompt from the Start Conversation prompt
            // 3) Fallback generic roleplay instruction
            string system = !string.IsNullOrWhiteSpace(systemMessage)
                ? systemMessage
                : (!string.IsNullOrWhiteSpace(_baseSystemPrompt)
                    ? _baseSystemPrompt
                    : "You are a roleplay assistant responding as the patient/doctor according to prior instructions. Reply conversationally and concisely.");

            // If the caller provides a custom system message, but structured output is enabled,
            // we still need to append the output-format instruction.
            if (structuredRoleplayResponses && !string.IsNullOrWhiteSpace(systemMessage))
                system = systemMessage + "\n\n" + StructuredResponseInstruction;

            yield return SendChatRequest(userMessage, system, 0.7f, 200, onResult, onError, "[SEND CONV]", "[RESPONSE CONV]", recordInHistory: true);
        }

        /// <summary>
        /// Dedicated per-turn pronunciation tracking. This is separate from the roleplay conversation.
        /// Input is the Whisper transcript (best-effort). Returns JSON-only feedback; not spoken.
        /// </summary>
        public IEnumerator EvaluatePronunciationTurn(string userTranscript, Action<PronunciationTurnFeedback> onResult, Action<string> onError)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("API key is missing.");
                yield break;
            }

            if (string.IsNullOrWhiteSpace(userTranscript))
            {
                onError?.Invoke("User transcript is empty.");
                yield break;
            }

            // CHECK 1: Detect if transcript is German or contains German text
            // This prevents scoring non-German speech as correct German pronunciation
            bool isGermanText = IsGermanText(userTranscript);
            if (!isGermanText)
            {
                // User spoke non-German → immediate 0.0 score penalty
                var germanOnlyFeedback = new PronunciationTurnFeedback
                {
                    score = 0.0f,
                    mistakes = new[] { "Non-German language detected" },
                    feedback = "Muss auf Deutsch sprechen! (Must speak in German!)"
                };
                onResult?.Invoke(germanOnlyFeedback);
                yield break; // Exit without calling GPT
            }

            const string system =
                "You are a strict German speech-quality coach for medical oral exams. " +
                "You receive only an ASR transcript (Whisper), so you must give best-effort feedback based on the written words. " +
                "Focus on likely pronunciation problems, grammar mistakes, poor word choice, and fluency issues implied by the transcript. " +
                "Return ONLY one JSON object (no markdown, no extra text) with exactly these keys: " +
                "score (0-5 number), mistakes (array of strings), feedback (string, German), grammarIssues (array of strings), wordChoiceIssues (array of strings), fluencyIssues (array of strings), summary (string, German), confidence (0-1 number). " +
                "IMPORTANT: Do not roleplay. Do not ask questions. Keep feedback short and actionable.";

            PronunciationTurnFeedback parsed = null;
            yield return SendChatRequest(
                userTranscript,
                system,
                temperature: 0.2f,
                maxTokens: 220,
                onResult: content =>
                {
                    if (TryParsePronunciationTurnFeedback(content, out var res))
                        parsed = res;
                    onResult?.Invoke(parsed ?? new PronunciationTurnFeedback {
                        score = 0f,
                        mistakes = Array.Empty<string>(),
                        grammarIssues = Array.Empty<string>(),
                        wordChoiceIssues = Array.Empty<string>(),
                        fluencyIssues = Array.Empty<string>(),
                        feedback = (content ?? string.Empty).Trim(),
                        summary = string.Empty,
                        confidence = 0f
                    });
                },
                onError: onError,
                sendTag: "[SEND PRON]",
                responseTag: "[RESPONSE PRON]",
                recordInHistory: false
            );
        }

        /// <summary>
        /// Detects if the text contains German language markers.
        /// Checks for German-specific characters, umlauts, and common German word patterns.
        /// Returns true if text is predominantly German, false if non-German detected.
        /// </summary>
        private static bool IsGermanText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // German-specific characters and umlauts
            int germanCharCount = 0;
            int nonLatinCount = 0;
            int latinCount = 0;

            foreach (char c in text)
            {
                // Count German-specific characters: ä, ö, ü, ß
                if (c == 'ä' || c == 'ö' || c == 'ü' || c == 'ß' || 
                    c == 'Ä' || c == 'Ö' || c == 'Ü')
                {
                    germanCharCount += 2; // Weight German chars higher
                }
                // Check for Latin alphabet
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    latinCount++;
                }
                // Check for non-Latin scripts (Arabic, Cyrillic, etc.)
                else if ((c >= '\u0600' && c <= '\u06FF') ||  // Arabic
                         (c >= '\u0400' && c <= '\u04FF') ||  // Cyrillic (Serbian, Russian, etc.)
                         (c >= '\u0370' && c <= '\u03FF'))    // Greek
                {
                    nonLatinCount++;
                }
            }

            // If we detected non-Latin characters (Arabic, Cyrillic, Greek), immediately fail
            if (nonLatinCount > 0)
            {
                Debug.Log($"[WhisperGptEvaluator] Detected non-Latin script ({nonLatinCount} chars): '{text.Substring(0, Math.Min(50, text.Length))}'");
                return false;
            }

            // Check for Latin alphabet presence (German uses Latin alphabet)
            // If very little Latin is present, it's not German
            if (latinCount < 3)
            {
                Debug.Log($"[WhisperGptEvaluator] Insufficient Latin characters ({latinCount}): '{text.Substring(0, Math.Min(50, text.Length))}'");
                return false;
            }

            // If we found German-specific characters, it's definitely German
            if (germanCharCount > 0)
            {
                Debug.Log($"[WhisperGptEvaluator] German text detected (umlauts found): '{text.Substring(0, Math.Min(50, text.Length))}'");
                return true;
            }

            // Distinctly German words that don't commonly appear in English, French, Spanish, or Italian.
            // Words like "der", "die", "das", "ist", "ein", "und" are excluded because they appear
            // in Dutch, Danish, or are close enough to cause false-positives with English speakers.
            var germanWords = new[]
            {
                "ich", "nicht", "auch", "noch", "habe", "hatte", "gibt", "geht", "seit", "oder",
                "nach", "sehr", "aber", "weil", "dass", "hier", "kein", "keine", "mehr", "wenn",
                "dann", "bitte", "danke", "arzt", "krank", "schmerz", "fieber", "husten", "kopf",
                "bauch", "medizin", "behandlung", "patient", "haben", "werden", "symptom"
            };

            string lowerText = text.ToLower();
            int matchCount = 0;
            foreach (var word in germanWords)
            {
                // Simple word boundary check
                if (lowerText.Contains(" " + word + " ") || lowerText.StartsWith(word + " ") || lowerText.EndsWith(" " + word))
                    matchCount++;
            }

            // Require at least 2 distinct German word matches to reduce false-positives.
            bool hasGermanWords = matchCount >= 2;
            if (hasGermanWords)
            {
                Debug.Log($"[WhisperGptEvaluator] German words detected ({matchCount} matches): '{text.Substring(0, Math.Min(50, text.Length))}'");
            }
            else
            {
                Debug.Log($"[WhisperGptEvaluator] Insufficient German markers ({matchCount}/2 required): '{text.Substring(0, Math.Min(50, text.Length))}'");
            }

            return hasGermanWords;
        }


        private IEnumerator SendChatRequest(string userContent, string systemContent, float temperature, int maxTokens, Action<string> onResult, Action<string> onError, string sendTag, string responseTag, bool recordInHistory)
        {
            string url = "https://api.openai.com/v1/chat/completions";

            // Build messages dynamically.
            // IMPORTANT: chat completions are stateless; we must re-send role/system + recent history each request.
            List<ChatMessage> messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(systemContent))
                messages.Add(new ChatMessage { role = "system", content = systemContent });

            if (useConversationHistory && recordInHistory)
            {
                if (!string.IsNullOrWhiteSpace(perTurnRoleReminder))
                    messages.Add(new ChatMessage { role = "system", content = perTurnRoleReminder });

                // Append recent history (already excludes system messages).
                int keep = Mathf.Max(0, maxHistoryMessages);
                int start = Mathf.Max(0, _conversationHistory.Count - keep);
                for (int i = start; i < _conversationHistory.Count; i++)
                    messages.Add(_conversationHistory[i]);
            }

            if (!string.IsNullOrEmpty(userContent))
                messages.Add(new ChatMessage { role = "user", content = userContent });

            var chatRequest = new ChatRequest
            {
                // Default to gpt-4o if not set, for much lower latency than gpt-4.
                model = !string.IsNullOrWhiteSpace(chatModel) ? chatModel : "gpt-4o",
                messages = messages.ToArray(),
                temperature = temperature,
                max_tokens = maxTokens
            };

            string jsonBody = JsonUtility.ToJson(chatRequest);
            Debug.Log("[GptAndWhisper] " + sendTag + " " + jsonBody);

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            using (var request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    string responseText = request.downloadHandler.text;
                    Debug.Log("[GptAndWhisper] " + responseTag + " " + responseText);
                    try
                    {
                        var respObj = JsonUtility.FromJson<OpenAIResponse>(responseText);
                        if (respObj != null && respObj.choices != null && respObj.choices.Length > 0 && respObj.choices[0].message != null)
                        {
                            string assistantContent = respObj.choices[0].message.content;
                            Debug.Log("[GptAndWhisper] " + responseTag + " Assistant: " + assistantContent);

                            string spokenForApp = assistantContent;
                            if (structuredRoleplayResponses && recordInHistory)
                            {
                                if (TryParseStructuredRoleplayResponse(assistantContent, out var spokenText, out var nextQuestion, out var pronScore, out var pronMistakes, out var pronSummary))
                                {
                                    // Store the hidden meta for later use (do not speak it).
                                    if (enablePronunciationMeta)
                                    {
                                        LastPronunciationScore = pronScore;
                                        LastPronunciationMistakes = pronMistakes ?? Array.Empty<string>();
                                        LastPronunciationSummary = pronSummary;
                                    }
                                    else
                                    {
                                        ClearLastPronunciationMeta();
                                    }

                                    spokenForApp = CombineSpokenText(spokenText, nextQuestion);
                                }
                                else
                                {
                                    // If parsing failed, keep the raw response but clear meta to avoid stale data.
                                    ClearLastPronunciationMeta();
                                }
                            }

                            if (useConversationHistory && recordInHistory)
                            {
                                if (!string.IsNullOrEmpty(userContent))
                                    _conversationHistory.Add(new ChatMessage { role = "user", content = userContent });
                                // IMPORTANT: store only the spoken text in history so the model doesn't learn/echo JSON.
                                if (!string.IsNullOrEmpty(spokenForApp))
                                    _conversationHistory.Add(new ChatMessage { role = "assistant", content = spokenForApp });
                            }

                            onResult?.Invoke(spokenForApp);
                        }
                        else
                        {
                    Debug.LogWarning($"[GptAndWhisper] Unexpected response shape for {responseTag}, passing raw text.");
                            onResult?.Invoke(responseText);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning("[GptAndWhisper] Failed to parse response JSON: " + ex.Message + ". Passing raw response.");
                        onResult?.Invoke(responseText);
                    }
                }
                else
                {
                    string errorMsg = $"Error: {request.error}\nResponse: {request.downloadHandler.text}";
                    onError?.Invoke(errorMsg);
                }
            }
        }

        /// <summary>
        /// Speak text using ElevenLabs TTS with the appropriate voice for the scenario.
        /// </summary>
        public void SpeakResponse(string text, ElevenLabsTTS.ScenarioType scenario)
        {
            if (!enableTTS)
            {
                Debug.Log("[GptAndWhisper] TTS is disabled.");
                return;
            }

            if (ttsComponent == null)
            {
                Debug.LogWarning("[GptAndWhisper] ElevenLabsTTS component not assigned. Cannot speak response.");
                return;
            }

            Debug.Log($"[GptAndWhisper] Speaking response with TTS ({scenario}): {text}");
            ttsComponent.Speak(text, scenario);
        }

        private static string EscapeJsonString(string str)
        {
            return str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        }

        private static string CombineSpokenText(string spokenText, string nextQuestion)
        {
            string a = (spokenText ?? string.Empty).Trim();
            string b = (nextQuestion ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(a)) return b;
            if (string.IsNullOrEmpty(b)) return a;
            return a + "\n\n" + b;
        }

        private static bool TryParseStructuredRoleplayResponse(
            string content,
            out string spokenText,
            out string nextQuestion,
            out float pronunciationScore,
            out string[] pronunciationMistakes,
            out string pronunciationSummary)
        {
            spokenText = null;
            nextQuestion = null;
            pronunciationScore = 0f;
            pronunciationMistakes = Array.Empty<string>();
            pronunciationSummary = null;

            if (string.IsNullOrWhiteSpace(content))
                return false;

            string trimmed = content.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                return false;

            JObject obj;
            try { obj = JObject.Parse(trimmed); }
            catch { return false; }

            spokenText = (string)(obj["spokenText"] ?? obj["replyText"] ?? obj["text"] ?? "");
            nextQuestion = (string)(obj["nextQuestion"] ?? obj["next"] ?? "");

            var pron = obj["pronunciation"] as JObject;
            if (pron != null)
            {
                pronunciationScore = (float?)pron["score"] ?? 0f;
                pronunciationSummary = (string)(pron["summary"] ?? "");
                var arr = pron["mistakes"] as JArray;
                if (arr != null)
                {
                    var list = new List<string>();
                    foreach (var t in arr)
                    {
                        var s = (string)t;
                        if (!string.IsNullOrWhiteSpace(s))
                            list.Add(s.Trim());
                    }
                    pronunciationMistakes = list.ToArray();
                }
            }

            // Consider it valid if at least one of spokenText/nextQuestion exists.
            return !string.IsNullOrWhiteSpace(spokenText) || !string.IsNullOrWhiteSpace(nextQuestion);
        }

        private static bool TryParsePronunciationTurnFeedback(string content, out PronunciationTurnFeedback result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(content))
                return false;

            string trimmed = content.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                return false;

            JObject obj;
            try { obj = JObject.Parse(trimmed); }
            catch { return false; }

            string[] ParseStringArray(string key)
            {
                var arr = obj[key] as JArray;
                if (arr == null)
                    return Array.Empty<string>();

                var list = new List<string>();
                foreach (var t in arr)
                {
                    var s = (string)t;
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s.Trim());
                }
                return list.ToArray();
            }

            var mistakes = ParseStringArray("mistakes");
            var grammarIssues = ParseStringArray("grammarIssues");
            var wordChoiceIssues = ParseStringArray("wordChoiceIssues");
            var fluencyIssues = ParseStringArray("fluencyIssues");

            result = new PronunciationTurnFeedback
            {
                score = (float?)obj["score"] ?? 0f,
                mistakes = mistakes,
                feedback = ((string)obj["feedback"] ?? string.Empty).Trim(),
                grammarIssues = grammarIssues,
                wordChoiceIssues = wordChoiceIssues,
                fluencyIssues = fluencyIssues,
                summary = ((string)obj["summary"] ?? string.Empty).Trim(),
                confidence = Mathf.Clamp01((float?)obj["confidence"] ?? 0f),
            };
            return true;
        }
    }
}
