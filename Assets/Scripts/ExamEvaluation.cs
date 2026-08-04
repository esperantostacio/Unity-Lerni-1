using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace MedicalExam
{
    /// <summary>
    /// Handles parsing and storage of medical exam evaluation results
    /// </summary>
    [Serializable]
    public class ExamEvaluation
    {
        public string scenarioName;
        public RoleType roleType;
        public DateTime examDate;
        
        [Header("Legacy Scores (out of 5) - Deprecated")]
        public float terminologie;      // Medical terminology
        public float verstaendlichkeit; // Clarity/Understandability
        public float aussprache;        // Pronunciation

        [Header("5D Evaluation Scores (out of 3)")]
        public float kommunikation;              // Communication & Language Competence (0-3)
        public float hoerverstehen;              // Listening & Information Processing (0-3)
        public float gespraechsfuehrung;         // Conversation Management & Structure (0-3)
        public float empathie;                   // Empathy & Patient Care (0-3)
        public float vollstaendigkeit;           // Completeness of Medical History (0-3)

        [Header("Legacy Per-skill Feedback - Deprecated")]
        [TextArea(1, 4)]
        public string terminologieFeedback;
        [TextArea(1, 4)]
        public string verstaendlichkeitFeedback;
        [TextArea(1, 4)]
        public string ausspracheFeedback;

        [Header("5D Evaluation Feedback")]
        [TextArea(1, 3)]
        public string kommunikationFeedback;
        [TextArea(1, 3)]
        public string hoerverstehenFeedback;
        [TextArea(1, 3)]
        public string gespraechsfuehrungFeedback;
        [TextArea(1, 3)]
        public string empathieFeedback;
        [TextArea(1, 3)]
        public string vollstaendigkeitFeedback;

        [Header("D2D 3D Evaluation Scores")]
        public float sprachlicheAngemessenheit;      // Sprachliche Angemessenheit (0-7)
        public float inhaltlicheAngemessenheit;       // Inhaltliche Angemessenheit (0-3)
        public float malusPatientensicherheit;        // Malus Patientensicherheit (0-5, negative deduction)
        public float gesamtD2D;                       // Legacy Gesamt D2D (0-10) or CEO grid total (0-20)

        [Header("D2D 3D Evaluation Feedback")]
        [TextArea(1, 3)]
        public string sprachlicheAngemessenheitFeedback;
        [TextArea(1, 3)]
        public string inhaltlicheAngemessenheitFeedback;
        [TextArea(1, 3)]
        public string malusPatientensicherheitFeedback;

        [Header("D2D CEO Evaluation Scores")]
        public bool usesD2DFiveCriteria;
        public float d2dContent;
        public float d2dConversation;
        public float d2dVocabulary;
        public float d2dGrammar;
        public float d2dPronunciation;
        public float d2dTotalMax = 10f;
        public float d2dPassingThreshold = 6f;

        [Header("D2D CEO Evaluation Feedback")]
        [TextArea(1, 3)]
        public string d2dContentFeedback;
        [TextArea(1, 3)]
        public string d2dConversationFeedback;
        [TextArea(1, 3)]
        public string d2dVocabularyFeedback;
        [TextArea(1, 3)]
        public string d2dGrammarFeedback;
        [TextArea(1, 3)]
        public string d2dPronunciationFeedback;
        [TextArea(1, 3)]
        public string d2dVerdict;
        [TextArea(2, 6)]
        public string d2dStrengthsSummary;
        [TextArea(2, 6)]
        public string d2dAreasForImprovementSummary;
        [TextArea(2, 6)]
        public string d2dCriticalErrorsSummary;

        [Header("Overall")]
        [Range(0, 100)]
        public float overallScore;

        [Range(0, 20)]
        public float totalPoints;  // D2P supports legacy 15-point and newer 20-point grids; D2D CEO grid also uses /20

        [Header("D2P Scale")]
        public float totalPointsMax = 15f; // D2P legacy grid uses 15, newer grids use 20

        [TextArea(2, 8)]
        public string overallFeedback;
        
        [TextArea(3, 10)]
        public string feedbackText;
        
        public bool passed;  // BESTANDEN / NICHT BESTANDEN
        
        [Header("Conversation Log")]
        [TextArea(5, 20)]
        public string conversationTranscript;
        
        public ExamEvaluation()
        {
            examDate = DateTime.Now;
        }
        
        public enum RoleType
        {
            DoctorToDoctor,
            DoctorToPatient
        }
        
        /// <summary>
        /// Parses evaluation text from AI response - supports both JSON 5D and legacy regex formats
        /// </summary>
        public static ExamEvaluation ParseFromAIResponse(string aiResponse, string conversationLog, string scenarioName, RoleType role)
        {
            ExamEvaluation eval = new ExamEvaluation
            {
                scenarioName = scenarioName,
                roleType = role,
                conversationTranscript = conversationLog
            };
            
            try
            {
                // Try D2D 3D parsing first if role is DoctorToDoctor
                if (role == RoleType.DoctorToDoctor && TryParseJSON3D_D2D(aiResponse, eval))
                {
                    Debug.Log($"[ExamEvaluation] ✓ Successfully parsed D2D 3D JSON: Sprache={eval.sprachlicheAngemessenheit}/7 Inhalt={eval.inhaltlicheAngemessenheit}/3 Malus={eval.malusPatientensicherheit}/5 Gesamt={eval.gesamtD2D}/15");
                    return eval;
                }

                // Try JSON parsing first (for 5D evaluation format)
                if (TryParseJSON5D(aiResponse, eval))
                {
                    Debug.Log($"[ExamEvaluation] ✓ Successfully parsed 5D JSON: K={eval.kommunikation} H={eval.hoerverstehen} G={eval.gespraechsfuehrung} E={eval.empathie} V={eval.vollstaendigkeit}");
                    return eval;
                }

                // Fallback to regex parsing (legacy 3D format)
                Debug.Log("[ExamEvaluation] JSON parsing failed, falling back to legacy regex parsing...");
                
                // Parse Terminologie (using InvariantCulture for reliable parsing)
                Match termMatch = Regex.Match(aiResponse, @"Terminologie[:\s]+(\d+(?:\.\d+)?)\s*/?\s*5", RegexOptions.IgnoreCase);
                if (termMatch.Success)
                {
                    float.TryParse(termMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out eval.terminologie);
                }
                
                // Parse Verständlichkeit
                Match verstMatch = Regex.Match(aiResponse, @"Verst[äa]ndlichkeit[:\s]+(\d+(?:\.\d+)?)\s*/?\s*5", RegexOptions.IgnoreCase);
                if (verstMatch.Success)
                {
                    float.TryParse(verstMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out eval.verstaendlichkeit);
                }
                
                // Parse Aussprache
                Match aussMatch = Regex.Match(aiResponse, @"Aussprache[:\s]+(\d+(?:\.\d+)?)\s*/?\s*5", RegexOptions.IgnoreCase);
                if (aussMatch.Success)
                {
                    float.TryParse(aussMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out eval.aussprache);
                }
                
                // Parse Overall Score
                Match overallMatch = Regex.Match(aiResponse, @"Overall\s+Score[:\s]+(\d+(?:\.\d+)?)\s*/?\s*100", RegexOptions.IgnoreCase);
                if (overallMatch.Success)
                {
                    float.TryParse(overallMatch.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out eval.overallScore);
                }
                else
                {
                    // Calculate from average if not provided
                    eval.overallScore = ((eval.terminologie + eval.verstaendlichkeit + eval.aussprache) / 15f) * 100f;
                }
                
                // Extract feedback text
                Match feedbackMatch = Regex.Match(aiResponse, @"Feedback[:\s]+(.*)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (feedbackMatch.Success)
                {
                    eval.feedbackText = feedbackMatch.Groups[1].Value.Trim();
                }
                else
                {
                    // If no specific feedback section, use the entire response
                    eval.feedbackText = aiResponse;
                }

                // For legacy parsers that only provide one feedback block.
                if (string.IsNullOrWhiteSpace(eval.overallFeedback))
                {
                    eval.overallFeedback = eval.feedbackText;
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[ExamEvaluation] Error parsing evaluation: {e.Message}");
                eval.feedbackText = aiResponse; // Store raw response as fallback
                eval.overallFeedback = eval.feedbackText;
            }
            
            return eval;
        }

        /// <summary>
        /// Attempts to parse 5D JSON evaluation format
        /// </summary>
        private static bool TryParseJSON5D(string jsonText, ExamEvaluation eval)
        {
            try
            {
                // Find JSON object in the response
                int startIdx = jsonText.IndexOf("{");
                int endIdx = jsonText.LastIndexOf("}");
                
                if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
                    return false;
                
                string jsonStr = jsonText.Substring(startIdx, endIdx - startIdx + 1);
                var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(jsonStr);

                // Helper to extract score from nested structure
                float ExtractScore(Newtonsoft.Json.Linq.JToken token)
                {
                    if (token == null) return 0f;

                    // If it's a direct number
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Float || token.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                    {
                        return token.ToObject<float>();
                    }

                    // If it's an object with "score" property
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var scoreToken = token["score"];
                        if (scoreToken != null && (scoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || scoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                            return scoreToken.ToObject<float>();
                    }

                    return 0f;
                }

                // Helper to extract feedback
                string ExtractFeedback(Newtonsoft.Json.Linq.JToken token)
                {
                    if (token == null) return string.Empty;

                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        return token.ToObject<string>();

                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var feedbackToken = token["feedback"];
                        if (feedbackToken != null && feedbackToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
                            return feedbackToken.ToObject<string>() ?? string.Empty;
                    }

                    return string.Empty;
                }

                // Parse 5D scores
                eval.kommunikation = ExtractScore(jsonObj["kommunikation"]);
                eval.hoerverstehen = ExtractScore(jsonObj["hoerverstehen"]);
                eval.gespraechsfuehrung = ExtractScore(jsonObj["gespraechsfuehrung"]);
                eval.empathie = ExtractScore(jsonObj["empathie"]);
                eval.vollstaendigkeit = ExtractScore(jsonObj["vollstaendigkeit"]);
                eval.aussprache = ExtractScore(jsonObj["aussprache"]);

                Debug.Log($"[ExamEvaluation] 5D Scores extracted: K={eval.kommunikation} H={eval.hoerverstehen} G={eval.gespraechsfuehrung} E={eval.empathie} V={eval.vollstaendigkeit} A={eval.aussprache}");

                // Parse feedback
                eval.kommunikationFeedback = ExtractFeedback(jsonObj["kommunikation"]);
                eval.hoerverstehenFeedback = ExtractFeedback(jsonObj["hoerverstehen"]);
                eval.gespraechsfuehrungFeedback = ExtractFeedback(jsonObj["gespraechsfuehrung"]);
                eval.empathieFeedback = ExtractFeedback(jsonObj["empathie"]);
                eval.vollstaendigkeitFeedback = ExtractFeedback(jsonObj["vollstaendigkeit"]);
                eval.ausspracheFeedback = ExtractFeedback(jsonObj["aussprache"]);

                Debug.Log($"[ExamEvaluation] 5D Feedback extracted (sample): '{(eval.kommunikationFeedback?.Length > 30 ? eval.kommunikationFeedback.Substring(0, 30) + "..." : eval.kommunikationFeedback)}'");

                // Parse overall score
                var overallScoreToken = jsonObj["overallScore"];
                if (overallScoreToken != null && (overallScoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || overallScoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                    eval.overallScore = overallScoreToken.ToObject<float>();

                // Parse overall feedback
                var overallFeedbackToken = jsonObj["overallFeedback"];
                if (overallFeedbackToken != null && overallFeedbackToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    eval.overallFeedback = overallFeedbackToken.ToObject<string>();

                // Parse total points (5D: 0-18 max)
                var totalPointsToken = jsonObj["totalPoints"];
                if (totalPointsToken != null && (totalPointsToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || totalPointsToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                    eval.totalPoints = totalPointsToken.ToObject<float>();

                // Parse passed flag
                var passedToken = jsonObj["passed"];
                if (passedToken != null && passedToken.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                    eval.passed = passedToken.ToObject<bool>();

                Debug.Log($"[ExamEvaluation] Overall: score={eval.overallScore}/100, points={eval.totalPoints}/18, passed={eval.passed}");

                // Build feedback text if needed
                if (string.IsNullOrWhiteSpace(eval.feedbackText))
                {
                    eval.feedbackText = eval.overallFeedback;
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ExamEvaluation] JSON 5D parsing failed: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Attempts to parse D2D JSON evaluation format.
        /// Supports both the legacy 3D schema and the CEO 5-criterion /20 schema.
        /// </summary>
        private static bool TryParseJSON3D_D2D(string jsonText, ExamEvaluation eval)
        {
            try
            {
                int startIdx = jsonText.IndexOf("{");
                int endIdx = jsonText.LastIndexOf("}");
                if (startIdx < 0 || endIdx < 0 || endIdx < startIdx)
                    return false;

                string jsonStr = jsonText.Substring(startIdx, endIdx - startIdx + 1);
                var jsonObj = Newtonsoft.Json.Linq.JObject.Parse(jsonStr);

                // Check for CEO 5-criterion schema first.
                var criteriaObj = jsonObj["criteria"] as Newtonsoft.Json.Linq.JObject;
                var contentToken = criteriaObj?["content"] ?? jsonObj["content"];
                var conversationToken = criteriaObj?["conversation"] ?? criteriaObj?["interaction"] ?? criteriaObj?["conversationalAbility"] ?? jsonObj["conversation"] ?? jsonObj["interaction"] ?? jsonObj["conversationalAbility"];
                var vocabularyToken = criteriaObj?["vocabulary"] ?? criteriaObj?["expressiveness"] ?? criteriaObj?["expressivenessVocabulary"] ?? jsonObj["vocabulary"] ?? jsonObj["expressiveness"] ?? jsonObj["expressivenessVocabulary"];
                var grammarToken = criteriaObj?["grammar"] ?? jsonObj["grammar"];
                var pronunciationToken = criteriaObj?["pronunciation"] ?? criteriaObj?["pronunciationIntonation"] ?? jsonObj["pronunciation"] ?? jsonObj["pronunciationIntonation"];
                bool hasFiveCriteria = contentToken != null || conversationToken != null || vocabularyToken != null || grammarToken != null || pronunciationToken != null || jsonObj["totalScore"] != null;

                // Check for legacy D2D-specific keys
                var sprachToken = jsonObj["sprachlicheAngemessenheit"];
                var inhaltToken = jsonObj["inhaltlicheAngemessenheit"];
                var malusToken = jsonObj["malusPatientensicherheit"];

                // Must match either the CEO schema or the legacy schema.
                if (!hasFiveCriteria && sprachToken == null && inhaltToken == null)
                    return false;

                float ExtractScore(Newtonsoft.Json.Linq.JToken token)
                {
                    if (token == null) return 0f;
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Float || token.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                        return token.ToObject<float>();
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var scoreToken = token["score"];
                        if (scoreToken != null && (scoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || scoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                            return scoreToken.ToObject<float>();
                    }
                    return 0f;
                }

                string ExtractFeedback(Newtonsoft.Json.Linq.JToken token)
                {
                    if (token == null) return string.Empty;
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        return token.ToObject<string>();
                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Object)
                    {
                        var feedbackToken = token["feedback"];
                        if (feedbackToken != null && feedbackToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
                            return feedbackToken.ToObject<string>() ?? string.Empty;
                    }
                    return string.Empty;
                }

                string ExtractStringArray(Newtonsoft.Json.Linq.JToken token)
                {
                    if (token == null)
                        return string.Empty;

                    if (token.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                    {
                        var lines = new List<string>();
                        foreach (var item in token)
                        {
                            string value = item?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(value))
                                lines.Add("- " + value);
                        }

                        return lines.Count == 0 ? string.Empty : string.Join("\n", lines);
                    }

                    return token.ToString().Trim();
                }

                if (hasFiveCriteria)
                {
                    eval.usesD2DFiveCriteria = true;
                    eval.d2dContent = Mathf.Clamp(ExtractScore(contentToken), 0f, 3.99f);
                    eval.d2dConversation = Mathf.Clamp(ExtractScore(conversationToken), 0f, 3.99f);
                    eval.d2dVocabulary = Mathf.Clamp(ExtractScore(vocabularyToken), 0f, 3.99f);
                    eval.d2dGrammar = Mathf.Clamp(ExtractScore(grammarToken), 0f, 3.99f);
                    eval.d2dPronunciation = Mathf.Clamp(ExtractScore(pronunciationToken), 0f, 3.99f);

                    eval.d2dContentFeedback = ExtractFeedback(contentToken);
                    eval.d2dConversationFeedback = ExtractFeedback(conversationToken);
                    eval.d2dVocabularyFeedback = ExtractFeedback(vocabularyToken);
                    eval.d2dGrammarFeedback = ExtractFeedback(grammarToken);
                    eval.d2dPronunciationFeedback = ExtractFeedback(pronunciationToken);

                    var totalScoreToken = jsonObj["totalScore"] ?? jsonObj["total"] ?? jsonObj["totalPoints"];
                    float totalScore = 0f;
                    if (totalScoreToken != null && (totalScoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || totalScoreToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                        totalScore = totalScoreToken.ToObject<float>();
                    if (totalScore <= 0f)
                        totalScore = eval.d2dContent + eval.d2dConversation + eval.d2dVocabulary + eval.d2dGrammar + eval.d2dPronunciation;

                    float passingThreshold = 12f;
                    var passingThresholdToken = jsonObj["passingThreshold"] ?? jsonObj["passThreshold"];
                    if (passingThresholdToken != null && (passingThresholdToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || passingThresholdToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                        passingThreshold = passingThresholdToken.ToObject<float>();

                    eval.d2dTotalMax = 20f;
                    eval.d2dPassingThreshold = passingThreshold;
                    eval.gesamtD2D = Mathf.Clamp(totalScore, 0f, eval.d2dTotalMax);
                    eval.totalPoints = eval.gesamtD2D;
                    eval.totalPointsMax = eval.d2dTotalMax;

                    var overallFeedbackToken5 = jsonObj["overallFeedback"] ?? jsonObj["summary"] ?? jsonObj["feedback"];
                    if (overallFeedbackToken5 != null && overallFeedbackToken5.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        eval.overallFeedback = overallFeedbackToken5.ToObject<string>();

                    var verdictToken = jsonObj["finalVerdict"] ?? jsonObj["verdict"];
                    if (verdictToken != null && verdictToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
                        eval.d2dVerdict = verdictToken.ToObject<string>();

                    eval.d2dStrengthsSummary = ExtractStringArray(jsonObj["strengths"]);
                    eval.d2dAreasForImprovementSummary = ExtractStringArray(jsonObj["areasForImprovement"] ?? jsonObj["areas_for_improvement"]);
                    eval.d2dCriticalErrorsSummary = ExtractStringArray(jsonObj["criticalErrors"] ?? jsonObj["critical_errors"]);

                    var passedToken5 = jsonObj["passed"];
                    if (passedToken5 != null && passedToken5.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                        eval.passed = passedToken5.ToObject<bool>();
                    else if (!string.IsNullOrWhiteSpace(eval.d2dVerdict))
                        eval.passed = eval.d2dVerdict.Trim().Equals("PASSED", StringComparison.OrdinalIgnoreCase);
                    else
                        eval.passed = eval.gesamtD2D >= eval.d2dPassingThreshold;

                    if (string.IsNullOrWhiteSpace(eval.feedbackText))
                    {
                        var feedbackBuilder = new System.Text.StringBuilder();
                        if (!string.IsNullOrWhiteSpace(eval.overallFeedback))
                            feedbackBuilder.Append(eval.overallFeedback.Trim());
                        if (!string.IsNullOrWhiteSpace(eval.d2dStrengthsSummary))
                            feedbackBuilder.Append("\n\nStärken:\n").Append(eval.d2dStrengthsSummary);
                        if (!string.IsNullOrWhiteSpace(eval.d2dAreasForImprovementSummary))
                            feedbackBuilder.Append("\n\nVerbesserung:\n").Append(eval.d2dAreasForImprovementSummary);
                        if (!string.IsNullOrWhiteSpace(eval.d2dCriticalErrorsSummary))
                            feedbackBuilder.Append("\n\nKritische Fehler:\n").Append(eval.d2dCriticalErrorsSummary);
                        eval.feedbackText = feedbackBuilder.ToString().Trim();
                    }

                    Debug.Log($"[ExamEvaluation] D2D CEO: Content={eval.d2dContent}/3.99, Conversation={eval.d2dConversation}/3.99, Vocabulary={eval.d2dVocabulary}/3.99, Grammar={eval.d2dGrammar}/3.99, Pronunciation={eval.d2dPronunciation}/3.99, Total={eval.gesamtD2D}/20, Passed={eval.passed}");
                    return true;
                }

                eval.sprachlicheAngemessenheit = Mathf.Clamp(ExtractScore(sprachToken), 0f, 7f);
                eval.inhaltlicheAngemessenheit = Mathf.Clamp(ExtractScore(inhaltToken), 0f, 3f);
                eval.malusPatientensicherheit = Mathf.Clamp(ExtractScore(malusToken), 0f, 5f);

                eval.sprachlicheAngemessenheitFeedback = ExtractFeedback(sprachToken);
                eval.inhaltlicheAngemessenheitFeedback = ExtractFeedback(inhaltToken);
                eval.malusPatientensicherheitFeedback = ExtractFeedback(malusToken);

                // Parse legacy gesamt (total)
                var gesamtToken = jsonObj["gesamt"];
                if (gesamtToken != null && (gesamtToken.Type == Newtonsoft.Json.Linq.JTokenType.Float || gesamtToken.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                    eval.gesamtD2D = Mathf.Clamp(gesamtToken.ToObject<float>(), 0f, 10f);
                else
                    eval.gesamtD2D = Mathf.Clamp(eval.sprachlicheAngemessenheit + eval.inhaltlicheAngemessenheit - eval.malusPatientensicherheit, 0f, 10f);

                // Also set totalPoints for compatibility
                eval.totalPoints = eval.gesamtD2D;
                eval.d2dTotalMax = 10f;
                eval.d2dPassingThreshold = 6f;

                // Parse overall feedback
                var overallFeedbackToken = jsonObj["overallFeedback"];
                if (overallFeedbackToken != null && overallFeedbackToken.Type == Newtonsoft.Json.Linq.JTokenType.String)
                    eval.overallFeedback = overallFeedbackToken.ToObject<string>();

                // Parse passed flag
                var passedToken = jsonObj["passed"];
                if (passedToken != null && passedToken.Type == Newtonsoft.Json.Linq.JTokenType.Boolean)
                    eval.passed = passedToken.ToObject<bool>();
                else
                    eval.passed = eval.gesamtD2D >= eval.d2dPassingThreshold;

                if (string.IsNullOrWhiteSpace(eval.feedbackText))
                    eval.feedbackText = eval.overallFeedback;

                Debug.Log($"[ExamEvaluation] D2D 3D: Sprache={eval.sprachlicheAngemessenheit}/7, Inhalt={eval.inhaltlicheAngemessenheit}/3, Malus={eval.malusPatientensicherheit}/5, Gesamt={eval.gesamtD2D}/10, Passed={eval.passed}");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[ExamEvaluation] D2D 3D JSON parsing failed: {e.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Formats evaluation for display
        /// </summary>
        public string GetFormattedDisplay()
        {
            string perSkill = "";
            if (!string.IsNullOrWhiteSpace(terminologieFeedback) || !string.IsNullOrWhiteSpace(verstaendlichkeitFeedback) || !string.IsNullOrWhiteSpace(ausspracheFeedback))
            {
                perSkill = $@"--- PER-SKILL FEEDBACK ---
Terminologie: {terminologieFeedback}
Verst e4ndlichkeit: {verstaendlichkeitFeedback}
Aussprache: {ausspracheFeedback}

";
            }

            return $@"=== MEDICAL EXAM EVALUATION ===

Scenario: {scenarioName}
Role: {roleType}
Date: {examDate:yyyy-MM-dd HH:mm}

--- SCORES ---
Terminologie: {terminologie:F1}/5
Verständlichkeit: {verstaendlichkeit:F1}/5
Aussprache: {aussprache:F1}/5

Overall Score: {overallScore:F0}/100

--- FEEDBACK ---
{(string.IsNullOrWhiteSpace(overallFeedback) ? feedbackText : overallFeedback)}

{perSkill}

===============================";
        }
        
        /// <summary>
        /// Gets color based on score
        /// </summary>
        public static Color GetScoreColor(float score, float maxScore)
        {
            float percentage = (score / maxScore) * 100f;
            
            if (percentage >= 80) return new Color(0.2f, 0.8f, 0.2f); // Green
            if (percentage >= 60) return new Color(0.8f, 0.8f, 0.2f); // Yellow
            if (percentage >= 40) return new Color(1f, 0.6f, 0f);     // Orange
            return new Color(0.8f, 0.2f, 0.2f);                       // Red
        }
        
        /// <summary>
        /// Uses GPT-4 to reliably parse evaluation text into structured format
        /// </summary>
        public static IEnumerator ParseWithGPT(string aiResponse, string conversationLog, string scenarioName, RoleType role, string openAIKey, System.Action<ExamEvaluation> onComplete)
        {
            string gptPrompt = $@"Extract the medical exam evaluation scores from the following text and return ONLY a valid JSON object with this exact format:
{{
  ""terminologie"": 3.5,
  ""verstaendlichkeit"": 4.0,
  ""aussprache"": 3.0,
  ""overallScore"": 75,
  ""feedback"": ""The full feedback text here""
}}

Rules:
- terminologie, verstaendlichkeit, aussprache: numbers between 0-5
- overallScore: number between 0-100
- feedback: string with the evaluation feedback
- Return ONLY valid JSON, no other text
- If a score is missing, estimate it based on the feedback
- If no scores found at all, use reasonable defaults based on the feedback tone

Evaluation text to parse:
{aiResponse}";

            // Create request to OpenAI API
            string url = "https://api.openai.com/v1/chat/completions";
            
            var requestBody = new
            {
                model = "gpt-4",
                messages = new[]
                {
                    new { role = "system", content = "You are a precise data extraction assistant. Extract evaluation scores and return only valid JSON." },
                    new { role = "user", content = gptPrompt }
                },
                temperature = 0.1,
                max_tokens = 500
            };
            
            string jsonBody = JsonUtility.ToJson(requestBody);
            // Note: JsonUtility doesn't handle nested arrays well, so we'll build JSON manually
            jsonBody = $@"{{
  ""model"": ""gpt-4"",
  ""messages"": [
    {{""role"": ""system"", ""content"": ""You are a precise data extraction assistant. Extract evaluation scores and return only valid JSON.""}},
    {{""role"": ""user"", ""content"": {EscapeJsonString(gptPrompt)}}}
  ],
  ""temperature"": 0.1,
  ""max_tokens"": 500
}}";
            
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            
            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {openAIKey}");
                
                yield return request.SendWebRequest();
                
                ExamEvaluation eval = new ExamEvaluation
                {
                    scenarioName = scenarioName,
                    roleType = role,
                    conversationTranscript = conversationLog
                };
                
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string response = request.downloadHandler.text;
                        Debug.Log($"[ExamEvaluation] GPT parsing response: {response}");
                        
                        // Extract the JSON from GPT response
                        var gptResponse = JsonUtility.FromJson<GPTResponse>(response);
                        if (gptResponse?.choices != null && gptResponse.choices.Length > 0)
                        {
                            string extractedJson = gptResponse.choices[0].message.content.Trim();
                            
                            // Remove markdown code blocks if present
                            extractedJson = extractedJson.Replace("```json", "").Replace("```", "").Trim();
                            
                            Debug.Log($"[ExamEvaluation] Extracted JSON: {extractedJson}");
                            
                            // Parse the evaluation JSON
                            var parsedEval = JsonUtility.FromJson<EvaluationData>(extractedJson);
                            
                            eval.terminologie = parsedEval.terminologie;
                            eval.verstaendlichkeit = parsedEval.verstaendlichkeit;
                            eval.aussprache = parsedEval.aussprache;
                            eval.overallScore = parsedEval.overallScore;
                            eval.feedbackText = parsedEval.feedback;
                            
                            Debug.Log($"[ExamEvaluation] Successfully parsed with GPT: T={eval.terminologie}, V={eval.verstaendlichkeit}, A={eval.aussprache}, Overall={eval.overallScore}");
                        }
                        else
                        {
                            Debug.LogError("[ExamEvaluation] GPT response has no choices");
                            eval.feedbackText = aiResponse;
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[ExamEvaluation] Error parsing GPT response: {e.Message}");
                        // Fallback to regex parsing
                        eval = ParseFromAIResponse(aiResponse, conversationLog, scenarioName, role);
                    }
                }
                else
                {
                    Debug.LogError($"[ExamEvaluation] GPT API error: {request.error}");
                    // Fallback to regex parsing
                    eval = ParseFromAIResponse(aiResponse, conversationLog, scenarioName, role);
                }
                
                onComplete?.Invoke(eval);
            }
        }
        
        public static string EscapeJsonString(string str)
        {
            return "\"" + str.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }
        
        [Serializable]
        public class GPTResponse
        {
            public GPTChoice[] choices;
        }
        
        [Serializable]
        public class GPTChoice
        {
            public GPTMessage message;
        }
        
        [Serializable]
        public class GPTMessage
        {
            public string content;
        }
        
        [Serializable]
        public class EvaluationData
        {
            public float terminologie;
            public float verstaendlichkeit;
            public float aussprache;
            public float overallScore;
            public string feedback;
        }
    }
    
    /// <summary>
    /// Stores history of exam evaluations
    /// </summary>
    [CreateAssetMenu(fileName = "ExamHistory", menuName = "Medical Exam/Exam History", order = 2)]
    public class ExamHistory : ScriptableObject
    {
        public List<ExamEvaluation> evaluations = new List<ExamEvaluation>();
        
        public void AddEvaluation(ExamEvaluation eval)
        {
            evaluations.Add(eval);
            Debug.Log($"[ExamHistory] Added evaluation. Total evaluations: {evaluations.Count}");
        }
        
        public List<ExamEvaluation> GetEvaluationsByScenario(string scenarioName)
        {
            return evaluations.FindAll(e => e.scenarioName == scenarioName);
        }
        
        public List<ExamEvaluation> GetEvaluationsByRole(ExamEvaluation.RoleType role)
        {
            return evaluations.FindAll(e => e.roleType == role);
        }
        
        public ExamEvaluation GetLatestEvaluation()
        {
            if (evaluations.Count == 0) return null;
            return evaluations[evaluations.Count - 1];
        }
        
        public float GetAverageOverallScore()
        {
            if (evaluations.Count == 0) return 0f;
            
            float sum = 0f;
            foreach (var eval in evaluations)
            {
                sum += eval.overallScore;
            }
            return sum / evaluations.Count;
        }
        
        public void ClearHistory()
        {
            evaluations.Clear();
            Debug.Log("[ExamHistory] History cleared");
        }
    }
}
