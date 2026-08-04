using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MedicalExam
{
    /// <summary>
    /// Evaluates medical exam performance using fine-tuned GPT-4 model.
    /// Receives conversation transcript and realtime observations, returns structured JSON evaluation.
    /// </summary>
    public class FineTunedGPT4EvaluationService : MonoBehaviour
    {

      


        [Header("Configuration")]
        [SerializeField] private OpenAI.OpenAIConfig openAIConfig;
        [SerializeField] private bool useFineTunedModel = true;

        [Header("Fine-tuned Model Routing")]
        [Tooltip("Fine-tuned model ID used for question planning (the first AI). If empty, falls back to OpenAIConfig.fineTunedModelId.")]
        [SerializeField] private string plannerFineTunedModelId = "";

        [Tooltip("Fine-tuned model ID used for Doctor-to-Doctor final evaluation. If empty, falls back to OpenAIConfig.fineTunedModelId.")]
        [SerializeField] private string evalDoctorToDoctorFineTunedModelId = "";

        [Tooltip("Fine-tuned model ID used for Doctor-to-Patient final evaluation. If empty, falls back to OpenAIConfig.fineTunedModelId.")]
        [SerializeField] private string evalDoctorToPatientFineTunedModelId = "";

        [Header("Prompt Sources")]
        [Tooltip("If enabled, planning prompts can be overridden by RemotePromptManager keys (finetune.plan.*).")]
        [SerializeField] private bool useRemotePlannerPrompts = true;

        [Tooltip("If enabled, planning prompts are taken from the Inspector fields below (highest priority).")]
        [SerializeField] private bool useInspectorPlannerPrompts = false;

        [TextArea(2, 10)]
        [FormerlySerializedAs("inspectorPlanSystemPrompt")]
        [SerializeField] private string promptToFinetuned1System = "";

        [TextArea(2, 14)]
        [FormerlySerializedAs("inspectorPlanUserTemplate")]
        [SerializeField] private string promptToFinetuned1User = "";

        [Tooltip("If enabled, evaluation system prompts can be overridden by RemotePromptManager keys (finetune.eval.system.*).")]
        [SerializeField] private bool useRemoteEvaluatorSystemPrompts = true;

        [Tooltip("If enabled, evaluation system prompts are taken from the Inspector fields below (highest priority).")]
        [SerializeField] private bool useInspectorEvaluatorSystemPrompts = false;

        [TextArea(2, 10)]
        [FormerlySerializedAs("inspectorEvalSystemPromptDoctorToDoctor")]
        [SerializeField] private string promptToFeedbackFinetunedDoctor = "";

        [TextArea(2, 10)]
        [FormerlySerializedAs("inspectorEvalSystemPromptDoctorToPatient")]
        [SerializeField] private string promptToFeedbackFinetunedPatient = "";
        




        [Header("Claude Evaluation (replaces GPT-4o for final eval)")]
        [Tooltip("When enabled, final evaluation (D2D + D2P) is sent to Claude instead of GPT-4o.")]
        [SerializeField] private bool useClaudeForEvaluation = false;

        [Tooltip("ClaudeEvaluationService component. Assign the same GameObject's component here.")]
        [SerializeField] private ClaudeEvaluationService claudeService;

        [Header("Networking")]
        [Tooltip("HTTP timeout (seconds) for OpenAI requests. Prevents long hangs on slow networks.")]
        [SerializeField] private int httpTimeoutSeconds = 30;

        private const string OPENAI_API_URL = "https://api.openai.com/v1/chat/completions";

        private static string ApplyPlanPlaceholders(
            string template,
            MedicalExamScenario scenario,
            ExamEvaluation.RoleType roleType,
            string language)
        {
            if (string.IsNullOrWhiteSpace(template)) return template;

            string scenarioName = scenario ? scenario.scenarioName : "Unknown";
            string topic = scenario ? scenario.medicalTopic : "Unknown";
            string context = scenario ? (scenario.scenarioContext ?? "") : "";
            int durationMin = scenario ? scenario.examDurationMinutes : 5;

            string roleText = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                ? "DoctorToDoctor"
                : "DoctorToPatient";

            return template
                .Replace("{SCENARIO_NAME}", scenarioName)
                .Replace("{TOPIC}", topic)
                .Replace("{CONTEXT}", context)
                .Replace("{DURATION_MIN}", durationMin.ToString())
                .Replace("{ROLE_TYPE}", roleText)
                .Replace("{LANGUAGE}", language ?? "Deutsch");
        }

        private string ResolvePlanSystemPrompt()
        {
            if (useInspectorPlannerPrompts && !string.IsNullOrWhiteSpace(promptToFinetuned1System))
                return promptToFinetuned1System;

            if (useRemotePlannerPrompts)
                return RemotePromptManager.Get("finetune.plan.system", "");

            return "";
        }

        private string ResolvePlanUserTemplate(string localFallback)
        {
            if (useInspectorPlannerPrompts && !string.IsNullOrWhiteSpace(promptToFinetuned1User))
                return promptToFinetuned1User;

            if (useRemotePlannerPrompts)
                return RemotePromptManager.Get("finetune.plan.user_template", localFallback);

            return localFallback;
        }

        private string ResolveEvaluatorSystemPrompt(ExamEvaluation.RoleType roleType)
        {
            if (useInspectorEvaluatorSystemPrompts)
            {
                string preferred = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                    ? promptToFeedbackFinetunedDoctor
                    : promptToFeedbackFinetunedPatient;

                if (!string.IsNullOrWhiteSpace(preferred))
                    return preferred;
            }

            if (useRemoteEvaluatorSystemPrompts)
            {
                string key = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                    ? "finetune.eval.system.doctor_to_doctor"
                    : "finetune.eval.system.doctor_to_patient";

                return RemotePromptManager.Get(key, "");
            }

            return "";
        }

        private string ResolvePlannerModel(string baseModel)
        {
            if (!useFineTunedModel) return baseModel;
            if (!string.IsNullOrWhiteSpace(plannerFineTunedModelId)) return plannerFineTunedModelId.Trim();
            if (!string.IsNullOrWhiteSpace(openAIConfig?.fineTunedModelId)) return openAIConfig.fineTunedModelId.Trim();
            return baseModel;
        }

        private string ResolveEvaluatorModel(string baseModel, ExamEvaluation.RoleType roleType)
        {
            if (!useFineTunedModel) return baseModel;

            string preferred = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                ? evalDoctorToDoctorFineTunedModelId
                : evalDoctorToPatientFineTunedModelId;

            if (!string.IsNullOrWhiteSpace(preferred)) return preferred.Trim();
            if (!string.IsNullOrWhiteSpace(openAIConfig?.fineTunedModelId)) return openAIConfig.fineTunedModelId.Trim();
            return baseModel;
        }
        
        // Evaluation result structure matching JSON output
        [Serializable]
        public class EvaluationResponse
        {
            public float terminologie;
            public float verstaendlichkeit;
            public float aussprache;
            public int overallScore;
            public string generalFeedback;
            public string terminologieFeedback;
            public string verstaendlichkeitFeedback;

            // Correct field name
            [JsonProperty("ausspracheFeedback")]
            public string ausspracheFeedback;

            // Backward-compat typo (some prompts/models may still emit this)
            [JsonProperty("ausspracheeFeedback")]
            public string ausspracheeFeedback;
        }

        /// <summary>
        /// Generates a short context + suggested questions to seed the realtime agent.
        /// Uses the fine-tuned model when configured.
        /// </summary>
        public void GenerateConversationPlan(
            MedicalExamScenario scenario,
            ExamEvaluation.RoleType roleType,
            System.Action<string> onSuccess,
            System.Action<string> onError)
        {
            if (!openAIConfig)
            {
                onError?.Invoke("OpenAI config not assigned");
                return;
            }

            StartCoroutine(GenerateConversationPlanCoroutine(scenario, roleType, onSuccess, onError));
        }

        private IEnumerator GenerateConversationPlanCoroutine(
            MedicalExamScenario scenario,
            ExamEvaluation.RoleType roleType,
            System.Action<string> onSuccess,
            System.Action<string> onError)
        {
            string baseModel = !string.IsNullOrWhiteSpace(openAIConfig.chatModelId)
                ? openAIConfig.chatModelId
                : "gpt-4o-mini";

            string modelToUse = ResolvePlannerModel(baseModel);

            string roleText = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                ? "Doctor-to-doctor (colleague/examiner asks for structured case presentation and clinical reasoning)."
                : "Doctor-to-patient (patient answers naturally; doctor performs anamnesis).";

            string language = "English";
            if (scenario != null && scenario.language == ExamLanguage.Deutsch)
                language = "Deutsch";

            string localPlanUserTemplate = $@"Create a conversation plan for this scenario.

SCENARIO NAME: {(scenario ? scenario.scenarioName : "Unknown")}
TOPIC: {(scenario ? scenario.medicalTopic : "Unknown")}
CONTEXT: {(scenario ? scenario.scenarioContext : "")}
EXAM DURATION MINUTES: {(scenario ? scenario.examDurationMinutes : 5)}
ROLE TYPE: {roleText}
LANGUAGE: {language}

Remember: return ONLY plain text in the requested structure.";

            string planSystem = ResolvePlanSystemPrompt();
            string planUserMessage = ResolvePlanUserTemplate(localPlanUserTemplate);
            planSystem = ApplyPlanPlaceholders(planSystem, scenario, roleType, language);
            planUserMessage = ApplyPlanPlaceholders(planUserMessage, scenario, roleType, language);

            yield return SendChatCompletionRequest(
                modelToUse,
                planSystem,
                planUserMessage,
                temperature: 0.6f,
                maxTokens: 700,
                onSuccess: onSuccess,
                onError: err =>
                {
                    // If a fine-tuned model returns 404 (model not found), retry once with base model.
                    if (useFineTunedModel && modelToUse.StartsWith("ft:") && err.Contains("HTTP 404"))
                    {
                        Debug.LogWarning($"[FineTunedGPT4] Plan model not found (404). Retrying with base model '{baseModel}'.");
                        StartCoroutine(SendChatCompletionRequest(
                            baseModel,
                            planSystem,
                            planUserMessage,
                            temperature: 0.6f,
                            maxTokens: 700,
                            onSuccess: onSuccess,
                            onError: onError
                        ));
                        return;
                    }

                    onError?.Invoke(err);
                }
            );
        }

        /// <summary>
        /// Sends conversation transcript to fine-tuned GPT-4 for evaluation
        /// </summary>
        public void EvaluateConversation(
            string conversationTranscript,
            string realtimeObservations,
            MedicalExamScenario scenario,
            ExamEvaluation.RoleType roleType,
            string questionPlan, // Added this parameter
            System.Action<EvaluationResponse> onSuccess,
            System.Action<string> onError)
        {
            if (!openAIConfig)
            {
                onError("OpenAI config not assigned");
                return;
            }

            Debug.Log($"[FineTunedGPT4] Preparing evaluation request | transcriptChars={conversationTranscript?.Length ?? 0} | observationsChars={realtimeObservations?.Length ?? 0} | scenario={(scenario ? scenario.scenarioName : "Unknown")}");
            string transcriptPreview = string.IsNullOrEmpty(conversationTranscript) ? "<empty>" : conversationTranscript.Substring(0, Math.Min(200, conversationTranscript.Length));
            Debug.Log($"[FineTunedGPT4] Transcript preview (first 200 chars): {transcriptPreview}");

            StartCoroutine(EvaluateConversationCoroutine(
                conversationTranscript,
                realtimeObservations,
                scenario,
                roleType,
                questionPlan, // Pass it down
                onSuccess,
                onError
            ));
        }



        private IEnumerator EvaluateConversationCoroutine(
            string conversationTranscript,
            string realtimeObservations,
            MedicalExamScenario scenario,
            ExamEvaluation.RoleType roleType,
            string questionPlan, // Added this parameter
            System.Action<EvaluationResponse> onSuccess,
            System.Action<string> onError)
        {
            // Falls back to OpenAI's flagship reasoning model when no fine-tuned/config model
            // is set — evaluation quality matters more here than cost or latency.
            string baseModel = !string.IsNullOrWhiteSpace(openAIConfig.chatModelId)
                ? openAIConfig.chatModelId
                : "gpt-5.6-sol";

            string modelToUse = ResolveEvaluatorModel(baseModel, roleType);

            // Build ONLY what we actually want to send to the evaluator.
            // Put the full transcript first (with optional Whisper transcript already embedded by the caller).
            // Keep the JSON schema instruction ONLY once (in the system prompt).
            var sbUser = new StringBuilder(8_192);
            sbUser.AppendLine("CONVERSATION TRANSCRIPT:");
            sbUser.AppendLine(conversationTranscript ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(realtimeObservations))
            {
                sbUser.AppendLine();
                sbUser.AppendLine("REALTIME FEEDBACK (secondary input):");
                sbUser.AppendLine(realtimeObservations);
            }

            if (!string.IsNullOrWhiteSpace(questionPlan))
            {
                sbUser.AppendLine();
                sbUser.AppendLine("CONVERSATION PLAN USED:");
                sbUser.AppendLine(questionPlan);
            }

            string userMessage = sbUser.ToString().Trim();

            // Choose evaluator system prompt (Inspector/Remote/Local fallback).
            string evalSystem = ResolveEvaluatorSystemPrompt(roleType) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(evalSystem))
                evalSystem = "You are a strict medical oral-exam evaluator.";

            bool retriedWithoutResponseFormat = false;

            System.Action<string> handleAssistantMessage = assistantMessage =>
            {
                try
                {
                    Debug.Log($"[FineTunedGPT4] Raw assistant message (preview 300 chars): {assistantMessage.Substring(0, Math.Min(300, assistantMessage.Length))}");

                    string json = ExtractFirstJsonObject(assistantMessage);
                    if (string.IsNullOrWhiteSpace(json) || json == "{}")
                        throw new Exception("No JSON object found in model output");

                    // Robust parse: support both flat schema and nested {score,feedback} schema.
                    var obj = JObject.Parse(json);

                    float ParseScoreToken(JToken tok)
                    {
                        if (tok == null) return 0f;
                        if (tok.Type == JTokenType.Float || tok.Type == JTokenType.Integer) return tok.Value<float>();
                        if (tok.Type == JTokenType.Object)
                        {
                            var scoreTok = tok["score"] ?? tok["value"] ?? tok["rating"];
                            if (scoreTok != null && (scoreTok.Type == JTokenType.Float || scoreTok.Type == JTokenType.Integer))
                                return scoreTok.Value<float>();
                        }
                        var s = tok.Type == JTokenType.String ? tok.Value<string>() : tok.ToString();
                        if (string.IsNullOrWhiteSpace(s)) return 0f;
                        s = s.Trim();
                        var slash = s.IndexOf('/');
                        if (slash > 0) s = s.Substring(0, slash);
                        s = s.Replace(',', '.');
                        return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
                    }

                    string ParseFeedbackToken(JToken tok)
                    {
                        if (tok == null || tok.Type != JTokenType.Object) return string.Empty;
                        var s = (string)(tok["feedback"] ?? tok["comment"] ?? tok["why"] ?? tok["reason"] ?? "");
                        return string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
                    }

                    (float score, string feedback) ParseSkill(string key)
                    {
                        var node = obj[key];
                        float score = ParseScoreToken(node);
                        string fb = ParseFeedbackToken(node);
                        if (string.IsNullOrWhiteSpace(fb))
                        {
                            if (key == "aussprache")
                                fb = (string)(obj["ausspracheFeedback"] ?? obj["ausspracheeFeedback"] ?? "");
                            else
                                fb = (string)(obj[key + "Feedback"] ?? "");
                        }
                        return (score, string.IsNullOrWhiteSpace(fb) ? string.Empty : fb.Trim());
                    }

                    (float t, string tfb) = ParseSkill("terminologie");
                    (float v, string vfb) = ParseSkill("verstaendlichkeit");
                    (float a, string afb) = ParseSkill("aussprache");

                    int overall = 0;
                    var overallTok = obj["overallScore"] ?? obj["overall"] ?? obj["score"];
                    if (overallTok != null)
                    {
                        if (overallTok.Type == JTokenType.Integer) overall = overallTok.Value<int>();
                        else overall = Mathf.RoundToInt(ParseScoreToken(overallTok));
                    }

                    string general = (string)(obj["generalFeedback"] ?? obj["overallFeedback"] ?? obj["feedback"] ?? obj["feedbackText"] ?? "");
                    general = string.IsNullOrWhiteSpace(general) ? string.Empty : general.Trim();

                    var evaluation = new EvaluationResponse
                    {
                        terminologie = t,
                        verstaendlichkeit = v,
                        aussprache = a,
                        overallScore = overall,
                        generalFeedback = general,
                        terminologieFeedback = tfb,
                        verstaendlichkeitFeedback = vfb,
                        ausspracheFeedback = afb,
                        ausspracheeFeedback = string.IsNullOrWhiteSpace(afb) ? (string)(obj["ausspracheeFeedback"] ?? "") : ""
                    };

                    Debug.Log($"[FineTunedGPT4] Evaluation parsed - T={evaluation.terminologie}, V={evaluation.verstaendlichkeit}, A={evaluation.aussprache}, Overall={evaluation.overallScore}");
                    onSuccess?.Invoke(evaluation);
                }
                catch (Exception e)
                {
                    Debug.LogError($"[FineTunedGPT4] Parse error: {e.Message}");
                    onError?.Invoke($"JSON parse error: {e.Message}");
                }
            };

            // ── Claude path ──────────────────────────────────────────────────────────
            if (useClaudeForEvaluation && claudeService != null && claudeService.IsConfigured)
            {
                Debug.Log("[FineTunedGPT4] Routing evaluation to Claude (useClaudeForEvaluation=true).");
                bool done = false;
                claudeService.Evaluate(
                    evalSystem,
                    userMessage,
                    text => { handleAssistantMessage(text); done = true; },
                    err  => { onError?.Invoke(err);         done = true; });
                yield return new WaitUntil(() => done);
                yield break;
            }
            // ────────────────────────────────────────────────────────────────────────

            Debug.Log($"[FineTunedGPT4] Sending request to model={modelToUse} | temp=0.2 | max_tokens=1500 | response_format=json_object");

            yield return SendChatCompletionRequest(
                modelToUse,
                evalSystem,
                userMessage,
                temperature: 0.2f,
                maxTokens: 1500,
                onSuccess: handleAssistantMessage,
                onError: err =>
                {
                    // Some endpoints/models may not support response_format; retry once without it.
                    if (!retriedWithoutResponseFormat && (err.Contains("response_format") || err.Contains("Unrecognized request argument")))
                    {
                        retriedWithoutResponseFormat = true;
                        Debug.LogWarning("[FineTunedGPT4] API rejected response_format. Retrying evaluation without response_format (schema still enforced).");
                        StartCoroutine(SendChatCompletionRequest(
                            modelToUse,
                            evalSystem,
                            userMessage,
                            temperature: 0.2f,
                            maxTokens: 1500,
                            onSuccess: handleAssistantMessage,
                            onError: onError,
                            forceJsonObject: false
                        ));
                        return;
                    }

                    if (useFineTunedModel && modelToUse.StartsWith("ft:") && err.Contains("HTTP 404"))
                    {
                        Debug.LogWarning($"[FineTunedGPT4] Eval model not found (404). Retrying with base model '{baseModel}'.");
                        StartCoroutine(SendChatCompletionRequest(
                            baseModel,
                            evalSystem,
                            userMessage,
                            temperature: 0.2f,
                            maxTokens: 1500,
                            onSuccess: handleAssistantMessage,
                            onError: onError,
                            forceJsonObject: true
                        ));
                        return;
                    }

                    onError?.Invoke(err);
                }
            ,
            forceJsonObject: true
            );
        }

        private IEnumerator SendChatCompletionRequest(
            string model,
            string systemPrompt,
            string userPrompt,
            float temperature,
            int maxTokens,
            System.Action<string> onSuccess,
            System.Action<string> onError,
            bool forceJsonObject = false)
        {
            Debug.Log($"[FineTunedGPT4] HTTP request start | model={model} | temp={temperature} | max_tokens={maxTokens}");

            object responseFormat = forceJsonObject
                ? new { type = "json_object" }
                : null;

            var requestData = new
            {
                model = model,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                temperature = temperature,
                max_tokens = maxTokens,
                response_format = responseFormat
            };

            string jsonRequest = JsonConvert.SerializeObject(requestData);
            byte[] postData = Encoding.UTF8.GetBytes(jsonRequest);

            using (UnityWebRequest request = new UnityWebRequest(OPENAI_API_URL, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(postData);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {openAIConfig.apiKey}");
                request.timeout = Mathf.Clamp(httpTimeoutSeconds, 5, 120);

                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string responseText = request.downloadHandler.text;
                        JObject jsonResponse = JObject.Parse(responseText);
                        string assistantMessage = jsonResponse["choices"]?[0]?["message"]?["content"]?.ToString()?.Trim();

                        if (string.IsNullOrWhiteSpace(assistantMessage))
                        {
                            onError?.Invoke($"Empty response from model '{model}'.");
                            yield break;
                        }

                        onSuccess?.Invoke(assistantMessage);
                    }
                    catch (Exception e)
                    {
                        onError?.Invoke($"Response parse error from model '{model}': {e.Message}");
                    }
                }
                else
                {
                    long code = request.responseCode;
                    string body = request.downloadHandler != null ? request.downloadHandler.text : "";
                    string bodyPreview = string.IsNullOrEmpty(body) ? "<empty>" : (body.Length > 800 ? body.Substring(0, 800) + "..." : body);
                    Debug.LogError($"[FineTunedGPT4] HTTP {code} error calling {OPENAI_API_URL} | model={model} | unityError={request.error}\nBody: {bodyPreview}");
                    onError?.Invoke($"API Error: HTTP {code} ({request.error}). Body: {bodyPreview}");
                }
            }
        }

        private static string ExtractFirstJsonObject(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "{}";
            text = text.Trim();

            if (text.StartsWith("{") && text.EndsWith("}"))
                return text;

            int start = text.IndexOf('{');
            if (start < 0) return "{}";

            int depth = 0;
            bool inString = false;
            bool escape = false;

            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape)
                    {
                        escape = false;
                        continue;
                    }
                    if (c == '\\') { escape = true; continue; }
                    if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1);
                }
            }

            return "{}";
        }


    }
}
