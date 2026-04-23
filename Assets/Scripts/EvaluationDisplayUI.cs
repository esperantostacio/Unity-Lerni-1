using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Text;
using MedicalExam.RAG;

namespace MedicalExam
{
    /// <summary>
    /// Handles visual display of 5D evaluation results
    /// </summary>
    public class EvaluationDisplayUI : MonoBehaviour
    {
        private const string EvaluationDumpFileName = "LastFinalEvaluationPayload.txt";
        private const string FinalEvaluationRequestDumpFileName = "LastFinalEvaluationRequest.txt";
        private const string FinalEvaluationResponseDumpFileName = "LastFinalEvaluationResponse.txt";
        private const string DefaultEvidenceLogFileName = "medical_exam_evidence_log.txt";

        [Header("OpenAI Config (optional)")]
        [Tooltip("If assigned, uses chatModelId for chat/completions evaluation/parsing calls. If not assigned, defaults to gpt-4o-mini.")]
        [SerializeField] private OpenAI.OpenAIConfig openAIConfig;

        [Header("RAG (Medical Knowledge Context)")]
        [Tooltip("If assigned, retrieves relevant medical knowledge and injects it into the evaluation prompt for better grading.")]
        [SerializeField] private RAGIntegrationHelper ragIntegrationHelper;

       
        
       


        [Header("Result Panels (toggle by role)")]
        [Tooltip("Parent GameObject containing the D2P (5D) result UI. Activated for DoctorToPatient.")]
        [SerializeField] private GameObject d2pResultsPanel;
        [Tooltip("Parent GameObject containing the D2D (3D) result UI. Activated for DoctorToDoctor.")]
        [SerializeField] private GameObject d2dResultsPanel;

        [Header("5D Score Text Fields (X/3) – D2P")]
        [SerializeField] private TextMeshProUGUI kommunikationText;
        [SerializeField] private TextMeshProUGUI hoerverstehenText;
        [SerializeField] private TextMeshProUGUI gespraechsfuehrungText;
        [SerializeField] private TextMeshProUGUI empathieText;
        [SerializeField] private TextMeshProUGUI vollstaendigkeitText;

        [Header("3D Score Text Fields – D2D")]
        [SerializeField] private TextMeshProUGUI sprachlicheAngemessenheitText;   // X/7
        [SerializeField] private TextMeshProUGUI inhaltlicheAngemessenheitText;   // X/3
        [SerializeField] private TextMeshProUGUI malusPatientensicherheitText;    // X/5
        
        [Header("Overall Feedback & Score")]
        [SerializeField] private TextMeshProUGUI feedbackText;

        [Header("Gesamt Score – D2P")]
        [SerializeField] private TextMeshProUGUI totalPointsD2PText;   // X/15 or X/20 display (Doctor-to-Patient)

        [Header("Gesamt Score – D2D")]
        [SerializeField] private TextMeshProUGUI totalPointsD2DText;   // X/10 display (Doctor-to-Doctor)
        
        [Header("Loading Indicator")]
        [SerializeField] private GameObject loadingIndicator;

        [Header("UI Coloring")]
        [Tooltip("If enabled, colors score text (red/yellow/green) based on performance. Disable to keep the prefab/theme colors.")]
        [SerializeField] private bool colorCodeScores = false;

        [Header("Feedback Animation")]
        [Tooltip("Animator to trigger 'flip' animation when evaluation feedback starts displaying.")]
        [SerializeField] private Animator feedbackAnimator;

        private readonly System.Collections.Generic.Dictionary<Graphic, Color> _initialUiColors = new System.Collections.Generic.Dictionary<Graphic, Color>();

        private static string StripEmojis(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return System.Text.RegularExpressions.Regex.Replace(text, @"[\u2600-\u27BF]|[\uD83C][\uDF00-\uDFFF]|[\uD83D][\uDC00-\uDE4F]|[\uD83D][\uDE80-\uDEFF]|[\uD83E][\uDD00-\uDDFF]", "");
        }

        private string GetChatModelId()
        {
            if (openAIConfig != null && !string.IsNullOrWhiteSpace(openAIConfig.chatModelId))
                return openAIConfig.chatModelId.Trim();
            return "gpt-4o-mini";
        }

        private static float ResolveD2PTotalMax(float totalPointsRaw, float kommunikationRaw, float hoerverstehenRaw, float gespraechsfuehrungRaw, float empathieRaw, float vollstaendigkeitRaw)
        {
            // New grid support: 5 criteria up to 3.99 each and total up to 20.
            if (totalPointsRaw > 15.0001f)
                return 20f;

            if (kommunikationRaw > 3.0001f || hoerverstehenRaw > 3.0001f || gespraechsfuehrungRaw > 3.0001f || empathieRaw > 3.0001f || vollstaendigkeitRaw > 3.0001f)
                return 20f;

            return 15f;
        }

        private static float ResolveD2PPassThreshold(float totalMax)
        {
            return totalMax >= 20f ? 12f : 9f;
        }

        private static float ResolveD2PPerSkillMax(float totalMax)
        {
            return totalMax >= 20f ? 4f : 3f;
        }

        private static void ApplyD2PConservativeScoringGuards(ExamEvaluation evaluation)
        {
            if (evaluation == null)
                return;

            float perSkillSum = Mathf.Max(0f,
                evaluation.kommunikation +
                evaluation.hoerverstehen +
                evaluation.gespraechsfuehrung +
                evaluation.empathie +
                evaluation.vollstaendigkeit);

            if (evaluation.totalPoints > perSkillSum + 0.01f)
            {
                Debug.LogWarning($"[EvaluationDisplayUI] D2P guard: totalPoints ({evaluation.totalPoints:F2}) exceeded per-skill sum ({perSkillSum:F2}). Clamping.");
                evaluation.totalPoints = perSkillSum;
            }

            float passThreshold = ResolveD2PPassThreshold(evaluation.totalPointsMax > 0f ? evaluation.totalPointsMax : 15f);
            evaluation.passed = evaluation.totalPoints >= passThreshold;
        }

        private static string BuildUserFocusedEvaluationEvidence(string conversationTranscript, string realtimeAIFeedback)
        {
            var sb = new StringBuilder(4096);
            sb.AppendLine("EVALUATION RULES (MUST FOLLOW):");
            sb.AppendLine("1) Score ONLY what the USER said.");
            sb.AppendLine("2) AI lines are CONTEXT ONLY and must not increase the score.");
            sb.AppendLine("3) Do not reward AI quality, prompt quality, or case instructions.");
            sb.AppendLine("4) If user performance is weak or nonsensical, scores must be low accordingly.");
            sb.AppendLine();

            var userOnly = new StringBuilder(2048);
            var turns = new StringBuilder(4096);

            int userTurnIndex = 0;
            string lastAiLine = string.Empty;
            var lines = (conversationTranscript ?? string.Empty).Replace("\r", string.Empty).Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i]?.Trim();
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("AI:", StringComparison.OrdinalIgnoreCase))
                {
                    lastAiLine = line.Length > 3 ? line.Substring(3).Trim() : string.Empty;
                    continue;
                }

                if (!line.StartsWith("User:", StringComparison.OrdinalIgnoreCase))
                    continue;

                string userLine = line.Length > 5 ? line.Substring(5).Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(userLine))
                    continue;

                userTurnIndex++;
                userOnly.Append("UserTurn ").Append(userTurnIndex).Append(": ").AppendLine(userLine);

                turns.Append("Turn ").Append(userTurnIndex).AppendLine(":");
                turns.Append("AI (context only): ").AppendLine(string.IsNullOrWhiteSpace(lastAiLine) ? "[none]" : lastAiLine);
                turns.Append("User (score this): ").AppendLine(userLine);
                turns.AppendLine();
            }

            if (userTurnIndex == 0)
            {
                sb.AppendLine("USER_ONLY_TRANSCRIPT_FOR_SCORING:");
                sb.AppendLine("[NO USER UTTERANCES FOUND]");
            }
            else
            {
                sb.AppendLine("USER_ONLY_TRANSCRIPT_FOR_SCORING:");
                sb.Append(userOnly);
                sb.AppendLine();
                sb.AppendLine("TURN_CONTEXT (AI shown for context only, DO NOT SCORE AI):");
                sb.Append(turns);
            }

            sb.AppendLine();
            sb.AppendLine("REALTIME_AI_FEEDBACK_CONTEXT (optional, do not let this override user evidence):");
            sb.AppendLine(string.IsNullOrWhiteSpace(realtimeAIFeedback) ? "[none]" : realtimeAIFeedback);

            return sb.ToString();
        }

        private static string ParseStringArrayFieldAnyKey(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null || keys.Length == 0)
                return string.Empty;

            foreach (var key in keys)
            {
                var token = GetTokenByKey(obj, key);
                if (token == null)
                    continue;

                if (token.Type == JTokenType.Array)
                {
                    var lines = new List<string>();
                    foreach (var item in token)
                    {
                        string value = item?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            lines.Add("- " + value);
                    }

                    if (lines.Count > 0)
                        return string.Join("\n", lines);

                    continue;
                }

                string single = token.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(single))
                    return single;
            }

            return string.Empty;
        }

        private static JToken GetD2DCriterionToken(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null)
                return null;

            var criteriaObj = GetTokenByKey(obj, "criteria") as JObject;
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                var fromCriteria = GetTokenByKey(criteriaObj, key);
                if (fromCriteria != null)
                    return fromCriteria;

                var fromRoot = GetTokenByKey(obj, key);
                if (fromRoot != null)
                    return fromRoot;
            }

            return null;
        }

        private static (float score, string feedback) ParseScoreAndFeedback(JToken token)
        {
            if (token == null)
                return (0f, string.Empty);

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                return (token.Value<float>(), string.Empty);

            if (token is JObject node)
            {
                float score = ParseFloatFieldAnyKey(node, "score", "value", "points");
                string feedback = ParseStringFieldAnyKey(node, "feedback", "comment", "why", "reason", "evidence");
                return (score, feedback);
            }

            return (0f, token.ToString().Trim());
        }

        private static string BuildD2DFiveCriteriaFeedback(ExamEvaluation evaluation)
        {
            var sections = new List<string>();

            if (!string.IsNullOrWhiteSpace(evaluation.overallFeedback))
                sections.Add(evaluation.overallFeedback.Trim());

            var criteriaLines = new List<string>();
            criteriaLines.Add($"Inhalt: {evaluation.d2dContent:F2} / 3.99" + (string.IsNullOrWhiteSpace(evaluation.d2dContentFeedback) ? string.Empty : $" - {evaluation.d2dContentFeedback}"));
            criteriaLines.Add($"Interaktion: {evaluation.d2dConversation:F2} / 3.99" + (string.IsNullOrWhiteSpace(evaluation.d2dConversationFeedback) ? string.Empty : $" - {evaluation.d2dConversationFeedback}"));
            criteriaLines.Add($"Wortschatz: {evaluation.d2dVocabulary:F2} / 3.99" + (string.IsNullOrWhiteSpace(evaluation.d2dVocabularyFeedback) ? string.Empty : $" - {evaluation.d2dVocabularyFeedback}"));
            criteriaLines.Add($"Grammatik: {evaluation.d2dGrammar:F2} / 3.99" + (string.IsNullOrWhiteSpace(evaluation.d2dGrammarFeedback) ? string.Empty : $" - {evaluation.d2dGrammarFeedback}"));
            criteriaLines.Add($"Aussprache und Intonation: {evaluation.d2dPronunciation:F2} / 3.99" + (string.IsNullOrWhiteSpace(evaluation.d2dPronunciationFeedback) ? string.Empty : $" - {evaluation.d2dPronunciationFeedback}"));
            sections.Add(string.Join("\n", criteriaLines));

            if (!string.IsNullOrWhiteSpace(evaluation.d2dStrengthsSummary))
                sections.Add("Stärken:\n" + evaluation.d2dStrengthsSummary.Trim());

            if (!string.IsNullOrWhiteSpace(evaluation.d2dAreasForImprovementSummary))
                sections.Add("Verbesserungsbereiche:\n" + evaluation.d2dAreasForImprovementSummary.Trim());

            if (!string.IsNullOrWhiteSpace(evaluation.d2dCriticalErrorsSummary))
                sections.Add("Kritische Fehler:\n" + evaluation.d2dCriticalErrorsSummary.Trim());

            return string.Join("\n\n", sections).Trim();
        }

        private static bool TryBuildD2DEvaluation(JObject obj, string scenarioName, ExamEvaluation.RoleType roleType, string conversationTranscript, out ExamEvaluation evaluation)
        {
            evaluation = null;
            if (obj == null)
                return false;

            var contentToken = GetD2DCriterionToken(obj, "content");
            var conversationToken = GetD2DCriterionToken(obj, "conversation", "interaction", "conversationalAbility");
            var vocabularyToken = GetD2DCriterionToken(obj, "vocabulary", "expressiveness", "expressivenessVocabulary");
            var grammarToken = GetD2DCriterionToken(obj, "grammar");
            var pronunciationToken = GetD2DCriterionToken(obj, "pronunciation", "pronunciationIntonation");

            bool hasFiveCriteria = contentToken != null || conversationToken != null || vocabularyToken != null || grammarToken != null || pronunciationToken != null || GetTokenByKey(obj, "totalScore") != null || GetTokenByKey(obj, "finalVerdict") != null;

            if (hasFiveCriteria)
            {
                (float contentScore, string contentFeedback) = ParseScoreAndFeedback(contentToken);
                (float conversationScore, string conversationFeedback) = ParseScoreAndFeedback(conversationToken);
                (float vocabularyScore, string vocabularyFeedback) = ParseScoreAndFeedback(vocabularyToken);
                (float grammarScore, string grammarFeedback) = ParseScoreAndFeedback(grammarToken);
                (float pronunciationScore, string pronunciationFeedback) = ParseScoreAndFeedback(pronunciationToken);

                float totalScore = ParseFloatFieldAnyKey(obj, "totalScore", "total", "totalPoints", "gesamt", "gesamtpunkte");
                if (totalScore <= 0f)
                    totalScore = contentScore + conversationScore + vocabularyScore + grammarScore + pronunciationScore;

                float passingThreshold = ParseFloatFieldAnyKey(obj, "passingThreshold", "passThreshold");
                if (passingThreshold <= 0f)
                    passingThreshold = 12f;

                string overallFeedback = ParseStringFieldAnyKey(obj, "overallFeedback", "summary", "generalFeedback", "feedbackText", "feedback");
                string finalVerdict = ParseStringFieldAnyKey(obj, "finalVerdict", "verdict");
                string strengths = ParseStringArrayFieldAnyKey(obj, "strengths");
                string areas = ParseStringArrayFieldAnyKey(obj, "areasForImprovement", "areas_for_improvement");
                string criticalErrors = ParseStringArrayFieldAnyKey(obj, "criticalErrors", "critical_errors");

                evaluation = new ExamEvaluation
                {
                    scenarioName = scenarioName,
                    roleType = roleType,
                    conversationTranscript = conversationTranscript,
                    usesD2DFiveCriteria = true,
                    d2dContent = Mathf.Clamp(contentScore, 0f, 3.99f),
                    d2dConversation = Mathf.Clamp(conversationScore, 0f, 3.99f),
                    d2dVocabulary = Mathf.Clamp(vocabularyScore, 0f, 3.99f),
                    d2dGrammar = Mathf.Clamp(grammarScore, 0f, 3.99f),
                    d2dPronunciation = Mathf.Clamp(pronunciationScore, 0f, 3.99f),
                    d2dContentFeedback = contentFeedback,
                    d2dConversationFeedback = conversationFeedback,
                    d2dVocabularyFeedback = vocabularyFeedback,
                    d2dGrammarFeedback = grammarFeedback,
                    d2dPronunciationFeedback = pronunciationFeedback,
                    d2dTotalMax = 20f,
                    d2dPassingThreshold = passingThreshold,
                    gesamtD2D = Mathf.Clamp(totalScore, 0f, 20f),
                    totalPoints = Mathf.Clamp(totalScore, 0f, 20f),
                    totalPointsMax = 20f,
                    overallFeedback = overallFeedback,
                    d2dVerdict = finalVerdict,
                    d2dStrengthsSummary = strengths,
                    d2dAreasForImprovementSummary = areas,
                    d2dCriticalErrorsSummary = criticalErrors
                };

                var passedToken = GetTokenByKey(obj, "passed");
                if (passedToken != null && passedToken.Type == JTokenType.Boolean)
                    evaluation.passed = passedToken.Value<bool>();
                else if (!string.IsNullOrWhiteSpace(finalVerdict))
                    evaluation.passed = finalVerdict.Trim().Equals("PASSED", StringComparison.OrdinalIgnoreCase);
                else
                    evaluation.passed = evaluation.totalPoints >= evaluation.d2dPassingThreshold;

                evaluation.feedbackText = BuildD2DFiveCriteriaFeedback(evaluation);
                return true;
            }

            (float sprachScore, string sprachFb) = ParseScoreAndFeedback(obj, "sprachlicheAngemessenheit");
            (float inhaltScore, string inhaltFb) = ParseScoreAndFeedback(obj, "inhaltlicheAngemessenheit");
            (float malusScore, string malusFb) = ParseScoreAndFeedback(obj, "malusPatientensicherheit");

            if (sprachScore <= 0f && inhaltScore <= 0f && GetTokenByKey(obj, "gesamt") == null)
                return false;

            float gesamt = ParseFloatFieldAnyKey(obj, "gesamt", "gesamtpunkte", "total", "totalPoints");
            if (gesamt <= 0f)
                gesamt = Mathf.Max(0f, sprachScore + inhaltScore - malusScore);
            string overallFeedbackD2D = ParseStringFieldAnyKey(obj, "overallFeedback", "generalFeedback", "feedbackText", "feedback", "gesamtfeedback");

            evaluation = new ExamEvaluation
            {
                scenarioName = scenarioName,
                roleType = roleType,
                conversationTranscript = conversationTranscript,
                sprachlicheAngemessenheit = Mathf.Clamp(sprachScore, 0f, 7f),
                inhaltlicheAngemessenheit = Mathf.Clamp(inhaltScore, 0f, 3f),
                malusPatientensicherheit = Mathf.Clamp(malusScore, 0f, 5f),
                sprachlicheAngemessenheitFeedback = sprachFb,
                inhaltlicheAngemessenheitFeedback = inhaltFb,
                malusPatientensicherheitFeedback = malusFb,
                gesamtD2D = Mathf.Clamp(gesamt, 0f, 10f),
                totalPoints = Mathf.Clamp(gesamt, 0f, 10f),
                d2dTotalMax = 10f,
                d2dPassingThreshold = 6f,
                overallFeedback = overallFeedbackD2D,
                feedbackText = overallFeedbackD2D
            };

            var legacyPassedToken = GetTokenByKey(obj, "passed");
            if (legacyPassedToken != null && legacyPassedToken.Type == JTokenType.Boolean)
                evaluation.passed = legacyPassedToken.Value<bool>();
            else
                evaluation.passed = evaluation.gesamtD2D >= evaluation.d2dPassingThreshold;

            return true;
        }

        private void CacheInitialUiColors()
        {
            void CacheGraphic(Graphic graphic)
            {
                if (graphic == null) return;
                if (_initialUiColors.ContainsKey(graphic)) return;
                _initialUiColors.Add(graphic, graphic.color);
            }

           

         
          
            CacheGraphic(kommunikationText);
            CacheGraphic(hoerverstehenText);
            CacheGraphic(gespraechsfuehrungText);
            CacheGraphic(empathieText);
            CacheGraphic(vollstaendigkeitText);
            CacheGraphic(sprachlicheAngemessenheitText);
            CacheGraphic(inhaltlicheAngemessenheitText);
            CacheGraphic(malusPatientensicherheitText);
            CacheGraphic(totalPointsD2PText);
            CacheGraphic(totalPointsD2DText);
        }

        private void RestoreInitialUiColors()
        {
            foreach (var kvp in _initialUiColors)
            {
                if (kvp.Key == null) continue;
                kvp.Key.color = kvp.Value;
            }
        }

        private static void TryWriteTextFile(string absolutePath, string content)
        {
            try
            {
                var dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(absolutePath, content ?? string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[EvaluationDisplayUI] Could not write debug dump to '{absolutePath}': {ex.Message}");
            }
        }

        private static void TryAppendEvidenceLogSection(string title, string content)
        {
            if (string.IsNullOrWhiteSpace(title))
                title = "SECTION";

            try
            {
                string persistentPath = Path.Combine(Application.persistentDataPath, DefaultEvidenceLogFileName);
                var dir = Path.GetDirectoryName(persistentPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var sw = new StreamWriter(persistentPath, append: true, Encoding.UTF8))
                {
                    sw.WriteLine("--- " + title.Trim() + " ---");
                    if (!string.IsNullOrEmpty(content))
                        sw.WriteLine(content);
                    sw.WriteLine("--- END " + title.Trim() + " ---\n");
                }
            }
            catch { }

#if UNITY_EDITOR
            try
            {
                string assetsPath = Path.Combine(Application.dataPath, DefaultEvidenceLogFileName);
                var dir = Path.GetDirectoryName(assetsPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using (var sw = new StreamWriter(assetsPath, append: true, Encoding.UTF8))
                {
                    sw.WriteLine("--- " + title.Trim() + " ---");
                    if (!string.IsNullOrEmpty(content))
                        sw.WriteLine(content);
                    sw.WriteLine("--- END " + title.Trim() + " ---\n");
                }
            }
            catch { }
#endif
        }

        private static void DumpFinalEvaluationRequest(
            string url,
            string model,
            string systemPrompt,
            string userPrompt,
            string jsonBody,
            string scenarioName)
        {
            var sb = new StringBuilder(16_384);
            sb.AppendLine("=== FINAL EVALUATOR REQUEST DUMP ===");
            sb.AppendLine($"UTC: {DateTime.UtcNow:O}");
            sb.AppendLine($"URL: {url}");
            sb.AppendLine($"Model: {model}");
            sb.AppendLine($"Scenario: {scenarioName}");
            sb.AppendLine();

            sb.AppendLine("--- SYSTEM PROMPT ---");
            sb.AppendLine(systemPrompt ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine("--- USER PROMPT ---");
            sb.AppendLine(userPrompt ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine("--- REQUEST JSON BODY (NO API KEY) ---");
            sb.AppendLine(jsonBody ?? string.Empty);

            string persistentPath = Path.Combine(Application.persistentDataPath, FinalEvaluationRequestDumpFileName);
            TryWriteTextFile(persistentPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote final evaluator request dump: {persistentPath}");

#if UNITY_EDITOR
            string assetsPath = Path.Combine(Application.dataPath, FinalEvaluationRequestDumpFileName);
            TryWriteTextFile(assetsPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote final evaluator request dump (Assets): {assetsPath}");
#endif

            TryAppendEvidenceLogSection("FINAL_EVALUATOR_REQUEST", sb.ToString());
        }

        private static void DumpFinalEvaluationResponse(
            string model,
            string scenarioName,
            string rawHttpResponse,
            string extractedAssistantContent,
            string extractedJsonObject,
            string parseError)
        {
            var sb = new StringBuilder(24_576);
            sb.AppendLine("=== FINAL EVALUATOR RESPONSE DUMP ===");
            sb.AppendLine($"UTC: {DateTime.UtcNow:O}");
            sb.AppendLine($"Model: {model}");
            sb.AppendLine($"Scenario: {scenarioName}");
            if (!string.IsNullOrWhiteSpace(parseError))
                sb.AppendLine($"ParseError: {parseError}");
            sb.AppendLine();

            sb.AppendLine("--- RAW HTTP RESPONSE (OpenAI JSON) ---");
            sb.AppendLine(rawHttpResponse ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine("--- EXTRACTED assistant_message.content ---");
            sb.AppendLine(extractedAssistantContent ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine("--- EXTRACTED JSON OBJECT (what we parsed) ---");
            sb.AppendLine(extractedJsonObject ?? string.Empty);

            string persistentPath = Path.Combine(Application.persistentDataPath, FinalEvaluationResponseDumpFileName);
            TryWriteTextFile(persistentPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote final evaluator response dump: {persistentPath}");

#if UNITY_EDITOR
            string assetsPath = Path.Combine(Application.dataPath, FinalEvaluationResponseDumpFileName);
            TryWriteTextFile(assetsPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote final evaluator response dump (Assets): {assetsPath}");
#endif

            TryAppendEvidenceLogSection("FINAL_EVALUATOR_RESPONSE", sb.ToString());
        }

        private static void DumpFinalEvaluationPayload(
            string model,
            string systemPrompt,
            string userPrompt,
            string conversationTranscript,
            string realtimeAIFeedback,
            string scenarioName)
        {
            var sb = new StringBuilder(16_384);
            sb.AppendLine("=== EVALUATION PROMPT SOURCE ===");
            sb.AppendLine($"Model: {model}");
            sb.AppendLine($"Scenario: {scenarioName}");
            sb.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            sb.AppendLine("Prompt Source: RemotePromptManager (Google Sheets CSV or local fallback)");
            sb.AppendLine("- Keys: eval.one_call.d2d.system / eval.one_call.d2p.system (role-specific system prompt)");
            sb.AppendLine("- Keys: eval.one_call.d2d.user_template / eval.one_call.d2p.user_template (role-specific user template with {COMBINED_INPUT} placeholder)");
            sb.AppendLine("If remote unavailable, uses hardcoded defaults in EvaluationDisplayUI.cs");
            sb.AppendLine();
            sb.AppendLine("--- SYSTEM PROMPT SENT TO AI ---");
            sb.AppendLine(systemPrompt ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine($"--- CONVERSATION TRANSCRIPT ({conversationTranscript?.Length ?? 0} chars) ---");
            sb.AppendLine(conversationTranscript ?? string.Empty);
            sb.AppendLine();

            sb.AppendLine($"--- REALTIME AI FEEDBACK / PRONUNCIATION NOTES ({realtimeAIFeedback?.Length ?? 0} chars) ---");
            sb.AppendLine(realtimeAIFeedback ?? string.Empty);

            string persistentPath = Path.Combine(Application.persistentDataPath, EvaluationDumpFileName);
            TryWriteTextFile(persistentPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote evaluation payload dump: {persistentPath}");

#if UNITY_EDITOR
            string assetsPath = Path.Combine(Application.dataPath, EvaluationDumpFileName);
            TryWriteTextFile(assetsPath, sb.ToString());
            Debug.Log($"[EvaluationDisplayUI] Wrote evaluation payload dump (Assets): {assetsPath}");
#endif
        }
        
        /// <summary>
        /// Parse raw feedback from realtime AI and display evaluation
        /// This sends the complete feedback cache to ChatGPT for parsing
        /// </summary>
        public void ParseAndDisplayFeedback(string rawFeedback, string apiKey, string scenarioName, ExamEvaluation.RoleType roleType, System.Action<ExamEvaluation> onComplete = null)
        {
            if (string.IsNullOrWhiteSpace(rawFeedback))
            {
                Debug.LogError("[EvaluationDisplayUI] Raw feedback is empty! Cannot parse.");
                return;
            }
            Debug.Log($"[EvaluationDisplayUI] ➜ Sending feedback to ChatGPT (second AI). Characters: {rawFeedback.Length}");
            Debug.Log($"[EvaluationDisplayUI] Raw feedback:\n{rawFeedback}");
            
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogError("[EvaluationDisplayUI] API key is required for parsing!");
                return;
            }
            
            Debug.Log($"[EvaluationDisplayUI] Starting to parse feedback ({rawFeedback.Length} chars)");
            Debug.Log($"[EvaluationDisplayUI] Raw feedback:\n{rawFeedback}");
            
            // Show loading indicator
            if (loadingIndicator != null)
                loadingIndicator.SetActive(true);

            // Note: Local fallback parsing removed - now using only GPT-4 second-round parsing for 5D evaluation
            try
            {
                Debug.Log("[EvaluationDisplayUI] Waiting for GPT-4 second-round parsing (5D evaluation)...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[EvaluationDisplayUI] Local fallback parse error: {ex.Message}");
            }
            
            // Start parsing coroutine
            StartCoroutine(SendFeedbackToGPTForParsing(rawFeedback, apiKey, scenarioName, roleType, onComplete));
        }

        /// <summary>
        /// Single-request evaluation:
        /// Sends the full conversation transcript PLUS the realtime AI feedback (at the end)
        /// to GPT-4, asking it to output JSON with 5D scores (legacy 0-3 each, newer up to 3.99 each) + feedback per dimension + overall feedback.
        /// </summary>
        public void ParseAndDisplayConversationAndRealtimeFeedback(
            string conversationTranscript,
            string realtimeAIFeedback,
            string apiKey,
            string scenarioName,
            ExamEvaluation.RoleType roleType,
            System.Action<ExamEvaluation> onComplete = null)
        {
            bool transcriptEmpty = string.IsNullOrWhiteSpace(conversationTranscript);
            bool feedbackEmpty = string.IsNullOrWhiteSpace(realtimeAIFeedback);

            // Realtime feedback is OPTIONAL. In some flows we intentionally stop realtime + mic at evaluation time.
            // In that case we evaluate based on transcript (and optional Whisper transcript injected upstream).
            if (transcriptEmpty && feedbackEmpty)
            {
                Debug.LogError("[EvaluationDisplayUI] Both conversation transcript and realtime feedback are empty! Cannot evaluate.");
                return;
            }

            if (transcriptEmpty)
                Debug.LogWarning("[EvaluationDisplayUI] Conversation transcript is empty; evaluation will rely on realtime feedback only.");

            if (feedbackEmpty)
                Debug.LogWarning("[EvaluationDisplayUI] Realtime AI feedback is empty; evaluation will rely on transcript only.");

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogError("[EvaluationDisplayUI] API key is required for evaluation!");
                return;
            }

            Debug.Log($"[EvaluationDisplayUI] Sending ONE gpt-4o request with transcript+realtime feedback. Transcript chars: {conversationTranscript?.Length ?? 0}, Feedback chars: {realtimeAIFeedback?.Length ?? 0}");

            // Show loading indicator
            if (loadingIndicator != null)
                loadingIndicator.SetActive(true);

            // IMPORTANT: This method is intended to produce ONE final evaluation (via GPT).
            // Do not display or callback with provisional/local fallback results here, otherwise callers
            // (e.g., MedicalExamManager.OnEvaluationParsed) will run twice (0-score then final).
            // If you want a provisional UI update, implement it explicitly as a separate mode.

            StartCoroutine(SendTranscriptAndRealtimeFeedbackToGPTForEvaluation(conversationTranscript ?? "", realtimeAIFeedback ?? "", apiKey, scenarioName, roleType, onComplete));
        }

        // Collapse *excessive* duplicated consecutive characters sometimes present in realtime feedback.
        // IMPORTANT: do NOT collapse normal number formatting like "100" -> "10".
        private static string CollapseConsecutiveDuplicates(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            var sb = new System.Text.StringBuilder(input.Length);
            char prev = '\0';
            int runLen = 0;

            for (int i = 0; i < input.Length; i++)
            {
                char c = input[i];
                if (c == prev)
                {
                    runLen++;

                    // For digits: keep up to 2 repeats (so "100" stays "100").
                    // For non-digits: keep up to 2 repeats as well; drop only when the run is excessive.
                    if (runLen <= 2)
                        sb.Append(c);

                    continue;
                }

                prev = c;
                runLen = 1;
                sb.Append(c);
            }

            return sb.ToString();
        }
        
        /// <summary>
        /// Sends raw feedback to ChatGPT for parsing into structured evaluation
        /// </summary>
        private IEnumerator SendFeedbackToGPTForParsing(string rawFeedback, string apiKey, string scenarioName, ExamEvaluation.RoleType roleType, System.Action<ExamEvaluation> onComplete)
        {
            Debug.Log("[EvaluationDisplayUI] 📤 Sending feedback to ChatGPT for parsing...");
            
            string parsingPrompt = $@"You are a second-round assistance parser in a medical examination app. 

We are sending you raw feedback from a realtime AI that evaluated a medical student's performance. The feedback contains 5 dimensions (legacy scores 0-3, newer rubric scores up to 3.99):
- Kommunikation (communication, grammar, terminology, fluency)
- Hörverstehen (listening comprehension, understanding)
- Gesprächsführung (conversation management, structure)
- Empathie (empathy, patient-centered communication)
- Vollständigkeit (completeness, all topics covered)
- Total Points (legacy total out of 15, newer total out of 20)
- Overall feedback text

RAW FEEDBACK FROM REALTIME AI:
{rawFeedback}

TASK: Parse this feedback and extract the scores. The feedback might be in German or English, and might have typos or grammar issues. Be flexible in parsing.

Return ONLY a valid JSON object with this exact format:
{{
    ""kommunikation"": {{""score"": [number 0-4], ""feedback"": ""...""}},
    ""hoerverstehen"": {{""score"": [number 0-4], ""feedback"": ""...""}},
    ""gespraechsfuehrung"": {{""score"": [number 0-4], ""feedback"": ""...""}},
    ""empathie"": {{""score"": [number 0-4], ""feedback"": ""...""}},
    ""vollstaendigkeit"": {{""score"": [number 0-4], ""feedback"": ""...""}},
    ""totalPoints"": [number 0-20],
  ""overallFeedback"": ""[the detailed feedback text, cleaned up if needed]""
}}

If any score is missing, set it to 0.
Make sure all numbers are valid and within the specified ranges.";

            string url = "https://api.openai.com/v1/chat/completions";
                        string model = GetChatModelId();
            string jsonBody = $@"{{
    ""model"": ""{model}"",
  ""messages"": [
    {{""role"": ""system"", ""content"": ""You are an expert parser for medical exam evaluations. Extract scores accurately and return valid JSON.""}},
    {{""role"": ""user"", ""content"": {EscapeJsonString(parsingPrompt)}}}
  ],
  ""temperature"": 0.1,
  ""max_tokens"": 1000
}}";

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            
            using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                
                Debug.Log("[EvaluationDisplayUI] Sending request to ChatGPT...");
                Debug.Log($"[EvaluationDisplayUI] Authorization header set (key length: {apiKey?.Length ?? 0})");
                yield return request.SendWebRequest();
                
                // Hide loading indicator
                if (loadingIndicator != null)
                    loadingIndicator.SetActive(false);
                
                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string response = request.downloadHandler.text;
                        Debug.Log($"[EvaluationDisplayUI] ✓ ChatGPT response received (length: {response.Length})");
                            Debug.Log("[EvaluationDisplayUI] ⇦ Received ChatGPT parse response (second AI).");

                        // Parse OpenAI response robustly (JsonUtility is strict and often fails when
                        // the model includes markdown fences or slightly different shapes).
                        var root = JObject.Parse(response);
                        var contentToken = root["choices"]?[0]?["message"]?["content"];
                        var content = ExtractContentText(contentToken);

                        if (string.IsNullOrWhiteSpace(content))
                        {
                            Debug.LogError("[EvaluationDisplayUI] ❌ Empty content in ChatGPT response choices[0].message.content");
                            yield break;
                        }

                        if (!TryExtractJsonObject(content, out var evalObj, out var parseError))
                        {
                            Debug.LogError($"[EvaluationDisplayUI] ❌ Could not extract JSON object from ChatGPT content: {parseError}");
                            Debug.LogError($"[EvaluationDisplayUI] Raw content:\n{content}");
                            yield break;
                        }

                        // Parse the 5-dimensional scores (legacy 0-3 each, newer up to 3.99 each)
                        (float komScore, string komFb) = ParseScoreAndFeedback(evalObj, "kommunikation");
                        (float hoerScore, string hoerFb) = ParseScoreAndFeedback(evalObj, "hoerverstehen");
                        (float gesprScore, string gesprFb) = ParseScoreAndFeedback(evalObj, "gespraechsfuehrung");
                        (float empScore, string empFb) = ParseScoreAndFeedback(evalObj, "empathie");
                        (float vollScore, string vollFb) = ParseScoreAndFeedback(evalObj, "vollstaendigkeit");
                        float totalPoints = ParseFloatFieldAnyKey(evalObj, "totalPoints", "total", "gesamt");
                        string overallFeedback = ParseStringFieldAnyKey(evalObj, "overallFeedback", "feedback", "allgemeineFeedback");
                        float d2pTotalMax = ResolveD2PTotalMax(totalPoints, komScore, hoerScore, gesprScore, empScore, vollScore);
                        float d2pPerSkillMax = ResolveD2PPerSkillMax(d2pTotalMax);
                        float d2pPassThreshold = ResolveD2PPassThreshold(d2pTotalMax);

                        var evaluation = new ExamEvaluation
                        {
                            scenarioName = scenarioName,
                            roleType = roleType,
                            conversationTranscript = "", // Not needed for display
                            kommunikation = Mathf.Clamp(komScore, 0f, d2pPerSkillMax),
                            hoerverstehen = Mathf.Clamp(hoerScore, 0f, d2pPerSkillMax),
                            gespraechsfuehrung = Mathf.Clamp(gesprScore, 0f, d2pPerSkillMax),
                            empathie = Mathf.Clamp(empScore, 0f, d2pPerSkillMax),
                            vollstaendigkeit = Mathf.Clamp(vollScore, 0f, d2pPerSkillMax),
                            totalPoints = Mathf.Clamp(totalPoints, 0f, d2pTotalMax),
                            totalPointsMax = d2pTotalMax,
                            kommunikationFeedback = komFb,
                            hoerverstehenFeedback = hoerFb,
                            gespraechsfuehrungFeedback = gesprFb,
                            empathieFeedback = empFb,
                            vollstaendigkeitFeedback = vollFb,
                            overallFeedback = overallFeedback,
                            feedbackText = overallFeedback
                        };

                        evaluation.passed = evaluation.totalPoints >= d2pPassThreshold;

                        Debug.Log($"[EvaluationDisplayUI] ✓ 5D evaluation parsed from second-round parsing: K={evaluation.kommunikation}, H={evaluation.hoerverstehen}, G={evaluation.gespraechsfuehrung}, E={evaluation.empathie}, V={evaluation.vollstaendigkeit}, Total={evaluation.totalPoints}/{evaluation.totalPointsMax}, Passed={evaluation.passed}");

                        DisplayEvaluation(evaluation);
                        onComplete?.Invoke(evaluation);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[EvaluationDisplayUI] ❌ Error parsing ChatGPT response: {e.Message}");
                        Debug.LogError($"[EvaluationDisplayUI] Stack trace: {e.StackTrace}");
                    }
                }
                else
                {
                    Debug.LogError($"[EvaluationDisplayUI] ❌ ChatGPT API error: {request.error}");
                    Debug.LogError($"[EvaluationDisplayUI] Response code: {request.responseCode}");
                    if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                    {
                        Debug.LogError($"[EvaluationDisplayUI] Error response: {request.downloadHandler.text}");
                    }
                }
            }
        }

        private IEnumerator SendTranscriptAndRealtimeFeedbackToGPTForEvaluation(
            string conversationTranscript,
            string realtimeAIFeedback,
            string apiKey,
            string scenarioName,
            ExamEvaluation.RoleType roleType,
            System.Action<ExamEvaluation> onComplete)
        {
            Debug.Log("[EvaluationDisplayUI] Sending transcript+realtime feedback to chat model for evaluation...");

            // ── Retrieve RAG context for evaluation ──
            string ragContext = "";
            if (ragIntegrationHelper != null && ragIntegrationHelper.IsReady)
            {
                bool ragDone = false;
                var scenarioTags = new List<string>();
                if (!string.IsNullOrWhiteSpace(scenarioName))
                {
                    foreach (var part in scenarioName.ToLowerInvariant().Replace("_", " ").Split(' '))
                    {
                        string t = part.Trim();
                        if (t.Length > 2 && !scenarioTags.Contains(t))
                            scenarioTags.Add(t);
                    }
                }
                if (scenarioTags.Count == 0) scenarioTags.Add("fsp");
                scenarioTags.Add("medical_german");

                ragIntegrationHelper.RetrieveForQuery(
                    scenarioName ?? "Fachsprachprüfung",
                    scenarioTags,
                    (context) => { ragContext = context; ragDone = true; },
                    (error) => { Debug.LogWarning($"[EvaluationDisplayUI] RAG retrieval failed: {error}"); ragDone = true; }
                );

                float ragTimeout = 5f;
                float ragStart = Time.time;
                yield return new WaitUntil(() => ragDone || (Time.time - ragStart > ragTimeout));

                if (!string.IsNullOrWhiteSpace(ragContext))
                    Debug.Log($"[EvaluationDisplayUI] RAG context injected into evaluation ({ragContext.Length} chars)");
            }

            // Guard against overly large payloads causing request/context limits (often surfaces as HTTP 400).
            // We try to include everything, but we must stay within API limits.
            const int maxCombinedChars = 28000;

            // Build user-focused evidence: score only user utterances, keep AI turns as context only.
            string combinedInputRaw = BuildUserFocusedEvaluationEvidence(conversationTranscript ?? "", realtimeAIFeedback ?? "");

            // Inject RAG medical knowledge if available
            if (!string.IsNullOrWhiteSpace(ragContext))
            {
                combinedInputRaw = "REFERENCE MEDICAL KNOWLEDGE (use this to verify correctness of student answers):\n" + ragContext +
                    "\n\n" + combinedInputRaw;
            }

            string caseReference = BuildEvaluationCaseReference(roleType, scenarioName);
            if (!string.IsNullOrWhiteSpace(caseReference))
                combinedInputRaw = caseReference + "\n\n" + combinedInputRaw;

            string combinedInput = combinedInputRaw;
            if (combinedInputRaw.Length > maxCombinedChars)
            {
                int head = maxCombinedChars / 2;
                int tail = maxCombinedChars - head;
                combinedInput = combinedInputRaw.Substring(0, head)
                               + "\n\n...[TRUNCATED to fit API limits]...\n\n"
                               + combinedInputRaw.Substring(combinedInputRaw.Length - tail);
                Debug.LogWarning($"[EvaluationDisplayUI] Combined transcript+feedback is very large ({combinedInputRaw.Length} chars). Truncated to {combinedInput.Length} chars to avoid API 400.");
            }

            // Debug: help diagnose "all zeros" evaluations caused by missing evidence.
            bool hasUserLine = !string.IsNullOrEmpty(conversationTranscript) && conversationTranscript.Contains("User:");
            if (!hasUserLine)
                Debug.LogWarning("[EvaluationDisplayUI] Transcript contains no 'User:' lines. If you spoke, onUserTranscript may not be firing; Whisper transcript should compensate if mic capture works.");

            string evidencePreview = combinedInput.Length > 500 ? combinedInput.Substring(0, 500) + "..." : combinedInput;
            Debug.Log($"[EvaluationDisplayUI] Evidence preview (first 500 chars):\n{evidencePreview}");

            // Remote-only prompts: CSV must provide explicit role-specific prompt pairs.
            string promptTemplateKey = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                ? "eval.one_call.d2d.user_template"
                : "eval.one_call.d2p.user_template";
            string systemMsgKey = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                ? "eval.one_call.d2d.system"
                : "eval.one_call.d2p.system";

            string promptTemplate = ApplyEvaluationPromptPlaceholders(RemotePromptManager.GetForScenario(promptTemplateKey, MedicalExamManager.ActiveScenarioId, ""), roleType, scenarioName);
            if (string.IsNullOrWhiteSpace(promptTemplate))
            {
                Debug.LogError($"[EvaluationDisplayUI] CRITICAL: {promptTemplateKey} not found in remote CSV! Evaluation cannot proceed.");
                if (loadingIndicator != null)
                    loadingIndicator.SetActive(false);
                yield break;
            }

            string prompt = promptTemplate.Replace("{COMBINED_INPUT}", combinedInput);

            string url = "https://api.openai.com/v1/chat/completions";
            string model = GetChatModelId();
            string systemMsg = ApplyEvaluationPromptPlaceholders(RemotePromptManager.GetForScenario(systemMsgKey, MedicalExamManager.ActiveScenarioId, ""), roleType, scenarioName);
            if (string.IsNullOrWhiteSpace(systemMsg))
            {
                Debug.LogError($"[EvaluationDisplayUI] CRITICAL: {systemMsgKey} not found in remote CSV! Evaluation cannot proceed.");
                if (loadingIndicator != null)
                    loadingIndicator.SetActive(false);
                yield break;
            }

            DumpFinalEvaluationPayload(model, systemMsg, prompt, conversationTranscript, realtimeAIFeedback, scenarioName);

            string jsonBody = "{\n" +
                              "  \"model\": \"" + model + "\",\n" +
                              "  \"messages\": [\n" +
                              "    {\"role\": \"system\", \"content\": " + EscapeJsonString(systemMsg) + "},\n" +
                              "    {\"role\": \"user\", \"content\": " + EscapeJsonString(prompt) + "}\n" +
                              "  ],\n" +
                              "  \"temperature\": 0.2,\n" +
                              "  \"max_tokens\": 900\n" +
                              "}";

            // Persist EXACT request + also append it into the evidence log.
            DumpFinalEvaluationRequest(url, model, systemMsg, prompt, jsonBody, scenarioName);

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                // Hide loading indicator
                if (loadingIndicator != null)
                    loadingIndicator.SetActive(false);

                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[EvaluationDisplayUI] Chat evaluation API error (model={model}): {request.error}");
                    Debug.LogError($"[EvaluationDisplayUI] Response code: {request.responseCode}");
                    if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                    {
                        Debug.LogError($"[EvaluationDisplayUI] Error response: {request.downloadHandler.text}");
                    }
                    Debug.LogError($"[EvaluationDisplayUI] Request JSON length: {jsonBody.Length}. Prompt length: {prompt.Length}.");

                    DumpFinalEvaluationResponse(
                        model,
                        scenarioName,
                        rawHttpResponse: request.downloadHandler?.text,
                        extractedAssistantContent: "",
                        extractedJsonObject: "",
                        parseError: $"HTTP_ERROR: {request.error} (code={request.responseCode})");
                    yield break;
                }

                string response = request.downloadHandler.text;
                JObject root = null;
                JToken contentToken = null;
                string content = null;

                try
                {
                    root = JObject.Parse(response);
                    contentToken = root["choices"]?[0]?["message"]?["content"];
                    content = ExtractContentText(contentToken);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EvaluationDisplayUI] Error parsing HTTP response JSON: {ex.Message}");
                    yield break;
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    Debug.LogError("[EvaluationDisplayUI] Empty content from GPT-4 evaluation response.");
                    DumpFinalEvaluationResponse(
                        model,
                        scenarioName,
                        rawHttpResponse: response,
                        extractedAssistantContent: "",
                        extractedJsonObject: "",
                        parseError: "EMPTY_CONTENT");
                    yield break;
                }

                if (!TryExtractJsonObject(content, out var obj, out var parseError))
                {
                    Debug.LogError($"[EvaluationDisplayUI] Could not extract JSON object from GPT-4 evaluation response: {parseError}");
                    Debug.LogError($"[EvaluationDisplayUI] Raw content:\n{content}");
                    DumpFinalEvaluationResponse(
                        model,
                        scenarioName,
                        rawHttpResponse: response,
                        extractedAssistantContent: content,
                        extractedJsonObject: "",
                        parseError: parseError);
                    yield break;
                }

                // --- REPAIR FALLBACK: If keys are missing (e.g. model outputted wrong structure), ask GPT to fix it ---
                // For D2D, check for D2D-specific keys; for D2P, check for 5D keys
                bool isD2D = roleType == ExamEvaluation.RoleType.DoctorToDoctor;
                bool hasExpectedKeys;
                if (isD2D)
                {
                    hasExpectedKeys = GetTokenByKey(obj, "criteria") != null ||
                                     GetTokenByKey(obj, "totalScore") != null ||
                                     GetTokenByKey(obj, "finalVerdict") != null ||
                                     GetTokenByKey(obj, "sprachlicheAngemessenheit") != null ||
                                     GetTokenByKey(obj, "inhaltlicheAngemessenheit") != null ||
                                     GetTokenByKey(obj, "gesamt") != null;
                }
                else
                {
                    hasExpectedKeys = GetTokenByKey(obj, "kommunikation") != null ||
                                     GetTokenByKey(obj, "hoerverstehen") != null ||
                                     GetTokenByKey(obj, "totalPoints") != null;
                }

                if (!hasExpectedKeys)
                {
                    Debug.LogWarning("[EvaluationDisplayUI] Response JSON missing required 5D keys. Attempting structure repair...");
                    string repairedContent = null;
                    
                    yield return RepairJSONResponse(content, apiKey, model, url, (c) => { repairedContent = c; });

                    if (!string.IsNullOrWhiteSpace(repairedContent))
                    {
                        if (TryExtractJsonObject(repairedContent, out var obj2, out var parseError2))
                        {
                            Debug.Log("[EvaluationDisplayUI] Repair successful, using corrected JSON.");
                            content = repairedContent;
                            obj = obj2;
                        }
                        else
                        {
                            Debug.LogError($"[EvaluationDisplayUI] Repair returned invalid JSON: {parseError2}");
                        }
                    }
                    else
                    {
                         Debug.LogError("[EvaluationDisplayUI] Repair failed or returned empty content.");
                    }
                }
                // -----------------------------------------------------------------------------------------------------

                DumpFinalEvaluationResponse(
                    model,
                    scenarioName,
                    rawHttpResponse: response,
                    extractedAssistantContent: content,
                    extractedJsonObject: obj.ToString(),
                    parseError: "");

                try
                {
                    ExamEvaluation evaluation;

                    if (isD2D)
                    {
                        if (!TryBuildD2DEvaluation(obj, scenarioName, roleType, conversationTranscript, out evaluation))
                            throw new Exception("Unsupported D2D evaluation JSON structure.");

                        if (evaluation.usesD2DFiveCriteria)
                        {
                            Debug.Log($"[EvaluationDisplayUI] D2D CEO evaluation parsed. Content={evaluation.d2dContent}, Conversation={evaluation.d2dConversation}, Vocabulary={evaluation.d2dVocabulary}, Grammar={evaluation.d2dGrammar}, Pronunciation={evaluation.d2dPronunciation}, Total={evaluation.totalPoints}/{evaluation.totalPointsMax}, Passed={evaluation.passed}");
                        }
                        else
                        {
                            Debug.Log($"[EvaluationDisplayUI] D2D 3D evaluation parsed. Sprache={evaluation.sprachlicheAngemessenheit}/7, Inhalt={evaluation.inhaltlicheAngemessenheit}/3, Malus={evaluation.malusPatientensicherheit}/5, Gesamt={evaluation.gesamtD2D}/10, Passed={evaluation.passed}");
                        }
                    }
                    else
                    {
                        // Parse 5D evaluation scores (legacy 0-3 each, newer up to 3.99 each) for D2P
                        (float komScore, string komFb) = ParseScoreAndFeedback(obj, "kommunikation");
                        (float hoerScore, string hoerFb) = ParseScoreAndFeedback(obj, "hoerverstehen");
                        (float gesprScore, string gesprFb) = ParseScoreAndFeedback(obj, "gespraechsfuehrung");
                        (float empScore, string empFb) = ParseScoreAndFeedback(obj, "empathie");
                        (float vollScore, string vollFb) = ParseScoreAndFeedback(obj, "vollstaendigkeit");
                        
                        // Parse totalPoints (legacy 0-15, newer 0-20) and overall feedback
                        float totalPoints = ParseFloatFieldAnyKey(obj, "totalPoints", "total", "gesamt", "gesamtpunkte");
                        string overallFeedback = ParseStringFieldAnyKey(obj, "overallFeedback", "generalFeedback", "feedbackText", "feedback", "gesamtfeedback");
                        float d2pTotalMax = ResolveD2PTotalMax(totalPoints, komScore, hoerScore, gesprScore, empScore, vollScore);
                        float d2pPerSkillMax = ResolveD2PPerSkillMax(d2pTotalMax);
                        float d2pPassThreshold = ResolveD2PPassThreshold(d2pTotalMax);

                        evaluation = new ExamEvaluation
                        {
                            scenarioName = scenarioName,
                            roleType = roleType,
                            conversationTranscript = conversationTranscript,
                            // 5D scores (legacy max 3, newer max 4)
                            kommunikation = Mathf.Clamp(komScore, 0f, d2pPerSkillMax),
                            hoerverstehen = Mathf.Clamp(hoerScore, 0f, d2pPerSkillMax),
                            gespraechsfuehrung = Mathf.Clamp(gesprScore, 0f, d2pPerSkillMax),
                            empathie = Mathf.Clamp(empScore, 0f, d2pPerSkillMax),
                            vollstaendigkeit = Mathf.Clamp(vollScore, 0f, d2pPerSkillMax),
                            // 5D feedback
                            kommunikationFeedback = komFb,
                            hoerverstehenFeedback = hoerFb,
                            gespraechsfuehrungFeedback = gesprFb,
                            empathieFeedback = empFb,
                            vollstaendigkeitFeedback = vollFb,
                            // Total and overall
                            totalPoints = Mathf.Clamp(totalPoints, 0f, d2pTotalMax),
                            totalPointsMax = d2pTotalMax,
                            overallFeedback = overallFeedback,
                            feedbackText = overallFeedback
                        };

                        // Determine pass/fail based on detected scale (legacy >=9/15, newer >=12/20)
                        evaluation.passed = evaluation.totalPoints >= d2pPassThreshold;
                        ApplyD2PConservativeScoringGuards(evaluation);

                        Debug.Log($"[EvaluationDisplayUI] 5D evaluation parsed. K={evaluation.kommunikation}, H={evaluation.hoerverstehen}, G={evaluation.gespraechsfuehrung}, E={evaluation.empathie}, V={evaluation.vollstaendigkeit}, Total={evaluation.totalPoints}/{evaluation.totalPointsMax}, Passed={evaluation.passed}");
                    }

                    DisplayEvaluation(evaluation);
                    onComplete?.Invoke(evaluation);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EvaluationDisplayUI] Error building evaluation from parsed data: {ex.Message}");
                }
            }
        }

        private static string BuildEvaluationCaseReference(ExamEvaluation.RoleType roleType, string scenarioName)
        {
            string roleLabel = roleType == ExamEvaluation.RoleType.DoctorToDoctor ? "d2d" : "d2p";
            string scenarioContext = string.Empty;

            if (RemoteScenarioManager.TryGetScenario(MedicalExamManager.ActiveScenarioId, out var scenarioRow) && scenarioRow != null)
            {
                scenarioContext = scenarioRow.scenarioContext ?? string.Empty;
            }

            string terms = JoinJsonItems(MedicalExamManager.ActiveCaseTermsJson);
            string coreFacts = JoinJsonItems(MedicalExamManager.ActiveCaseCoreFactsJson);
            string expectedQuestions = JoinJsonItems(MedicalExamManager.ActiveCaseExpectedQuestionsJson);

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("ACTIVE CASE REFERENCE:");
            builder.AppendLine($"- Scenario: {scenarioName}");
            builder.AppendLine($"- Role: {roleLabel}");
            if (!string.IsNullOrWhiteSpace(MedicalExamManager.ActiveCaseId))
                builder.AppendLine($"- Case id: {MedicalExamManager.ActiveCaseId}");
            if (!string.IsNullOrWhiteSpace(MedicalExamManager.ActiveCaseTitle))
                builder.AppendLine($"- Case title: {MedicalExamManager.ActiveCaseTitle}");
            if (!string.IsNullOrWhiteSpace(terms))
                builder.AppendLine($"- Case terminology: {terms}");
            if (!string.IsNullOrWhiteSpace(MedicalExamManager.ActiveCaseText))
                builder.AppendLine($"- Full case context: {MedicalExamManager.ActiveCaseText}");
            else if (!string.IsNullOrWhiteSpace(scenarioContext))
                builder.AppendLine($"- Scenario context: {scenarioContext}");
            if (!string.IsNullOrWhiteSpace(coreFacts))
                builder.AppendLine($"- Core facts for completeness: {coreFacts}");
            if (!string.IsNullOrWhiteSpace(expectedQuestions))
                builder.AppendLine($"- Relevant question focus: {expectedQuestions}");

            return builder.ToString().Trim();
        }

        private static string ApplyEvaluationPromptPlaceholders(string template, ExamEvaluation.RoleType roleType, string scenarioName)
        {
            if (string.IsNullOrWhiteSpace(template))
                return template;

            string currentTheme = string.Empty;
            string scenarioContext = string.Empty;
            string topicD2D = string.Empty;
            string topicD2P = string.Empty;

            if (RemoteScenarioManager.TryGetScenario(MedicalExamManager.ActiveScenarioId, out var scenarioRow) && scenarioRow != null)
            {
                currentTheme = scenarioRow.theme ?? string.Empty;
                scenarioContext = scenarioRow.scenarioContext ?? string.Empty;
                topicD2D = scenarioRow.topicD2D ?? string.Empty;
                topicD2P = scenarioRow.topicD2P ?? string.Empty;
            }

            string termsJson = MedicalExamManager.ActiveCaseTermsJson ?? string.Empty;
            string coreFactsJson = MedicalExamManager.ActiveCaseCoreFactsJson ?? string.Empty;
            string expectedQuestionsJson = MedicalExamManager.ActiveCaseExpectedQuestionsJson ?? string.Empty;
            string terms = JoinJsonItems(termsJson);
            string coreFacts = JoinJsonItems(coreFactsJson);
            string expectedQuestions = JoinJsonItems(expectedQuestionsJson);
            string caseContext = string.IsNullOrWhiteSpace(MedicalExamManager.ActiveCaseText) ? scenarioContext : MedicalExamManager.ActiveCaseText;
            string primaryTerm = string.Empty;
            if (!string.IsNullOrWhiteSpace(terms))
                primaryTerm = terms.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(primaryTerm))
                primaryTerm = currentTheme;

            return template
                .Replace("{SCENARIO_ID}", MedicalExamManager.ActiveScenarioId ?? "default")
                .Replace("{SCENARIO_NAME}", scenarioName ?? string.Empty)
                .Replace("{CASE_THEME}", currentTheme)
                .Replace("{CASE_PRIMARY_TERM}", primaryTerm ?? string.Empty)
                .Replace("{CASE_TERMS}", terms)
                .Replace("{CASE_TERMS_JSON}", termsJson)
                .Replace("{CASE_CONTEXT}", caseContext)
                .Replace("{CURRENT_THEME}", currentTheme)
                .Replace("{THEME_FIRST_TERM}", primaryTerm ?? string.Empty)
                .Replace("{THEME_TERMS}", terms)
                .Replace("{TERMS_JSON}", termsJson)
                .Replace("{CORE_FACTS}", coreFacts)
                .Replace("{CORE_FACTS_JSON}", coreFactsJson)
                .Replace("{EXPECTED_QUESTIONS}", expectedQuestions)
                .Replace("{EXPECTED_QUESTIONS_JSON}", expectedQuestionsJson)
                .Replace("{CONTEXT}", caseContext)
                .Replace("{CASE_ID}", MedicalExamManager.ActiveCaseId ?? string.Empty)
                .Replace("{CASE_TITLE}", MedicalExamManager.ActiveCaseTitle ?? string.Empty)
                .Replace("{CASE_INDEX}", MedicalExamManager.ActiveCaseIndex >= 0 ? (MedicalExamManager.ActiveCaseIndex + 1).ToString() : string.Empty)
                .Replace("{TOPIC_D2D}", topicD2D)
                .Replace("{TOPIC_D2P}", topicD2P)
                .Replace("{ROLE}", roleType == ExamEvaluation.RoleType.DoctorToDoctor ? "d2d" : "d2p");
        }

        private static string JoinJsonItems(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return string.Empty;

            try
            {
                var token = JToken.Parse(rawJson);
                JToken itemsToken = token.Type == JTokenType.Object ? token["items"] ?? token["terms"] ?? token : token;
                var values = new List<string>();
                if (itemsToken is JArray array)
                {
                    foreach (var item in array)
                    {
                        string value = item?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(value))
                            values.Add(value);
                    }
                }
                return values.Count == 0 ? string.Empty : string.Join(", ", values);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static (float score, string feedback) ParseScoreAndFeedback(JObject root, string key)
        {
            if (root == null || string.IsNullOrWhiteSpace(key)) return (0f, "");
            var nodeToken = GetTokenByKey(root, key);
            var node = nodeToken as JObject;
            if (node == null)
            {
                // Back-compat: sometimes models return direct numeric fields.
                var directScore = ParseFloatFieldAnyKey(root, key);

                // Newer/flat schema: per-skill feedback is provided as separate keys.
                string feedbackKey = key + "Feedback";
                string flatFeedback = ParseStringFieldAnyKey(root, feedbackKey);
                return (directScore, flatFeedback);
            }

            float score = 0f;
            var scoreToken = GetTokenByKey(node, "score");
            if (scoreToken != null)
            {
                if (scoreToken.Type == JTokenType.Float || scoreToken.Type == JTokenType.Integer) score = scoreToken.Value<float>();
                else score = ParseFloatFieldAnyKey(node, "score");
            }

            string feedback = ParseStringFieldAnyKey(node, "feedback", "comment", "why", "reason");
            return (score, feedback);
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return string.Empty;
            key = key.Trim().ToLowerInvariant();
            key = key.Replace("ä", "a").Replace("ö", "o").Replace("ü", "u").Replace("ß", "ss");
            // Keep only letters/numbers/underscore
            var sb = new System.Text.StringBuilder(key.Length);
            foreach (var c in key)
            {
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            }
            return sb.ToString();
        }

        private static JToken GetTokenByKey(JObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key)) return null;

            // Fast path exact
            var t = obj[key];
            if (t != null) return t;

            // Case/variant-insensitive scan
            string target = NormalizeKey(key);
            foreach (var p in obj.Properties())
            {
                if (NormalizeKey(p.Name) == target)
                    return p.Value;
            }

            return null;
        }

        private static float ParseFloatFieldAnyKey(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null || keys.Length == 0) return 0f;

            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var token = GetTokenByKey(obj, k);
                if (token == null) continue;

                if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                    return token.Value<float>();

                // Handle strings like "4/5" or "3,5"
                var s = token.ToString().Trim();
                if (string.IsNullOrEmpty(s)) continue;
                var slashIdx = s.IndexOf('/');
                if (slashIdx > 0) s = s.Substring(0, slashIdx);
                s = s.Replace(',', '.');
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return v;
            }

            return 0f;
        }

        private static string ParseStringFieldAnyKey(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null || keys.Length == 0) return string.Empty;
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var token = GetTokenByKey(obj, k);
                if (token == null) continue;
                var s = token.Type == JTokenType.String ? (string)token : token.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return string.Empty;
        }

        /// <summary>
        /// Transcribe captured user mic audio (PCM16 16kHz mono) using OpenAI's transcription API.
        /// Returns plain text (not JSON).
        /// </summary>
        public IEnumerator TranscribeUserAudioPcm16WithWhisper(
            byte[] pcm16,
            int sampleRateHz,
            string apiKey,
            Action<string> onSuccess,
            Action<string> onError = null)
        {
            if (pcm16 == null || pcm16.Length < 2)
            {
                onError?.Invoke("No audio to transcribe.");
                yield break;
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                onError?.Invoke("Missing API key.");
                yield break;
            }

            // Build WAV (16-bit PCM, mono)
            byte[] wavBytes;
            try
            {
                wavBytes = BuildWavFromPcm16(pcm16, sampleRateHz, 1);
            }
            catch (Exception ex)
            {
                onError?.Invoke($"Failed to build WAV: {ex.Message}");
                yield break;
            }

            string url = "https://api.openai.com/v1/audio/transcriptions";
            var form = new WWWForm();
            // Prefer newer transcription models when available.
            // This is a drop-in replacement for whisper-1 on the same endpoint.
            form.AddField("model", "gpt-4o-transcribe");
            form.AddBinaryData("file", wavBytes, "user_audio.wav", "audio/wav");
            // FORCE German language mode for medical exam context (FSP)
            form.AddField("language", "de");

            using (var request = UnityEngine.Networking.UnityWebRequest.Post(url, form))
            {
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    var errBody = request.downloadHandler?.text;
                    var msg = $"Audio transcription failed: {request.error} (HTTP {request.responseCode})";
                    Debug.LogError($"[EvaluationDisplayUI] {msg}\n{errBody}");
                    onError?.Invoke(msg);
                    yield break;
                }

                try
                {
                    var body = request.downloadHandler.text;
                    var root = JObject.Parse(body);
                    var text = (string)(root["text"] ?? "");
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        onError?.Invoke("Transcription returned empty transcript.");
                        yield break;
                    }

                    onSuccess?.Invoke(text.Trim());
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[EvaluationDisplayUI] Failed to parse transcription response: {ex.Message}");
                    onError?.Invoke($"Failed to parse transcription response: {ex.Message}");
                }
            }
        }

        private static byte[] BuildWavFromPcm16(byte[] pcm16, int sampleRateHz, int channels)
        {
            if (pcm16 == null) throw new ArgumentNullException(nameof(pcm16));
            if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

            int byteRate = sampleRateHz * channels * 2;
            int dataLen = pcm16.Length;

            using (var ms = new System.IO.MemoryStream(44 + dataLen))
            using (var bw = new System.IO.BinaryWriter(ms, Encoding.UTF8))
            {
                // RIFF header
                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + dataLen);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                // fmt subchunk
                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16); // PCM
                bw.Write((short)1); // audio format = PCM
                bw.Write((short)channels);
                bw.Write(sampleRateHz);
                bw.Write(byteRate);
                bw.Write((short)(channels * 2)); // block align
                bw.Write((short)16); // bits per sample

                // data subchunk
                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(dataLen);
                bw.Write(pcm16);

                bw.Flush();
                return ms.ToArray();
            }
        }
        
        /// <summary>
        /// Helper to escape JSON strings properly
        /// </summary>
        private static string EscapeJsonString(string str)
        {
            if (string.IsNullOrEmpty(str)) return "\"\"";
            
            return "\"" + str
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t") + "\"";
        }
        
        /// <summary>
        /// JSON response structure from ChatGPT
        /// </summary>
        [Serializable]
        private class GPTResponse
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
            public string content;
        }
        
        /// <summary>
        /// Parsed evaluation data structure (DEPRECATED - now using ExamEvaluation with 5D scores)
        /// </summary>
        [Serializable]
        private class EvaluationData
        {
            // Legacy class kept for backward compatibility - no longer used
        }

        private static string ExtractContentText(JToken contentToken)
        {
            if (contentToken == null) return null;

            // Chat Completions typically returns a string, but some newer shapes may return an array.
            if (contentToken.Type == JTokenType.String) return (string)contentToken;

            if (contentToken.Type == JTokenType.Array)
            {
                // Common pattern: [{"type":"text","text":"..."}, ...]
                var sb = new System.Text.StringBuilder();
                foreach (var part in contentToken)
                {
                    var text = (string)(part?["text"] ?? part?["content"]);
                    if (!string.IsNullOrEmpty(text)) sb.Append(text);
                    else sb.Append(part?.ToString());
                }
                return sb.ToString();
            }

            return contentToken.ToString();
        }

        private static bool TryExtractJsonObject(string content, out JObject obj, out string error)
        {
            obj = null;
            error = null;

            if (string.IsNullOrWhiteSpace(content))
            {
                error = "content empty";
                return false;
            }

            // Strip common code fences.
            var cleaned = content.Trim();
            cleaned = cleaned.Replace("```json", "").Replace("```", "").Trim();

            try
            {
                obj = JObject.Parse(cleaned);
                return true;
            }
            catch
            {
                // Fall through to substring extraction.
            }

            var start = cleaned.IndexOf('{');
            var end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                error = "no JSON object braces found";
                return false;
            }

            var slice = cleaned.Substring(start, end - start + 1);
            try
            {
                obj = JObject.Parse(slice);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static float ParseFloatField(JObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key)) return 0f;

            var token = obj[key];
            if (token == null) return 0f;

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<float>();
            }

            // Handle values like "4/5" or "3,5" or "4.0".
            var s = token.ToString().Trim();
            if (string.IsNullOrEmpty(s)) return 0f;

            // If formatted like "4/5" take the numerator.
            var slashIdx = s.IndexOf('/');
            if (slashIdx > 0) s = s.Substring(0, slashIdx);

            // Normalize decimal comma.
            s = s.Replace(',', '.');

            return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }
        
        /// <summary>
        /// Displays an evaluation result with sliders and scores (with smooth animations)
        /// </summary>
        public void DisplayEvaluation(ExamEvaluation evaluation)
        {
            if (evaluation == null)
            {
                Debug.LogWarning("[EvaluationDisplayUI] Evaluation is null!");
                return;
            }

            // Trigger flip animation when feedback starts
            if (feedbackAnimator != null)
            {
                feedbackAnimator.SetTrigger("flip");
                Debug.Log("[EvaluationDisplayUI] Triggered 'flip' animation on feedbackAnimator");
            }

            bool isD2D = evaluation.roleType == ExamEvaluation.RoleType.DoctorToDoctor;

            // Toggle panels: show only the relevant result panel
            if (d2pResultsPanel != null) d2pResultsPanel.SetActive(!isD2D);
            if (d2dResultsPanel != null) d2dResultsPanel.SetActive(isD2D);

            if (isD2D)
            {
                if (evaluation.usesD2DFiveCriteria)
                {
                    if (sprachlicheAngemessenheitText != null)
                        sprachlicheAngemessenheitText.text = $"Inhalt {evaluation.d2dContent:F2} / 3.99";

                    if (inhaltlicheAngemessenheitText != null)
                        inhaltlicheAngemessenheitText.text = $"Interaktion {evaluation.d2dConversation:F2} / 3.99";

                    if (malusPatientensicherheitText != null)
                    {
                        malusPatientensicherheitText.text = $"Wortschatz {evaluation.d2dVocabulary:F2} / 3.99";
                        if (sprachlicheAngemessenheitText != null)
                            malusPatientensicherheitText.color = sprachlicheAngemessenheitText.color;
                    }

                    if (sprachlicheAngemessenheitScoreText != null)
                        sprachlicheAngemessenheitScoreText.text = $"Inhalt {evaluation.d2dContent:F2} / 3.99";

                    if (inhaltlicheAngemessenheitScoreText != null)
                        inhaltlicheAngemessenheitScoreText.text = $"Interaktion {evaluation.d2dConversation:F2} / 3.99";

                    if (malusPatientensicherheitScoreText != null)
                    {
                        malusPatientensicherheitScoreText.text = $"Wortschatz {evaluation.d2dVocabulary:F2} / 3.99";
                        if (sprachlicheAngemessenheitScoreText != null)
                            malusPatientensicherheitScoreText.color = sprachlicheAngemessenheitScoreText.color;
                    }

                    if (d2dFinalScoreText != null)
                        d2dFinalScoreText.text = $"{evaluation.totalPoints:F1} / 20";

                    if (totalPointsD2DText != null)
                        totalPointsD2DText.text = $"{evaluation.totalPoints:F1} / 20";

                    if (feedbackText != null)
                        feedbackText.text = StripEmojis(string.IsNullOrWhiteSpace(evaluation.feedbackText) ? BuildD2DFiveCriteriaFeedback(evaluation) : evaluation.feedbackText);

                    Debug.Log($"[EvaluationDisplayUI] Displayed D2D CEO evaluation: Total={evaluation.totalPoints}/20, Passed: {evaluation.passed}");
                }
                else
                {
                    // ---- D2D 3D display ----
                    if (sprachlicheAngemessenheitText != null)
                        sprachlicheAngemessenheitText.text = $"{evaluation.sprachlicheAngemessenheit:F1} / 7";

                    if (inhaltlicheAngemessenheitText != null)
                        inhaltlicheAngemessenheitText.text = $"{evaluation.inhaltlicheAngemessenheit:F1} / 3";

                    if (malusPatientensicherheitText != null)
                    {
                        malusPatientensicherheitText.text = $"-{evaluation.malusPatientensicherheit:F1} / 5";
                        ColorUtility.TryParseHtmlString(evaluation.malusPatientensicherheit > 0f ? "#F45356" : "#38AC65", out var malusColor);
                        malusPatientensicherheitText.color = malusColor;
                    }

                    if (d2dFinalScoreText != null)
                        d2dFinalScoreText.text = $"{evaluation.gesamtD2D:F1} / 10";

                    if (totalPointsD2DText != null)
                        totalPointsD2DText.text = $"{evaluation.gesamtD2D:F1} / 10";

                    if (feedbackText != null)
                    {
                        string overall = evaluation.overallFeedback ?? evaluation.feedbackText ?? "";
                        string detailedFeedback = "";

                        if (!string.IsNullOrWhiteSpace(evaluation.sprachlicheAngemessenheitFeedback))
                            detailedFeedback += $"\n\nSprachliche Angemessenheit: {evaluation.sprachlicheAngemessenheitFeedback}";
                        if (!string.IsNullOrWhiteSpace(evaluation.inhaltlicheAngemessenheitFeedback))
                            detailedFeedback += $"\n\nInhaltliche Angemessenheit: {evaluation.inhaltlicheAngemessenheitFeedback}";
                        if (!string.IsNullOrWhiteSpace(evaluation.malusPatientensicherheitFeedback))
                            detailedFeedback += $"\n\nMalus Patientensicherheit: {evaluation.malusPatientensicherheitFeedback}";

                        feedbackText.text = StripEmojis(overall + detailedFeedback);
                    }

                    Debug.Log($"[EvaluationDisplayUI] Displayed D2D 3D evaluation: Sprache={evaluation.sprachlicheAngemessenheit}/7, Inhalt={evaluation.inhaltlicheAngemessenheit}/3, Malus={evaluation.malusPatientensicherheit}/5, Gesamt={evaluation.gesamtD2D}/10, Passed: {evaluation.passed}");
                }
            }
            else
            {
                // ---- D2P 5D display ----
                float d2pTotalMax = evaluation.totalPointsMax > 0f ? evaluation.totalPointsMax : (evaluation.totalPoints > 15f ? 20f : 15f);
                float d2pPerSkillMax = ResolveD2PPerSkillMax(d2pTotalMax);
                int d2pPerSkillMaxInt = Mathf.RoundToInt(d2pPerSkillMax);
                int d2pTotalMaxInt = Mathf.RoundToInt(d2pTotalMax);

                if (kommunikationScoreText != null)
                    kommunikationScoreText.text = $"{Mathf.RoundToInt(evaluation.kommunikation)} / {d2pPerSkillMaxInt}";
                
                if (hoerverstehenScoreText != null)
                    hoerverstehenScoreText.text = $"{Mathf.RoundToInt(evaluation.hoerverstehen)} / {d2pPerSkillMaxInt}";
                
                if (gespraechsfuehrungScoreText != null)
                    gespraechsfuehrungScoreText.text = $"{Mathf.RoundToInt(evaluation.gespraechsfuehrung)} / {d2pPerSkillMaxInt}";
                
                if (empathieScoreText != null)
                    empathieScoreText.text = $"{Mathf.RoundToInt(evaluation.empathie)} / {d2pPerSkillMaxInt}";
                
                if (vollstaendigkeitScoreText != null)
                    vollstaendigkeitScoreText.text = $"{Mathf.RoundToInt(evaluation.vollstaendigkeit)} / {d2pPerSkillMaxInt}";
                
                // Display total score (legacy /15, newer /20)
                if (d2pFinalScoreText != null)
                    d2pFinalScoreText.text = $"{Mathf.RoundToInt(evaluation.totalPoints)} / {d2pTotalMaxInt}";
                
                if (totalPointsD2PText != null)
                    totalPointsD2PText.text = $"{Mathf.RoundToInt(evaluation.totalPoints)} / {d2pTotalMaxInt}";

                // Display general feedback (combine overall + per-dimension feedback)
                if (feedbackText != null)
                {
                    string overall = evaluation.overallFeedback ?? evaluation.feedbackText ?? "";
                    string detailedFeedback = "";
                    
                    if (!string.IsNullOrWhiteSpace(evaluation.kommunikationFeedback))
                        detailedFeedback += $"\n\nKommunikation: {evaluation.kommunikationFeedback}";
                    if (!string.IsNullOrWhiteSpace(evaluation.hoerverstehenFeedback))
                        detailedFeedback += $"\n\nHörverstehen: {evaluation.hoerverstehenFeedback}";
                    if (!string.IsNullOrWhiteSpace(evaluation.gespraechsfuehrungFeedback))
                        detailedFeedback += $"\n\nGesprächsführung: {evaluation.gespraechsfuehrungFeedback}";
                    if (!string.IsNullOrWhiteSpace(evaluation.empathieFeedback))
                        detailedFeedback += $"\n\nEmpathie: {evaluation.empathieFeedback}";
                    if (!string.IsNullOrWhiteSpace(evaluation.vollstaendigkeitFeedback))
                        detailedFeedback += $"\n\nVollständigkeit: {evaluation.vollstaendigkeitFeedback}";
                    
                    feedbackText.text = StripEmojis(overall + detailedFeedback);
                }
                
                Debug.Log($"[EvaluationDisplayUI] Displayed 5D evaluation: {evaluation.totalPoints}/{d2pTotalMax}, Passed: {evaluation.passed}");
            }

            // Display pass/fail status (shared between D2D and D2P)
            if (resultStatusText != null)
            {
                if (evaluation.passed)
                {
                    resultStatusText.text = "Bestanden";
                    ColorUtility.TryParseHtmlString("#38AC65", out var passedColor);
                    resultStatusText.color = passedColor;
                }
                else
                {
                    resultStatusText.text = "Nicht Bestanden";
                    ColorUtility.TryParseHtmlString("#F45356", out var failedColor);
                    resultStatusText.color = failedColor;
                }
            }
        }

        /// <summary>
        /// Internal coroutine that displays evaluation UI after a delay
        /// </summary>
      


        private void Set5DUiActive(bool active)
        {
           
            if (kommunikationText != null) kommunikationText.gameObject.SetActive(active);
            if (hoerverstehenText != null) hoerverstehenText.gameObject.SetActive(active);
            if (gespraechsfuehrungText != null) gespraechsfuehrungText.gameObject.SetActive(active);
            if (empathieText != null) empathieText.gameObject.SetActive(active);
            if (vollstaendigkeitText != null) vollstaendigkeitText.gameObject.SetActive(active);
        }
        
        /// <summary>
        /// Animates a score from 0 to target value smoothly
        /// </summary>
        // AnimateScoreDisplay removed - legacy method for slider-based UI (no longer used)
        
        
        /// <summary>
        /// Display feedback text with animation
        /// </summary>
        private IEnumerator DisplayFeedbackDelayed(ExamEvaluation evaluation, float delay)
        {
            yield return new WaitForSeconds(delay);
            
            if (feedbackText != null)
            {
                // Show overall feedback
                string overall = !string.IsNullOrWhiteSpace(evaluation.overallFeedback) ? evaluation.overallFeedback : evaluation.feedbackText;

                // Include per-dimension feedback (D2D or D2P)
                bool isD2D = evaluation.roleType == ExamEvaluation.RoleType.DoctorToDoctor;
                string perDimension = "";

                if (isD2D)
                {
                    if (evaluation.usesD2DFiveCriteria)
                    {
                        perDimension = "\n\n---\n" + BuildD2DFiveCriteriaFeedback(evaluation);
                    }
                    else if (!string.IsNullOrWhiteSpace(evaluation.sprachlicheAngemessenheitFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.inhaltlicheAngemessenheitFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.malusPatientensicherheitFeedback))
                    {
                        perDimension = "\n\n---\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.sprachlicheAngemessenheitFeedback))
                            perDimension += $"Sprachliche Angemessenheit: {evaluation.sprachlicheAngemessenheitFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.inhaltlicheAngemessenheitFeedback))
                            perDimension += $"Inhaltliche Angemessenheit: {evaluation.inhaltlicheAngemessenheitFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.malusPatientensicherheitFeedback))
                            perDimension += $"Malus Patientensicherheit: {evaluation.malusPatientensicherheitFeedback}";
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(evaluation.kommunikationFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.hoerverstehenFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.gespraechsfuehrungFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.empathieFeedback) ||
                        !string.IsNullOrWhiteSpace(evaluation.vollstaendigkeitFeedback))
                    {
                        perDimension = "\n\n---\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.kommunikationFeedback))
                            perDimension += $"Kommunikation: {evaluation.kommunikationFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.hoerverstehenFeedback))
                            perDimension += $"Hörverstehen: {evaluation.hoerverstehenFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.gespraechsfuehrungFeedback))
                            perDimension += $"Gesprächsführung: {evaluation.gespraechsfuehrungFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.empathieFeedback))
                            perDimension += $"Empathie: {evaluation.empathieFeedback}\n";
                        if (!string.IsNullOrWhiteSpace(evaluation.vollstaendigkeitFeedback))
                            perDimension += $"Vollständigkeit: {evaluation.vollstaendigkeitFeedback}";
                    }
                }

                string body = StripEmojis(overall ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(perDimension))
                    body += StripEmojis(perDimension);

                feedbackText.text = body;
            }
            
            // Display totalPoints – separate fields for D2P and D2D
            bool isD2DDelayed = evaluation.roleType == ExamEvaluation.RoleType.DoctorToDoctor;
            if (isD2DDelayed)
            {
                if (totalPointsD2DText != null)
                {
                    if (colorCodeScores)
                        totalPointsD2DText.color = evaluation.passed ? Color.green : Color.red;
                    totalPointsD2DText.text = evaluation.usesD2DFiveCriteria ? $"{evaluation.totalPoints:F1}/20" : $"{evaluation.gesamtD2D:F1}/10";
                }
                if (d2dFinalScoreText != null)
                    d2dFinalScoreText.text = evaluation.usesD2DFiveCriteria ? $"{evaluation.totalPoints:F1}/20" : $"{evaluation.gesamtD2D:F1}/10";
                Debug.Log($"[EvaluationDisplayUI] Displaying D2D gesamt: {(evaluation.usesD2DFiveCriteria ? evaluation.totalPoints : evaluation.gesamtD2D):F1}/{(evaluation.usesD2DFiveCriteria ? 20 : 10)}, Passed: {evaluation.passed}");
            }
            else
            {
                float d2pTotalMax = evaluation.totalPointsMax > 0f ? evaluation.totalPointsMax : (evaluation.totalPoints > 15f ? 20f : 15f);
                int d2pTotalMaxInt = Mathf.RoundToInt(d2pTotalMax);
                if (totalPointsD2PText != null)
                {
                    if (colorCodeScores)
                        totalPointsD2PText.color = evaluation.passed ? Color.green : Color.red;
                    totalPointsD2PText.text = $"{evaluation.totalPoints:F0}/{d2pTotalMaxInt}";
                }
                if (d2pFinalScoreText != null)
                    d2pFinalScoreText.text = $"{evaluation.totalPoints:F0}/{d2pTotalMaxInt}";
                Debug.Log($"[EvaluationDisplayUI] Displaying D2P totalPoints: {evaluation.totalPoints:F0}/{d2pTotalMaxInt}, Passed: {evaluation.passed}");
            }
        }
        
        /// <summary>
        /// Animate overall score number from 0 to target
        /// </summary>
        // AnimateOverallScore removed - legacy method for old 0-100 scoring system
        
        /// <summary>
        /// Updates a single score display instantly (no animation) - kept for backward compatibility
        /// </summary>
        // DisplayScore removed - legacy method for slider-based UI (no longer used)
        
        /// <summary>
        /// Clears all display fields
        /// </summary>
       
        /// <summary>
        /// Setup sliders (call this in Start or when initializing UI)
        /// </summary>
        private IEnumerator RepairJSONResponse(string invalidJson, string apiKey, string model, string url, System.Action<string> onSuccess)
        {
            // Simple strict schema instruction for 5D evaluation
            string systemMsg = "You are a JSON repair assistant. The user has an evaluation result in a wrong format. " +
                               "You must output JSON that STRICTLY follows this 5D evaluation schema, mapping the existing data to it best-effort:" +
                               "\\n{\\n" +
                               "  \"kommunikation\": { \"score\": (0-4), \"feedback\": \"string\" },\\n" +
                               "  \"hoerverstehen\": { \"score\": (0-4), \"feedback\": \"string\" },\\n" +
                               "  \"gespraechsfuehrung\": { \"score\": (0-4), \"feedback\": \"string\" },\\n" +
                               "  \"empathie\": { \"score\": (0-4), \"feedback\": \"string\" },\\n" +
                               "  \"vollstaendigkeit\": { \"score\": (0-4), \"feedback\": \"string\" },\\n" +
                               "  \"totalPoints\": (0-20),\\n" +
                               "  \"overallFeedback\": \"string\"\\n" +
                               "}";

            string userMsg = "Fix this JSON:\\n" + invalidJson;

            // Manual JSON construction
            string jsonBody = "{" +
                              "  \"model\": \"" + model + "\"," +
                              "  \"messages\": [" +
                              "    { \"role\": \"system\", \"content\": \"" + EscapeJsonString(systemMsg) + "\" }," +
                              "    { \"role\": \"user\", \"content\": \"" + EscapeJsonString(userMsg) + "\" }" +
                              "  ]," +
                              "  \"temperature\": 0.1," +
                              "  \"max_tokens\": 1000" +
                              "}";

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        var root = JObject.Parse(request.downloadHandler.text);
                        var contentToken = root["choices"]?[0]?["message"]?["content"];
                        var content = ExtractContentText(contentToken);
                        if (!string.IsNullOrWhiteSpace(content))
                            onSuccess?.Invoke(content);
                    }
                    catch { }
                }
            }
        }

        [Header("Alternative UI (Compact View)")]
        [SerializeField] private TextMeshProUGUI resultStatusText;   // Shows "Bestanden" (green) or "nicht Bestanden" (red)
        
        [Header("Individual Score Text Fields (5D) – D2P")]
        [SerializeField] private TextMeshProUGUI kommunikationScoreText;        // "X/3" or "X/4" format
        [SerializeField] private TextMeshProUGUI hoerverstehenScoreText;       // "X/3" or "X/4" format
        [SerializeField] private TextMeshProUGUI gespraechsfuehrungScoreText;   // "X/3" or "X/4" format
        [SerializeField] private TextMeshProUGUI empathieScoreText;            // "X/3" or "X/4" format
        [SerializeField] private TextMeshProUGUI vollstaendigkeitScoreText;    // "X/3" or "X/4" format
        [SerializeField] private TextMeshProUGUI d2pFinalScoreText;              // "X/15" or "X/20" format (D2P Gesamt)

        [Header("Individual Score Text Fields (3D) – D2D")]
        [SerializeField] private TextMeshProUGUI sprachlicheAngemessenheitScoreText;   // "X/7" format
        [SerializeField] private TextMeshProUGUI inhaltlicheAngemessenheitScoreText;   // "X/3" format
        [SerializeField] private TextMeshProUGUI malusPatientensicherheitScoreText;    // "X/5" format
        [SerializeField] private TextMeshProUGUI d2dFinalScoreText;                    // "X/10" format (D2D Gesamt)

        /// <summary>
        /// Update the compact alternative UI with scores and pass/fail status (D2P 5D version)
        /// </summary>
        public void UpdateAlternativeUI(float kommunikation, float hoerverstehen, float gespraechsfuehrung, float empathie, float vollstaendigkeit, bool passed)
        {
            // Show D2P panel, hide D2D panel
            if (d2pResultsPanel != null) d2pResultsPanel.SetActive(true);
            if (d2dResultsPanel != null) d2dResultsPanel.SetActive(false);

            float d2pTotalMax = ResolveD2PTotalMax(kommunikation + hoerverstehen + gespraechsfuehrung + empathie + vollstaendigkeit, kommunikation, hoerverstehen, gespraechsfuehrung, empathie, vollstaendigkeit);
            int d2pPerSkillMaxInt = Mathf.RoundToInt(ResolveD2PPerSkillMax(d2pTotalMax));
            int d2pTotalMaxInt = Mathf.RoundToInt(d2pTotalMax);

            // Individual score text fields (5D)
            if (kommunikationScoreText != null)
                kommunikationScoreText.text = $"{Mathf.RoundToInt(kommunikation)} / {d2pPerSkillMaxInt}";
            
            if (hoerverstehenScoreText != null)
                hoerverstehenScoreText.text = $"{Mathf.RoundToInt(hoerverstehen)} / {d2pPerSkillMaxInt}";
            
            if (gespraechsfuehrungScoreText != null)
                gespraechsfuehrungScoreText.text = $"{Mathf.RoundToInt(gespraechsfuehrung)} / {d2pPerSkillMaxInt}";
            
            if (empathieScoreText != null)
                empathieScoreText.text = $"{Mathf.RoundToInt(empathie)} / {d2pPerSkillMaxInt}";
            
            if (vollstaendigkeitScoreText != null)
                vollstaendigkeitScoreText.text = $"{Mathf.RoundToInt(vollstaendigkeit)} / {d2pPerSkillMaxInt}";
            
            // Final score (legacy /15, newer /20)
            if (d2pFinalScoreText != null)
            {
                float totalScore = Mathf.RoundToInt(kommunikation + hoerverstehen + gespraechsfuehrung + empathie + vollstaendigkeit);
                d2pFinalScoreText.text = $"{totalScore} / {d2pTotalMaxInt}";
            }

            if (resultStatusText != null)
            {
                if (passed)
                {
                    resultStatusText.text = "Bestanden";
                    ColorUtility.TryParseHtmlString("#38AC65", out var passedColor2);
                    resultStatusText.color = passedColor2;
                }
                else
                {
                    resultStatusText.text = "nicht Bestanden";
                    ColorUtility.TryParseHtmlString("#F45356", out var failedColor2);
                    resultStatusText.color = failedColor2;
                }
            }
        }

        /// <summary>
        /// Update the compact alternative UI with D2D 3D scores and pass/fail status
        /// </summary>
        public void UpdateAlternativeUI_D2D(float sprachliche, float inhaltliche, float malus, float gesamt, bool passed)
        {
            // Show D2D panel, hide D2P panel
            if (d2pResultsPanel != null) d2pResultsPanel.SetActive(false);
            if (d2dResultsPanel != null) d2dResultsPanel.SetActive(true);

            if (sprachlicheAngemessenheitScoreText != null)
                sprachlicheAngemessenheitScoreText.text = $"{sprachliche:F1} / 7";

            if (inhaltlicheAngemessenheitScoreText != null)
                inhaltlicheAngemessenheitScoreText.text = $"{inhaltliche:F1} / 3";

            if (malusPatientensicherheitScoreText != null)
            {
                malusPatientensicherheitScoreText.text = $"-{malus:F1} / 5";
                ColorUtility.TryParseHtmlString(malus > 0f ? "#F45356" : "#38AC65", out var malusColor);
                malusPatientensicherheitScoreText.color = malusColor;
            }

            if (d2dFinalScoreText != null)
                d2dFinalScoreText.text = $"{gesamt:F1} / 10";

            if (resultStatusText != null)
            {
                if (passed)
                {
                    resultStatusText.text = "Bestanden";
                    ColorUtility.TryParseHtmlString("#38AC65", out var passedColor2);
                    resultStatusText.color = passedColor2;
                }
                else
                {
                    resultStatusText.text = "nicht Bestanden";
                    ColorUtility.TryParseHtmlString("#F45356", out var failedColor2);
                    resultStatusText.color = failedColor2;
                }
            }
        }

        private void Start()
        {
            CacheInitialUiColors();
            
        }
    }
}

