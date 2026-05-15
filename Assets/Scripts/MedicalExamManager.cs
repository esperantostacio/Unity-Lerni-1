using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.Serialization;
using TMPro;
using OpenAI;
using Newtonsoft.Json.Linq;
using MedicalExam.RAG;
//read the script in START_HERE.md in the repo to udnerstand how script work and you consume less token and undertnat the porject
namespace MedicalExam
{
    public enum ExamPhase
    {
        Greeting,
        Presentation,
        Discussion,
        Terms,
        Anamnesis,
        Summary
    }

    [System.Serializable]
    public class PhaseConfig
    {
        public ExamPhase phase;
        public float durationSeconds = 60f;
        [Tooltip("CSV key for the system instruction (e.g., realtime.phase.greeting)")]
        public string systemInstruction = "realtime.phase.greeting";
        public bool useTools = true;
    }

    /// <summary>
    /// Manages medical oral exam flow, UI, and evaluation
    /// </summary>
    public class MedicalExamManager : MonoBehaviour
    {
        [Header("Phase Management")]
        [SerializeField] private bool enablePhaseManagement = true;
        [SerializeField] private List<PhaseConfig> phaseConfigs;
        private ExamPhase currentPhase = ExamPhase.Greeting;
        private float phaseTimer = 0f;

        // Conversation gating
        private bool _conversationReady = false;

        [Header("OpenAI Realtime API")]
        [Tooltip("If true, uses the new OpenAI Realtime API (WebSocket) instead of Whisper+GPT-4o.")]
        [SerializeField] private bool useOpenAIRealtime = false;
        [SerializeField] private OpenAIRealtimeClient realtimeClient;
        [SerializeField] private RealtimeMicrophone realtimeMicrophone;

        // If the user speaks before the initial AI response arrives, buffer their utterances and flush once ready.
        private readonly Queue<string> _pendingUserUtterances = new Queue<string>();

        private bool _autoMicSegmentsRunning;

        [Header("Evidence Logging")]
        [Tooltip("If enabled, appends per-turn pronunciation feedback + transcripts into a persistent text log file under Application.persistentDataPath.")]
        [SerializeField] private bool enableEvidenceLogFile = true;

        [Tooltip("File name for the evidence log (stored under Application.persistentDataPath).")]
        [SerializeField] private string evidenceLogFileName = "medical_exam_evidence_log.txt";

        private SessionEvidenceLogger _evidenceLogger;
        private bool _evidenceSessionStarted;

        private void EnsureEvidenceLoggerSessionStarted(string reason)
        {
            if (!enableEvidenceLogFile)
                return;

            if (_evidenceLogger == null)
                _evidenceLogger = new SessionEvidenceLogger(string.IsNullOrWhiteSpace(evidenceLogFileName) ? "medical_exam_evidence_log.txt" : evidenceLogFileName);

            if (_evidenceSessionStarted)
                return;

            string title = $"{selectedRole} | {GetScenarioName()} | {reason}";
            string schemaHint = selectedRole == RoleType.DoctorToDoctor
                ? "D2D — criteria: {content, conversation, vocabulary, grammar, pronunciation}, finalVerdict, totalScore (/20), strengths, areasForImprovement, criticalErrors, overallFeedback"
                : "D2P — criteria: {Kommunikation, Verständnis, Struktur, Empathie, Vollständigkeit}, totalScore (/20), generalFeedback";
            _evidenceLogger.StartSession(title, schemaHint);
            _evidenceSessionStarted = true;
            Debug.Log($"[MedicalExamManager] Evidence log started: {_evidenceLogger.LogFilePath}");
        }

        private List<PhaseConfig> BuildDefaultPhaseConfigsForRole(RoleType role)
        {
            var configs = new List<PhaseConfig>();

            if (role == RoleType.DoctorToDoctor)
            {
                // D2D fixed 4-phase flow before evaluation.
                configs.Add(new PhaseConfig { phase = ExamPhase.Greeting, durationSeconds = 60, systemInstruction = "realtime.phase.greeting", useTools = true });
                configs.Add(new PhaseConfig { phase = ExamPhase.Presentation, durationSeconds = 480, systemInstruction = "realtime.phase.presentation", useTools = true });
                configs.Add(new PhaseConfig { phase = ExamPhase.Discussion, durationSeconds = 420, systemInstruction = "realtime.phase.discussion", useTools = true });
                configs.Add(new PhaseConfig { phase = ExamPhase.Terms, durationSeconds = 240, systemInstruction = "realtime.phase.terms", useTools = true });
            }
            else
            {
                // D2P remains Greeting -> Anamnesis -> Summary.
                configs.Add(new PhaseConfig { phase = ExamPhase.Greeting, durationSeconds = 60, systemInstruction = "realtime.phase.greeting", useTools = true });
                configs.Add(new PhaseConfig { phase = ExamPhase.Anamnesis, durationSeconds = 600, systemInstruction = "realtime.phase.anamnesis", useTools = true });
                configs.Add(new PhaseConfig { phase = ExamPhase.Summary, durationSeconds = 300, systemInstruction = "realtime.phase.summary", useTools = true });
            }

            return configs;
        }

        private void EnsureRoleSpecificPhaseConfig()
        {
            if (!enablePhaseManagement)
                return;

            if (selectedRole == RoleType.None)
                return;

            phaseConfigs = BuildDefaultPhaseConfigsForRole(selectedRole);
            if (phaseConfigs.Count > 0)
            {
                currentPhase = phaseConfigs[0].phase;
                phaseTimer = 0f;
            }
        }

        private void RequestPronunciationTracking(string userTranscript)
        {
            if (string.IsNullOrWhiteSpace(userTranscript))
                return;

            // Need at least one evaluator: legacy GptAndWhisper component OR the realtime client's API key.
            bool hasLegacy   = gptAndWhisper != null;
            bool hasRealtime = useOpenAIRealtime && realtimeClient != null;
            if (!hasLegacy && !hasRealtime)
                return;

            EnsureEvidenceLoggerSessionStarted("RequestPronunciationTracking");

            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendLine("USER_UTTERANCE: " + userTranscript.Trim());
            }
            catch { }

            // Fire-and-forget: do not block the main conversation reply.
            if (hasLegacy)
            {
                // Legacy path: GptAndWhisper handles the HTTP call + German language detection.
                StartCoroutine(gptAndWhisper.EvaluatePronunciationTurn(
                    userTranscript,
                    res => StorePronunciationTurnResult(userTranscript, res),
                    err => StorePronunciationTurnError(err)
                ));
            }
            else
            {
                // Realtime path: direct lightweight HTTP call using the realtime client's API key.
                // Language detection is skipped — the Realtime session already forces Whisper to
                // transcribe in German (input_audio_transcription.language = "de").
                StartCoroutine(EvaluatePronunciationDirect(
                    userTranscript,
                    realtimeClient.ApiKey,
                    res => StorePronunciationTurnResult(userTranscript, res),
                    err => StorePronunciationTurnError(err)
                ));
            }

            // Azure Pronunciation Assessment: evaluate this turn's recorded audio, then immediately
            // mark the start sample for the next turn so we capture only one utterance at a time.
            if (useAzurePronunciation && azurePronunciation != null && azurePronunciation.IsConfigured)
            {
                azurePronunciation.EvaluateTurn(
                    userTranscript,
                    res =>
                    {
                        _pronunciationNotesLog.Add($"[Azure] {res.feedback}");
                        try
                        {
                            if (_evidenceLogger != null)
                                _evidenceLogger.AppendSection("AZURE_PRONUNCIATION_TURN", res.feedback);
                        }
                        catch { }
                    },
                    err => Debug.LogWarning($"[Azure] Pronunciation error: {err}")
                );
                // Immediately re-mark so the NEXT user turn starts from the right position.
                azurePronunciation.MarkTurnStart();
            }
        }

        // Shared success handler for both legacy and realtime pronunciation tracking paths.
        private void StorePronunciationTurnResult(string userTranscript, GptAndWhisper.PronunciationTurnFeedback res)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                sb.Append("User: ").Append(userTranscript.Trim());
                sb.Append("\nScore: ").Append(res?.score.ToString("0.0") ?? "0.0").Append("/5");

                if (res?.mistakes != null && res.mistakes.Length > 0)
                    sb.Append("\nMistakes: ").Append(string.Join(", ", res.mistakes));

                if (!string.IsNullOrWhiteSpace(res?.feedback))
                    sb.Append("\nFeedback: ").Append(res.feedback.Trim());

                _pronunciationNotesLog.Add(sb.ToString());

                string structuredNote = FormatSpeechTrackingNote(userTranscript, res);
                if (!string.IsNullOrWhiteSpace(structuredNote))
                    _speechTrackingNotesLog.Add(structuredNote);

                try
                {
                    if (_evidenceLogger != null)
                    {
                        _evidenceLogger.AppendSection("PRONUNCIATION_TURN_FEEDBACK", sb.ToString());
                        if (!string.IsNullOrWhiteSpace(structuredNote))
                            _evidenceLogger.AppendSection("STRUCTURED_SPEECH_TURN_FEEDBACK", structuredNote);
                    }
                }
                catch { }

                Debug.Log("[MedicalExamManager] (hidden) Pronunciation turn feedback stored.");
            }
            catch { }
        }

        // Shared error handler for both pronunciation tracking paths.
        private void StorePronunciationTurnError(string err)
        {
            Debug.LogWarning($"[MedicalExamManager] Pronunciation turn eval failed: {err}");
            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendLine("PRONUNCIATION_TURN_FEEDBACK_ERROR: " + (err ?? string.Empty));
            }
            catch { }
        }

        /// <summary>
        /// Lightweight direct GPT-4o-mini call for per-turn pronunciation tracking in Realtime mode.
        /// Mirrors GptAndWhisper.EvaluatePronunciationTurn but uses a provided API key and skips
        /// the text-based German language check (Realtime already forces Whisper to transcribe in German).
        /// </summary>
        private IEnumerator EvaluatePronunciationDirect(
            string userTranscript,
            string apiKey,
            Action<GptAndWhisper.PronunciationTurnFeedback> onResult,
            Action<string> onError)
        {
            const string url = "https://api.openai.com/v1/chat/completions";
            const string system =
                "You are a strict German speech-quality coach for medical oral exams. " +
                "You receive only an ASR transcript (Whisper), so give best-effort feedback based on the written words. " +
                "Focus on grammar mistakes, poor word choice, and fluency issues implied by the transcript. " +
                "Return ONLY one JSON object (no markdown, no extra text) with exactly these keys: " +
                "score (0-5 number), mistakes (array of strings), feedback (string, German), " +
                "grammarIssues (array of strings), wordChoiceIssues (array of strings), " +
                "fluencyIssues (array of strings), summary (string, German), confidence (0-1 number). " +
                "Do not roleplay. Do not ask questions. Keep feedback short and actionable.";

            var body = new JObject
            {
                ["model"]       = "gpt-4o-mini",   // lightweight per-turn call
                ["temperature"] = 0.2,
                ["max_tokens"]  = 220,
                ["messages"]    = new JArray
                {
                    new JObject { ["role"] = "system", ["content"] = system },
                    new JObject { ["role"] = "user",   ["content"] = userTranscript }
                }
            };

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(body.ToString(Newtonsoft.Json.Formatting.None));
            using (var req = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                req.uploadHandler   = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);
                yield return req.SendWebRequest();

                if (req.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    onError?.Invoke($"HTTP {req.error}");
                    yield break;
                }

                GptAndWhisper.PronunciationTurnFeedback parsed = null;
                string raw = "";
                try
                {
                    var resp = JObject.Parse(req.downloadHandler.text);
                    raw = resp["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                    var fb = JObject.Parse(raw.Trim());

                    string[] ParseArr(string key)
                    {
                        var arr = fb[key] as JArray;
                        if (arr == null) return Array.Empty<string>();
                        var list = new List<string>();
                        foreach (var t in arr) { var s = (string)t; if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim()); }
                        return list.ToArray();
                    }

                    parsed = new GptAndWhisper.PronunciationTurnFeedback
                    {
                        score            = (float?)fb["score"] ?? 0f,
                        mistakes         = ParseArr("mistakes"),
                        feedback         = ((string)fb["feedback"]  ?? "").Trim(),
                        grammarIssues    = ParseArr("grammarIssues"),
                        wordChoiceIssues = ParseArr("wordChoiceIssues"),
                        fluencyIssues    = ParseArr("fluencyIssues"),
                        summary          = ((string)fb["summary"]   ?? "").Trim(),
                        confidence       = Mathf.Clamp01((float?)fb["confidence"] ?? 0f),
                    };
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MedicalExamManager] EvaluatePronunciationDirect parse error: {ex.Message}");
                }

                onResult?.Invoke(parsed ?? new GptAndWhisper.PronunciationTurnFeedback
                {
                    score            = 0f,
                    mistakes         = Array.Empty<string>(),
                    grammarIssues    = Array.Empty<string>(),
                    wordChoiceIssues = Array.Empty<string>(),
                    fluencyIssues    = Array.Empty<string>(),
                    feedback         = raw.Trim(),
                    summary          = "",
                    confidence       = 0f,
                });
            }
        }

        private static string FormatSpeechTrackingNote(string userTranscript, GptAndWhisper.PronunciationTurnFeedback res)
        {
            if (res == null)
                return string.Empty;

            var sb = new System.Text.StringBuilder(320);
            sb.Append("User: ").Append((userTranscript ?? string.Empty).Trim());
            sb.Append("\nSpeechScore: ").Append(res.score.ToString("0.0")).Append("/5");
            sb.Append("\nConfidence: ").Append(Mathf.Clamp01(res.confidence).ToString("0.00"));

            if (res.mistakes != null && res.mistakes.Length > 0)
                sb.Append("\nPronunciationOrGeneralMistakes: ").Append(string.Join(", ", res.mistakes));

            if (res.grammarIssues != null && res.grammarIssues.Length > 0)
                sb.Append("\nGrammarIssues: ").Append(string.Join(", ", res.grammarIssues));

            if (res.wordChoiceIssues != null && res.wordChoiceIssues.Length > 0)
                sb.Append("\nWordChoiceIssues: ").Append(string.Join(", ", res.wordChoiceIssues));

            if (res.fluencyIssues != null && res.fluencyIssues.Length > 0)
                sb.Append("\nFluencyIssues: ").Append(string.Join(", ", res.fluencyIssues));

            if (!string.IsNullOrWhiteSpace(res.summary))
                sb.Append("\nSummary: ").Append(res.summary.Trim());

            return sb.ToString();
        }

        private static string BuildTurnNotesSection(string header, List<string> notes)
        {
            if (notes == null || notes.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder(1024);
            sb.Append("\n\n[").Append(header).Append("]\n");
            for (int i = 0; i < notes.Count; i++)
            {
                var note = notes[i];
                if (string.IsNullOrWhiteSpace(note))
                    continue;
                sb.Append("--- TURN ").Append(i + 1).Append(" ---\n");
                sb.Append(note.Trim()).Append("\n");
            }

            return sb.ToString();
        }

        // Called by Start Conversation button
        /// <summary>
        /// Called when the user clicks the Start Conversation button.
        /// Handles both OpenAI Realtime and legacy GPT-4 paths.
        /// </summary>
        public void StartConversation()
        {
            Debug.Log("[MedicalExamManager] Start Conversation button clicked!");
            
            EnsureEvidenceLoggerSessionStarted("StartConversation");
            string scenarioPrompt = ApplyPromptPlaceholders(ResolveWhisperStartPromptForSelectedRole());
            
            if (string.IsNullOrWhiteSpace(scenarioPrompt))
            {
                Debug.LogError("[MedicalExamManager] No scenario prompt set for selected role!");
                return;
            }

            // Persist the exact (remote + placeholder-resolved) start prompt used in this run.
            try { _startPromptUsedForRun = scenarioPrompt; } catch { }

            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("START_PROMPT_SENT_TO_ROLEPLAY_MODEL", scenarioPrompt);
            }
            catch { }

            // Prepare UI (hide start button, show loading)
            _realtimeSessionReady = false;
            if (recordingIndicator != null) recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: true);

            // Start exam state
            StartExamConversation();

            // Branch: OpenAI Realtime vs Legacy GPT-4
            if (useOpenAIRealtime && realtimeClient != null)
            {
                Debug.Log("[MedicalExamManager] Starting OpenAI Realtime Session...");
                if (startConversationPanel != null) startConversationPanel.SetActive(true);
                
                // Start microphone immediately for Realtime
                EnsureAutoMicSegmentsRunning(reason: "StartConversation-Realtime");
                
                // Use GenerateSystemPrompt() which properly pulls remote prompts + guardrails,
                // instead of the simple ResolveWhisperStartPromptForSelectedRole() which can
                // fall back to bare English inspector fields with no scenario context.
                string realtimePrompt = GenerateSystemPrompt();
                if (string.IsNullOrWhiteSpace(realtimePrompt))
                {
                    Debug.LogError("[MedicalExamManager] GenerateSystemPrompt() returned empty. strictOnlineCsvOnly is active, so conversation will not start.");
                    SetStartConversationUiState(showStart: true, showLoading: false);
                    return;
                }
                StartCoroutine(StartRealtimeSessionWithOptionalRag(realtimePrompt));
                return;
            }

            // Legacy GPT-4 Path
            if (gptAndWhisper == null)
            {
                Debug.LogError("[MedicalExamManager] GptAndWhisper not assigned!");
                SetStartConversationUiState(showStart: true, showLoading: false);
                return;
            }

            Debug.Log("[MedicalExamManager] Starting legacy GPT-4 conversation...");
            EnsureAutoMicSegmentsRunning(reason: "StartConversation-GPT4");

            // Role rule: both D2D and D2P wait for the user (candidate) to speak first.
            bool aiStartsFirst = false;
            if (!aiStartsFirst)
            {
                gptAndWhisper?.ResetConversation(scenarioPrompt);
                _conversationReady = true;

                if (generalFeedbackText != null)
                    generalFeedbackText.text = "Conversation ready. Please start speaking.";

                SetStartConversationUiState(showStart: false, showLoading: false);
                if (startConversationLoadingIndicator != null) startConversationLoadingIndicator.SetActive(false);
                HideIndicatorSmooth(aiTalkingIndicator, aiTalkingIndicatorCanvasGroup, ref _talkingIndicatorFadeCoroutine);
                ShowIndicatorSmooth(recordingIndicator, recordingIndicatorCanvasGroup, ref _recordingIndicatorFadeCoroutine);

                try { StartCoroutine(FlushPendingUserUtterances()); } catch { }
                return;
            }

            StartCoroutine(gptAndWhisper.SendPromptToGpt(
                scenarioPrompt,
                response => {
                    Debug.Log($"[MedicalExamManager] [RESPONSE] Received initial GPT-4 response: {response}");

                    _conversationReady = true;
                    if (generalFeedbackText != null) generalFeedbackText.text = "GPT-4: " + response;
                    if (aiLiveTranscriptText != null) aiLiveTranscriptText.text = response;

                    try { _fullConversationLog += "AI: " + response + "\n"; } catch { }
                    try { if (_evidenceLogger != null) _evidenceLogger.AppendSection("INITIAL_AI_RESPONSE", response); } catch { }

                    try
                    {
                        var scenario = selectedRole == RoleType.DoctorToDoctor
                            ? ElevenLabsTTS.ScenarioType.DoctorToDoctor
                            : ElevenLabsTTS.ScenarioType.DoctorToPatient;
                        gptAndWhisper?.SpeakResponse(response, scenario);
                    }
                    catch { }

                    try { StartCoroutine(FlushPendingUserUtterances()); } catch { }
                },
                error => {
                    Debug.LogError($"[MedicalExamManager] Error from GPT-4: {error}");
                    _conversationReady = false;
                    if (generalFeedbackText != null) generalFeedbackText.text = "Error: " + error;
                    SetStartConversationUiState(showStart: CanShowStartConversationButton(), showLoading: false);
                },
                systemMessage: null,
                usePromptAsSystemOnly: true
            ));
        }
        [Header("Azure Pronunciation Assessment")]
        [Tooltip("When enabled, each user turn is assessed by Azure Speech in addition to gpt-4o-mini.")]
        [SerializeField] private bool useAzurePronunciation = false;
        [SerializeField] private AzurePronunciationService azurePronunciation;

        [Header("Whisper->GPT Integration")]
        [Tooltip("Evaluator for sending/receiving chat and handling Whisper/eval flows.")]
        [SerializeField] private GptAndWhisper gptAndWhisper;
        
        [Header("Exam Duration")]
        [Tooltip("Exam duration in minutes")]
        [SerializeField] private int examDurationMinutes = 20;
        
        [Header("Role Selection")]
        [Tooltip("Currently selected role type")]
        [SerializeField] private RoleType selectedRole = RoleType.None;
        
        [Header("UI References")]
        [SerializeField] private GameObject roleSelectionPanel;
        [Tooltip("CanvasGroup for the role selection panel. Used to fade out the selection UI after choosing a role.")]
        [SerializeField] private CanvasGroup selectionGroup;
        [SerializeField] private Button doctorButton;
        [SerializeField] private  PlaySoundOnEnable PlaySoundOnEnabledoctor;
        [SerializeField] private  PlaySoundOnEnable PlaySoundOnEnablepatient;
        [SerializeField] private Button patientButton;
        [SerializeField] private GameObject startConversationPanel;
        [SerializeField] private Button startConversationButton;
        [SerializeField] private TextMeshProUGUI scenarioTitleText;
        [SerializeField] private TextMeshProUGUI timerText;
        [SerializeField] private GameObject evaluationPanel;
        [SerializeField] private GameObject evaluationLoadingIndicator; // Loading spinner/text
        [SerializeField] private GameObject evaluationResultsContent; // The actual results (sliders, feedback)
        [SerializeField] private GameObject feedbackPanel; // Parent container for feedback UI
        [SerializeField] private EvaluationDisplayUI evaluationDisplayUI;

        [Header("Evaluation Text Output")]
        [Tooltip("Optional: a TextMeshProUGUI that will display the final evaluator's generalFeedback (or fallback overallFeedback/feedbackText).")]
        [SerializeField] private TextMeshProUGUI generalFeedbackText;

        [Header("Live Transcript")]
        [Tooltip("Optional: a TextMeshProUGUI that will display the AI's spoken transcript live (questions/prompts). Appends fragments as they arrive.")]
        [SerializeField] private TextMeshProUGUI aiLiveTranscriptText;

        [Tooltip("CanvasGroup for AI transcript fade-in animation (0 to 0.7 alpha).")]
        [SerializeField] private CanvasGroup aiLiveTranscriptCanvasGroup;

        [Header("Live Transcript Streaming")]
        [Tooltip("How many words to append to the live transcript per step (smooths realtime fragment spam).")]
        [SerializeField] private int aiTranscriptWordsPerStep = 3;

        [Tooltip("Delay between each step while the AI is speaking.")]
        [SerializeField] private float aiTranscriptStepSeconds = 0.25f;

        [Tooltip("Duration for the AI transcript fade-in animation (seconds).")]
        [SerializeField] private float aiTranscriptFadeDuration = 0.5f;
        
        [Header("Conversation UI Indicators")]
        [SerializeField] private GameObject recordingIndicator;
        [SerializeField] private GameObject aiTalkingIndicator;
        [SerializeField] private GameObject transcriptpanel;
        [SerializeField] private GameObject startConversationIndicator;
        [Tooltip("Shown after pressing Start Conversation; hidden when the AI starts speaking.")]
        [SerializeField] private GameObject startConversationLoadingIndicator;

        [Header("Smooth Indicator Transitions")]
        [Tooltip("Optional CanvasGroup on the AI Talking indicator — enables fade-in/out and pulse instead of snap.")]
        [SerializeField] private CanvasGroup aiTalkingIndicatorCanvasGroup;
        [Tooltip("Optional CanvasGroup on the Recording indicator — enables smooth fade transitions.")]
        [SerializeField] private CanvasGroup recordingIndicatorCanvasGroup;
        [Tooltip("Seconds for talking/recording indicator to fade in or out.")]
        [SerializeField] private float indicatorFadeDuration = 0.25f;
        [Tooltip("Pulse min alpha for the AI talking indicator while AI is speaking (0.4 = gentle pulse).")]
        [Range(0.3f, 1f)] [SerializeField] private float talkingIndicatorPulseMin = 0.45f;
        [Tooltip("Pulse speed multiplier for the AI talking indicator.")]
        [Range(0.5f, 4f)] [SerializeField] private float talkingIndicatorPulseSpeed = 2.0f;

        [Header("Start Conversation Gating")]
        [Tooltip("Deprecated: Start Conversation is always allowed after selecting a role.")]
        [SerializeField] private bool allowStartConversationOnlyOncePerScene = false;
        
        [Header("Evaluation Scene Changes")]
        [Tooltip("Optional: GameObject to move when evaluation starts.")]
        [SerializeField] private GameObject objectToMoveOnEvaluation;
        
        [Tooltip("Target position for the object during evaluation.")]
        [SerializeField] private Vector3 evaluationPosition;
        
        [Tooltip("Target rotation for the object during evaluation.")]
        [SerializeField] private Vector3 evaluationRotation;
        
        [Header("Surface Touch Positioning")]
        [Tooltip("GameObject that changes position based on scenario selection and evaluation state.")]
        [SerializeField] private GameObject surfacetouch;
        
        [Tooltip("Transform for position/rotation when scenario is chosen (start of app).")]
        [SerializeField] private Transform scenarioPositionTransform;
        
        [Tooltip("Transform for position when evaluation starts (when feed panel appears).")]
        [SerializeField] private Transform evaluationPositionTransform;
        
        [Header("Conversation Control")]
        [SerializeField] private Button endConversationButton;
        [Tooltip("Optional pause/resume button for realtime conversation. Wire this to ToggleRealtimePauseResume().")]
        [SerializeField] private Button pauseConversationButton;
        
        [Header("Raycast Objects")]
        [Tooltip("Raycast object to disable when evaluation is triggered")]
        [SerializeField] private GameObject raycastObject1;
        [Tooltip("Raycast object to enable when evaluation is triggered")]
        [SerializeField] private GameObject raycastObject2;
        
        [Header("Debug Testing (Send Text as Speech)")]
        [Tooltip("Enable to show debug panel with test message buttons")]
        [SerializeField] private bool enableDebugPanel = false;
        [SerializeField] private GameObject debugPanel;
        [SerializeField] private Button debugButton1;
        [SerializeField] private Button debugButton2;
        [SerializeField] private Button debugButton3;
        [SerializeField] private Button debugButton4;
        [SerializeField] private Button debugButton5;
        
        [Header("Dependencies")]
        [SerializeField] private GameObject doctorAvatar;
        [SerializeField] private GameObject patientAvatar;
        [SerializeField] private WelcomeAudioPlayer welcomeAudioPlayer;

        [Header("Custom Whisper / Fine-Tuned Options")]
        [Tooltip("If enabled, always run Whisper post-hoc and include a large prompt bank (inline or TextAsset) when calling the fine-tuned evaluator.")]
        [SerializeField] private bool useWhisperWithCustomFineTuned = false;
        [Tooltip("Optional TextAsset containing a large prompt/answer bank to prepend to the final evaluation payload.")]
        [SerializeField] private TextAsset bigPromptAsset;
        [TextArea(3, 30)]
        [Tooltip("Optional inline large prompt/answer bank. Used if TextAsset is not provided.")]
        [SerializeField] private string bigPromptInline = "";
        
        [Tooltip("If enabled, use a single Whisper->GPT flow (no realtime draft). The Whisper transcript + provided prompt will be sent to one GPT call which returns the final evaluation shown in UI.")]
        [SerializeField] private bool useWhisperOnlySingleGPT = false;
        [Tooltip("Optional TextAsset containing the single evaluation prompt to send along with the Whisper transcript.")]
        [SerializeField] private TextAsset whisperEvalPromptAsset;

        [Header("Environment Parents")]
        [Tooltip("Enabled for Doctor-to-Doctor scenario; disabled otherwise.")]
        [SerializeField] private GameObject doctorToDoctorEnvironmentParent;
        [Tooltip("Enabled for Doctor-to-Patient scenario; disabled otherwise.")]
        [SerializeField] private GameObject doctorToPatientEnvironmentParent;
        
        [Header("Scenario Details Panels")]
        [Tooltip("Shown when Doctor-to-Patient is selected.")]
        [SerializeField] private GameObject patientToDoctorDetailPanel;
        [Tooltip("Shown when Doctor-to-Doctor is selected.")]
        [SerializeField] private GameObject doctorToDoctorDetailPanel;

        [Tooltip("Main text field inside the Doctor-to-Patient detail panel.")]
        [SerializeField] private TMP_Text patientToDoctorDetailText;

        [Tooltip("Main text field inside the Doctor-to-Doctor detail panel.")]
        [SerializeField] private TMP_Text doctorToDoctorDetailText;
        [SerializeField] private AudioSource aiAudioSource; // Reference to the AI's audio source for checking if speaking
        [SerializeField] private FineTunedGPT4EvaluationService fineTunedEvalService;

        [Header("Audio / TTS")]
        [Tooltip("If enabled, the realtime AI will be muted while it speaks its raw evaluation. We still capture the transcript text.")]
        [SerializeField] private bool muteRealtimeEvaluationAudio = false;

        [Header("Whisper / Mic (Non-Realtime)")]
        [Tooltip("If using Whisper-only single GPT flow, assign the MicrophoneStreamer used to capture mic audio.")]
        [SerializeField] private MicrophoneStreamer whisperMicrophoneStreamer;

        [Tooltip("Optional: WhisperRecorder component that buffers segments and raises OnSegmentComplete when silence is detected.")]
        [SerializeField] private WhisperRecorder whisperRecorder;

        [Header("Whisper / Auto Recording (RMS trigger)")]
        [Tooltip("Enable auto mic segment detection: start when volume high, extend by 2s while speaking, stop after silence or 7s max.")]
        [SerializeField] private bool enableAutoMicSegments = true;

        [Tooltip("If enabled, the realtime AI audio volume is set to 0 while it speaks its raw (realtime) evaluation feedback. Transcript capture is unaffected. Volume is restored to 1 when the final fine-tuned evaluation is ready to be spoken.")]
        [SerializeField] private bool silenceRealtimeEvaluationAudioVolumeToZero = false;

        [Tooltip("If enabled, when evaluation is triggered we only disable upstream mic audio (so your voice/noise won't interrupt). We do NOT mute the AI and we do NOT interrupt.")]
        [SerializeField] private bool evaluationOnlyMuteMicUpstream = true;

        [Tooltip("If enabled, we will let the realtime agent provide its raw evaluation first (captured as transcript text), then do final GPT-4 evaluation. Recommended: ON (adds pronunciation/flow signals).")]
        [SerializeField] private bool requestRealtimeEvaluationBeforeFinal = true;

        [Header("Evaluation Settings")]
        [SerializeField] private bool autoEvaluateOnTimeEnd = true;
        [SerializeField] private ExamHistory examHistory;

        [Header("Evaluation Guard (Minimum Evidence)")]
        [Tooltip("If the user presses Evaluate with too little conversation, we show an 'insufficient evidence' fail instead of calling the evaluators.")]
        [SerializeField] private bool enforceMinimumEvidenceForEvaluation = true;
        
        // Internal: one-shot bypass for the minimum-evidence guard, set by manual debug trigger
        private bool _bypassEvidenceGuardOnce = false;

        [Tooltip("Minimum number of user words required before we allow evaluation.")]
        [SerializeField] private int minUserWordsForEvaluation = 8;

        [Tooltip("Minimum transcript characters required before we allow evaluation.")]
        [SerializeField] private int minTranscriptCharsForEvaluation = 40;

        [Header("Evaluation Guard (Medical Signal)")]
        [Tooltip("If enabled, clamp scores to a low range when the user's transcript lacks basic medical/structured content (prevents random speech from scoring high).")]
        [SerializeField] private bool enforceLowScoresWhenMedicalSignalIsLow = true;

        [Tooltip("Apply the medical-signal guard only after the user has spoken enough to reasonably expect medical structure/keywords. Prevents harsh 'Signal-Keywords: 0' feedback on very short tests.")]
        [SerializeField] private bool medicalSignalGuardOnlyAfterMinimumUserSpeech = true;

        [Tooltip("Minimum number of user words before the medical-signal guard is allowed to clamp scores.")]
        [SerializeField] private int minUserWordsForMedicalSignalGuard = 25;

        [Tooltip("Minimum number of user-speech characters before the medical-signal guard is allowed to clamp scores.")]
        [SerializeField] private int minUserCharsForMedicalSignalGuard = 160;

        [Tooltip("Minimum number of DISTINCT medical/structure keywords that must appear in user speech.")]
        [SerializeField] private int minDistinctMedicalKeywords = 4;

        [Tooltip("Maximum overallScore allowed when medical signal is low.")]
        [SerializeField] private float maxOverallScoreWhenMedicalSignalIsLow = 25f;

        [Tooltip("Maximum per-skill score (0-5) allowed when medical signal is low.")]
        [SerializeField] private float maxSubscoreWhenMedicalSignalIsLow = 1.5f;

        private static readonly string[] MedicalSignalKeywords = new[]
        {
            // Core exam structure
            "anamnese", "vorerkrank", "medikation", "medikamente", "allerg", "allergie", "allergien",
            "dd", "differential", "diagnose", "verdachtsdiagnose", "befund", "befunde",
            "therapie", "behandlung", "diagnostik", "labor", "ekg", "ct", "mrt", "sono", "ultraschall",
            // Common symptoms / red flags
            "schmerz", "schmerzen", "fieber", "atemnot", "dyspnoe", "husten", "brustschmerz",
            "übelkeit", "uebelkeit", "erbrechen", "durchfall", "schwindel", "synkope", "blutung",
            // Useful question structure
            "seit", "dauer", "stärke", "staerke", "ausstrahl", "ausstrahlung", "faktoren", "trigger",
            // Chronic / risk
            "diabetes", "hyperton", "blutdruck", "puls", "nikotin", "rauch", "alkohol"
        };

        private bool HasEnoughMedicalSignal(out int distinctMatches)
        {
            distinctMatches = 0;

            if (!enforceLowScoresWhenMedicalSignalIsLow)
                return true;

            // Combine best-available user speech sources.
            string userSpeech = "";
            try
            {
                if (!string.IsNullOrWhiteSpace(_fullConversationLog))
                {
                    var lines = _fullConversationLog.Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        if (line != null && line.StartsWith("User:", StringComparison.Ordinal))
                            userSpeech += " " + line.Substring("User:".Length);
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(_whisperUserTranscript))
                userSpeech += " " + _whisperUserTranscript;

            userSpeech = (userSpeech ?? string.Empty).ToLowerInvariant();
            // If there is no user speech at all, treat as "short sample" and do NOT clamp
            // when the guard is configured to apply only after minimum user speech.
            // This prevents immediate harsh feedback when the user triggers evaluation early.
            if (string.IsNullOrWhiteSpace(userSpeech))
            {
                return medicalSignalGuardOnlyAfterMinimumUserSpeech ? true : false;
            }

            // If the user hasn't spoken much yet, don't apply the strict medical-signal clamp.
            // We still count distinct keyword hits for logging, but we treat the signal as "enough".
            if (medicalSignalGuardOnlyAfterMinimumUserSpeech)
            {
                int userChars = userSpeech.Length;
                int userWords = 0;
                try
                {
                    userWords = userSpeech.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
                }
                catch { userWords = 0; }

                if (userWords < Mathf.Max(0, minUserWordsForMedicalSignalGuard) || userChars < Mathf.Max(0, minUserCharsForMedicalSignalGuard))
                {
                    // Skip clamping for short samples.
                    // distinctMatches will be filled below for diagnostic purposes.
                }
            }

            // Count distinct keyword hits.
            var hit = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < MedicalSignalKeywords.Length; i++)
            {
                string k = MedicalSignalKeywords[i];
                if (string.IsNullOrEmpty(k)) continue;
                if (userSpeech.Contains(k)) hit.Add(k);
            }

            distinctMatches = hit.Count;

            if (medicalSignalGuardOnlyAfterMinimumUserSpeech)
            {
                int userChars = userSpeech.Length;
                int userWords = 0;
                try
                {
                    userWords = userSpeech.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
                }
                catch { userWords = 0; }

                if (userWords < Mathf.Max(0, minUserWordsForMedicalSignalGuard) || userChars < Mathf.Max(0, minUserCharsForMedicalSignalGuard))
                {
                    // Short sample: allow evaluation without forcing the signal-keyword clamp.
                    return true;
                }
            }

            return distinctMatches >= Mathf.Max(0, minDistinctMedicalKeywords);
        }

        [Header("Remote Prompts (CSV URL)")]
        [Tooltip("If enabled, prompt text is loaded from a remote CSV (Google Sheets OR Excel Online/OneDrive/SharePoint) at runtime. Falls back to local prompts if remote fails.")]
        [SerializeField] private bool useRemotePrompts = true;

        [Tooltip("Remote CSV URL (must be a direct CSV response, publicly reachable without login).\n" +
             "Examples:\n" +
             "- Google Sheets: https://docs.google.com/spreadsheets/d/<ID>/gviz/tq?tqx=out:csv&sheet=Prompts\n" +
             "- Excel/OneDrive: a share link that downloads the file as CSV (must return CSV, not HTML).")]
        [UnityEngine.Serialization.FormerlySerializedAs("googleSheetCsvUrl")]
        [SerializeField] private string remotePromptsCsvUrl = "https://docs.google.com/spreadsheets/d/e/2PACX-1vT1gLT7Wilguui1K0jIevQGSpdpeABsC1DLxWXcrQgTcs7MDvkYTem-ax8Gx-FdnNDhzvsWsVpACCI6/pub?output=csv";

        [Tooltip("Prompt locale to select from the sheet (e.g., 'de', 'en', or '*').")]
        [SerializeField] private string promptLocale = "de";

        [Tooltip("If enabled, only online CSV data is allowed. No local/cache prompt fallback is used, so missing or broken CSV is visible immediately in logs.")]
        [SerializeField] private bool strictOnlineCsvOnly = true;

        [Header("Remote Scenarios (Same CSV)")]
        [Tooltip("If enabled, scenario metadata is read from the SAME CSV URL using columns scenario_id/scenario_name/scenario_context/topic_d2d/topic_d2p/rag_tags and optional cases_json.")]
        [SerializeField] private bool useRemoteScenarioData = true;

        [Tooltip("Scenario ID to resolve from the shared CSV (e.g., cardiology, nephrology, default).")]
        [SerializeField] private string activeScenarioId = "default";

        [Tooltip("If true, uses only the single custom case specified in activeScenarioId (set via the web dashboard). If false (default), a random case is picked from all available cases in the CSV at start.")]
        [SerializeField] private bool useCustomCaseOnly = false;

        [Header("Scenario Case Library")]
        [Tooltip("If enabled, uses cases_json from the scenario row. One case is selected and injected into {CONTEXT} for greeting/anamnesis/summary prompts.")]
        [SerializeField] private bool useScenarioCasesJson = true;

        [Tooltip("Optional fixed case index. Use -1 for random case selection.")]
        [SerializeField] private int fixedScenarioCaseIndex = -1;

        private string _selectedCaseScenarioId;
        private string _selectedCaseId;
        private string _selectedCaseTitle;
        private string _selectedCaseText;
        private string _selectedCaseTermsJson;
        private string _selectedCaseCoreFactsJson;
        private string _selectedCaseExpectedQuestionsJson;
        private readonly Dictionary<ExamPhase, string> _selectedCasePhaseTermsJson = new Dictionary<ExamPhase, string>();
        private readonly Dictionary<ExamPhase, string> _selectedCasePhaseCoreFactsJson = new Dictionary<ExamPhase, string>();
        private readonly Dictionary<ExamPhase, string> _selectedCasePhaseExpectedQuestionsJson = new Dictionary<ExamPhase, string>();
        private int _selectedCaseIndex = -1;

        /// <summary>Public accessor for active scenario ID (used by voice and other services)</summary>
        public static string ActiveScenarioId { get; private set; } = "default";
        public static string ActiveCaseId { get; private set; } = string.Empty;
        public static string ActiveCaseTitle { get; private set; } = string.Empty;
        public static string ActiveCaseText { get; private set; } = string.Empty;
        public static string ActiveCaseTermsJson { get; private set; } = string.Empty;
        public static string ActiveCaseCoreFactsJson { get; private set; } = string.Empty;
        public static string ActiveCaseExpectedQuestionsJson { get; private set; } = string.Empty;
        public static int ActiveCaseIndex { get; private set; } = -1;

        [Tooltip("Optional local fallback scenario when remote scenario row is not available.")]
        [SerializeField] private MedicalExamScenario currentScenario;

        [Tooltip("Optional fallback RAG tags if remote/local scenario has no rag_tags.")]
        [SerializeField] private string fallbackRagTags = "general_medical";

        [Header("Remote Overrides (Whisper + Evaluation Prompts)")]
           [Tooltip("If enabled (and useRemotePrompts=true), overrides the prompts from the remote CSV.\n" +
             "Keys:\n" +
             "- whisper.start.doctor_to_patient\n" +
             "- whisper.start.doctor_to_doctor\n" +
             "- eval.prompt.doctor_to_patient\n" +
             "- eval.prompt.doctor_to_doctor")]
        [SerializeField] private bool useRemoteWhisperAndEvaluationPrompts = true;

        [Header("Local Fallback Prompts (Used if Remote Disabled/Fails)")]
        [TextArea(5, 20)]
        public string startPromptDoctorToPatient = "Du bist ein Patient, der einen Arzt aufsucht. Szenario: {SCENARIO_NAME}. Thema: {TOPIC}. Kontext: {CONTEXT}. Dauer: {DURATION_MIN} Minuten. Sprache: {LANGUAGE}. Antworte ausschließlich auf Deutsch.";

        [TextArea(5, 20)]
        public string startPromptDoctorToDoctor = "Du bist ein erfahrener Oberarzt, der einen Assistenzarzt in einer mündlichen medizinischen Prüfung (FSP) prüft. Szenario: {SCENARIO_NAME}. Thema: {TOPIC}. Kontext: {CONTEXT}. Dauer: {DURATION_MIN} Minuten. Sprache: {LANGUAGE}. Antworte ausschließlich auf Deutsch.";

        [Header("Prompt Sources (Per Prompt)")]
        [Tooltip("Use Inspector-provided prompts for the realtime START system prompt (highest priority).")]
        [SerializeField] private bool useInspectorRealtimeStartPrompts = false; // Always false - use remote only

        [Tooltip("Allow RemotePromptManager overrides for the realtime START system prompt.")]
        [SerializeField] private bool useRemoteRealtimeStartPrompts = true;

        [Tooltip("Use Inspector-provided prompts for the realtime DRAFT EVALUATION (highest priority).")]
        [SerializeField] private bool useInspectorRealtimeDraftEvaluationPrompts = false;

        [Tooltip("Allow RemotePromptManager overrides for the realtime DRAFT EVALUATION prompts.")]
        [SerializeField] private bool useRemoteRealtimeDraftEvaluationPrompts = true;

        [Header("RAG + Realtime")]
        [Tooltip("If enabled, enriches the initial realtime system prompt with RAG context before opening the session.")]
        [SerializeField] private bool useRagForRealtimeStart = true;

        [Tooltip("Optional RAG helper component in scene. If null or not ready, app falls back to non-RAG prompt.")]
        [SerializeField] private RAGIntegrationHelper ragIntegrationHelper;

        [Tooltip("Max seconds to wait for RAG retrieval before starting realtime with base prompt.")]
        [SerializeField] private float ragStartTimeoutSeconds = 8f;

        [Header("Question Planning")]
        [Tooltip("If enabled, a fine-tuned GPT-4 call generates a short context + suggested questions, then we inject it into the realtime system prompt before starting.")]
        [SerializeField] private bool useFineTunedQuestionPlanOnStart = true;

        [Tooltip("Max seconds to wait for the question-plan request before starting without it.")]
        [SerializeField] private float questionPlanTimeoutSeconds = 15f;
        
        // Exam state
        private bool _examActive = false;
        private float _examStartTime;
        private float _examDuration;

        // Timer should start when the AI actually begins speaking (not when the user clicks Start Conversation).
        private bool _examTimerStarted;
        private bool _evaluationRequested = false;
        private bool _examCompletionTriggered = false; // Guards against double-firing TriggerExamCompletion
        private bool _conversationStarted = false; // Track if conversation actually started
        private bool _aiWasSpeaking = false; // Track previous speaking state
        private float _lastAudioStopTime = 0f; // Track when audio stopped
        private const float AUDIO_STOP_DELAY = 0.5f; // Wait 0.5 seconds before switching to recording
        private bool _evaluationUIShown = false; // Track if evaluation/loading UI has been displayed
        private bool _feedbackParseTriggered = false; // Track if the second AI parse has been fired
        private float _lastFeedbackFragmentTime = 0f; // Last time we received feedback during evaluation
        private float _parseStartTime = 0f; // When we triggered the second AI parse
        private float _feedbackQuietStart = 0f; // Tracks quiet time after last COMPLETE FEEDBACK CACHE

        // Audio mute bookkeeping (so we can restore state)
        private bool _storedAiAudioMuteState = false;
        private bool _aiAudioMuteStateBeforeEval = false;

        // Audio volume bookkeeping (so we can restore state)
        private bool _storedAiAudioVolumeState = false;
        private float _aiAudioVolumeBeforeEval = 1f;

        // Once we have the final evaluation, we ignore any further realtime "evaluation" transcript to avoid loops.
        private bool _evaluationCompleted = false;

        [Header("Evaluation Trigger")]
        [Tooltip("If enabled, the realtime agent must say the end-marker keyword (e.g., 'DEEPLY' or 'EVALUATION_DONE') to trigger the final (GPT) evaluation.")]
        [SerializeField] private bool triggerFinalEvaluationOnDeeplyKeyword = true;

        [Tooltip("If enabled, try to parse the realtime evaluation draft (if it is JSON) and show it immediately before the final enhanced evaluation arrives.")]
        [SerializeField] private bool showRealtimeDraftEvaluationInUi = false;

        [Tooltip("Seconds to wait for the realtime agent to say the end-marker keyword before triggering final evaluation anyway.")]
        [SerializeField] private float deeplyKeywordTimeoutSeconds = 33f;

        [Tooltip("Max seconds to wait for the realtime agent to finish speaking before we send the draft-evaluation prompt. If exceeded, we interrupt and proceed.")]
        [SerializeField] private float maxWaitForAgentToFinishBeforeDraftEvalSeconds = 20f;

        private bool _deepEvalTriggered = false;
        private float _evaluationRequestStartTime = 0f;

        // Track when we programmatically send the realtime draft-evaluation prompt.
        // This prevents the final-eval timeout from firing before the draft prompt was even sent.
        private float _draftEvalPromptSentTime = 0f;
        private bool _receivedAnyRealtimeEvalFragment = false;

        [Tooltip("After the realtime agent says the evaluation end phrase, wait this many seconds of quiet (no new transcript fragments) before sending the final GPT-4 evaluation.")]
        [SerializeField] private float realtimeEvalPostEndQuietSeconds = 20f;

        private bool _realtimeEvalEndPhraseSeen = false;
        private Coroutine _realtimeEvalQuietWaitCoroutine;

        private bool _realtimeAgentSpeaking = false;
        private Coroutine _sendDraftEvalPromptCoroutine;

        // Realtime session readiness (session.created received). Used to switch Doctor→Patient
        // from Loading → Recording even when the agent does not speak first.
        private bool _realtimeSessionReady = false;
        private bool _realtimePausedByUser = false;
        private float _realtimePauseStartedAt = -1f;

        [Header("Startup UI Recovery")]
        [Tooltip("If enabled, briefly validates startup UI state and restores role selection if panels are hidden due to startup race conditions.")]
        [SerializeField] private bool enableStartupUiWatchdog = true;

        [Tooltip("How long after scene start to keep checking and repairing startup UI state (seconds).")]
        [SerializeField] private float startupUiWatchdogSeconds = 8f;
        private bool _realtimeCallbacksBound = false;
        
        // Conversation tracking
        private string _fullConversationLog = "";
        private string _realtimeAIFeedback = ""; // Capture any feedback the Realtime AI gives

        // Hidden per-turn pronunciation notes (not spoken). Populated when structured roleplay responses are enabled.
        private readonly List<string> _pronunciationNotesLog = new List<string>();
        private readonly List<string> _speechTrackingNotesLog = new List<string>();

        // The evaluation prompt is sent programmatically (and we unsubscribe user transcript events during evaluation),
        // so we log it manually into the transcript for debugging/payload clarity.
        private bool _loggedEvaluationRequestLine = false;
        private bool _evaluationAiLineOpen = false;

        // Keep track of the conversation-time realtime prompt so we can override to evaluator mode at the end.
        private string _realtimeConversationSystemPrompt = "";
        private ExamEvaluation _currentEvaluation;
        private Coroutine _evaluationPlaybackCoroutine;

        // Optional post-hoc Whisper transcript of the user's mic audio (better than realtime partial transcripts)
        private string _whisperUserTranscript = "";

        // If question plan bootstrap was enabled, keep the plan used for this run for the final evaluation.
        private string _questionPlanUsedForRun = "";
        // If a whisper-start prompt is configured for this run, store it so we can include it in evaluations.
        private string _startPromptUsedForRun = "";
        
        // Sentence buffering for AI speech fragments
        private string _currentAISentenceBuffer = "";
        private float _lastAITranscriptTime = 0f;
        private const float SENTENCE_COMPLETE_DELAY = 1.5f; // Wait 1.5s after last fragment to finalize sentence

        // Live transcript UI: show ONLY the current AI turn (cleared when the user speaks, then next AI turn starts fresh).
        private string _currentAiUiTurnBuffer = "";
        private bool _userSpokeSinceLastAiUiTurn = true;
        private Coroutine _aiTranscriptStreamingCoroutine;
        private Coroutine _aiTranscriptFadeCoroutine;
        private Coroutine _talkingIndicatorPulseCoroutine;
        private Coroutine _talkingIndicatorFadeCoroutine;
        private Coroutine _recordingIndicatorFadeCoroutine;
        private readonly System.Collections.Generic.Queue<string> _aiWordQueue = new System.Collections.Generic.Queue<string>(256);
        private string _aiDisplayedTurnText = "";
        private string _aiWordRemainder = "";
        private bool _aiAudioStarted = false; // Track if audio has started playing for this turn
        private float _aiAudioCheckStartTime = 0f;
        private const float AI_AUDIO_DETECT_TIMEOUT = 2f; // Max 2 seconds to wait for audio to start

        // Current AI turn transcript buffer (cleared each time the AI starts speaking a new turn)
        private string _currentRealtimeTranscript = "";

        // Sentence buffering for USER speech fragments (realtime transcripts often arrive word-by-word)
        private string _currentUserSentenceBuffer = "";
        private float _lastUserTranscriptTime = 0f;
        private Coroutine _userFinalizeCoroutine;

        private Coroutine _selectionFadeCoroutine;
        private const float SELECTION_FADE_SECONDS = 2f;

        private bool CanShowStartConversationButton()
        {
            if (selectedRole == RoleType.None)
                return false;

            return true;
        }

        private void SetStartConversationUiState(bool showStart, bool showLoading)
        {
            // Ensure we never show both (prevents overlap).
            if (showStart && showLoading)
                showLoading = false;

            if (startConversationButton != null)
            {
                startConversationButton.gameObject.SetActive(showStart);
                startConversationButton.interactable = showStart;
            }

            if (startConversationIndicator != null)
                startConversationIndicator.SetActive(showStart);

            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(showLoading);
        }

        private static bool ContainsEvaluationHandoffKeyword(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return false;
            string t = transcript.Trim().ToLowerInvariant();

            // Requested explicit phrase (typo-tolerant)
            if (t.Contains("im finished with evalution") || t.Contains("i'm finished with evalution") || t.Contains("i am finished with evalution")) return true;
            if (t.Contains("im finished with evaluation") || t.Contains("i'm finished with evaluation") || t.Contains("i am finished with evaluation")) return true;

            // Preferred explicit marker (easy to match, language-independent)
            if (t.Contains("evaluation_done") || t.Contains("evaluation done") || t.Contains("evaluation finished")) return true;

            // Backward compatible marker we previously used
            if (t.Contains("deeply") || t.Contains("deep evaluation") || t.Contains("tiefgehend")) return true;

            // Some common phrases users/models might produce
            if (t.Contains("finished evaluation") || t.Contains("i'm finished") || t.Contains("i am finished") || t.Contains("evaluation complete")) return true;

            return false;
        }

        private bool TryParseRealtimeDraftEvaluation(string raw, out ExamEvaluation evaluation, out string error)
        {
            evaluation = null;
            error = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                error = "realtime feedback empty";
                return false;
            }

            // Remove the end marker if it's included in the captured text.
            string cleaned = raw.Replace("EVALUATION_DONE", "").Trim();

            // Extract the first JSON object in the string.
            int start = cleaned.IndexOf('{');
            int end = cleaned.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                error = "no JSON object found in realtime feedback";
                return false;
            }

            string json = cleaned.Substring(start, end - start + 1);
            Debug.Log($"[MedicalExamManager] 📊 Attempting to parse JSON from evaluation:\n{json.Substring(0, Mathf.Min(500, json.Length))}...");

            JObject obj;
            try
            {
                obj = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                error = $"failed to parse realtime JSON: {ex.Message}";
                Debug.LogError($"[MedicalExamManager]  JSON Parse Error: {error}");
                return false;
            }

            float t = (float?)obj["terminologie"] ?? 0f;
            float v = (float?)obj["verstaendlichkeit"] ?? 0f;
            float a = (float?)obj["aussprache"] ?? 0f;
            float overall = (float?)obj["overallScore"] ?? 0f;

            // Try to parse 5D dimensions (if present)
            float kom = (float?)obj["kommunikation"] ?? ParseScoreFromNode(obj["kommunikation"]);
            float hoer = (float?)obj["hoerverstehen"] ?? ParseScoreFromNode(obj["hoerverstehen"]);
            float gespr = (float?)obj["gespraechsfuehrung"] ?? ParseScoreFromNode(obj["gespraechsfuehrung"]);
            float emp = (float?)obj["empathie"] ?? ParseScoreFromNode(obj["empathie"]);
            float voll = (float?)obj["vollstaendigkeit"] ?? ParseScoreFromNode(obj["vollstaendigkeit"]);

            Debug.Log($"[MedicalExamManager] ✓ 5D Scores Parsed: K={kom} H={hoer} G={gespr} E={emp} V={voll} | Overall={overall}");

            string general = (string)(obj["generalFeedback"] ?? obj["overallFeedback"] ?? "");
            string tfb = (string)(obj["terminologieFeedback"] ?? "");
            string vfb = (string)(obj["verstaendlichkeitFeedback"] ?? "");
            string afb = (string)(obj["ausspracheFeedback"] ?? obj["ausspracheeFeedback"] ?? "");

            // 5D feedback
            string komfb = (string)(obj["kommunikationFeedback"] ?? "");
            string hoerfb = (string)(obj["hoerverstehenFeedback"] ?? "");
            string gesprfb = (string)(obj["gespraechsfuehrungFeedback"] ?? "");
            string empfb = (string)(obj["empathieFeedback"] ?? "");
            string vollfb = (string)(obj["vollstaendigkeitFeedback"] ?? "");
            
            // Also capture aussprache feedback from 5D structure
            string ausspr5d = (string)(obj["aussprache"] as JObject)?["feedback"] ?? "";
            if (!string.IsNullOrEmpty(ausspr5d)) afb = ausspr5d;

            evaluation = new ExamEvaluation
            {
                scenarioName = GetScenarioName(),
                roleType = selectedRole == RoleType.DoctorToDoctor ? ExamEvaluation.RoleType.DoctorToDoctor : ExamEvaluation.RoleType.DoctorToPatient,
                conversationTranscript = _fullConversationLog,
                // Legacy 3D scores (fallback)
                terminologie = Mathf.Clamp(t, 0f, 5f),
                verstaendlichkeit = Mathf.Clamp(v, 0f, 5f),
                aussprache = Mathf.Clamp(a, 0f, 5f),
                // 5D scores (new)
                kommunikation = Mathf.Clamp(kom, 0f, 3f),
                hoerverstehen = Mathf.Clamp(hoer, 0f, 3f),
                gespraechsfuehrung = Mathf.Clamp(gespr, 0f, 3f),
                empathie = Mathf.Clamp(emp, 0f, 3f),
                vollstaendigkeit = Mathf.Clamp(voll, 0f, 3f),
                // Overall
                overallScore = Mathf.Clamp(overall, 0f, 100f),
                feedbackText = general,
                overallFeedback = general,
                // Legacy feedback
                terminologieFeedback = tfb,
                verstaendlichkeitFeedback = vfb,
                ausspracheFeedback = afb,
                // 5D feedback
                kommunikationFeedback = komfb,
                hoerverstehenFeedback = hoerfb,
                gespraechsfuehrungFeedback = gesprfb,
                empathieFeedback = empfb,
                vollstaendigkeitFeedback = vollfb
            };

            return true;
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

        private static float ParseScoreFromNode(Newtonsoft.Json.Linq.JToken node)
        {
            if (node == null) return 0f;
            if (node.Type == Newtonsoft.Json.Linq.JTokenType.Float || node.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                return node.Value<float>();

            if (node.Type == Newtonsoft.Json.Linq.JTokenType.Object)
            {
                var obj = node as Newtonsoft.Json.Linq.JObject;
                var score = obj?["score"];
                if (score != null && (score.Type == Newtonsoft.Json.Linq.JTokenType.Float || score.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                    return score.Value<float>();

                // Some models use value/rating.
                score = obj?["value"] ?? obj?["rating"];
                if (score != null && (score.Type == Newtonsoft.Json.Linq.JTokenType.Float || score.Type == Newtonsoft.Json.Linq.JTokenType.Integer))
                    return score.Value<float>();
            }

            // Last resort: parse numeric prefix from string.
            string s = node.Type == Newtonsoft.Json.Linq.JTokenType.String ? node.Value<string>() : node.ToString();
            if (string.IsNullOrWhiteSpace(s)) return 0f;
            s = s.Trim();
            var slash = s.IndexOf('/');
            if (slash > 0) s = s.Substring(0, slash);
            s = s.Replace(',', '.');
            return float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0f;
        }

        private static string ParseFeedbackFromNode(Newtonsoft.Json.Linq.JToken node)
        {
            if (node == null) return string.Empty;
            if (node.Type == Newtonsoft.Json.Linq.JTokenType.Object)
            {
                var obj = node as Newtonsoft.Json.Linq.JObject;
                string fb = (string)(obj?["feedback"] ?? obj?["comment"] ?? obj?["why"] ?? obj?["reason"] ?? "");
                return string.IsNullOrWhiteSpace(fb) ? string.Empty : fb.Trim();
            }
            return string.Empty;
        }

        private static string GetStringAny(Newtonsoft.Json.Linq.JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return string.Empty;
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var tok = obj[k];
                if (tok == null) continue;
                var s = tok.Type == Newtonsoft.Json.Linq.JTokenType.String ? tok.Value<string>() : tok.ToString();
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return string.Empty;
        }

        private static float GetFloatAny(Newtonsoft.Json.Linq.JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return 0f;
            foreach (var k in keys)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                var tok = obj[k];
                if (tok == null) continue;
                float v = ParseScoreFromNode(tok);
                if (Mathf.Abs(v) > 0.0001f) return v;
            }
            return 0f;
        }

        private static (float score, string feedback) ParseSkill(Newtonsoft.Json.Linq.JObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key)) return (0f, string.Empty);

            var node = obj[key];
            float score = ParseScoreFromNode(node);
            string feedback = ParseFeedbackFromNode(node);

            // Flat schema fallbacks: terminologieFeedback, etc.
            if (string.IsNullOrWhiteSpace(feedback))
            {
                if (key == "aussprache")
                    feedback = GetStringAny(obj, "ausspracheFeedback", "ausspracheeFeedback");
                else
                    feedback = GetStringAny(obj, key + "Feedback");
            }

            return (score, feedback);
        }

        private void ShowProvisionalEvaluation(ExamEvaluation evaluation)
        {
            if (evaluation == null) return;

            // Ensure evaluation UI is visible and in results state (without marking evaluation complete).
            if (evaluationPanel != null)
                evaluationPanel.SetActive(true);
            if (feedbackPanel != null)
                feedbackPanel.SetActive(true);
            if (evaluationDisplayUI != null)
                evaluationDisplayUI.gameObject.SetActive(true);

            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(false);
            if (evaluationResultsContent != null)
                evaluationResultsContent.SetActive(true);

            evaluationDisplayUI?.DisplayEvaluation(evaluation);
            Debug.Log($"[MedicalExamManager] 🟣 Provisional realtime evaluation shown: T={evaluation.terminologie}, V={evaluation.verstaendlichkeit}, A={evaluation.aussprache}, Overall={evaluation.overallScore}");
        }
        
        public enum RoleType
        {
            None,
            DoctorToDoctor,
            DoctorToPatient
        }
        
        private void Start()
        {
            Debug.Log("[MedicalExamManager] Initializing...");

            // Start evidence logging early so the file definitely exists and the path is printed.
            // NOTE: Application.persistentDataPath is NOT inside the Unity project folder.
            try
            {
                EnsureEvidenceLoggerSessionStarted("Start");
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendLine("INFO: Evidence log active at: " + _evidenceLogger.LogFilePath);
                Debug.Log("[MedicalExamManager] persistentDataPath=" + Application.persistentDataPath);
            }
            catch { }

            // Remote prompts are optional and safe: if fetch fails, we fall back to local prompts.
            RemotePromptManager.EnsureStarted(useRemotePrompts, remotePromptsCsvUrl, promptLocale, strictOnlineCsvOnly);
            RemoteScenarioManager.EnsureStarted(useRemotePrompts && useRemoteScenarioData, remotePromptsCsvUrl, promptLocale, strictOnlineCsvOnly);
            
            // Scenario selection: custom single case (web-managed) OR random pick from CSV
            if (useCustomCaseOnly)
            {
                // Use the single case specified in activeScenarioId (managed via web dashboard)
                ActiveScenarioId = string.IsNullOrWhiteSpace(activeScenarioId) ? "default" : activeScenarioId;
                ResetSelectedCaseCache();
                Debug.Log($"[MedicalExamManager] Using custom case only: {ActiveScenarioId}");
            }
            else if (useRemoteScenarioData)
            {
                StartCoroutine(SelectRandomScenarioWhenReady());
            }
            else
            {
                ActiveScenarioId = string.IsNullOrWhiteSpace(activeScenarioId) ? "default" : activeScenarioId;
                ResetSelectedCaseCache();
            }
            
            // ...existing code...
            
            // Setup UI
            SetupUI();
            
            // Initially show role selection
            ShowRoleSelection();

            if (enableStartupUiWatchdog)
                StartCoroutine(EnsureStartupUiVisibleCoroutine());
            
            // Hide evaluation panel initially
            if (evaluationPanel != null)
                evaluationPanel.SetActive(false);
            
            // Initialize evaluation UI
            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(false);
            if (evaluationResultsContent != null)
                evaluationResultsContent.SetActive(false);
            
            // Initialize surfacetouch at evaluation position (role selection state)
            if (surfacetouch != null && evaluationPositionTransform != null)
            {
                surfacetouch.transform.position = evaluationPositionTransform.position;
                surfacetouch.transform.rotation = evaluationPositionTransform.rotation;
                Debug.Log($"[MedicalExamManager] Initialized surfacetouch at evaluation position: {evaluationPositionTransform.position}");
            }

            // NEW: Initialize Phases
            if (enablePhaseManagement)
            {
               // Keep startup defaults role-aware; if no role yet, default to D2D shape.
               if (phaseConfigs == null || phaseConfigs.Count == 0)
               {
                   var bootstrapRole = selectedRole == RoleType.None ? RoleType.DoctorToDoctor : selectedRole;
                   phaseConfigs = BuildDefaultPhaseConfigsForRole(bootstrapRole);
               }
               if (phaseConfigs.Count > 0)
               {
                   currentPhase = phaseConfigs[0].phase;
                   phaseTimer = 0f;
               }
            }
                
            Debug.Log("[MedicalExamManager] Ready! Waiting for role selection...");
        }

        private IEnumerator EnsureStartupUiVisibleCoroutine()
        {
            // Let one frame pass so all scene objects finish their first activation pass.
            yield return null;

            float timeout = Mathf.Max(1f, startupUiWatchdogSeconds);
            float endAt = Time.time + timeout;

            while (Time.time < endAt)
            {
                // Only guard the idle startup state before any role/conversation begins.
                if (selectedRole != RoleType.None || _conversationStarted || _examActive)
                    yield break;

                bool rolePanelHidden = roleSelectionPanel != null && !roleSelectionPanel.activeSelf;
                bool startPanelShownWithoutRole = startConversationPanel != null && startConversationPanel.activeSelf;
                bool selectionGroupInvisible = selectionGroup != null && selectionGroup.alpha < 0.95f;
                bool selectionGroupBlocked = selectionGroup != null && (!selectionGroup.interactable || !selectionGroup.blocksRaycasts);

                if (rolePanelHidden || selectionGroupInvisible || selectionGroupBlocked || startPanelShownWithoutRole)
                {
                    Debug.LogWarning("[MedicalExamManager] Startup UI watchdog restored role-selection canvas state.");
                    ShowRoleSelection();
                }

                yield return new WaitForSeconds(0.25f);
            }
        }

        private void OnRealtimeSessionReady()
        {
            _realtimeSessionReady = true;

            if (!_examActive || !_conversationStarted || _evaluationRequested)
                return;

            // Both D2D and D2P wait for the student to speak first.
            // Show recording indicator (mic open, awaiting student) for both roles.
            SetStartConversationUiState(showStart: false, showLoading: false);
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);
            HideIndicatorSmooth(aiTalkingIndicator, aiTalkingIndicatorCanvasGroup, ref _talkingIndicatorFadeCoroutine);
            ShowIndicatorSmooth(recordingIndicator, recordingIndicatorCanvasGroup, ref _recordingIndicatorFadeCoroutine);
        }
        
        private void Update()
        {
            // Update timer during active exam
            if (_examActive && _examTimerStarted && !_evaluationRequested && !_realtimePausedByUser)
            {
                UpdateExamTimer();

                // NEW: Phase Logic
                if (enablePhaseManagement)
                {
                    UpdatePhaseLogic();
                }
            }
            
            // Check AI audio state and update UI indicators
            if (_conversationStarted && _examActive)
            {
                CheckAISpeakingState();
            }
            
            // Finalize buffered AI sentence if no new fragments for a while
            if (!string.IsNullOrEmpty(_currentAISentenceBuffer) && 
                Time.time - _lastAITranscriptTime > SENTENCE_COMPLETE_DELAY)
            {
                FinalizeSentenceBuffer();
            }

            // Watchdog (legacy): if evaluation requested, no parse triggered yet, and quiet for 5s after last cache, force parse
            if (_evaluationRequested && !_feedbackParseTriggered && _realtimeAIFeedback.Length >= 20 && !triggerFinalEvaluationOnDeeplyKeyword)
            {
                float quietElapsed = (_feedbackQuietStart > 0f) ? (Time.time - _feedbackQuietStart) : 0f;
                if (quietElapsed >= 5f)
                {
                    Debug.Log($"[MedicalExamManager] ⏳ Quiet for {quietElapsed:F1}s after COMPLETE FEEDBACK CACHE. Forcing local+second-AI parse now with {_realtimeAIFeedback.Length} chars.");
                    ForceParseNow();
                }
            }

            // If we require the realtime agent to say 'DEEPLY', trigger the final evaluation only on keyword.
            // Fail-safe: trigger after a timeout so we don't hang forever.
            if (_evaluationRequested && triggerFinalEvaluationOnDeeplyKeyword && !_deepEvalTriggered && !_feedbackParseTriggered)
            {
                // If we're expecting a realtime draft-evaluation (requested via SendTextMessage),
                // do not allow the timeout to fire before that prompt is actually sent.
                if (requestRealtimeEvaluationBeforeFinal && _draftEvalPromptSentTime <= 0f && _sendDraftEvalPromptCoroutine != null)
                    return;

                float start;
                if (requestRealtimeEvaluationBeforeFinal && _draftEvalPromptSentTime > 0f)
                    start = _draftEvalPromptSentTime;
                else
                    start = _evaluationRequestStartTime > 0f ? _evaluationRequestStartTime : Time.time;

                float timeout = Mathf.Max(5f, deeplyKeywordTimeoutSeconds);
                if (Time.time - start >= timeout)
                {
                    Debug.LogWarning($"[MedicalExamManager] ⏳ 'DEEPLY' keyword not received after {timeout:0}s. Triggering final evaluation anyway.");
                    _deepEvalTriggered = true;
                    StartFinalEvaluationPipeline();
                }
            }

            // Warn if parse started but results panel is still hidden after 12s
            if (_feedbackParseTriggered && _parseStartTime > 0f && Time.time - _parseStartTime >= 12f)
            {
                bool resultsHidden = evaluationResultsContent != null && !evaluationResultsContent.activeSelf;
                if (resultsHidden)
                {
                    Debug.LogWarning("[MedicalExamManager] ⚠️ Second AI parse seems stalled (>12s). Check API key/network and EvaluationDisplayUI logs.");
                    // Reset timer to avoid repeated warnings
                    _parseStartTime = Time.time;
                }
            }

            // UI guard: once a conversation has started, keep the Start button from reappearing.
            EnforceStartConversationUiGuard();
        }

        private void EnforceStartConversationUiGuard()
        {
            // During evaluation we never want start/loading UI visible.
            if (_evaluationRequested)
            {
                SetStartConversationUiState(showStart: false, showLoading: false);
                return;
            }

            // If conversation is running, Start must stay hidden.
            if (_conversationStarted)
            {
                // While waiting for the AI to begin speaking (timer not started yet), show loading only.
                if (_examActive && !_examTimerStarted)
                {
                    // Doctor→Patient: once the realtime session is ready, we should be in Recording (user speaks first).
                    if (selectedRole == RoleType.DoctorToPatient && _realtimeSessionReady)
                    {
                        SetStartConversationUiState(showStart: false, showLoading: false);
                        if (startConversationLoadingIndicator != null)
                            startConversationLoadingIndicator.SetActive(false);
                        if (aiTalkingIndicator != null)
                            aiTalkingIndicator.SetActive(false);
                        if (recordingIndicator != null)
                            recordingIndicator.SetActive(true);
                    }
                    else
                    {
                        SetStartConversationUiState(showStart: false, showLoading: true);
                        // Ensure exclusivity while loading
                        if (recordingIndicator != null)
                            recordingIndicator.SetActive(false);
                        if (aiTalkingIndicator != null)
                            aiTalkingIndicator.SetActive(false);
                    }
                }
                else
                    SetStartConversationUiState(showStart: false, showLoading: false);
                return;
            }

            // Not started yet: only show Start after role selection, never show loading.
            if (selectedRole != RoleType.None && startConversationPanel != null && startConversationPanel.activeSelf)
                SetStartConversationUiState(showStart: CanShowStartConversationButton(), showLoading: false);
            else
                SetStartConversationUiState(showStart: false, showLoading: false);
        }
        
        // Open AI Realtime
        private string _pendingRealtimeInstructions;

        private string _baseSystemInstructions;

        private string ResolvePhaseInstructionText(PhaseConfig config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.systemInstruction))
                return string.Empty;

            string roleSuffix = selectedRole == RoleType.DoctorToDoctor ? ".d2d" : ".d2p";
            string phasePromptKey = config.systemInstruction + roleSuffix;

            // Phase prompts are intentionally generic now: one prompt per phase/role.
            // Scenario-specific data comes from placeholders like {CURRENT_THEME} and {THEME_TERMS}.
            string instructionText = RemotePromptManager.Get(phasePromptKey, config.systemInstruction);
            return ApplyPromptPlaceholders(instructionText);
        }

        private void StartRealtimeSession(string instructions)
        {
            if (realtimeClient == null)
            {
                Debug.LogError("[MedicalExamManager] RealtimeClient not assigned!");
                return;
            }
            
            if (realtimeMicrophone == null)
            {
                Debug.LogError("[MedicalExamManager] RealtimeMicrophone not assigned!");
                return;
            }

            EnsureRealtimeClientEventBindings();

            _baseSystemInstructions = EnsureRoleIdentityInInstructions(instructions);
            _pendingRealtimeInstructions = _baseSystemInstructions;

            // Apply Phase 1 instruction immediately ensuring we start correctly
            if (enablePhaseManagement && phaseConfigs != null && phaseConfigs.Count > 0)
            {
                var p1 = phaseConfigs[0];
                currentPhase = p1.phase;
                phaseTimer = 0f;
                string phasePromptKey = p1.systemInstruction + (selectedRole == RoleType.DoctorToDoctor ? ".d2d" : ".d2p");
                string instructionText = ResolvePhaseInstructionText(p1);
                _pendingRealtimeInstructions = $"{instructions}\n\nAKTUELLE PHASE: {p1.phase}\nANWEISUNG: {instructionText}";
                _pendingRealtimeInstructions = EnsureRoleIdentityInInstructions(_pendingRealtimeInstructions);
                Debug.Log($"[MedicalExamManager] Initial phase set: {p1.phase} with instruction key: {phasePromptKey}");
            }

            realtimeClient.Connect();
        }

        private void EnsureRealtimeClientEventBindings()
        {
            if (realtimeClient == null || _realtimeCallbacksBound)
                return;

            realtimeClient.OnConnected += OnRealtimeConnected;
            realtimeClient.OnTranscriptDelta += OnRealtimeTranscriptDelta;
            realtimeClient.OnAudioStarted += OnRealtimeAudioStarted;
            realtimeClient.OnAudioFinished += OnRealtimeAudioFinished;
            realtimeClient.OnMistakeLogged += OnRealtimeMistakeLogged;
            realtimeClient.OnFinishPhase += OnRealtimeFinishPhase;
            realtimeClient.OnUserTranscriptCompleted += OnRealtimeUserTranscriptCompleted;
            realtimeClient.OnAITranscriptCompleted += OnRealtimeAITranscriptCompleted;
            realtimeClient.OnResponseCreated += OnRealtimeResponseCreated;
            realtimeClient.OnError += OnRealtimeError;
            realtimeClient.OnUserSpeechStarted += OnRealtimeUserSpeechStarted;

            _realtimeCallbacksBound = true;
        }

        private void OnRealtimeError(string err)
        {
            Debug.LogError($"[MedicalExamManager] Realtime API Error: {err}");
        }

        private void ResetSelectedCaseCache()
        {
            _selectedCaseScenarioId = null;
            _selectedCaseId = null;
            _selectedCaseTitle = null;
            _selectedCaseText = null;
            _selectedCaseTermsJson = null;
            _selectedCaseCoreFactsJson = null;
            _selectedCaseExpectedQuestionsJson = null;
            _selectedCasePhaseTermsJson.Clear();
            _selectedCasePhaseCoreFactsJson.Clear();
            _selectedCasePhaseExpectedQuestionsJson.Clear();
            _selectedCaseIndex = -1;
            ActiveCaseId = string.Empty;
            ActiveCaseTitle = string.Empty;
            ActiveCaseText = string.Empty;
            ActiveCaseTermsJson = string.Empty;
            ActiveCaseCoreFactsJson = string.Empty;
            ActiveCaseExpectedQuestionsJson = string.Empty;
            ActiveCaseIndex = -1;
        }

        private IEnumerator SelectRandomScenarioWhenReady()
        {
            // Wait briefly for remote manager instance and data to become available.
            float waited = 0f;
            while (!RemoteScenarioManager.IsReady && waited < 5f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
            }

            // Give the remote fetch/cache a little time so startup picks a real online scenario.
            var ids = RemoteScenarioManager.GetAllScenarioIds(excludeDefault: true);
            while (ids.Count == 0 && waited < 8f)
            {
                yield return new WaitForSeconds(0.25f);
                waited += 0.25f;
                ids = RemoteScenarioManager.GetAllScenarioIds(excludeDefault: true);
            }

            if (ids.Count > 0)
            {
                // Prefer the three exam themes requested by product flow.
                var preferred = new List<string>();
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    string low = id.Trim().ToLowerInvariant();
                    if (low == "cardiology" || low == "cardio" || low == "nephrology" || low == "nephro" || low == "neurology" || low == "neuro")
                        preferred.Add(id);
                }

                var source = preferred.Count > 0 ? preferred : ids;
                int idx = UnityEngine.Random.Range(0, source.Count);
                activeScenarioId = source[idx];
                Debug.Log($"[MedicalExamManager] Random scenario selected: {activeScenarioId} (preferred themes pool={preferred.Count}, total={ids.Count})");
            }
            else
            {
                Debug.Log("[MedicalExamManager] No specific scenarios found, using default.");
                if (string.IsNullOrWhiteSpace(activeScenarioId))
                    activeScenarioId = "default";
            }

            ActiveScenarioId = activeScenarioId;
            ResetSelectedCaseCache();
        }

        private IEnumerator StartRealtimeSessionWithOptionalRag(string baseInstructions)
        {
            // Wait for the remote CSV to finish loading before building the system prompt.
            // Without this, GenerateSystemPrompt() may run before the fetch completes and
            // return an empty or stale prompt, causing the AI to start with no context.
            float waitedForPrompts = 0f;
            while (!RemotePromptManager.IsLoaded && waitedForPrompts < 12f)
            {
                yield return new WaitForSeconds(0.25f);
                waitedForPrompts += 0.25f;
            }
            if (waitedForPrompts > 0f)
                Debug.Log($"[MedicalExamManager] Waited {waitedForPrompts:F1}s for RemotePromptManager to load before building system prompt.");

            // Regenerate the prompt now that prompts are guaranteed to be loaded.
            string freshPrompt = GenerateSystemPrompt();
            string promptToUse = !string.IsNullOrWhiteSpace(freshPrompt) ? freshPrompt : (baseInstructions ?? string.Empty);

            if (string.IsNullOrWhiteSpace(promptToUse))
            {
                Debug.LogError("[MedicalExamManager] System prompt is empty after waiting for remote load. Aborting realtime session.");
                SetStartConversationUiState(showStart: true, showLoading: false);
                yield break;
            }

            if (!useRagForRealtimeStart)
            {
                StartRealtimeSession(promptToUse);
                yield break;
            }

            if (ragIntegrationHelper == null || !ragIntegrationHelper.IsReady)
            {
                Debug.Log("[MedicalExamManager] RAG helper unavailable/not ready. Starting realtime without RAG context.");
                StartRealtimeSession(promptToUse);
                yield break;
            }

            var scenarioTags = BuildRagTagsForCurrentScenario();
            bool done = false;
            string enhancedPrompt = promptToUse;

            ragIntegrationHelper.EnhanceSystemPrompt(
                promptToUse,
                scenarioTags,
                onEnhancedPromptReady: (enhanced) =>
                {
                    enhancedPrompt = string.IsNullOrWhiteSpace(enhanced) ? promptToUse : enhanced;
                    done = true;
                },
                onError: (error) =>
                {
                    Debug.LogWarning($"[MedicalExamManager] RAG enhancement error: {error}. Continuing with base prompt.");
                    done = true;
                }
            );

            float timeout = Mathf.Max(0f, ragStartTimeoutSeconds);
            float start = Time.time;
            while (!done && Time.time - start < timeout)
                yield return null;

            if (!done)
                Debug.LogWarning("[MedicalExamManager] RAG enhancement timed out. Starting realtime with base prompt.");

            StartRealtimeSession(done ? enhancedPrompt : promptToUse);
        }
        
        private void OnRealtimeFinishPhase()
        {
            Debug.Log("[MedicalExamManager] AI triggered finish_phase tool. Advancing phase...");
            AdvancePhase();
        }

        private void OnRealtimeConnected()
        {
            Debug.Log("[MedicalExamManager] Connected to OpenAI Realtime API.");
            
            if (realtimeClient != null)
            {
                // Determine tool availability based on phase
                bool toolsEnabled = true;
                if (enablePhaseManagement && phaseConfigs != null && phaseConfigs.Count > 0)
                {
                   var config = phaseConfigs.Find(p => p.phase == currentPhase);
                   if (config != null) toolsEnabled = config.useTools;
                }

                var tools = GetRealtimeTools(toolsEnabled);
                // Get realtime voice from scenario or use defaults
                string realtimeVoice = GetRealtimeVoiceForRole();
                _pendingRealtimeInstructions = EnsureRoleIdentityInInstructions(_pendingRealtimeInstructions);
                realtimeClient.SendSessionUpdate(_pendingRealtimeInstructions, tools, realtimeVoice);

                // Wait for the server to confirm session.updated before triggering the first AI
                // response. Calling CreateResponse() immediately after SendSessionUpdate() risks
                // a race condition where response.create reaches the server before the updated
                // instructions are applied — causing the AI to reply with no persona (plain GPT).
                // This is especially noticeable for D2D where the Chefaerztin role must be set first.
                Action onUpdatedOnce = null;
                onUpdatedOnce = () =>
                {
                    realtimeClient.OnSessionUpdated -= onUpdatedOnce;

                    OnRealtimeSessionReady();

                    // Both D2D and D2P wait for the user (candidate) to speak first.
                    Debug.Log("[MedicalExamManager] session.updated confirmed — waiting for user to speak first (D2D and D2P).");
                };
                realtimeClient.OnSessionUpdated += onUpdatedOnce;

                EnsureAutoMicSegmentsRunning("RealtimeConnected");

                // Mark the audio position so the first user utterance is captured correctly.
                if (useAzurePronunciation && azurePronunciation != null && azurePronunciation.IsConfigured)
                    azurePronunciation.MarkTurnStart();
            }
        }

        private void OnRealtimeMistakeLogged(string description, string severity)
        {
            Debug.Log($"[MedicalExamManager] Mistake Logged: {severity} - {description}");
            // Add to logs for final report
            if (_evidenceLogger != null)
            {
                try { _evidenceLogger.AppendSection($"REALTIME_MISTAKE_{severity.ToUpper()}", description); } catch {}
            }
            _pronunciationNotesLog.Add($"[Mistake] {severity}: {description}");
        }

        private void OnRealtimeTranscriptDelta(string textDelta)
        {
            // Append incoming text transcript from AI
            if (string.IsNullOrEmpty(textDelta)) return;

            // Accumulate fragments into the current turn buffer, then display.
            // The buffer is cleared in OnRealtimeAudioStarted when a new AI turn begins.
            _currentRealtimeTranscript += textDelta;

            if (aiLiveTranscriptText != null)
            {
                aiLiveTranscriptText.text = _currentRealtimeTranscript;
            }

            // Keep the full conversation log for evaluation (this accumulates permanently)
            _lastAITranscriptTime = Time.time;
            
            // Also store for evidence logging
            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendLine("AI_TRANSCRIPT_FRAGMENT: " + textDelta);
            }
            catch { }
        }

        /// <summary>
        /// Called when a new AI response is created (before any transcript or audio deltas arrive).
        /// This is the correct place to clear the transcript buffer so we don't lose early words.
        /// </summary>
        private void OnRealtimeResponseCreated()
        {
            if (!_evaluationRequested && !_evaluationCompleted)
            {
                _currentRealtimeTranscript = "";
                if (aiLiveTranscriptText != null)
                    aiLiveTranscriptText.text = string.Empty;
                // Reset transcript alpha so the next fade-in starts from 0
                if (aiLiveTranscriptCanvasGroup != null)
                    aiLiveTranscriptCanvasGroup.alpha = 0f;
            }
        }

        private void OnRealtimeAudioStarted()
        {
            Debug.Log("[MedicalExamManager] 🎤 AI Audio Started (Realtime)");
            _aiWasSpeaking = true;
            if (transcriptpanel != null)
                transcriptpanel.SetActive(true);
            if (aiLiveTranscriptCanvasGroup != null)
            {
                if (_aiTranscriptFadeCoroutine != null) StopCoroutine(_aiTranscriptFadeCoroutine);
                _aiTranscriptFadeCoroutine = StartCoroutine(FadeCanvasGroup(aiLiveTranscriptCanvasGroup, aiLiveTranscriptCanvasGroup.alpha, 1f, aiTranscriptFadeDuration));
            }
            ShowIndicatorSmooth(aiTalkingIndicator, aiTalkingIndicatorCanvasGroup, ref _talkingIndicatorFadeCoroutine);
            if (aiTalkingIndicatorCanvasGroup != null)
            {
                if (_talkingIndicatorPulseCoroutine != null) StopCoroutine(_talkingIndicatorPulseCoroutine);
                _talkingIndicatorPulseCoroutine = StartCoroutine(PulseCanvasGroup(aiTalkingIndicatorCanvasGroup, talkingIndicatorPulseMin, 1f, talkingIndicatorPulseSpeed));
            }
            HideIndicatorSmooth(recordingIndicator, recordingIndicatorCanvasGroup, ref _recordingIndicatorFadeCoroutine);
        }

        private void OnRealtimeAudioFinished()
        {
            Debug.Log("[MedicalExamManager] 🔴 AI Audio Finished (Realtime) - Ready to record");
            _aiWasSpeaking = false;
            if (_talkingIndicatorPulseCoroutine != null) { StopCoroutine(_talkingIndicatorPulseCoroutine); _talkingIndicatorPulseCoroutine = null; }
            HideIndicatorSmooth(aiTalkingIndicator, aiTalkingIndicatorCanvasGroup, ref _talkingIndicatorFadeCoroutine);
            if (_examActive)
                ShowIndicatorSmooth(recordingIndicator, recordingIndicatorCanvasGroup, ref _recordingIndicatorFadeCoroutine);
        }

        private void ShowIndicatorSmooth(GameObject obj, CanvasGroup cg, ref Coroutine fadeRef)
        {
            if (obj == null) return;
            obj.SetActive(true);
            if (cg != null)
            {
                if (fadeRef != null) StopCoroutine(fadeRef);
                fadeRef = StartCoroutine(FadeCanvasGroup(cg, cg.alpha, 1f, indicatorFadeDuration));
            }
        }

        private void HideIndicatorSmooth(GameObject obj, CanvasGroup cg, ref Coroutine fadeRef)
        {
            if (obj == null) return;
            if (cg != null)
            {
                if (fadeRef != null) StopCoroutine(fadeRef);
                fadeRef = StartCoroutine(FadeCanvasGroupThenDisable(cg, obj, cg.alpha, 0f, indicatorFadeDuration));
            }
            else
            {
                obj.SetActive(false);
            }
        }

        private IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float duration)
        {
            if (cg == null) yield break;
            cg.alpha = from;
            float elapsed = 0f;
            float d = Mathf.Max(duration, 0.01f);
            while (elapsed < d)
            {
                elapsed += Time.unscaledDeltaTime;
                cg.alpha = Mathf.Lerp(from, to, elapsed / d);
                yield return null;
            }
            cg.alpha = to;
        }

        private IEnumerator FadeCanvasGroupThenDisable(CanvasGroup cg, GameObject obj, float from, float to, float duration)
        {
            yield return FadeCanvasGroup(cg, from, to, duration);
            if (obj != null) obj.SetActive(false);
        }

        private IEnumerator PulseCanvasGroup(CanvasGroup cg, float minAlpha, float maxAlpha, float speed)
        {
            if (cg == null) yield break;
            while (true)
            {
                float t = (Mathf.Sin(Time.unscaledTime * speed * Mathf.PI) + 1f) * 0.5f;
                cg.alpha = Mathf.Lerp(minAlpha, maxAlpha, t);
                yield return null;
            }
        }

        /// <summary>
        /// Called when the OpenAI Realtime API finishes transcribing one complete user utterance.
        /// Routes into the existing OnUserSpoke pipeline so user text is captured in _fullConversationLog.
        /// </summary>
        private void OnRealtimeUserSpeechStarted()
        {
            if (_examActive && !_examTimerStarted)
            {
                _examTimerStarted = true;
                _examStartTime = Time.time;
                SetStartConversationUiState(showStart: false, showLoading: false);
                if (startConversationLoadingIndicator != null)
                    startConversationLoadingIndicator.SetActive(false);
                Debug.Log("[MedicalExamManager] ⏱️ Exam timer started — student began speaking.");
            }
        }

        private void OnRealtimeUserTranscriptCompleted(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return;
            Debug.Log($"[MedicalExamManager] 🎤 Realtime user transcript completed: {transcript}");
            // Fire pronunciation tracking immediately on the complete utterance (fire-and-forget,
            // runs in parallel with OnUserSpoke so it doesn't add conversational latency).
            RequestPronunciationTracking(transcript);
            OnUserSpoke(transcript);
        }

        /// <summary>
        /// Called when the OpenAI Realtime API provides the complete AI speech transcript for one turn.
        /// Writes a clean "AI: ..." line into _fullConversationLog so the evaluation pipeline has data.
        /// </summary>
        private void OnRealtimeAITranscriptCompleted(string fullTranscript)
        {
            if (string.IsNullOrWhiteSpace(fullTranscript)) return;
            string cleaned = fullTranscript.Replace("\r", "").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) return;

            _fullConversationLog += $"AI: {cleaned}\n";
            Debug.Log($"[MedicalExamManager] ✅ AI transcript committed to conversation log ({cleaned.Length} chars). Log now {_fullConversationLog.Length} chars total.");

            // Also feed into evidence logger
            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendLine("AI_FULL_TURN: " + cleaned);
            }
            catch { }
        }

        private void SetupUI()
        {
            // Setup button listeners
            if (doctorButton != null)
            {
                doctorButton.onClick.RemoveAllListeners();
                doctorButton.onClick.AddListener(() => {
                    if (PlaySoundOnEnabledoctor != null)
                        PlaySoundOnEnabledoctor.PlayOneShot();
                    OnRoleSelected(RoleType.DoctorToDoctor);
                });
            }
            
            if (patientButton != null)
            {
                patientButton.onClick.RemoveAllListeners();
                patientButton.onClick.AddListener(() => {
                    if (PlaySoundOnEnablepatient != null)
                        PlaySoundOnEnablepatient.PlayOneShot();
                    OnRoleSelected(RoleType.DoctorToPatient);
                });
            }
            
            // Start Conversation button
            if (startConversationButton != null)
            {
                startConversationButton.onClick.RemoveAllListeners();
                startConversationButton.onClick.AddListener(StartConversation);
            }
            
            // End Conversation button
            if (endConversationButton != null)
            {
                endConversationButton.onClick.RemoveAllListeners();
                endConversationButton.onClick.AddListener(InterruptAndEvaluate);
                endConversationButton.gameObject.SetActive(false); // Hidden initially
            }

            // Pause/Resume button
            if (pauseConversationButton != null)
            {
                pauseConversationButton.onClick.RemoveAllListeners();
                pauseConversationButton.onClick.AddListener(ToggleRealtimePauseResume);
                pauseConversationButton.gameObject.SetActive(false); // Hidden initially
            }
            
            
            // Update scenario title
            if (scenarioTitleText != null)
                scenarioTitleText.text = GetScenarioName();
            
           
            
            // Hide start conversation panel initially
            if (startConversationPanel != null)
            {
                startConversationPanel.SetActive(false);
            }
            
            // Initialize UI indicators - all hidden initially
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);
            if (transcriptpanel != null)
                transcriptpanel.SetActive(false);
            if (startConversationIndicator != null)
                startConversationIndicator.SetActive(false);

            // Ensure the loading indicator is not visible at startup.
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);

            // Ensure Start Conversation button isn't visible until a role is selected.
            SetStartConversationUiState(showStart: false, showLoading: false);
        }
        
       
        
       
        private void ShowRoleSelection()
        {
            if (roleSelectionPanel != null)
                roleSelectionPanel.SetActive(true);

            // Ensure selection UI is visible (in case it was previously faded out)
            if (selectionGroup != null)
            {
                selectionGroup.alpha = 1f;
                selectionGroup.interactable = true;
                selectionGroup.blocksRaycasts = true;
            }
                
            // Hide both avatars (ensure only one is shown at a time after selection)
            if (doctorAvatar != null)
            {
                doctorAvatar.SetActive(false);
                Debug.Log("[MedicalExamManager] Doctor avatar hidden");
            }
            if (patientAvatar != null)
            {
                patientAvatar.SetActive(false);
                Debug.Log("[MedicalExamManager] Patient avatar hidden");
            }

            // Hide environments until a role is selected
            if (doctorToDoctorEnvironmentParent != null)
                doctorToDoctorEnvironmentParent.SetActive(false);
            if (doctorToPatientEnvironmentParent != null)
                doctorToPatientEnvironmentParent.SetActive(false);

            // Hide detail panels until a role is selected
            if (patientToDoctorDetailPanel != null)
                patientToDoctorDetailPanel.SetActive(false);
            if (doctorToDoctorDetailPanel != null)
                doctorToDoctorDetailPanel.SetActive(false);

            // Hide the talking/start panel while selecting a role.
            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: false);
        }

        // Back button handler: returns to scenario/role selection panel.
        public void BackToScenarioSelection()
        {
            Debug.Log("[MedicalExamManager] Back to scenario selection requested.");

            // Stop any ongoing coroutines related to conversation/evaluation UI.
            if (_evaluationProcessCoroutine != null)
            {
                StopCoroutine(_evaluationProcessCoroutine);
                _evaluationProcessCoroutine = null;
            }
            if (_aiTranscriptStreamingCoroutine != null)
            {
                StopCoroutine(_aiTranscriptStreamingCoroutine);
                _aiTranscriptStreamingCoroutine = null;
            }
            if (_realtimeEvalQuietWaitCoroutine != null)
            {
                StopCoroutine(_realtimeEvalQuietWaitCoroutine);
                _realtimeEvalQuietWaitCoroutine = null;
            }
            if (_sendDraftEvalPromptCoroutine != null)
            {
                StopCoroutine(_sendDraftEvalPromptCoroutine);
                _sendDraftEvalPromptCoroutine = null;
            }
            if (_userFinalizeCoroutine != null)
            {
                StopCoroutine(_userFinalizeCoroutine);
                _userFinalizeCoroutine = null;
            }
            if (_evaluationPlaybackCoroutine != null)
            {
                StopCoroutine(_evaluationPlaybackCoroutine);
                _evaluationPlaybackCoroutine = null;
            }

            _realtimePausedByUser = false;

            // Stop evaluation audio if any.
            StopEvaluationAudio();

            // Reset exam/conversation state.
            _examActive = false;
            _conversationStarted = false;
            _examTimerStarted = false;
            _evaluationRequested = false;
            _examCompletionTriggered = false;
            _evaluationCompleted = false;
            _evaluationUIShown = false;
            _feedbackParseTriggered = false;
            _deepEvalTriggered = false;
            _realtimeSessionReady = false;
            _realtimeAgentSpeaking = false;
            _realtimeAIFeedback = "";
            _fullConversationLog = "";
            _whisperUserTranscript = "";
            _pronunciationNotesLog.Clear();
            _speechTrackingNotesLog.Clear();
            _pendingUserUtterances.Clear();
            _currentAISentenceBuffer = "";
            _currentAiUiTurnBuffer = "";
            _aiDisplayedTurnText = "";
            _aiWordRemainder = "";
            _aiWordQueue.Clear();
            _aiAudioStarted = false;
            _currentUserSentenceBuffer = "";

            // Restore any audio mute/volume state.
            RestoreAiAudioMuteStateIfNeeded();
            RestoreAiAudioVolumeAfterRealtimeEvaluationIfNeeded(restoreToOne: true);

            // Stop auto-mic segments if they were running.
            if (enableAutoMicSegments)
            {
                try { whisperRecorder?.Unsubscribe(); } catch { }
                try { if (whisperRecorder != null) whisperRecorder.OnSegmentComplete.RemoveListener(OnWhisperSegmentComplete); } catch { }
                try { whisperMicrophoneStreamer?.StopStreaming(); } catch { }
                try { realtimeMicrophone?.StopStreaming(); } catch { } // Also stop realtime microphone
                try { if (useOpenAIRealtime && realtimeClient != null) realtimeClient.Disconnect(); } catch { } // Disconnect socket
                _autoMicSegmentsRunning = false;
            }

            // Hide conversation/evaluation UI.
            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            if (evaluationPanel != null)
                evaluationPanel.SetActive(false);
            if (feedbackPanel != null)
                feedbackPanel.SetActive(false);
            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(false);
            if (evaluationResultsContent != null)
                evaluationResultsContent.SetActive(false);

            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);
            if (startConversationIndicator != null)
                startConversationIndicator.SetActive(false);
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(false);
            if (pauseConversationButton != null)
                pauseConversationButton.gameObject.SetActive(false);

            // Hide detail panels when returning to selection
            if (patientToDoctorDetailPanel != null)
                patientToDoctorDetailPanel.SetActive(false);
            if (doctorToDoctorDetailPanel != null)
                doctorToDoctorDetailPanel.SetActive(false);

            // Reset role selection state and show the selection UI.
            selectedRole = RoleType.None;
            ShowRoleSelection();
        }
        
        public void OnRoleSelected(RoleType role)
        {
            selectedRole = role;
            Debug.Log($"[MedicalExamManager] Role selected: {role}");

            // Always use the role-specific phase map so D2D never falls back to Anamnesis/Summary labels.
            EnsureRoleSpecificPhaseConfig();

            // Hard gate: selecting a role must NOT start realtime.
            // Ensure any auto-started session is stopped and mic/upstream audio are disabled until Start Conversation is clicked.
            // ...existing code...

            // Reset conversation state so UI never shows loading/AI-talking due to stale flags.
            _examActive = false;
            _conversationStarted = false;
            _examTimerStarted = false;
            _evaluationRequested = false;
            _realtimeSessionReady = false;

            // Ensure exclusivity: when selecting a role we only want to show Start (no recording/AI/loading stacked).
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);
            
            // Fade out role selection UI over 2 seconds, then disable it
            if (_selectionFadeCoroutine != null)
            {
                StopCoroutine(_selectionFadeCoroutine);
                _selectionFadeCoroutine = null;
            }

            if (roleSelectionPanel != null)
            {
                if (selectionGroup != null)
                {
                    _selectionFadeCoroutine = StartCoroutine(FadeOutSelectionPanel());
                }
                else
                {
                    // Fallback: no CanvasGroup assigned
                    roleSelectionPanel.SetActive(false);
                }
            }
            
            // Show start conversation panel
            if (startConversationPanel != null)
            {
                startConversationPanel.SetActive(true);
                Debug.Log("[MedicalExamManager] Showing Start Conversation panel. Click button to begin.");
            }

            // Always reset the start/loading visuals on role selection.
            // Requirement: after picking a scenario/role, show ONLY Start Conversation (no loading).
            SetStartConversationUiState(showStart: CanShowStartConversationButton(), showLoading: false);
            
            // Keep avatars inactive until Start Conversation is pressed
            if (doctorAvatar != null)
                doctorAvatar.SetActive(false);
            if (patientAvatar != null)
                patientAvatar.SetActive(false);

            // Toggle environments based on selected role
            if (doctorToDoctorEnvironmentParent != null)
                doctorToDoctorEnvironmentParent.SetActive(role == RoleType.DoctorToDoctor);
            if (doctorToPatientEnvironmentParent != null)
                doctorToPatientEnvironmentParent.SetActive(role == RoleType.DoctorToPatient);

            // Always refresh role instruction text when a role is selected.
            RefreshRoleDetailPanelTexts();

            // Toggle detail panels based on selected role
            if (doctorToDoctorDetailPanel != null)
                doctorToDoctorDetailPanel.SetActive(role == RoleType.DoctorToDoctor);
            if (patientToDoctorDetailPanel != null)
                patientToDoctorDetailPanel.SetActive(role == RoleType.DoctorToPatient);
        }

        private void RefreshRoleDetailPanelTexts()
        {
            EnsureDetailPanelTextReferences();

            if (patientToDoctorDetailText != null)
                patientToDoctorDetailText.text = BuildDoctorToPatientDetailPanelText();

            if (doctorToDoctorDetailText != null)
                doctorToDoctorDetailText.text = BuildDoctorToDoctorDetailPanelText();
        }

        private void EnsureDetailPanelTextReferences()
        {
            if (patientToDoctorDetailText == null && patientToDoctorDetailPanel != null)
                patientToDoctorDetailText = patientToDoctorDetailPanel.GetComponentInChildren<TMP_Text>(true);

            if (doctorToDoctorDetailText == null && doctorToDoctorDetailPanel != null)
                doctorToDoctorDetailText = doctorToDoctorDetailPanel.GetComponentInChildren<TMP_Text>(true);
        }

        private string BuildDoctorToPatientDetailPanelText()
        {
            string caseSummary = string.IsNullOrWhiteSpace(_selectedCaseText)
                ? GetScenarioContext()
                : _selectedCaseText.Trim();

            string topic = GetScenarioTopicD2P();
            if (!string.IsNullOrWhiteSpace(topic))
            {
                string t = topic.Trim();
                if (!caseSummary.Contains(t, StringComparison.OrdinalIgnoreCase))
                    caseSummary = string.IsNullOrWhiteSpace(caseSummary) ? t : (caseSummary + " " + t);
            }

            return
                "Führen Sie ein strukturiertes Anamnesegespräch und erheben Sie dabei die persönlichen Daten, aktuellen Beschwerden, Vorerkrankungen, die Medikation, und Familienanamnese.\n\n" +
                "Leiten Sie daraus mögliche Verdachtsdiagnosen ab, unterbreiten Sie Vorschläge zur weiterführenden Diagnostik und Therapie und erläutern Sie die Maßnahmen.\n\n" +
                "Verwenden Sie dabei allgemein verständliche Formulierungen und verzichten Sie, soweit möglich, auf medizinische Fachterminologie. Schriftliche Notizen sind zulässig.\n\n" +
                "Patientenfall:\n" +
                (string.IsNullOrWhiteSpace(caseSummary) ? "Kein Falltext verfügbar." : caseSummary);
        }

        private string BuildDoctorToDoctorDetailPanelText()
        {
            var coreFacts = GetScenarioCoreFactsList();

            string diagnose = StripLeadingLabel(FindFirstFactContains(coreFacts, "verdachtsdiagnose", "diagnose"));
            string nameAge = StripLeadingLabel(FindFirstFactContains(coreFacts, "patient:"));
            string hergang = StripLeadingLabel(FindFirstFactContains(coreFacts, "symptomatik", "aufnahme"));
            string vorerkrankungen = StripLeadingLabel(FindFirstFactContains(coreFacts, "vorerkrank"));
            string allergien = StripLeadingLabel(FindFirstFactContains(coreFacts, "allerg"));
            string risikofaktoren = StripLeadingLabel(FindFirstFactContains(coreFacts, "risikofaktor"));
            string sozial = StripLeadingLabel(FindFirstFactContains(coreFacts, "sozialanamnese"));
            string familie = StripLeadingLabel(FindFirstFactContains(coreFacts, "familienanamnese"));
            string vitaleEd = StripLeadingLabel(FindFirstFactContains(coreFacts, "vitale zeichen ed", "vital"));
            string untersuchung = StripLeadingLabel(FindFirstFactContains(coreFacts, "klinische untersuchung", "untersuchung"));
            string laborUnauff = StripLeadingLabel(FindFirstFactContains(coreFacts, "laborwerte unauff", "laborwerte unauffällig", "normwertig"));
            string laborVeraendert = StripLeadingLabel(FindFirstFactContains(coreFacts, "laborwerte erh", "veränderte laborwerte", "troponin", "hba1c"));
            string ekg = StripLeadingLabel(FindFirstFactContains(coreFacts, "ecg", "ekg"));
            string medVor = StripLeadingLabel(FindFirstFactContains(coreFacts, "medikation vor", "medikation:"));
            string medNeu = StripLeadingLabel(FindFirstFactContains(coreFacts, "medikation station", "pausiert"));
            string vitaleAktuell = StripLeadingLabel(FindFirstFactContains(coreFacts, "vitale zeichen aktuell", "aktuell asymptomatisch", "aktuell:"));
            string procedere = StripLeadingLabel(FindFirstFactContains(coreFacts, "procedere", "geplantes procedere", "koronarangiografie", "pci"));

            var builder = new System.Text.StringBuilder(1024);
            builder.AppendLine("Stellen Sie den vorliegenden Patientenfall der ärztlichen Leitung vor und beantworten Sie mögliche Rückfragen. Verwenden Sie dabei die medizinische Fachterminologie.");
            builder.AppendLine();
            builder.AppendLine("Im Anschluss werden nach dem Zufallsprinzip fünf gängige medizinische Fachbegriffe ausgewählt. Bitte erklären Sie deren jeweilige Bedeutung einmal mündlich.");
            builder.AppendLine();
            builder.AppendLine("Patientenfall:");

            string title = string.IsNullOrWhiteSpace(_selectedCaseTitle) ? GetScenarioName() : _selectedCaseTitle.Trim();
            if (!string.IsNullOrWhiteSpace(title))
                builder.AppendLine(title);

            builder.AppendLine();
            builder.AppendLine("Informationsblatt:");
            AppendInfoLine(builder, "(Verdachts-)Diagnose/Leitsymptom", diagnose);
            AppendInfoLine(builder, "Name, Alter", nameAge);
            AppendInfoLine(builder, "Hergang bei der Aufnahme", hergang);
            AppendInfoLine(builder, "Vorerkrankungen", vorerkrankungen);
            AppendInfoLine(builder, "Allergien", allergien);
            AppendInfoLine(builder, "Risikofaktoren", risikofaktoren);
            AppendInfoLine(builder, "Sozialanamnese", sozial);
            AppendInfoLine(builder, "Familienanamnese", familie);
            AppendInfoLine(builder, "Vitalparameter bei Aufnahme", vitaleEd);
            AppendInfoLine(builder, "Körperliche Untersuchung", untersuchung);
            AppendInfoLine(builder, "Wichtige Laborwerte (unauffällig)", laborUnauff);
            AppendInfoLine(builder, "Wichtige Laborwerte (verändert)", laborVeraendert);
            AppendInfoLine(builder, "Wichtige apparative Untersuchungen", ekg);
            AppendInfoLine(builder, "Medikation bisher", medVor);
            AppendInfoLine(builder, "Neu angesetzt/umgestellt", medNeu);
            AppendInfoLine(builder, "Stationärer Verlauf", vitaleAktuell);
            AppendInfoLine(builder, "Procedere", procedere);

            return builder.ToString().Trim();
        }

        private static void AppendInfoLine(System.Text.StringBuilder builder, string label, string value)
        {
            if (builder == null || string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
                return;

            builder.AppendLine(label + ": " + value.Trim());
        }

        private List<string> GetScenarioCoreFactsList()
        {
            var items = new List<string>();
            AddItemsFromItemsJson(items, GetScenarioCoreFactsJson());
            return items;
        }

        private static string FindFirstFactContains(List<string> facts, params string[] tokens)
        {
            if (facts == null || facts.Count == 0 || tokens == null || tokens.Length == 0)
                return string.Empty;

            for (int i = 0; i < facts.Count; i++)
            {
                string fact = facts[i];
                if (string.IsNullOrWhiteSpace(fact))
                    continue;

                string lowerFact = fact.ToLowerInvariant();
                for (int t = 0; t < tokens.Length; t++)
                {
                    string token = tokens[t];
                    if (!string.IsNullOrWhiteSpace(token) && lowerFact.Contains(token.ToLowerInvariant()))
                        return fact.Trim();
                }
            }

            return string.Empty;
        }

        private static string StripLeadingLabel(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string clean = value.Trim();
            int idx = clean.IndexOf(':');
            if (idx >= 0 && idx < clean.Length - 1)
                return clean.Substring(idx + 1).Trim();

            return clean;
        }

        /// <summary>
        /// Gets the scenario description for the welcome message
        /// </summary>
        public string GetScenarioDescription()
        {
            string name = GetScenarioName();
            string topic = GetScenarioTopic();
            string context = GetScenarioContext();

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(topic))
            {
                return selectedRole == RoleType.DoctorToDoctor
                    ? "ein medizinisches Fachgespräch"
                    : "ein Patientengespräch";
            }

            if (!string.IsNullOrWhiteSpace(topic))
                return $"{name} ({context}) – {topic}";

            return string.IsNullOrWhiteSpace(context) ? name : $"{name} ({context})";
        }
        
        
        private IEnumerator FadeOutSelectionPanel()
        {
            if (selectionGroup == null)
                yield break;

            // Prevent additional interactions while fading
            selectionGroup.interactable = false;
            selectionGroup.blocksRaycasts = false;

            float elapsed = 0f;
            float startAlpha = selectionGroup.alpha;

            while (elapsed < SELECTION_FADE_SECONDS)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / SELECTION_FADE_SECONDS);
                selectionGroup.alpha = Mathf.Lerp(startAlpha, 0f, t);
                yield return null;
            }

            selectionGroup.alpha = 0f;

            if (roleSelectionPanel != null)
                roleSelectionPanel.SetActive(false);

            _selectionFadeCoroutine = null;
        }
        
        
        /// <summary>
        /// Prepares UI and state for conversation start (without actually starting).
        /// Called by WelcomeAudioPlayer before playing welcome audio.
        /// </summary>
        public void PrepareConversationStart()
        {
            _realtimeSessionReady = false;

            // Ensure exclusivity: Start must disappear, and no other indicators overlap with loading.
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);

            // Requirement: when Start is clicked, hide Start and show ONLY loading.
            SetStartConversationUiState(showStart: false, showLoading: true);

            // Start voice-activated listening immediately so the user can speak without pressing any other button.
            EnsureAutoMicSegmentsRunning(reason: "StartConversation");
        }
        
        private void StartExamConversation()
        {
            
            _examActive = true;
            _examDuration = Mathf.Max(1f, GetExamDurationMinutes()) * 60f; // Convert to seconds
            _examStartTime = -1f;
            _examTimerStarted = false;
            _evaluationRequested = false;
            _fullConversationLog = "";
            _realtimeAIFeedback = ""; // Reset feedback capture for new exam
            _currentAISentenceBuffer = ""; // Reset sentence buffer
            _lastAITranscriptTime = 0f;
            _evaluationUIShown = false; // Reset evaluation UI state for new run
            _feedbackParseTriggered = false; // Reset parse trigger flag
            _lastFeedbackFragmentTime = 0f;
            _feedbackQuietStart = 0f;
            _evaluationCompleted = false;

            _realtimeSessionReady = false;
            _realtimePauseStartedAt = -1f;

            // Ensure exclusivity at start: while connecting we should not show recording/AI-talking yet.
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);

            _draftEvalPromptSentTime = 0f;
            _receivedAnyRealtimeEvalFragment = false;

            _loggedEvaluationRequestLine = false;
            _evaluationAiLineOpen = false;

            _questionPlanUsedForRun = "";

            // Do NOT clear aiLiveTranscriptText here.
            // The initial AI response is written to the transcript area before/after this method,
            // and clearing here makes the first AI line appear to never show up.

            _aiAudioStarted = false;
            _aiAudioCheckStartTime = 0f;

            // Restore audio state from any previous run
            RestoreAiAudioMuteStateIfNeeded();
            if (useOpenAIRealtime && realtimeClient != null)
            {
                try { realtimeClient.SetOutputMuted(false); } catch { }
            }
            
            // Move surfacetouch to scenario position when conversation starts
            if (surfacetouch != null && scenarioPositionTransform != null)
            {
                surfacetouch.transform.position = scenarioPositionTransform.position;
                surfacetouch.transform.rotation = scenarioPositionTransform.rotation;
                Debug.Log($"[MedicalExamManager] Moved surfacetouch to scenario position: {scenarioPositionTransform.position}");
            }
            
            // Show appropriate avatar based on selected role (only one at a time)
            if (selectedRole == RoleType.DoctorToDoctor)
            {
                // Enable doctor avatar, disable patient avatar
                if (doctorAvatar != null)
                {
                    doctorAvatar.SetActive(true);
                    Debug.Log("[MedicalExamManager] ✓ Doctor avatar enabled");
                }
                if (patientAvatar != null)
                {
                    patientAvatar.SetActive(false);
                    Debug.Log("[MedicalExamManager] Patient avatar disabled");
                }
            }
            else if (selectedRole == RoleType.DoctorToPatient)
            {
                // Enable patient avatar, disable doctor avatar
                if (patientAvatar != null)
                {
                    patientAvatar.SetActive(true);
                    Debug.Log("[MedicalExamManager] ✓ Patient avatar enabled");
                }
                if (doctorAvatar != null)
                {
                    doctorAvatar.SetActive(false);
                    Debug.Log("[MedicalExamManager] Doctor avatar disabled");
                }
            }
            
            // Generate and apply system prompt
            string systemPrompt = GenerateSystemPrompt();
            Debug.Log($"[MedicalExamManager] Starting exam with prompt:\n{systemPrompt}");

            // Determine role type for planning
            ExamEvaluation.RoleType planRoleType = selectedRole == RoleType.DoctorToDoctor
                ? ExamEvaluation.RoleType.DoctorToDoctor
                : ExamEvaluation.RoleType.DoctorToPatient;

            // Optionally: pre-generate a question plan using fine-tuned GPT-4, then start realtime.
            // If we're using Whisper + a custom fine-tuned final evaluator with a large prompt bank,
            // skip the pre-start question-plan (we'll include the bank at final evaluation time).
            // If we are using whisper-driven modes, capture the configured start prompt
            // for later inclusion in the evaluation transcript.
            if (useWhisperWithCustomFineTuned || useWhisperOnlySingleGPT)
            {
                _startPromptUsedForRun = ApplyPromptPlaceholders(ResolveWhisperStartPromptForSelectedRole());
                if (!string.IsNullOrEmpty(_startPromptUsedForRun))
                    Debug.Log("[MedicalExamManager] Using whisper start prompt for this run (will be included in evaluation).");
            }

            // If the user selected Whisper-only single-GPT mode, DO NOT start the realtime agent.
            // Stop any running agent and ensure upstream mic is disabled so no realtime AI is used.
            if (useWhisperOnlySingleGPT)
            {
                Debug.Log("[MedicalExamManager] Whisper-only mode enabled: realtime agent will NOT be started.");
            }
           
            else
            {
                StartRealtimeWithPrompt(systemPrompt);
            }

            // Start background capture of user mic audio (up to 3 minutes) for optional Whisper transcription later.
            // This does NOT create a second microphone recording; it just buffers the same PCM we already stream.
            try
            {
                EnsureAutoMicSegmentsRunning(reason: "StartExamConversation");
            }
            catch { }
            // Mark conversation as started
            _conversationStarted = true;

            // Timer is frozen until the student speaks for the first time.
            // StartExamTimerOnFirstSpeech() (via OnRealtimeUserSpeechStarted) will set _examTimerStarted = true.
            _examTimerStarted = false;
            _examStartTime = -1f;
            Debug.Log("[MedicalExamManager] ⏱️ Timer frozen — waiting for student to speak.");

            // Show full exam duration as frozen display while waiting
            if (timerText != null)
            {
                int minutes = Mathf.FloorToInt(_examDuration / 60f);
                int seconds = Mathf.FloorToInt(_examDuration % 60f);
                timerText.text = $"{minutes:00}:{seconds:00}";
                timerText.color = Color.white;
            }
            
            // Show End Conversation button
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(true);
            if (pauseConversationButton != null)
                pauseConversationButton.gameObject.SetActive(useOpenAIRealtime);
            
            Debug.Log($"[MedicalExamManager] Exam started. Duration: {GetExamDurationMinutes()} minutes");
        }

        private void EnsureAutoMicSegmentsRunning(string reason)
        {
            if (useOpenAIRealtime)
            {
                 // Using Realtime API microphone instead of Whisper streamer
                 if (realtimeMicrophone != null)
                 {
                     realtimeMicrophone.StartStreaming();
                     _autoMicSegmentsRunning = true; 
                     Debug.Log($"[MedicalExamManager] REALTIME Mic streaming started. Reason={reason}");
                 }
                 return;
            }

            if (!enableAutoMicSegments)
            {
                Debug.Log($"[MedicalExamManager] Auto mic segments disabled (enableAutoMicSegments=false). Reason={reason}");
                return;
            }

            if (_autoMicSegmentsRunning)
                return;

            if (whisperMicrophoneStreamer == null)
                Debug.LogWarning($"[MedicalExamManager] Auto mic segments enabled, but whisperMicrophoneStreamer is NOT assigned. Reason={reason}");
            if (whisperRecorder == null)
                Debug.LogWarning($"[MedicalExamManager] Auto mic segments enabled, but whisperRecorder is NOT assigned. Reason={reason}");

            try
            {
                whisperMicrophoneStreamer?.StartStreaming();
                Debug.Log($"[MedicalExamManager] Mic streaming started for auto-segmentation. Reason={reason}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MedicalExamManager] Could not start microphone streamer. Reason={reason}. Error: {ex.Message}");
            }

            if (whisperRecorder != null)
            {
                whisperRecorder.SetMicrophoneStreamer(whisperMicrophoneStreamer, resubscribe: false);
                whisperRecorder.Subscribe();
                whisperRecorder.OnSegmentComplete.AddListener(OnWhisperSegmentComplete);
                Debug.Log($"[MedicalExamManager] WhisperRecorder subscribed (auto-segmentation). Reason={reason}");
            }

            _autoMicSegmentsRunning = true;
        }

        private System.Collections.IEnumerator FlushPendingUserUtterances()
        {
            while (_conversationReady && _pendingUserUtterances.Count > 0)
            {
                var text = _pendingUserUtterances.Dequeue();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                Debug.Log($"[MedicalExamManager] Flushing queued user utterance: {text}");
                if (gptAndWhisper != null)
                {
                    yield return StartCoroutine(gptAndWhisper.SendConversationMessage(
                        text,
                        gptResponse => {
                            Debug.Log($"[MedicalExamManager] GPT-4 conversational response: {gptResponse}");
                            if (generalFeedbackText != null)
                                generalFeedbackText.text = gptResponse;
                            if (aiLiveTranscriptText != null)
                                aiLiveTranscriptText.text = gptResponse;

                            // Store hidden pronunciation feedback for later (do not speak it).
                            if (gptAndWhisper != null && !string.IsNullOrWhiteSpace(gptAndWhisper.LastPronunciationSummary))
                                _pronunciationNotesLog.Add(gptAndWhisper.LastPronunciationSummary.Trim());

                            try
                            {
                                var scenario = selectedRole == RoleType.DoctorToDoctor
                                    ? ElevenLabsTTS.ScenarioType.DoctorToDoctor
                                    : ElevenLabsTTS.ScenarioType.DoctorToPatient;
                                gptAndWhisper?.SpeakResponse(gptResponse, scenario);
                            }
                            catch { }

                            _fullConversationLog += "AI: " + gptResponse + "\n";
                        },
                        error => {
                            Debug.LogError($"[MedicalExamManager] GPT-4 conversational error: {error}");
                        }
                    ));
                }
            }
        }

        private void StartExamTimerNow()
        {
            // Timer now starts at conversation begin, not at first AI speech.
            // This method is kept for backwards compatibility but does nothing if timer already started.
            if (_examTimerStarted)
                return;

            _examTimerStarted = true;
            _examStartTime = Time.time;
            SetStartConversationUiState(showStart: false, showLoading: false);
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);
            Debug.Log("[MedicalExamManager] ⏱️ Exam timer started (AI began speaking - fallback path)." );
        }

        // Handler for PCM segments produced by WhisperRecorder
        private void OnWhisperSegmentComplete(byte[] pcm16)
        {
            if (pcm16 == null || pcm16.Length == 0)
                return;

            // Show recording indicator while transcribing
            if (recordingIndicator != null) recordingIndicator.SetActive(true);
            if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(false);
            // Kick off transcription coroutine
            StartCoroutine(HandleWhisperSegment(pcm16));
        }

        private System.Collections.IEnumerator HandleWhisperSegment(byte[] pcm16)
        {
            if (evaluationDisplayUI == null)
            {
                Debug.LogWarning("[MedicalExamManager] Cannot transcribe whisper segment: EvaluationDisplayUI not assigned.");
                yield break;
            }

            string apiKey = GetAPIKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogWarning("[MedicalExamManager] Cannot transcribe whisper segment: no API key.");
                yield break;
            }

            bool done = false;
            string textResult = null;
            string err = null;

            yield return evaluationDisplayUI.TranscribeUserAudioPcm16WithWhisper(
                pcm16,
                16000,
                apiKey,
                t => { textResult = t; done = true; },
                e => { err = e; done = true; }
            );

            if (!done)
            {
                Debug.LogWarning("[MedicalExamManager] Whisper transcription coroutine returned without result.");
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(textResult))
            {
                // Check for "evaluate now" voice command
                string lowerTranscript = textResult.ToLower();
                if ((lowerTranscript.Contains("evaluate now") || lowerTranscript.Contains("evaluation now") || 
                     lowerTranscript.Contains("beurteile jetzt") || lowerTranscript.Contains("bewertung jetzt") ||
                     lowerTranscript.Contains("evaluate") && lowerTranscript.Contains("now")) && 
                    !_evaluationRequested && !_evaluationCompleted && _examActive)
                {
                    Debug.Log("[MedicalExamManager] 🎯 User said 'evaluate now' - triggering evaluation...");
                    if (recordingIndicator != null) recordingIndicator.SetActive(false);
                    if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(false);
                    InterruptAndEvaluate();
                    yield break;
                }

                // Always track pronunciation on each user utterance (separate AI request).
                RequestPronunciationTracking(textResult);
                if (!_conversationReady)
                {
                    Debug.LogWarning("[MedicalExamManager] Conversation not ready yet (waiting for initial AI response). Buffering this utterance.");

                    _pendingUserUtterances.Enqueue(textResult);
                    if (generalFeedbackText != null)
                        generalFeedbackText.text = "Captured your speech. Waiting for AI to start...";

                    if (recordingIndicator != null) recordingIndicator.SetActive(false);
                    if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(false);
                    yield break;
                }
                // Hide recording indicator, show AI speaking indicator
                if (recordingIndicator != null) recordingIndicator.SetActive(false);
                if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(true);
                if (transcriptpanel != null) transcriptpanel.SetActive(true);
                // Append to transcripts
                _whisperUserTranscript += textResult + "\n";
                _fullConversationLog += "User: " + textResult + "\n";
                Debug.Log($"[MedicalExamManager] Whisper transcript: {textResult}");

                // Send transcript to the conversational model (not the final evaluation)
                if (gptAndWhisper != null)
                {
                    yield return StartCoroutine(gptAndWhisper.SendConversationMessage(
                        textResult,
                        gptResponse => {
                            Debug.Log($"[MedicalExamManager] GPT-4 conversational response: {gptResponse}");
                            // Show the AI response text in the UI if TTS is not configured
                            if (generalFeedbackText != null)
                                generalFeedbackText.text = gptResponse;
                            if (aiLiveTranscriptText != null)
                                aiLiveTranscriptText.text = gptResponse;

                            // Store hidden pronunciation feedback for later (do not speak it).
                            if (gptAndWhisper != null && !string.IsNullOrWhiteSpace(gptAndWhisper.LastPronunciationSummary))
                                _pronunciationNotesLog.Add(gptAndWhisper.LastPronunciationSummary.Trim());

                            // Speak the AI response using ElevenLabs based on scenario
                            try
                            {
                                var scenario = selectedRole == RoleType.DoctorToDoctor
                                    ? ElevenLabsTTS.ScenarioType.DoctorToDoctor
                                    : ElevenLabsTTS.ScenarioType.DoctorToPatient;
                                gptAndWhisper?.SpeakResponse(gptResponse, scenario);
                            }
                            catch { }

                            // Append AI line to conversation log
                            _fullConversationLog += "AI: " + gptResponse + "\n";
                        },
                        error => {
                            Debug.LogError($"[MedicalExamManager] GPT-4 conversational error: {error}");
                        }
                    ));
                }
                else
                {
                    Debug.LogWarning("[MedicalExamManager] GptAndWhisper not assigned!");
                }
            }
            else
            {
                Debug.LogWarning($"[MedicalExamManager] Whisper transcription failed: {err}");
            }
        }

        private void StartRealtimeWithPrompt(string systemPrompt)
        {
            Debug.Log("[MedicalExamManager] StartRealtimeWithPrompt called, but RealtimeConversationManager has been removed.");
        }

        private void OnAgentSpeakingChanged(bool speaking)
        {
            bool wasSpeaking = _realtimeAgentSpeaking;
            _realtimeAgentSpeaking = speaking;

            // Clear the previous AI message when the AI starts speaking again (new question/response)
            // This ensures the transcript shows only the current AI turn
            if (!_evaluationRequested && !_evaluationCompleted && speaking && !wasSpeaking)
            {
                // Always clear the previous message when AI starts speaking
                _currentAiUiTurnBuffer = "";
                ResetAiTranscriptStreamingState();
                
                // Clear the text for new AI turn
                if (aiLiveTranscriptText != null)
                    aiLiveTranscriptText.text = string.Empty;

                _userSpokeSinceLastAiUiTurn = false;
                _aiAudioStarted = false; // Reset audio tracking for new turn
                _aiAudioCheckStartTime = Time.time; // Start audio detection timer
            }

            // Ensure the streaming coroutine is running while the agent is speaking.
            if (!_evaluationRequested && !_evaluationCompleted && speaking && _aiTranscriptStreamingCoroutine == null)
                _aiTranscriptStreamingCoroutine = StartCoroutine(AiTranscriptStreamingCoroutine());
        }

        private void ResetAiTranscriptStreamingState()
        {
            if (_aiTranscriptStreamingCoroutine != null)
            {
                StopCoroutine(_aiTranscriptStreamingCoroutine);
                _aiTranscriptStreamingCoroutine = null;
            }

            _aiWordQueue.Clear();
            _aiDisplayedTurnText = "";
            _aiWordRemainder = "";
        }

        private void AppendAiTranscriptFragmentWords(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment))
                return;

            string cleaned = fragment.Replace("\r", " ").Replace("\n", " ");
            if (!string.IsNullOrEmpty(_aiWordRemainder))
                cleaned = _aiWordRemainder + cleaned;

            // Split on whitespace; keep the last token as remainder if the fragment likely ended mid-word.
            // Heuristic: if the original fragment didn't end with whitespace, treat the last token as incomplete.
            bool endsWithWhitespace = char.IsWhiteSpace(fragment[fragment.Length - 1]);

            string[] tokens = cleaned.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;

            int countToAdd = tokens.Length;
            _aiWordRemainder = "";
            if (!endsWithWhitespace)
            {
                countToAdd = Math.Max(0, tokens.Length - 1);
                _aiWordRemainder = tokens[tokens.Length - 1];
            }

            for (int i = 0; i < countToAdd; i++)
                _aiWordQueue.Enqueue(tokens[i]);

            // Ensure the streaming coroutine is running.
            if (!_evaluationRequested && !_evaluationCompleted && _realtimeAgentSpeaking && _aiTranscriptStreamingCoroutine == null)
                _aiTranscriptStreamingCoroutine = StartCoroutine(AiTranscriptStreamingCoroutine());
        }

        private IEnumerator AiTranscriptStreamingCoroutine()
        {
            while (true)
            {
                if (aiLiveTranscriptText == null)
                    break;

                if (_evaluationRequested || _evaluationCompleted)
                    break;

                // Wait until audio actually starts playing before showing transcript
                if (!_aiAudioStarted)
                {
                    bool audioIsPlaying = aiAudioSource != null && aiAudioSource.isPlaying;
                    bool audioTimeoutElapsed = (Time.time - _aiAudioCheckStartTime) > AI_AUDIO_DETECT_TIMEOUT;
                    
                    if (audioIsPlaying || audioTimeoutElapsed || aiAudioSource == null)
                    {
                        _aiAudioStarted = true;
                        if (audioTimeoutElapsed && !audioIsPlaying)
                            Debug.LogWarning($"[MedicalExamManager] ⏱️ Audio detection timeout ({AI_AUDIO_DETECT_TIMEOUT}s) - showing transcript anyway");
                        else if (audioIsPlaying)
                            Debug.Log($"[MedicalExamManager] 🎵 AI audio started - beginning transcript display (buffered {_aiWordQueue.Count} words)");
                        else
                            Debug.LogWarning("[MedicalExamManager] No audio source - showing transcript immediately");
                    }
                    else
                    {
                        // Still waiting for audio
                        yield return null;
                        continue;
                    }
                }

                // If no queued words, wait for more while speaking; if speaking stopped, end.
                if (_aiWordQueue.Count == 0)
                {
                    if (_realtimeAgentSpeaking)
                    {
                        yield return null;
                        continue;
                    }
                    break;
                }

                int stepWords = Mathf.Clamp(aiTranscriptWordsPerStep, 1, 12);
                float stepDelay = Mathf.Max(0.01f, aiTranscriptStepSeconds);

                int take = Mathf.Min(stepWords, _aiWordQueue.Count);
                for (int i = 0; i < take; i++)
                {
                    string w = _aiWordQueue.Dequeue();
                    if (string.IsNullOrWhiteSpace(w))
                        continue;
                    if (_aiDisplayedTurnText.Length == 0)
                        _aiDisplayedTurnText = w;
                    else
                        _aiDisplayedTurnText += " " + w;
                }

                aiLiveTranscriptText.text = _aiDisplayedTurnText;
                yield return new WaitForSeconds(stepDelay);
            }

            _aiTranscriptStreamingCoroutine = null;
        }

        private IEnumerator WaitRealtimeQuietThenStartFinal()
        {
            float requiredQuiet = Mathf.Max(0f, realtimeEvalPostEndQuietSeconds);
            while (Time.time - _lastFeedbackFragmentTime < requiredQuiet)
                yield return null;

            _realtimeEvalQuietWaitCoroutine = null;
            StartFinalEvaluationPipeline();
        }
        
        private string GenerateSystemPrompt()
        {
            string scenarioName = GetScenarioName();
            string topic = GetScenarioTopic();
            string context = GetScenarioContext();
            int durationMinutes = GetExamDurationMinutes();
            ExamLanguage examLanguage = GetExamLanguage();

            string promptKey = selectedRole == RoleType.DoctorToDoctor
                ? "whisper.start.doctor_to_doctor"
                : "whisper.start.doctor_to_patient";

            // REMOTE ONLY: Get the complete system prompt from CSV (scenario-specific override if available)
            string basePrompt = RemotePromptManager.GetForScenario(promptKey, activeScenarioId, "");
            if (string.IsNullOrWhiteSpace(basePrompt))
            {
                Debug.LogError($"[MedicalExamManager] Remote prompt '{promptKey}' not found in CSV.");
                return string.Empty;
            }

            string resolved = ApplyPromptPlaceholders(basePrompt);
            
            // Append general guardrails
            string generalGuardrails = RemotePromptManager.GetForScenario("realtime.guardrails.general", activeScenarioId, "");
            if (!string.IsNullOrWhiteSpace(generalGuardrails))
            {
                resolved += "\n\n" + ApplyPromptPlaceholders(generalGuardrails);
            }

            if (selectedRole == RoleType.DoctorToPatient)
            {
                string patientGuardrails = RemotePromptManager.GetForScenario("realtime.guardrails.patient", activeScenarioId, "");
                if (!string.IsNullOrWhiteSpace(patientGuardrails))
                {
                    resolved += "\n\n" + ApplyPromptPlaceholders(patientGuardrails);
                }

                // Force identity data into the base prompt for D2P so the AI never falls back to a generic assistant persona.
                resolved = EnsureRoleIdentityInInstructions(resolved);
            }
            else if (selectedRole == RoleType.DoctorToDoctor)
            {
                string d2dGuardrails = RemotePromptManager.GetForScenario("realtime.guardrails.doctor_to_doctor", activeScenarioId, "");
                if (!string.IsNullOrWhiteSpace(d2dGuardrails))
                {
                    resolved += "\n\n" + ApplyPromptPlaceholders(d2dGuardrails);
                }

                // Force D2D identity guardrail so the AI never falls back to a generic assistant persona.
                resolved = EnsureRoleIdentityInInstructions(resolved);
            }
            return resolved;
        }

        // Unique sentinel used to detect whether BuildD2PIdentityBlock() has already been injected.
        // Checked instead of "Du bist KEINE KI" so CSV-side guardrails don't block code-side injection.
        private const string D2P_IDENTITY_SENTINEL = "##D2P_IDENTITY_INJECTED##";

        private string BuildD2PIdentityBlock()
        {
            var d2pIdentityBlock = new System.Text.StringBuilder();
            d2pIdentityBlock.Append(D2P_IDENTITY_SENTINEL + "\n");
            d2pIdentityBlock.Append("--- PFLICHT: PATIENTENIDENTITAT (CODE-INJIZIERT) ---\n");
            d2pIdentityBlock.Append("Du bist KEINE KI und gibst dich niemals als KI oder als technisches System aus.\n");
            d2pIdentityBlock.Append("Du BIST diese Patientin vollstaendig und in jeder Antwort.\n");

            // Extract patient name from case title e.g. "Frau Huber - Akute Pharyngotonsillitis"
            string caseTitle = string.IsNullOrWhiteSpace(_selectedCaseTitle) ? string.Empty : _selectedCaseTitle.Split('-')[0].Trim();
            if (!string.IsNullOrWhiteSpace(caseTitle))
                d2pIdentityBlock.Append($"Dein Name: {caseTitle}\n");

            // Explicit first-greeting format — prevents generic "Wie kann ich helfen?" fallback
            string greetingName = string.IsNullOrWhiteSpace(caseTitle) ? "ich" : caseTitle;
            d2pIdentityBlock.Append($"ERSTE ANTWORT — wenn der Arzt dich begrüßt:\n");
            d2pIdentityBlock.Append($"Sage GENAU: 'Guten Tag.' oder 'Guten Tag, Herr/Frau Doktor, ich bin {greetingName}.' — dann warte schweigend auf die erste Frage.\n");
            d2pIdentityBlock.Append("VERBOTEN als erste Antwort: 'Wie kann ich Ihnen helfen?', 'Was kann ich für Sie tun?', 'How can I help you?', 'What can I help you with?' oder JEDE ähnliche Assistenten-Phrase.\n");
            d2pIdentityBlock.Append("VERHALTENSREGEL: Antworte AUSSCHLIESSLICH auf direkte Fragen des Arztes. Gib NIEMALS Informationen spontan preis. Dumpfe NIEMALS alle Symptome auf einmal aus.\n");

            // Use full case core_facts directly (bypasses phase override which can be too short)
            string fullCaseCoreFacts = !string.IsNullOrWhiteSpace(_selectedCaseCoreFactsJson)
                ? GetItemsStringFromJson(_selectedCaseCoreFactsJson, "\n- ")
                : string.Empty;
            if (!string.IsNullOrWhiteSpace(fullCaseCoreFacts))
            {
                d2pIdentityBlock.Append("VOLLSTANDIGE PATIENTENDATEN (einzige Informationsquelle):\n- ");
                d2pIdentityBlock.Append(fullCaseCoreFacts);
                d2pIdentityBlock.Append("\n");
            }

            Debug.Log($"[MedicalExamManager] D2P identity block prepared. CaseTitle='{caseTitle}', CoreFactsCharCount={fullCaseCoreFacts.Length}");
            return d2pIdentityBlock.ToString().TrimEnd();
        }

        private string BuildD2DIdentityBlock()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("--- PFLICHT: ARZTIDENTITAT ---\n");
            sb.Append("Du bist KEINE KI. Du bist die ärztliche Leitung (Chefarzt/Chefärztin), die jetzt eine Fallvorstellung oder ein Anamnesegespräch abnimmt.\n");
            sb.Append("Antworte NIEMALS mit 'Was kann ich für Sie tun?', 'Wie kann ich helfen?' oder anderen generischen Assistenten-Phrasen.\n");
            sb.Append("Du sprichst NICHT zuerst. Warte still, bis der Kandidat beginnt zu sprechen — dann reagierst du als erfahrene ärztliche Führungskraft direkt auf das Gesagte.\n");
            sb.Append("Wenn der Kandidat beginnt, höre aufmerksam zu und stelle gezielte Rückfragen im Stil einer ärztlichen Leitung.\n");
            return sb.ToString().TrimEnd();
        }

        private string EnsureRoleIdentityInInstructions(string instructions)
        {
            string safeInstructions = string.IsNullOrWhiteSpace(instructions) ? string.Empty : instructions;

            if (selectedRole == RoleType.DoctorToPatient)
            {
                bool hasPatientGuardrails =
                    safeInstructions.IndexOf(D2P_IDENTITY_SENTINEL, StringComparison.Ordinal) >= 0;

                if (hasPatientGuardrails)
                    return safeInstructions;

                string patientBlock = BuildD2PIdentityBlock();
                if (string.IsNullOrWhiteSpace(patientBlock))
                    return safeInstructions;

                return string.IsNullOrWhiteSpace(safeInstructions)
                    ? patientBlock
                    : safeInstructions + "\n\n" + patientBlock;
            }

            if (selectedRole == RoleType.DoctorToDoctor)
            {
                bool hasD2DGuardrails =
                    safeInstructions.IndexOf("PFLICHT: ARZTIDENTITAT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    safeInstructions.IndexOf("Du sprichst NICHT zuerst", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hasD2DGuardrails)
                    return safeInstructions;

                string d2dBlock = BuildD2DIdentityBlock();
                if (string.IsNullOrWhiteSpace(d2dBlock))
                    return safeInstructions;

                return string.IsNullOrWhiteSpace(safeInstructions)
                    ? d2dBlock
                    : safeInstructions + "\n\n" + d2dBlock;
            }

            return safeInstructions;
        }

        
        private string ResolveWhisperStartPromptForSelectedRole()
        {
            if (useRemotePrompts && useRemoteWhisperAndEvaluationPrompts)
            {
                string key = selectedRole == RoleType.DoctorToDoctor
                    ? "whisper.start.doctor_to_doctor"
                    : "whisper.start.doctor_to_patient";

                // Scenario-aware lookup so themed keys like ".cardio/.nephro/.neuro" work for D2P as well.
                string remote = RemotePromptManager.GetForScenario(key, activeScenarioId, "");
                if (!string.IsNullOrWhiteSpace(remote))
                    return remote;

                if (strictOnlineCsvOnly)
                {
                    Debug.LogError($"[MedicalExamManager] Missing required online prompt key '{key}' in strictOnlineCsvOnly mode.");
                    return string.Empty;
                }
            }

            // Fallback to inspector fields
            return selectedRole == RoleType.DoctorToDoctor
                ? startPromptDoctorToDoctor
                : startPromptDoctorToPatient;
        }

        private string ResolveFinalEvaluationPromptHeader(ExamEvaluation.RoleType roleType)
        {
            if (useRemotePrompts && useRemoteWhisperAndEvaluationPrompts)
            {
                // Use role-specific evaluation system prompts with different scoring rubrics
                string key = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                    ? "eval.one_call.d2d.system"  // D2D: sprachliche 0-7, inhaltliche 0-3, malus 0-5 = 0-10 total
                    : "eval.one_call.d2p.system"; // D2P: sprachliche 0-7, inhaltliche 0-3, malus 0-5 = 0-10 total (focus on MONA completeness)

                string remote = RemotePromptManager.GetForScenario(key, activeScenarioId, "");
                return ApplyPromptPlaceholders(remote);
            }

            return "";
        }

        private string ResolveFinalEvaluationUserTemplate(ExamEvaluation.RoleType roleType)
        {
            if (useRemotePrompts && useRemoteWhisperAndEvaluationPrompts)
            {
                string key = roleType == ExamEvaluation.RoleType.DoctorToDoctor
                    ? "eval.one_call.d2d.user_template"
                    : "eval.one_call.d2p.user_template";

                string remote = RemotePromptManager.GetForScenario(key, activeScenarioId, "");
                return ApplyPromptPlaceholders(remote);
            }

            return "";
        }

        private string ApplyPromptPlaceholders(string template)
        {
            if (string.IsNullOrEmpty(template)) return template;

            string scenarioName = GetScenarioName();
            string topic = GetScenarioTopic();
            string context = GetScenarioContext();
            string duration = GetExamDurationMinutes().ToString();
            string topicD2D = GetScenarioTopicD2D();
            string topicD2P = GetScenarioTopicD2P();
            string ragTags = GetScenarioRagTagsString();
            string currentTheme = GetScenarioTheme();
            string caseTerms = GetScenarioTermsString();
            string casePrimaryTerm = GetScenarioPrimaryTerm();
            string caseTermsJson = GetScenarioTermsJson();
            string coreFactsJson = GetScenarioCoreFactsJson();
            string expectedQuestionsJson = GetScenarioExpectedQuestionsJson();
            string phaseTermsJson = GetSelectedCasePhaseTermsJson();
            string phaseCoreFactsJson = GetSelectedCasePhaseCoreFactsJson();
            string phaseExpectedQuestionsJson = GetSelectedCasePhaseExpectedQuestionsJson();
            string evaluationFocusJson = GetScenarioEvaluationFocusJson();
            string coreFacts = GetScenarioCoreFactsString();
            string expectedQuestions = GetScenarioExpectedQuestionsString();
            string phaseTerms = GetTermsStringFromJson(phaseTermsJson);
            string phaseCoreFacts = GetItemsStringFromJson(phaseCoreFactsJson, ", ");
            string phaseExpectedQuestions = GetItemsStringFromJson(phaseExpectedQuestionsJson, " | ");
            string selectedCaseId = string.IsNullOrWhiteSpace(_selectedCaseId) ? string.Empty : _selectedCaseId;
            string selectedCaseTitle = string.IsNullOrWhiteSpace(_selectedCaseTitle) ? string.Empty : _selectedCaseTitle;
            string selectedCaseIndex = _selectedCaseIndex >= 0 ? (_selectedCaseIndex + 1).ToString() : string.Empty;
            string currentPhaseName = GetExamPhaseKey(currentPhase);
            string caseContext = string.IsNullOrWhiteSpace(_selectedCaseText) ? context : _selectedCaseText;

            string language = GetExamLanguage() == ExamLanguage.Deutsch ? "Deutsch" : "English";
            string role = selectedRole == RoleType.DoctorToDoctor ? "DoctorToDoctor" : "DoctorToPatient";

            string result = template
                .Replace("{SCENARIO_NAME}", scenarioName)
                .Replace("{TOPIC}", topic)
                .Replace("{TOPIC_D2D}", topicD2D)
                .Replace("{TOPIC_D2P}", topicD2P)
                .Replace("{CONTEXT}", context)
                .Replace("{RAG_TAGS}", ragTags)
                .Replace("{CASE_CONTEXT}", caseContext)
                .Replace("{CASE_THEME}", currentTheme)
                .Replace("{CASE_TERMS}", caseTerms)
                .Replace("{CASE_PRIMARY_TERM}", casePrimaryTerm)
                .Replace("{CASE_TERMS_JSON}", caseTermsJson)
                .Replace("{CURRENT_THEME}", currentTheme)
                .Replace("{THEME}", currentTheme)
                .Replace("{THEME_TERMS}", caseTerms)
                .Replace("{THEME_FIRST_TERM}", casePrimaryTerm)
                .Replace("{TERMS_JSON}", caseTermsJson)
                .Replace("{PHASE_TERMS}", phaseTerms)
                .Replace("{PHASE_TERMS_JSON}", phaseTermsJson)
                .Replace("{CORE_FACTS_JSON}", coreFactsJson)
                .Replace("{PHASE_CORE_FACTS_JSON}", phaseCoreFactsJson)
                .Replace("{EXPECTED_QUESTIONS_JSON}", expectedQuestionsJson)
                .Replace("{PHASE_EXPECTED_QUESTIONS_JSON}", phaseExpectedQuestionsJson)
                .Replace("{EVALUATION_FOCUS_JSON}", evaluationFocusJson)
                .Replace("{CORE_FACTS}", coreFacts)
                .Replace("{PHASE_CORE_FACTS}", phaseCoreFacts)
                .Replace("{EXPECTED_QUESTIONS}", expectedQuestions)
                .Replace("{PHASE_EXPECTED_QUESTIONS}", phaseExpectedQuestions)
                .Replace("{CASE_ID}", selectedCaseId)
                .Replace("{CASE_TITLE}", selectedCaseTitle)
                .Replace("{CASE_INDEX}", selectedCaseIndex)
                .Replace("{CASE_TEXT}", context)
                .Replace("{CURRENT_PHASE}", currentPhaseName)
                .Replace("{SCENARIO_ID}", string.IsNullOrWhiteSpace(activeScenarioId) ? "default" : activeScenarioId.Trim())
                .Replace("{DURATION_MIN}", duration)
                .Replace("{LANGUAGE}", language)
                .Replace("{ROLE_TYPE}", role);
            
            // Remove any unreplaced SCENARIO bracket placeholders (e.g., [SCENARIO_NAME], [SCENARIO_TOPIC], [SCENARIO_CONTEXT])
            // NOTE: Only strip [SCENARIO_...] patterns. Do NOT strip all [...] brackets — the remote
            // CSV prompts use [X], [Name], [Begriff A] etc. as intentional AI placeholders.
            result = System.Text.RegularExpressions.Regex.Replace(result, @"\[SCENARIO_[^\]]*\]", "").Trim();
            
            return result;
        }

        private string GetScenarioTheme()
        {
            string themeFromSelectedCase = ExtractThemeFromTermsJson(GetScenarioTermsJson());
            if (!string.IsNullOrWhiteSpace(themeFromSelectedCase))
                return themeFromSelectedCase.Trim();

            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                string themeFromJson = ExtractThemeFromTermsJson(remote.termsJson);
                if (!string.IsNullOrWhiteSpace(themeFromJson))
                    return themeFromJson.Trim();

                if (!string.IsNullOrWhiteSpace(remote.theme))
                    return remote.theme.Trim();
            }

            return string.Empty;
        }

        private string GetScenarioPrimaryTerm()
        {
            var terms = GetScenarioTermsList();
            if (terms.Count > 0)
                return terms[0];

            string theme = GetScenarioTheme();
            if (!string.IsNullOrWhiteSpace(theme))
                return theme;

            return string.Empty;
        }

        private string GetScenarioTermsString()
        {
            var terms = GetScenarioTermsList();
            return terms.Count == 0 ? string.Empty : string.Join(", ", terms);
        }

        private string GetScenarioTermsJson()
        {
            string selectedPhaseTerms = GetSelectedCasePhaseTermsJson();
            if (!string.IsNullOrWhiteSpace(selectedPhaseTerms))
                return selectedPhaseTerms;

            if (!string.IsNullOrWhiteSpace(_selectedCaseTermsJson))
                return _selectedCaseTermsJson.Trim();

            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
                return string.IsNullOrWhiteSpace(remote.termsJson) ? string.Empty : remote.termsJson.Trim();

            return string.Empty;
        }

        private string GetScenarioCoreFactsJson()
        {
            string selectedPhaseCoreFacts = GetSelectedCasePhaseCoreFactsJson();
            if (!string.IsNullOrWhiteSpace(selectedPhaseCoreFacts))
                return selectedPhaseCoreFacts;

            if (!string.IsNullOrWhiteSpace(_selectedCaseCoreFactsJson))
                return _selectedCaseCoreFactsJson.Trim();

            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
                return string.IsNullOrWhiteSpace(remote.coreFactsJson) ? string.Empty : remote.coreFactsJson.Trim();

            return string.Empty;
        }

        private string GetScenarioExpectedQuestionsJson()
        {
            string selectedPhaseQuestions = GetSelectedCasePhaseExpectedQuestionsJson();
            if (!string.IsNullOrWhiteSpace(selectedPhaseQuestions))
                return selectedPhaseQuestions;

            if (!string.IsNullOrWhiteSpace(_selectedCaseExpectedQuestionsJson))
                return _selectedCaseExpectedQuestionsJson.Trim();

            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
                return string.IsNullOrWhiteSpace(remote.expectedQuestionsJson) ? string.Empty : remote.expectedQuestionsJson.Trim();

            return string.Empty;
        }

        private string GetScenarioEvaluationFocusJson()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
                return string.IsNullOrWhiteSpace(remote.evaluationFocusJson) ? string.Empty : remote.evaluationFocusJson.Trim();

            return string.Empty;
        }

        private string GetScenarioCoreFactsString()
        {
            return GetItemsStringFromJson(GetScenarioCoreFactsJson(), ", ");
        }

        private string GetScenarioExpectedQuestionsString()
        {
            return GetItemsStringFromJson(GetScenarioExpectedQuestionsJson(), " | ");
        }

        private string BuildActiveCaseEvaluationReference(ExamEvaluation.RoleType roleType)
        {
            string caseId = string.IsNullOrWhiteSpace(_selectedCaseId) ? string.Empty : _selectedCaseId.Trim();
            string caseTitle = string.IsNullOrWhiteSpace(_selectedCaseTitle) ? string.Empty : _selectedCaseTitle.Trim();
            string caseText = string.IsNullOrWhiteSpace(_selectedCaseText) ? string.Empty : _selectedCaseText.Trim();
            string coreFacts = GetScenarioCoreFactsString();
            string expectedQuestions = GetScenarioExpectedQuestionsString();
            string roleLabel = roleType == ExamEvaluation.RoleType.DoctorToDoctor ? "d2d" : "d2p";

            var builder = new System.Text.StringBuilder();
            builder.AppendLine("ACTIVE CASE REFERENCE:");
            builder.AppendLine($"- Scenario: {GetScenarioName()}");
            builder.AppendLine($"- Role: {roleLabel}");
            if (!string.IsNullOrWhiteSpace(caseId))
                builder.AppendLine($"- Case id: {caseId}");
            if (!string.IsNullOrWhiteSpace(caseTitle))
                builder.AppendLine($"- Case title: {caseTitle}");
            if (!string.IsNullOrWhiteSpace(GetScenarioTermsString()))
                builder.AppendLine($"- Case terminology: {GetScenarioTermsString()}");
            if (!string.IsNullOrWhiteSpace(caseText))
                builder.AppendLine($"- Full case context: {caseText}");
            else if (!string.IsNullOrWhiteSpace(GetScenarioContext()))
                builder.AppendLine($"- Scenario context: {GetScenarioContext()}");
            if (!string.IsNullOrWhiteSpace(coreFacts))
                builder.AppendLine($"- Core facts for completeness: {coreFacts}");
            if (!string.IsNullOrWhiteSpace(expectedQuestions))
                builder.AppendLine($"- Relevant question focus: {expectedQuestions}");

            return builder.ToString().Trim();
        }

        private static void AddItemsFromItemsJson(List<string> destination, string rawJson)
        {
            if (destination == null || string.IsNullOrWhiteSpace(rawJson))
                return;

            try
            {
                var token = JToken.Parse(rawJson);
                if (token is JObject obj)
                {
                    AddTermsToken(destination, obj["items"]);
                    return;
                }

                AddTermsToken(destination, token);
            }
            catch { }
        }

        private string GetSelectedCasePhaseTermsJson()
        {
            return GetPhaseOverride(_selectedCasePhaseTermsJson);
        }

        private string GetSelectedCasePhaseCoreFactsJson()
        {
            return GetPhaseOverride(_selectedCasePhaseCoreFactsJson);
        }

        private string GetSelectedCasePhaseExpectedQuestionsJson()
        {
            return GetPhaseOverride(_selectedCasePhaseExpectedQuestionsJson);
        }

        private string GetPhaseOverride(Dictionary<ExamPhase, string> overrides)
        {
            if (overrides == null)
                return string.Empty;

            if (overrides.TryGetValue(currentPhase, out string value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();

            return string.Empty;
        }

        private static string GetTermsStringFromJson(string rawJson)
        {
            var terms = new List<string>();
            AddTermsFromJson(terms, rawJson);
            return terms.Count == 0 ? string.Empty : string.Join(", ", terms);
        }

        private static string GetItemsStringFromJson(string rawJson, string separator)
        {
            var items = new List<string>();
            AddItemsFromItemsJson(items, rawJson);
            return items.Count == 0 ? string.Empty : string.Join(separator, items);
        }

        private List<string> GetScenarioTermsList()
        {
            var terms = new List<string>();

            string activeTermsJson = GetScenarioTermsJson();
            if (!string.IsNullOrWhiteSpace(activeTermsJson))
                AddTermsFromJson(terms, activeTermsJson);

            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                AddTermsFromJson(terms, remote.termsJson);

                if (terms.Count == 0 && !string.IsNullOrWhiteSpace(remote.theme))
                    terms.Add(remote.theme.Trim());
            }

            return terms;
        }

        private static string ExtractThemeFromTermsJson(string rawJson)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
                return string.Empty;

            try
            {
                var token = JToken.Parse(rawJson);
                if (token is JObject obj)
                {
                    string theme = obj.Value<string>("theme");
                    if (!string.IsNullOrWhiteSpace(theme))
                        return theme.Trim();
                }
            }
            catch { }

            return string.Empty;
        }

        private static void AddTermsFromJson(List<string> destination, string rawJson)
        {
            if (destination == null || string.IsNullOrWhiteSpace(rawJson))
                return;

            try
            {
                var token = JToken.Parse(rawJson);

                if (token is JObject obj)
                {
                    AddTermsToken(destination, obj["terms"]);
                    return;
                }

                AddTermsToken(destination, token);
            }
            catch { }
        }

        private static void AddTermsToken(List<string> destination, JToken token)
        {
            if (destination == null || token == null)
                return;

            if (token is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                {
                    string item = arr[i]?.ToString();
                    if (!string.IsNullOrWhiteSpace(item) && !destination.Contains(item.Trim()))
                        destination.Add(item.Trim());
                }
                return;
            }

            string raw = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            if (string.IsNullOrWhiteSpace(raw))
                return;

            string[] parts = raw.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string item = parts[i]?.Trim();
                if (!string.IsNullOrWhiteSpace(item) && !destination.Contains(item))
                    destination.Add(item);
            }
        }

        private string GetScenarioContext()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                if (useScenarioCasesJson && TryResolveCaseFromScenarioJson(remote, out string caseText))
                    return caseText;

                if (!string.IsNullOrWhiteSpace(remote.scenarioContext))
                    return remote.scenarioContext.Trim();
            }

            if (currentScenario != null && !string.IsNullOrWhiteSpace(currentScenario.scenarioContext))
                return currentScenario.scenarioContext.Trim();

            return "Allgemeinmedizin";
        }

        private bool TryResolveCaseFromScenarioJson(RemoteScenarioManager.ScenarioRow remote, out string caseText)
        {
            caseText = string.Empty;
            if (remote == null || string.IsNullOrWhiteSpace(remote.casesJson))
                return false;

            string scenarioId = string.IsNullOrWhiteSpace(activeScenarioId) ? "default" : activeScenarioId.Trim();
            if (string.Equals(_selectedCaseScenarioId, scenarioId, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(_selectedCaseText))
            {
                caseText = _selectedCaseText;
                return true;
            }

            var parsedCases = ParseCasesJson(remote.casesJson);
            if (parsedCases.Count == 0)
                return false;

            // Prefer cases that explicitly match the currently selected role.
            // Cases with no role are treated as generic and accepted for both modes.
            var roleMatchedCases = new List<ParsedCaseItem>(parsedCases.Count);
            for (int i = 0; i < parsedCases.Count; i++)
            {
                var c = parsedCases[i];
                if (DoesCaseMatchSelectedRole(c.role))
                    roleMatchedCases.Add(c);
            }

            var pool = roleMatchedCases.Count > 0 ? roleMatchedCases : parsedCases;

            int index;
            if (fixedScenarioCaseIndex >= 0)
                index = Mathf.Clamp(fixedScenarioCaseIndex, 0, pool.Count - 1);
            else
                index = UnityEngine.Random.Range(0, pool.Count);

            var selected = pool[index];
            _selectedCaseScenarioId = scenarioId;
            _selectedCaseIndex = index;
            _selectedCaseId = selected.caseId;
            _selectedCaseTitle = selected.title;
            _selectedCaseText = selected.text;
            _selectedCaseTermsJson = selected.termsJson;
            _selectedCaseCoreFactsJson = selected.coreFactsJson;
            _selectedCaseExpectedQuestionsJson = selected.expectedQuestionsJson;
            ActiveCaseIndex = _selectedCaseIndex;
            ActiveCaseId = string.IsNullOrWhiteSpace(_selectedCaseId) ? string.Empty : _selectedCaseId;
            ActiveCaseTitle = string.IsNullOrWhiteSpace(_selectedCaseTitle) ? string.Empty : _selectedCaseTitle;
            ActiveCaseText = string.IsNullOrWhiteSpace(_selectedCaseText) ? string.Empty : _selectedCaseText;
            ActiveCaseTermsJson = string.IsNullOrWhiteSpace(_selectedCaseTermsJson) ? string.Empty : _selectedCaseTermsJson;
            ActiveCaseCoreFactsJson = string.IsNullOrWhiteSpace(_selectedCaseCoreFactsJson) ? string.Empty : _selectedCaseCoreFactsJson;
            ActiveCaseExpectedQuestionsJson = string.IsNullOrWhiteSpace(_selectedCaseExpectedQuestionsJson) ? string.Empty : _selectedCaseExpectedQuestionsJson;
            CopyPhaseOverrides(selected.phaseTermsJsonByPhase, _selectedCasePhaseTermsJson);
            CopyPhaseOverrides(selected.phaseCoreFactsJsonByPhase, _selectedCasePhaseCoreFactsJson);
            CopyPhaseOverrides(selected.phaseExpectedQuestionsJsonByPhase, _selectedCasePhaseExpectedQuestionsJson);

            caseText = _selectedCaseText;
            Debug.Log($"[MedicalExamManager] Selected case for scenario '{scenarioId}': index={_selectedCaseIndex}, id='{_selectedCaseId}', title='{_selectedCaseTitle}', role='{selected.role}', roleMatchedPool={roleMatchedCases.Count}, totalCases={parsedCases.Count}");
            return true;
        }

        private bool DoesCaseMatchSelectedRole(string caseRole)
        {
            if (string.IsNullOrWhiteSpace(caseRole))
                return true;

            string r = caseRole.Trim().ToLowerInvariant();

            if (selectedRole == RoleType.DoctorToDoctor)
            {
                return r == "d2d" || r == "doctor_to_doctor" || r == "doctortodoctor" || r == "doctor-doctor";
            }

            if (selectedRole == RoleType.DoctorToPatient)
            {
                return r == "d2p" || r == "doctor_to_patient" || r == "doctortopatient" || r == "doctor-patient";
            }

            return true;
        }

        private struct ParsedCaseItem
        {
            public string caseId;
            public string role;
            public string title;
            public string text;
            public string termsJson;
            public string coreFactsJson;
            public string expectedQuestionsJson;
            public Dictionary<ExamPhase, string> phaseTermsJsonByPhase;
            public Dictionary<ExamPhase, string> phaseCoreFactsJsonByPhase;
            public Dictionary<ExamPhase, string> phaseExpectedQuestionsJsonByPhase;
        }

        private static List<ParsedCaseItem> ParseCasesJson(string rawJson)
        {
            var result = new List<ParsedCaseItem>();
            if (string.IsNullOrWhiteSpace(rawJson))
                return result;

            try
            {
                var token = JToken.Parse(rawJson);
                JArray casesArray = null;

                if (token is JArray tokenArray)
                    casesArray = tokenArray;
                else if (token is JObject tokenObj)
                    casesArray = tokenObj["cases"] as JArray;

                if (casesArray == null)
                    return result;

                for (int i = 0; i < casesArray.Count; i++)
                {
                    var entry = casesArray[i];
                    if (entry == null) continue;

                    if (entry.Type == JTokenType.String)
                    {
                        string plain = entry.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(plain))
                        {
                            result.Add(new ParsedCaseItem
                            {
                                caseId = $"case_{i + 1}",
                                role = string.Empty,
                                title = $"Case {i + 1}",
                                text = plain,
                                termsJson = string.Empty,
                                coreFactsJson = string.Empty,
                                expectedQuestionsJson = string.Empty,
                                phaseTermsJsonByPhase = new Dictionary<ExamPhase, string>(),
                                phaseCoreFactsJsonByPhase = new Dictionary<ExamPhase, string>(),
                                phaseExpectedQuestionsJsonByPhase = new Dictionary<ExamPhase, string>()
                            });
                        }
                        continue;
                    }

                    if (entry is JObject obj)
                    {
                        string text = (obj["text"] ?? obj["content"] ?? obj["case"] ?? obj["context"])?.ToString();
                        if (string.IsNullOrWhiteSpace(text))
                            continue;

                        string caseId = obj["case_id"]?.ToString();
                        if (string.IsNullOrWhiteSpace(caseId))
                            caseId = $"case_{i + 1}";

                        string title = obj["title"]?.ToString();
                        if (string.IsNullOrWhiteSpace(title))
                            title = caseId;

                        var phaseTermsJsonByPhase = new Dictionary<ExamPhase, string>();
                        var phaseCoreFactsJsonByPhase = new Dictionary<ExamPhase, string>();
                        var phaseExpectedQuestionsJsonByPhase = new Dictionary<ExamPhase, string>();
                        PopulatePhaseMetadataMaps(obj, phaseTermsJsonByPhase, phaseCoreFactsJsonByPhase, phaseExpectedQuestionsJsonByPhase);

                        result.Add(new ParsedCaseItem
                        {
                            caseId = caseId.Trim(),
                            role = (obj["role"]?.ToString() ?? string.Empty).Trim(),
                            title = title.Trim(),
                            text = text.Trim(),
                            termsJson = ((obj["terms"] ?? obj["term_focus"] ?? obj["term_priorities"])?.ToString() ?? string.Empty).Trim(),
                            coreFactsJson = ((obj["core_facts"] ?? obj["presentation_core_facts"] ?? obj["facts"])?.ToString() ?? string.Empty).Trim(),
                            expectedQuestionsJson = ((obj["expected_questions"] ?? obj["examiner_questions"] ?? obj["discussion_questions"])?.ToString() ?? string.Empty).Trim(),
                            phaseTermsJsonByPhase = phaseTermsJsonByPhase,
                            phaseCoreFactsJsonByPhase = phaseCoreFactsJsonByPhase,
                            phaseExpectedQuestionsJsonByPhase = phaseExpectedQuestionsJsonByPhase
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MedicalExamManager] Failed to parse cases_json: {ex.Message}");
            }

            return result;
        }

        private static void CopyPhaseOverrides(Dictionary<ExamPhase, string> source, Dictionary<ExamPhase, string> destination)
        {
            destination.Clear();
            if (source == null)
                return;

            foreach (var pair in source)
            {
                if (!string.IsNullOrWhiteSpace(pair.Value))
                    destination[pair.Key] = pair.Value.Trim();
            }
        }

        private static void PopulatePhaseMetadataMaps(
            JObject caseObj,
            Dictionary<ExamPhase, string> termsByPhase,
            Dictionary<ExamPhase, string> coreFactsByPhase,
            Dictionary<ExamPhase, string> expectedQuestionsByPhase)
        {
            if (caseObj == null)
                return;

            var phasesObj = (caseObj["phases"] ?? caseObj["phase_data"] ?? caseObj["phase_overrides"]) as JObject;
            if (phasesObj == null)
                return;

            foreach (var property in phasesObj.Properties())
            {
                if (!TryParseExamPhase(property.Name, out var phase))
                    continue;

                if (!(property.Value is JObject phaseObj))
                    continue;

                string termsJson = ((phaseObj["terms"] ?? phaseObj["term_focus"] ?? phaseObj["term_priorities"])?.ToString() ?? string.Empty).Trim();
                string coreFactsJson = ((phaseObj["core_facts"] ?? phaseObj["presentation_core_facts"] ?? phaseObj["facts"])?.ToString() ?? string.Empty).Trim();
                string expectedQuestionsJson = ((phaseObj["expected_questions"] ?? phaseObj["examiner_questions"] ?? phaseObj["discussion_questions"])?.ToString() ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(termsJson))
                    termsByPhase[phase] = termsJson;
                if (!string.IsNullOrWhiteSpace(coreFactsJson))
                    coreFactsByPhase[phase] = coreFactsJson;
                if (!string.IsNullOrWhiteSpace(expectedQuestionsJson))
                    expectedQuestionsByPhase[phase] = expectedQuestionsJson;
            }
        }

        private static bool TryParseExamPhase(string rawKey, out ExamPhase phase)
        {
            switch ((rawKey ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "greeting":
                    phase = ExamPhase.Greeting;
                    return true;
                case "presentation":
                    phase = ExamPhase.Presentation;
                    return true;
                case "discussion":
                    phase = ExamPhase.Discussion;
                    return true;
                case "terms":
                case "terminology":
                    phase = ExamPhase.Terms;
                    return true;
                case "anamnesis":
                    phase = ExamPhase.Anamnesis;
                    return true;
                case "summary":
                    phase = ExamPhase.Summary;
                    return true;
                default:
                    phase = ExamPhase.Greeting;
                    return false;
            }
        }

        private static string GetExamPhaseKey(ExamPhase phase)
        {
            switch (phase)
            {
                case ExamPhase.Greeting:
                    return "greeting";
                case ExamPhase.Presentation:
                    return "presentation";
                case ExamPhase.Discussion:
                    return "discussion";
                case ExamPhase.Terms:
                    return "terms";
                case ExamPhase.Anamnesis:
                    return "anamnesis";
                case ExamPhase.Summary:
                    return "summary";
                default:
                    return string.Empty;
            }
        }

        private string GetScenarioName()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                if (!string.IsNullOrWhiteSpace(remote.scenarioName))
                    return remote.scenarioName.Trim();
            }

            if (currentScenario != null && !string.IsNullOrWhiteSpace(currentScenario.scenarioName))
                return currentScenario.scenarioName.Trim();

            return "Medical Exam";
        }

        private string GetScenarioTopic()
        {
            return selectedRole == RoleType.DoctorToDoctor
                ? GetScenarioTopicD2D()
                : GetScenarioTopicD2P();
        }

        private string GetScenarioTopicD2D()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                if (!string.IsNullOrWhiteSpace(remote.topicD2D))
                    return remote.topicD2D.Trim();
            }

            if (currentScenario != null && !string.IsNullOrWhiteSpace(currentScenario.medicalTopic))
                return currentScenario.medicalTopic.Trim();

            return "Clinical Assessment";
        }

        private string GetScenarioTopicD2P()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                if (!string.IsNullOrWhiteSpace(remote.topicD2P))
                    return remote.topicD2P.Trim();
            }

            if (currentScenario != null && !string.IsNullOrWhiteSpace(currentScenario.medicalTopic))
                return currentScenario.medicalTopic.Trim();

            return "Clinical Assessment";
        }

        private string GetScenarioRagTagsString()
        {
            if (useRemotePrompts && useRemoteScenarioData)
            {
                string remoteTags = RemoteScenarioManager.ResolveRagTags(activeScenarioId);
                if (!string.IsNullOrWhiteSpace(remoteTags))
                    return remoteTags.Trim();
            }

            return string.IsNullOrWhiteSpace(fallbackRagTags) ? "general_medical" : fallbackRagTags.Trim();
        }

        /// <summary>
        /// Returns fixed female realtime voices by role.
        /// D2D uses shimmer, D2P uses coral.
        /// We intentionally ignore scenario CSV overrides to keep voices stable and consistent.
        /// </summary>
        private string GetRealtimeVoiceForRole()
        {
            return GetRealtimeVoiceForRole(selectedRole);
        }

        public string GetRealtimeVoiceForRole(RoleType role)
        {
            return role == RoleType.DoctorToDoctor ? "shimmer" : "coral";
        }

        private List<string> BuildRagTagsForCurrentScenario()
        {
            var tags = new List<string>();

            string raw = GetScenarioRagTagsString();
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var parts = raw.Split(',');
                for (int i = 0; i < parts.Length; i++)
                {
                    string tag = (parts[i] ?? string.Empty).Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(tag) && !tags.Contains(tag))
                        tags.Add(tag);
                }
            }

            string context = GetScenarioContext();
            if (!string.IsNullOrWhiteSpace(context))
            {
                string c = context.Trim().ToLowerInvariant();
                if (!tags.Contains(c))
                    tags.Add(c);
            }

            string roleTopic = GetScenarioTopic();
            if (!string.IsNullOrWhiteSpace(roleTopic))
            {
                string t = roleTopic.Trim().ToLowerInvariant();
                if (!tags.Contains(t))
                    tags.Add(t);
            }

            if (tags.Count == 0)
                tags.Add("general_medical");

            return tags;
        }

        private int GetExamDurationMinutes()
        {
            if (useRemotePrompts && useRemoteScenarioData && RemoteScenarioManager.TryGetScenario(activeScenarioId, out var remote) && remote != null)
            {
                if (remote.durationMinutes > 0)
                    return remote.durationMinutes;
            }

            return examDurationMinutes;
        }

        private ExamLanguage GetExamLanguage()
        {
            return ExamLanguage.Deutsch;
        }
        
        private void UpdateExamTimer()
        {
            if (!_examTimerStarted) return;
            
            float elapsedTime = Time.time - _examStartTime;
            float remainingTime = _examDuration - elapsedTime;
            
            // Clamp to 0 so timer doesn't go negative
            remainingTime = Mathf.Max(0f, remainingTime);
            
            if (timerText != null)
            {
                int minutes = Mathf.FloorToInt(remainingTime / 60);
                int seconds = Mathf.FloorToInt(remainingTime % 60);
                timerText.text = $"{minutes:00}:{seconds:00}";
                
                // Change color when time is running out
                if (remainingTime <= 0)
                {
                    timerText.color = Color.red;
                    timerText.text = "00:00";
                }
                else if (remainingTime < 60)
                {
                    timerText.color = Color.red;
                }
                else if (remainingTime < 120)
                {
                    timerText.color = Color.yellow;
                }
                else
                {
                    timerText.color = Color.white;
                }
            }
            
            // Auto-trigger evaluation when time runs out
            if (remainingTime <= 0 && autoEvaluateOnTimeEnd && !_evaluationRequested && !_evaluationCompleted)
            {
                Debug.Log("[MedicalExamManager] ⏰ Time's up! Interrupting AI and triggering evaluation...");
                InterruptAndEvaluate();
            }
        }
        
        private void OnAgentSpoke(string transcript)
        {
            // If we've already produced the final evaluation, ignore any further agent transcript.
            // This prevents re-triggering parsing when we ask the agent to read the final result aloud.
            if (_evaluationCompleted)
                return;

            // During NORMAL conversation: buffer fragments into complete sentences
            if (!_evaluationRequested)
            {
                // Live UI transcript: buffer the AI turn and append words for smooth streaming display.
                if (!string.IsNullOrEmpty(transcript))
                {
                    _currentAiUiTurnBuffer += transcript;
                    AppendAiTranscriptFragmentWords(transcript);
                }

                // Start the exam timer on the first agent speech fragment.
                if (_examActive && !_examTimerStarted)
                {
                    StartExamTimerNow();
                }

                // Detect if the agent has started giving evaluation feedback even if we didn't trigger it
                if (ContainsEvaluationCue(transcript))
                {
                    Debug.Log($"[MedicalExamManager] 🟡 Detected evaluation cue inside agent speech -> switching to evaluation mode. Fragment: '{transcript}'");
                    FinalizeSentenceBuffer(); // close any normal sentence collection
                    StartEvaluationCaptureFromAgent(transcript);
                    return; // let subsequent fragments be handled in evaluation branch
                }

                _currentAISentenceBuffer += transcript;
                _lastAITranscriptTime = Time.time;
                
                Debug.Log($"[MedicalExamManager] Agent spoke fragment: '{transcript}' (buffer: {_currentAISentenceBuffer.Length} chars)");
            }
            // During EVALUATION: just capture everything into one complete text
            else
            {
                if (!_receivedAnyRealtimeEvalFragment && !string.IsNullOrWhiteSpace(transcript))
                    _receivedAnyRealtimeEvalFragment = true;

                if (!_evaluationAiLineOpen)
                {
                    _fullConversationLog += "AI: ";
                    _evaluationAiLineOpen = true;
                }
                _fullConversationLog += transcript;

                _realtimeAIFeedback += transcript;
                _lastAITranscriptTime = Time.time;
                _lastFeedbackFragmentTime = Time.time;
                _feedbackQuietStart = Time.time; // Reset quiet timer whenever we log COMPLETE FEEDBACK CACHE
                
                Debug.Log($"[MedicalExamManager] 💾 Evaluation feedback fragment: '{transcript}'");
                Debug.Log($"[MedicalExamManager] 📝 Total feedback: {_realtimeAIFeedback.Length} chars");
                Debug.Log($"[MedicalExamManager] === COMPLETE FEEDBACK CACHE ===\n{_realtimeAIFeedback}\n=========================");

                // Keyword-gated handoff to deep evaluation.
                if (triggerFinalEvaluationOnDeeplyKeyword && !_deepEvalTriggered && !_feedbackParseTriggered && ContainsEvaluationHandoffKeyword(transcript))
                {
                    Debug.Log("[MedicalExamManager] 🔑 Received evaluation end-marker. Waiting for quiet window, then triggering final evaluation...");
                    _deepEvalTriggered = true;
                    _realtimeEvalEndPhraseSeen = true;

                    // Optional: show a provisional UI from the realtime draft. Default OFF.
                    if (showRealtimeDraftEvaluationInUi)
                    {
                        if (TryParseRealtimeDraftEvaluation(_realtimeAIFeedback, out var draftEval, out var draftErr))
                            ShowProvisionalEvaluation(draftEval);
                        else
                            Debug.LogWarning($"[MedicalExamManager] Could not parse realtime draft evaluation JSON: {draftErr}");
                    }

                    if (_evaluationProcessCoroutine != null)
                    {
                        StopCoroutine(_evaluationProcessCoroutine);
                        _evaluationProcessCoroutine = null;
                    }

                    // Do NOT interrupt here: we want to capture all remaining evaluation transcript.
                    // Wait for a quiet window to ensure we captured everything after the end phrase.
                    if (_realtimeEvalQuietWaitCoroutine != null)
                        StopCoroutine(_realtimeEvalQuietWaitCoroutine);
                    _realtimeEvalQuietWaitCoroutine = StartCoroutine(WaitRealtimeQuietThenStartFinal());
                    return;
                }
                
                if (!triggerFinalEvaluationOnDeeplyKeyword)
                {
                    // Reset delay timer - wait for AI to finish speaking completely
                    if (_evaluationProcessCoroutine != null)
                    {
                        StopCoroutine(_evaluationProcessCoroutine);
                    }
                    // Wait 2 seconds after last fragment, then send complete feedback to stronger AI
                    _evaluationProcessCoroutine = StartCoroutine(ProcessEvaluationAfterDelay(2f));
                }
            }
        }
        
        private bool _processingEvaluation = false;
        private Coroutine _evaluationProcessCoroutine = null;

        // Detect words that typically appear when the AI starts giving scores
        private bool ContainsEvaluationCue(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return false;

            string lower = transcript.ToLowerInvariant();
            return lower.Contains("terminologie")
                || lower.Contains("verständlichkeit")
                || lower.Contains("verstaendlichkeit")
                || lower.Contains("aussprache")
                || lower.Contains("overall score")
                || lower.Contains("overall")
                || lower.Contains("score:");
        }

        // Used when the agent spontaneously starts to evaluate without us triggering it
        private void StartEvaluationCaptureFromAgent(string firstFragment)
        {
            if (_evaluationRequested) return;

            Debug.Log("[MedicalExamManager] ⚠️ Switching to evaluation mode because the AI started giving feedback.");

            // Optional: silence/mute realtime audio while the agent is giving raw evaluation (we still capture transcript text)
            SilenceAiAudioVolumeForRealtimeEvaluationIfEnabled();
            MuteAiAudioForRealtimeEvaluationIfEnabled();
            _examActive = false;
            _conversationStarted = false;
            _lastFeedbackFragmentTime = Time.time;
            _feedbackQuietStart = Time.time;
            _evaluationRequestStartTime = Time.time;
            _deepEvalTriggered = false;

            _realtimeEvalEndPhraseSeen = false;
            if (_realtimeEvalQuietWaitCoroutine != null)
            {
                StopCoroutine(_realtimeEvalQuietWaitCoroutine);
                _realtimeEvalQuietWaitCoroutine = null;
            }

            if (_sendDraftEvalPromptCoroutine != null)
            {
                StopCoroutine(_sendDraftEvalPromptCoroutine);
                _sendDraftEvalPromptCoroutine = null;
            }

            // Mute realtime audio while the agent is giving raw evaluation (we still capture transcript text)
            MuteAiAudioForRealtimeEvaluationIfEnabled();

            // conversationManager has been removed

            // If we don't want the realtime agent's evaluation, stop it and jump straight to final evaluation pipeline.
            if (!requestRealtimeEvaluationBeforeFinal)
            {
                StartFinalEvaluationPipeline();
                return;
            }

            // Stop listening to user transcripts to avoid extra noise (conversationManager has been removed)
            // if (conversationManager != null && conversationManager.onUserTranscript != null)
            // {
                Debug.Log("[MedicalExamManager] 🔌 Unsubscribed from user transcripts (agent-led evaluation).");
            // }

            // Show evaluation/loading UI so the user sees we are processing
            if (!_evaluationUIShown)
            {
                ShowEvaluationLoading();
            }

            // Hide conversation indicators and buttons
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(false);
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);

            // Seed feedback cache with the first evaluation fragment
            if (!string.IsNullOrEmpty(firstFragment))
            {
                _realtimeAIFeedback += firstFragment;
                _lastAITranscriptTime = Time.time;
                _lastFeedbackFragmentTime = Time.time;
                _feedbackQuietStart = Time.time;
            }

            // Kick off the evaluation processing timer (legacy). When keyword gating is enabled, we wait for 'DEEPLY'.
            if (!triggerFinalEvaluationOnDeeplyKeyword)
            {
                if (_evaluationProcessCoroutine != null)
                {
                    StopCoroutine(_evaluationProcessCoroutine);
                }
                _evaluationProcessCoroutine = StartCoroutine(ProcessEvaluationAfterDelay(2f));
            }
        }
        
        private void FinalizeSentenceBuffer()
        {
            if (string.IsNullOrWhiteSpace(_currentAISentenceBuffer)) return;
            
            // Add complete sentence to conversation log
            string completeSentence = _currentAISentenceBuffer.Trim();
            _fullConversationLog += $"AI: {completeSentence}\n";
            Debug.Log($"[MedicalExamManager] ✓ Finalized AI sentence: '{completeSentence}'");
            
            // Clear buffer
            _currentAISentenceBuffer = "";
        }
        
        private IEnumerator ProcessEvaluationAfterDelay(float delay)
        {
            if (_processingEvaluation)
            {
                Debug.Log("[MedicalExamManager] Already processing evaluation, skipping...");
                yield break;
            }
            
            _processingEvaluation = true;
            
            Debug.Log($"[MedicalExamManager] ⏳ Waiting {delay} seconds to collect all feedback...");
            yield return new WaitForSeconds(delay);
            
            Debug.Log("[MedicalExamManager] === PROCESSING EVALUATION ===");
            Debug.Log($"[MedicalExamManager] Total feedback length: {_realtimeAIFeedback.Length} characters");
            Debug.Log($"[MedicalExamManager] === COMPLETE FEEDBACK CACHE (will be sent to API) ===");
            Debug.Log(_realtimeAIFeedback);
            Debug.Log($"[MedicalExamManager] === END FEEDBACK CACHE ===");
            
            if (string.IsNullOrEmpty(_realtimeAIFeedback) || _realtimeAIFeedback.Length < 20)
            {
                Debug.LogError("[MedicalExamManager]  Feedback is too short or empty! Cannot process evaluation.");
                Debug.LogError($"[MedicalExamManager] Feedback: '{_realtimeAIFeedback}'");
                _processingEvaluation = false;
                yield break;
            }
            
            // Show evaluation panel with loading indicator
            ShowEvaluationLoading();
            
            StartParsingFeedback();
        }

        // Centralized parse trigger (local fallback immediately, second AI async)
        private void StartParsingFeedback()
        {
            // Get API key
            string apiKey = GetAPIKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[MedicalExamManager] ❌ No API key found - cannot parse evaluation");
                _processingEvaluation = false;
                return;
            }

            if (evaluationDisplayUI != null)
            {
                Debug.Log($"[MedicalExamManager] 📤 Sending feedback to second AI (length: {_realtimeAIFeedback.Length})");
                string preview = _realtimeAIFeedback.Length > 200 ? _realtimeAIFeedback.Substring(0, 200) + "..." : _realtimeAIFeedback;
                Debug.Log($"[MedicalExamManager] ➤ Payload preview:\n{preview}");

                ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor 
                    ? ExamEvaluation.RoleType.DoctorToDoctor 
                    : ExamEvaluation.RoleType.DoctorToPatient;

                // First: try to transcribe the buffered mic audio with Whisper (background). Then: send ONE GPT-4 evaluation request.
                StartCoroutine(WhisperThenEvaluate(apiKey, evalRoleType));
                _feedbackParseTriggered = true;
                _parseStartTime = Time.time;
                _processingEvaluation = false; // Allow watchdog retry if needed
                Debug.Log("[MedicalExamManager]  ParseAndDisplayConversationAndRealtimeFeedback invoked (ONE GPT-4 request). Waiting for UI update.");
            }
            else
            {
                Debug.LogError("[MedicalExamManager] ❌ EvaluationDisplayUI is null!");
                _processingEvaluation = false;
            }
        }
        
        private void ShowEvaluationLoading()
        {
            Debug.Log("[MedicalExamManager] Showing evaluation panel with loading indicator...");

            // Move object to evaluation position if configured
            if (objectToMoveOnEvaluation != null)
            {
                objectToMoveOnEvaluation.transform.position = evaluationPosition;
                objectToMoveOnEvaluation.transform.eulerAngles = evaluationRotation;
                Debug.Log($"[MedicalExamManager] Moved {objectToMoveOnEvaluation.name} to evaluation position: {evaluationPosition}, rotation: {evaluationRotation}");
            }
            
            // Switch surfacetouch to evaluation position when feed panel appears
            if (surfacetouch != null && evaluationPositionTransform != null)
            {
                surfacetouch.transform.position = evaluationPositionTransform.position;
                surfacetouch.transform.rotation = evaluationPositionTransform.rotation;
                Debug.Log($"[MedicalExamManager] Switched surfacetouch to evaluation position: {evaluationPositionTransform.position}");
            }

            // Clear the live transcript text to prevent it from appearing in the feedback area
            if (aiLiveTranscriptText != null)
            {
                aiLiveTranscriptText.text = string.Empty;
                Debug.Log("[MedicalExamManager] ✓ Cleared AI live transcript");
            }

            // Clear any existing feedback text to prevent transcript bleeding
            if (generalFeedbackText != null)
            {
                generalFeedbackText.text = string.Empty;
                Debug.Log("[MedicalExamManager] ✓ Cleared general feedback text");
            }

            // If already shown, just ensure we're in loading state
            if (_evaluationUIShown)
            {
                if (evaluationLoadingIndicator != null)
                    evaluationLoadingIndicator.SetActive(true);
                if (evaluationResultsContent != null)
                    evaluationResultsContent.SetActive(false);
                Debug.Log("[MedicalExamManager] Evaluation UI already active; refreshed loading state.");
                return;
            }
            
            // Do NOT enable evaluationPanel here — it activates 2s after the animation trigger
            // in ShowEvaluationUIAfterDelay / SpeakFinalEvaluationViaRealtime.
            // feedbackPanel is always active (parent container), no toggling needed.

            // Enable EvaluationDisplayUI GameObject (needed for the GPT evaluation call to work)
            if (evaluationDisplayUI != null)
            {
                evaluationDisplayUI.gameObject.SetActive(true);
                Debug.Log("[MedicalExamManager] ✓ EvaluationDisplayUI GameObject enabled");
            }
            
            // Show loading, hide results
            if (evaluationLoadingIndicator != null)
            {
                evaluationLoadingIndicator.SetActive(true);
                Debug.Log("[MedicalExamManager] ✓ Loading indicator shown");
            }
            
            if (evaluationResultsContent != null)
            {
                evaluationResultsContent.SetActive(false);
                Debug.Log("[MedicalExamManager] Results content hidden (waiting for data)");
            }

            // Mark as shown to avoid duplicate setup
            _evaluationUIShown = true;
        }
        
        private void OnUserSpoke(string transcript)
        {
            if (string.IsNullOrEmpty(transcript)) return;

            // Mark that the user has responded; the next AI response should clear+replace the live transcript text.
            if (!_evaluationRequested)
                _userSpokeSinceLastAiUiTurn = true;

            // Buffer realtime transcript fragments so we write ONE clean user line per utterance.
            // Realtime can emit word-by-word deltas; this keeps the evaluation transcript readable.
            string t = transcript.Replace("\r", "").Replace("\n", " ").Trim();
            if (!string.IsNullOrWhiteSpace(t))
            {
                if (_currentUserSentenceBuffer.Length == 0)
                    _currentUserSentenceBuffer = t;
                else
                    _currentUserSentenceBuffer += " " + t;

                _lastUserTranscriptTime = Time.time;

                if (_userFinalizeCoroutine != null)
                    StopCoroutine(_userFinalizeCoroutine);
                _userFinalizeCoroutine = StartCoroutine(FinalizeUserUtteranceAfterDelay());

                Debug.Log($"[MedicalExamManager] User(fragment): {t}");
            }

            // Allow evaluation anytime (voice command) instead of forcing a full case first.
            if (!_evaluationRequested && ContainsEvaluationRequest(transcript))
            {
                Debug.Log("[MedicalExamManager] 🧾 Evaluation requested by user (voice command). Triggering now...");
                InterruptAndEvaluate();
                return;
            }
            
            // Check if user wants to end (best effort on fragments)
            string lowerTranscript = transcript.ToLower();
            if (lowerTranscript.Contains("this is all") || 
                lowerTranscript.Contains("that's all") ||
                lowerTranscript.Contains("das ist alles") ||
                lowerTranscript.Contains("das war alles"))
            {
                Debug.Log("[MedicalExamManager] User ended conversation. Requesting evaluation...");
                RequestEvaluation();
            }
        }

        // Public helper to record a fixed-length microphone clip and transcribe it via Whisper
        public void RecordAndTranscribeSeconds(int seconds = 5)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[MedicalExamManager] Must be in Play Mode to record audio.");
                return;
            }

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                Debug.LogError("[MedicalExamManager] No microphone devices detected.");
                return;
            }

            StartCoroutine(RecordAndTranscribeCoroutine(seconds));
        }

        private IEnumerator RecordAndTranscribeCoroutine(int seconds)
        {
            if (seconds <= 0) seconds = 5;

            string device = Microphone.devices[0];
            int sampleRate = 16000;

            Debug.Log($"[MedicalExamManager] Recording {seconds}s from mic '{device}' at {sampleRate}Hz...");

            AudioClip clip = Microphone.Start(device, false, seconds, sampleRate);
            if (clip == null)
            {
                Debug.LogError("[MedicalExamManager] Failed to start microphone recording.");
                yield break;
            }

            // Wait recording duration (+ small buffer)
            yield return new WaitForSeconds(seconds + 0.1f);

            try { if (Microphone.IsRecording(device)) Microphone.End(device); } catch { }

            if (clip == null)
            {
                Debug.LogError("[MedicalExamManager] Recorded clip is null.");
                yield break;
            }

            int samples = clip.samples * clip.channels;
            var data = new float[samples];
            clip.GetData(data, 0);

            // Convert float[] -> PCM16 byte[] (little-endian)
            var pcm16 = new byte[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                short s = (short)Mathf.Clamp(data[i] * 32767f, short.MinValue, short.MaxValue);
                pcm16[i * 2] = (byte)(s & 0xFF);
                pcm16[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            Debug.Log($"[MedicalExamManager] Recorded {seconds}s ({pcm16.Length} bytes). Sending to Whisper...");

            if (evaluationDisplayUI == null)
            {
                Debug.LogError("[MedicalExamManager] No EvaluationDisplayUI assigned to handle transcription.");
                yield break;
            }

            string apiKey = GetAPIKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[MedicalExamManager] No API key found for transcription.");
                yield break;
            }

            bool done = false;
            string resultText = null;
            string errorText = null;

            StartCoroutine(evaluationDisplayUI.TranscribeUserAudioPcm16WithWhisper(
                pcm16,
                sampleRate,
                apiKey,
                (transcribed) => { resultText = transcribed; done = true; },
                (err) => { errorText = err; done = true; }
            ));

            // Wait for transcription (with a reasonable timeout)
            float timeout = 30f;
            float startTime = Time.time;
            while (!done && Time.time - startTime < timeout) yield return null;

            if (!done)
            {
                Debug.LogError("[MedicalExamManager] Transcription timed out.");
                yield break;
            }

            if (!string.IsNullOrEmpty(errorText))
            {
                Debug.LogError($"[MedicalExamManager] Transcription error: {errorText}");
                yield break;
            }

            Debug.Log($"[MedicalExamManager] Transcription result: {resultText}");

            // Inject the transcribed text into the normal user transcript flow
            if (!string.IsNullOrWhiteSpace(resultText))
            {
                OnUserSpoke(resultText);
            }
        }

        private IEnumerator FinalizeUserUtteranceAfterDelay()
        {
            // Wait until the user has been quiet for a short moment.
            const float quietSeconds = 1.0f;
            while (Time.time - _lastUserTranscriptTime < quietSeconds)
                yield return null;

            string text = _currentUserSentenceBuffer.Trim();
            _currentUserSentenceBuffer = "";
            _userFinalizeCoroutine = null;

            if (string.IsNullOrWhiteSpace(text)) yield break;

            // Light cleanup
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            text = text.Replace(" ,", ",").Replace(" .", ".").Replace(" !", "!").Replace(" ?", "?");

            _fullConversationLog += $"User: {text}\n";
            Debug.Log($"[MedicalExamManager] User: {text}");
        }

        private void FinalizeUserSentenceBuffer()
        {
            if (_userFinalizeCoroutine != null)
            {
                StopCoroutine(_userFinalizeCoroutine);
                _userFinalizeCoroutine = null;
            }

            string text = (_currentUserSentenceBuffer ?? string.Empty).Trim();
            _currentUserSentenceBuffer = "";

            if (string.IsNullOrWhiteSpace(text)) return;

            while (text.Contains("  ")) text = text.Replace("  ", " ");
            text = text.Replace(" ,", ",").Replace(" .", ".").Replace(" !", "!").Replace(" ?", "?");

            _fullConversationLog += $"User: {text}\n";
            Debug.Log($"[MedicalExamManager] User: {text}");
        }

        private static bool ContainsEvaluationRequest(string transcript)
        {
            if (string.IsNullOrWhiteSpace(transcript)) return false;
            var t = transcript.Trim().ToLowerInvariant();

            // English
            if (t.Contains("evaluate") || t.Contains("evaluation") || t.Contains("score me") || t.Contains("grade me")) return true;
            // German
            if (t.Contains("bewerte") || t.Contains("bewertung") || t.Contains("benote") || t.Contains("punkte")) return true;

            // Common short phrases
            if (t.Contains("evaluate now") || t.Contains("bewerte mich")) return true;
            return false;
        }
        
        private void CheckAISpeakingState()
        {
            // Check if AI audio source is playing
            bool isAISpeaking = false;
            
            // Check realtime API audio
            if (useOpenAIRealtime && realtimeClient != null)
            {
                isAISpeaking = realtimeClient.IsAudioPlaying;
            }
            // Fallback: check legacy audio source
            else if (aiAudioSource != null)
            {
                isAISpeaking = aiAudioSource.isPlaying;
            }

            // Doctor→Patient: once session is ready (and before the first AI speech), keep Recording visible.
            if (selectedRole == RoleType.DoctorToPatient && _conversationStarted && _examActive && !_examTimerStarted && _realtimeSessionReady && !isAISpeaking)
            {
                SetStartConversationUiState(showStart: false, showLoading: false);
                if (startConversationLoadingIndicator != null)
                    startConversationLoadingIndicator.SetActive(false);
                if (aiTalkingIndicator != null)
                    aiTalkingIndicator.SetActive(false);
                if (recordingIndicator != null)
                    recordingIndicator.SetActive(true);
                return;
            }
            
            // If AI is currently speaking, update immediately
            if (isAISpeaking)
            {
                _lastAudioStopTime = 0f; // Reset stop timer
                
                if (!_aiWasSpeaking)
                {
                    _aiWasSpeaking = true;
                    // AI started speaking - show AI talking indicator, hide recording
                    Debug.Log("[MedicalExamManager] 🎤 AI is speaking");
                    // Ensure exclusivity: hide start/loading while AI is speaking.
                    SetStartConversationUiState(showStart: false, showLoading: false);
                    if (startConversationLoadingIndicator != null)
                        startConversationLoadingIndicator.SetActive(false);
                    if (aiTalkingIndicator != null)
                        aiTalkingIndicator.SetActive(true);
                    if (transcriptpanel != null)
                        transcriptpanel.SetActive(true);
                    if (recordingIndicator != null)
                        recordingIndicator.SetActive(false);
                }
            }
            else
            {
                // AI stopped speaking - but wait a bit before switching to recording
                if (_aiWasSpeaking)
                {
                    if (_lastAudioStopTime == 0f)
                    {
                        _lastAudioStopTime = Time.time;
                    }
                    
                    // Only switch to recording after delay
                    if (Time.time - _lastAudioStopTime >= AUDIO_STOP_DELAY)
                    {
                        _aiWasSpeaking = false;
                        _lastAudioStopTime = 0f;
                        
                        // AI stopped speaking - hide AI talking, show recording
                        Debug.Log("[MedicalExamManager] 🔴 Ready to record");
                        // Ensure exclusivity: hide loading when switching to recording.
                        SetStartConversationUiState(showStart: false, showLoading: false);
                        if (startConversationLoadingIndicator != null)
                            startConversationLoadingIndicator.SetActive(false);
                        if (aiTalkingIndicator != null)
                            aiTalkingIndicator.SetActive(false);
                        if (recordingIndicator != null && _examActive)
                            recordingIndicator.SetActive(true);
                    }
                }
            }
        }
        
        public void OnEndConversationClicked()
        {
            Debug.Log("[MedicalExamManager] 🛑 End Conversation button clicked!");
            InterruptAndEvaluate();
        }

        /// <summary>
        /// Toggle pause/resume for an active OpenAI realtime conversation.
        /// Wire a UI Button to this method.
        /// </summary>
        public void ToggleRealtimePauseResume()
        {
            if (_realtimePausedByUser)
                ResumeRealtimeConversation();
            else
                PauseRealtimeConversation();
        }

        public void PauseRealtimeConversation()
        {
            if (!useOpenAIRealtime)
            {
                Debug.LogWarning("[MedicalExamManager] Pause requested but OpenAI realtime is disabled.");
                return;
            }

            if (_evaluationRequested || !_conversationStarted)
            {
                Debug.LogWarning("[MedicalExamManager] Pause ignored: no active conversation to pause.");
                return;
            }

            if (_realtimePausedByUser)
                return;

            _realtimePausedByUser = true;
            _realtimePauseStartedAt = Time.time;
            _realtimeAgentSpeaking = false;

            try { realtimeMicrophone?.StopStreaming(); } catch { }
            try { realtimeClient?.SetOutputMuted(true); } catch { }

            _autoMicSegmentsRunning = false;

            if (recordingIndicator != null) recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null) aiTalkingIndicator.SetActive(false);
            if (generalFeedbackText != null) generalFeedbackText.text = "Conversation paused.";

            Debug.Log("[MedicalExamManager] ⏸️ Realtime conversation paused.");
        }

        public void ResumeRealtimeConversation()
        {
            if (!useOpenAIRealtime)
            {
                Debug.LogWarning("[MedicalExamManager] Resume requested but OpenAI realtime is disabled.");
                return;
            }

            if (!_realtimePausedByUser)
                return;

            if (_evaluationRequested || !_conversationStarted)
            {
                Debug.LogWarning("[MedicalExamManager] Resume ignored: conversation is no longer active.");
                _realtimePausedByUser = false;
                _realtimePauseStartedAt = -1f;
                return;
            }

            if (string.IsNullOrWhiteSpace(_pendingRealtimeInstructions))
                _pendingRealtimeInstructions = !string.IsNullOrWhiteSpace(_baseSystemInstructions) ? _baseSystemInstructions : GenerateSystemPrompt();

            _pendingRealtimeInstructions = EnsureRoleIdentityInInstructions(_pendingRealtimeInstructions);

            _realtimePausedByUser = false;

            // Freeze timer while paused by shifting its start time forward by pause duration.
            if (_examTimerStarted && _realtimePauseStartedAt > 0f)
            {
                float pausedSeconds = Mathf.Max(0f, Time.time - _realtimePauseStartedAt);
                _examStartTime += pausedSeconds;
            }
            _realtimePauseStartedAt = -1f;

            try { realtimeClient?.SetOutputMuted(false); } catch { }
            EnsureAutoMicSegmentsRunning("ResumeRealtimeConversation");

            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);
            if (recordingIndicator != null)
                recordingIndicator.SetActive(true);

            if (generalFeedbackText != null)
                generalFeedbackText.text = "Resuming conversation...";

            Debug.Log("[MedicalExamManager] ▶️ Realtime conversation resumed (session preserved).");
        }
        
        private void InterruptAndEvaluate()
        {
            if (_evaluationRequested)
            {
                Debug.LogWarning("[MedicalExamManager] Evaluation already requested");
                return;
            }

            // Ensure evidence log exists even if user triggers evaluation early.
            try { EnsureEvidenceLoggerSessionStarted("InterruptAndEvaluate"); } catch { }
            
            Debug.Log("[MedicalExamManager] Requesting evaluation from AI...");
            
            // Finalize any remaining buffered sentence
            FinalizeSentenceBuffer();
            FinalizeUserSentenceBuffer();

            if (enforceMinimumEvidenceForEvaluation && !_bypassEvidenceGuardOnce)
            {
                int transcriptChars = _fullConversationLog?.Length ?? 0;
                int userWords = 0;
                try
                {
                    if (!string.IsNullOrWhiteSpace(_fullConversationLog))
                    {
                        var lines = _fullConversationLog.Split('\n');
                        foreach (var line in lines)
                        {
                            if (!line.StartsWith("User:", StringComparison.Ordinal))
                                continue;
                            var content = line.Substring("User:".Length).Trim();
                            if (string.IsNullOrWhiteSpace(content))
                                continue;
                            userWords += content.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Length;
                        }
                    }
                }
                catch { }

                if (userWords < Mathf.Max(0, minUserWordsForEvaluation) || transcriptChars < Mathf.Max(0, minTranscriptCharsForEvaluation))
                {
                    Debug.LogWarning($"[MedicalExamManager] Not enough evidence to evaluate (userWords={userWords}, transcriptChars={transcriptChars}). Showing insufficient-evidence result.");

                    try
                    {
                        if (_evidenceLogger != null)
                            _evidenceLogger.AppendLine($"INSUFFICIENT_EVIDENCE: userWords={userWords}, transcriptChars={transcriptChars}");
                    }
                    catch { }

                    // Mark evaluation as requested so the conversation stops.
                    _evaluationRequested = true;
                    _examActive = false;
                    _conversationStarted = false;

                    if (startConversationPanel != null)
                        startConversationPanel.SetActive(false);
                    SetStartConversationUiState(showStart: false, showLoading: false);

                    if (!_evaluationUIShown)
                        ShowEvaluationLoading();
                    if (endConversationButton != null)
                        endConversationButton.gameObject.SetActive(false);
                    if (recordingIndicator != null)
                        recordingIndicator.SetActive(false);
                    if (aiTalkingIndicator != null)
                        aiTalkingIndicator.SetActive(false);

                    // conversationManager has been removed

                    ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor
                        ? ExamEvaluation.RoleType.DoctorToDoctor
                        : ExamEvaluation.RoleType.DoctorToPatient;

                    _currentEvaluation = new ExamEvaluation
                    {
                        scenarioName = GetScenarioName(),
                        roleType = evalRoleType,
                        conversationTranscript = _fullConversationLog,
                        terminologie = 0f,
                        verstaendlichkeit = 0f,
                        aussprache = 0f,
                        overallScore = 0f,
                        overallFeedback = $"Nicht genug Gesprächsinhalt für eine Bewertung (nur {userWords} Wörter). Bitte führen Sie ein kurzes Gespräch (z.B. Anamnese/Struktur), dann erneut Evaluation.",
                        feedbackText = $"Nicht genug Gesprächsinhalt für eine Bewertung (nur {userWords} Wörter). Bitte führen Sie ein kurzes Gespräch (z.B. Anamnese/Struktur), dann erneut Evaluation.",
                        terminologieFeedback = "Keine verwertbare Terminologie (zu wenig Daten).",
                        verstaendlichkeitFeedback = "Keine verwertbare Struktur/Klarheit (unvollständig/unklar).",
                        ausspracheFeedback = "Keine verwertbare Aussprachebeobachtung (zu wenig Daten)."
                    };

                    OnEvaluationParsed(_currentEvaluation);
                    // Reset one-shot bypass flag if we exit early
                    _bypassEvidenceGuardOnce = false;
                    return;
                }
            }

            // Debug: log evidence state right when evaluation is requested
            try
            {
                int userCount = 0;
                int aiCount = 0;
                if (!string.IsNullOrEmpty(_fullConversationLog))
                {
                    var lines = _fullConversationLog.Split('\n');
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("User:")) userCount++;
                        else if (line.StartsWith("AI:")) aiCount++;
                    }
                }

                Debug.Log($"[MedicalExamManager] 📎 Evidence snapshot @evaluation-request | transcriptChars={_fullConversationLog?.Length ?? 0} (UserLines={userCount}, AILines={aiCount}) | realtimeFeedbackChars={_realtimeAIFeedback?.Length ?? 0} | whisperChars={_whisperUserTranscript?.Length ?? 0}");
            }
            catch { }
            
            // Mark evaluation as requested
            _evaluationRequested = true;
            _examActive = false;
            _conversationStarted = false; // Stop checking audio state

            // Disable raycast object 1 and enable raycast object 2
            if (raycastObject1 != null)
                raycastObject1.SetActive(false);
            if (raycastObject2 != null)
                raycastObject2.SetActive(true);

            // Evaluation started: hide the entire Start Conversation panel/UI.
            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: false);
            _lastFeedbackFragmentTime = Time.time; // Start watchdog timer
            _feedbackQuietStart = Time.time;
            _evaluationRequestStartTime = Time.time;
            _deepEvalTriggered = false;

            _draftEvalPromptSentTime = 0f;
            _receivedAnyRealtimeEvalFragment = false;

            // Fresh capture buffer for this evaluation phase.
            _realtimeAIFeedback = "";
            _evaluationAiLineOpen = false;
            
            // Keep the session behavior the same (no forced unsubscribe here).

            // Show evaluation/loading UI immediately so the user sees the feedback container
            if (!_evaluationUIShown)
            {
                ShowEvaluationLoading();
            }

            // Hide End Conversation button
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(false);
            if (pauseConversationButton != null)
                pauseConversationButton.gameObject.SetActive(false);

                //where we active a new gameobject instead called process
            
            // Hide all conversation indicators
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);

            // ── Stop all realtime audio and disconnect ──
            // Stop the AI from talking immediately and close the WebSocket.
            if (useOpenAIRealtime && realtimeClient != null)
            {
                try { realtimeClient.StopPlaybackAndDisconnect(); }
                catch (System.Exception ex) { Debug.LogWarning($"[MedicalExamManager] Error stopping realtime client: {ex.Message}"); }
                Debug.Log("[MedicalExamManager] 🔌 Realtime client disconnected for evaluation.");
            }

            // Stop microphone streaming so no more audio is sent
            if (realtimeMicrophone != null)
            {
                try { realtimeMicrophone.StopStreaming(); }
                catch (System.Exception ex) { Debug.LogWarning($"[MedicalExamManager] Error stopping realtime mic: {ex.Message}"); }
                Debug.Log("[MedicalExamManager] 🎤 Realtime microphone stopped for evaluation.");
            }

            // Stop mic streaming and unsubscribe recorder so we don't keep capturing.
            if (enableAutoMicSegments)
            {
                try { whisperRecorder?.Unsubscribe(); } catch { }
                try { if (whisperRecorder != null) whisperRecorder.OnSegmentComplete.RemoveListener(OnWhisperSegmentComplete); } catch { }
                try { whisperMicrophoneStreamer?.StopStreaming(); } catch { }
                _autoMicSegmentsRunning = false;
                Debug.Log("[MedicalExamManager] Stopped microphone streaming for evaluation.");
            }
            
            // Send a text message to AI requesting evaluation
            if (true) // conversationManager has been removed, but we still process evaluation
            {
                // Keep background user-audio capture running during the realtime draft-eval phase.
                // We'll freeze it right before Whisper transcription so we capture as much as possible.

                // NOTE: Using only remote CSV prompts (eval.one_call.d2d.* / eval.one_call.d2p.*)
                // for the final evaluation. Realtime draft evaluation has been removed - directly proceed to final evaluation.
                if (requestRealtimeEvaluationBeforeFinal)
                {
                    Debug.LogWarning("[MedicalExamManager] requestRealtimeEvaluationBeforeFinal is enabled, but realtime draft evaluation has been removed. Skipping to final evaluation now.");
                }

                StartFinalEvaluationPipeline();
                // Reset one-shot bypass flag after starting final pipeline
                _bypassEvidenceGuardOnce = false;
                return;
            }
            else
            {
                Debug.LogError("[MedicalExamManager] No conversation manager to request evaluation from!");
            }
        }

        private void StartFinalEvaluationPipeline()
        {
            // If the evaluation-mode AI transcript was being streamed token-by-token, its line in
            // _fullConversationLog may still be open (no trailing \n). Close it now so the next
            // "User: ..." line doesn't get concatenated onto the same line, which would corrupt
            // the log the final evaluator receives.
            if (_evaluationAiLineOpen)
            {
                _fullConversationLog += "\n";
                _evaluationAiLineOpen = false;
            }

            // Evaluation started: ensure Start Conversation panel/UI is hidden.
            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: false);

            // Show evaluation/loading UI immediately so the user sees we're processing
            if (!_evaluationUIShown)
                ShowEvaluationLoading();

            // Get API key
            string apiKey = GetAPIKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[MedicalExamManager] ❌ No API key found - cannot run final evaluation");
                return;
            }

            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor
                ? ExamEvaluation.RoleType.DoctorToDoctor
                : ExamEvaluation.RoleType.DoctorToPatient;

            _feedbackParseTriggered = true;
            _parseStartTime = Time.time;

            Debug.Log("[MedicalExamManager] ▶️ Starting final evaluation (Whisper + ONE chat request). Realtime feedback (if any) will be included.");
            StartCoroutine(WhisperThenEvaluate(apiKey, evalRoleType));
        }

        private void MuteAiAudioForRealtimeEvaluationIfEnabled()
        {
            if (!muteRealtimeEvaluationAudio) return;
            if (aiAudioSource == null) return;

            if (!_storedAiAudioMuteState)
            {
                _aiAudioMuteStateBeforeEval = aiAudioSource.mute;
                _storedAiAudioMuteState = true;
            }

            aiAudioSource.mute = true;
        }

        private void SilenceAiAudioVolumeForRealtimeEvaluationIfEnabled()
        {
            if (!silenceRealtimeEvaluationAudioVolumeToZero) return;
            if (aiAudioSource == null) return;

            if (!_storedAiAudioVolumeState)
            {
                _aiAudioVolumeBeforeEval = aiAudioSource.volume;
                _storedAiAudioVolumeState = true;
            }

            aiAudioSource.volume = 0f;
        }

        private void RestoreAiAudioMuteStateIfNeeded()
        {
            if (aiAudioSource == null) return;
            if (!_storedAiAudioMuteState) return;

            aiAudioSource.mute = _aiAudioMuteStateBeforeEval;
            _storedAiAudioMuteState = false;
        }

        private void RestoreAiAudioVolumeAfterRealtimeEvaluationIfNeeded(bool restoreToOne)
        {
            if (aiAudioSource == null) return;
            if (!_storedAiAudioVolumeState) return;

            aiAudioSource.volume = restoreToOne ? 1f : _aiAudioVolumeBeforeEval;
            _storedAiAudioVolumeState = false;
        }

        [Header("Final Evaluation TTS")]
        [Tooltip("If enabled, the app speaks only the final feedback text via OpenAI TTS once the evaluator JSON is parsed.")]
        [SerializeField] private bool speakFinalEvaluationViaElevenLabs = true;

        [Tooltip("OpenAI TTS player for speaking evaluation results (separate from realtime PCM).")]
        [SerializeField] private OpenAITTSPlayer evaluationTTSPlayer;

        [Header("Final Evaluation Feedback FX")]
        [Tooltip("Animator to trigger when the AI starts speaking final feedback.")]
        [SerializeField] private Animator finalFeedbackAnimator;

        [Tooltip("GameObject to activate when the AI starts speaking final feedback.")]
        [SerializeField] private GameObject activefeedbck;

        [Tooltip("Voice selection for the spoken evaluator feedback.")]
        [SerializeField] private OpenAITTSPlayer.VoiceType finalEvaluationVoice = OpenAITTSPlayer.VoiceType.DoctorToDoctor;

        [Tooltip("If enabled, includes per-skill feedback (terminologie/verstaendlichkeit/aussprache) in the spoken evaluation.")]
        [SerializeField] private bool speakFinalEvaluationIncludePerSkillFeedback = true;

        [Tooltip("Max characters per ElevenLabs request chunk (longer feedback is split into multiple requests).")]
        [SerializeField, Range(200, 2000)] private int maxCharsPerFinalEvalTtsChunk = 900;

        /// <summary>
        /// Public method to replay the evaluation audio (for VoiceScore button)
        /// </summary>
        public void ReplayEvaluationAudio()
        {
            if (_currentEvaluation == null)
            {
                Debug.LogWarning("[MedicalExamManager] No evaluation available to replay!");
                return;
            }
            
            StopEvaluationAudio();
            
            Debug.Log("[MedicalExamManager] Replaying evaluation audio...");
            _evaluationPlaybackCoroutine = StartCoroutine(SpeakFinalEvaluationViaRealtime(_currentEvaluation));
        }
        
        /// <summary>
        /// Public method to stop the evaluation audio playback
        /// </summary>
        public void StopEvaluationAudio()
        {
            if (_evaluationPlaybackCoroutine != null)
            {
                StopCoroutine(_evaluationPlaybackCoroutine);
                _evaluationPlaybackCoroutine = null;
                Debug.Log("[MedicalExamManager] Stopped evaluation audio playback");
            }
            
            if (evaluationTTSPlayer != null)
            {
                evaluationTTSPlayer.Stop();
            }
            
            if (aiAudioSource != null && aiAudioSource.isPlaying)
            {
                aiAudioSource.Stop();
            }
        }

        private IEnumerator SpeakFinalEvaluationViaRealtime(ExamEvaluation evaluation)
        {
            // NOTE: This used to speak via the realtime agent, but conversationManager has been removed.
            // We now speak the final evaluation via ElevenLabs/OpenAI TTS (through GptAndWhisper) when enabled.
            if (!speakFinalEvaluationViaElevenLabs) yield break;
            if (evaluation == null) yield break;

            // Give the UI a frame to update first
            yield return null;

            // Stop any current audio and ensure volume is restored for the final spoken feedback.
            if (aiAudioSource != null)
            {
                try { aiAudioSource.Stop(); } catch { }
                aiAudioSource.mute = false;
                aiAudioSource.volume = 1f;
            }

            RestoreAiAudioVolumeAfterRealtimeEvaluationIfNeeded(true);

            // If evaluation failed upstream, don't narrate meaningless zeros.
            bool hasAnyScore = evaluation.overallScore > 0 || evaluation.terminologie > 0f || evaluation.verstaendlichkeit > 0f || evaluation.aussprache > 0f;
            bool hasAnyText = !string.IsNullOrWhiteSpace(evaluation.overallFeedback) || !string.IsNullOrWhiteSpace(evaluation.feedbackText)
                              || !string.IsNullOrWhiteSpace(evaluation.terminologieFeedback) || !string.IsNullOrWhiteSpace(evaluation.verstaendlichkeitFeedback) || !string.IsNullOrWhiteSpace(evaluation.ausspracheFeedback);
            if (!hasAnyScore && !hasAnyText)
            {
                Debug.LogWarning("[MedicalExamManager] Skipping spoken evaluation: evaluation appears empty (all zeros / no feedback). Check final evaluator output.");
                yield break;
            }

            // Build the spoken script and speak it in chunks.
            string speechText = BuildFinalEvaluationSpeechText(evaluation);
            if (string.IsNullOrWhiteSpace(speechText))
                yield break;

            var chunks = SplitTextForTts(speechText, Mathf.Clamp(maxCharsPerFinalEvalTtsChunk, 200, 2000));
            Debug.Log($"[MedicalExamManager] Speaking final evaluation via OpenAI TTS in {chunks.Count} chunk(s). Total chars={speechText.Length}.");

            bool isFirstChunk = true;
            for (int i = 0; i < chunks.Count; i++)
            {
                string chunk = chunks[i];
                if (string.IsNullOrWhiteSpace(chunk))
                    continue;

                // Determine voice based on scenario
                OpenAITTSPlayer.VoiceType voiceToUse = (selectedRole == RoleType.DoctorToDoctor) 
                    ? OpenAITTSPlayer.VoiceType.DoctorToDoctor
                    : OpenAITTSPlayer.VoiceType.DoctorToPatient;

                // Speak via OpenAI TTS
                if (evaluationTTSPlayer != null)
                {
                    evaluationTTSPlayer.Speak(chunk, voiceToUse);
                }
                else
                {
                    // Fallback to legacy path
                    gptAndWhisper?.SpeakResponse(chunk, 
                        selectedRole == RoleType.DoctorToDoctor 
                            ? ElevenLabsTTS.ScenarioType.DoctorToDoctor 
                            : ElevenLabsTTS.ScenarioType.DoctorToPatient);
                }

                // On first chunk, audio should start playing immediately
                if (isFirstChunk)
                {
                    isFirstChunk = false;
                    // Minimal delay to let audio initialize
                    yield return null;

                   

                    if (finalFeedbackAnimator != null)
                        finalFeedbackAnimator.SetTrigger("spin");

                    // Wait 1.5s for the flip/spin animation to play before showing scores
                    yield return new WaitForSeconds(2.5f);
                     if (activefeedbck != null && !activefeedbck.activeSelf)
                        activefeedbck.SetActive(true);
                    // Show feedbackPanel + evaluationPanel after animation trigger
                    if (feedbackPanel != null)
                        feedbackPanel.SetActive(true);
                    if (evaluationPanel != null)
                    {
                        evaluationPanel.SetActive(true);
                        Debug.Log("[MedicalExamManager] ✓ Evaluation panel shown (after animation delay)");
                    }

                    if (evaluationLoadingIndicator != null)
                    {
                        evaluationLoadingIndicator.SetActive(false);
                        Debug.Log("[MedicalExamManager] ✓ Loading indicator hidden (AI started speaking)");
                    }

                    if (evaluationResultsContent != null)
                    {
                        evaluationResultsContent.SetActive(true);
                        Debug.Log("[MedicalExamManager] ✓ Results content shown (AI started speaking)");
                    }

                    if (evaluationDisplayUI != null)
                    {
                        evaluationDisplayUI.DisplayEvaluation(evaluation);
                        Debug.Log("[MedicalExamManager] ✓ Evaluation displayed in UI (AI started speaking)");
                    }
                }

                // Wait until playback ends (best-effort).
                AudioSource evalAudioSrc = evaluationTTSPlayer != null ? evaluationTTSPlayer.PlaybackAudioSource : aiAudioSource;
                if (evalAudioSrc != null)
                {
                    // Wait for audio to actually start (API call takes time)
                    float startWait = Time.time;
                    while (!evalAudioSrc.isPlaying && Time.time - startWait < 15f)
                        yield return null;
                    
                    // Wait for audio to finish
                    float safetyTimeout = 120f;
                    float start = Time.time;
                    while (evalAudioSrc.isPlaying && Time.time - start < safetyTimeout)
                        yield return null;
                }
                else
                {
                    // If we can't observe playback state, avoid spamming calls.
                    yield return new WaitForSeconds(1.0f);
                }

                // Minimal pause between chunks for clarity.
                yield return new WaitForSeconds(0.05f);
            }
        }

       

        private string BuildFinalEvaluationSpeechText(ExamEvaluation evaluation)
        {
            string overallFeedback = !string.IsNullOrWhiteSpace(evaluation.overallFeedback)
                ? evaluation.overallFeedback
                : evaluation.feedbackText;

            bool isGerman = GetExamLanguage() == ExamLanguage.Deutsch;

            // Strict mode: narrate only the main feedback text section.
            if (!string.IsNullOrWhiteSpace(overallFeedback))
                return overallFeedback.Trim();

            // Fallback only if main feedback is missing.
            return isGerman
                ? "Kein finales Feedback verfügbar."
                : "No final feedback available.";
        }

        private static System.Collections.Generic.List<string> SplitTextForTts(string text, int maxChars)
        {
            var chunks = new System.Collections.Generic.List<string>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;

            string normalized = text.Replace("\r\n", "\n").Trim();
            if (normalized.Length <= maxChars)
            {
                chunks.Add(normalized);
                return chunks;
            }

            // Prefer splitting on paragraph boundaries first.
            string[] paras = normalized.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var current = new System.Text.StringBuilder(maxChars + 64);

            void Flush()
            {
                var s = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(s))
                    chunks.Add(s);
                current.Length = 0;
            }

            foreach (var para in paras)
            {
                string p = para.Trim();
                if (p.Length == 0) continue;

                if (p.Length > maxChars)
                {
                    // If one paragraph is huge, split by sentences.
                    string[] sentences = p.Split(new[] { ". ", "! ", "? " }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var s0 in sentences)
                    {
                        string s = s0.Trim();
                        if (s.Length == 0) continue;

                        // Add punctuation back loosely.
                        if (!s.EndsWith(".") && !s.EndsWith("!") && !s.EndsWith("?"))
                            s += ".";

                        if (current.Length + s.Length + 1 > maxChars)
                            Flush();

                        if (s.Length > maxChars)
                        {
                            // Worst-case hard split.
                            int idx = 0;
                            while (idx < s.Length)
                            {
                                int take = Math.Min(maxChars, s.Length - idx);
                                chunks.Add(s.Substring(idx, take).Trim());
                                idx += take;
                            }
                            continue;
                        }

                        if (current.Length > 0) current.Append(' ');
                        current.Append(s);
                    }
                    continue;
                }

                if (current.Length + p.Length + 2 > maxChars)
                    Flush();

                if (current.Length > 0) current.Append("\n\n");
                current.Append(p);
            }

            Flush();
            return chunks;
        }

        private IEnumerator WhisperThenEvaluate(string apiKey, ExamEvaluation.RoleType evalRoleType)
        {
            // conversationManager has been removed

            // Get buffered mic PCM16 from realtime manager (user only)
            byte[] pcm = null;
            try
            {
                pcm = null; // conversationManager has been removed
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MedicalExamManager] Could not read buffered user audio: {ex.Message}");
            }


            // Post-hoc user audio transcription (optional)
            // We attempt this even for short clips; very short clips may fail but it's still useful for debugging.
            if (evaluationDisplayUI != null && pcm != null && pcm.Length >= 16000 / 4) // ~0.25s (bytes)
            {
                bool done = false;
                string whisperText = null;
                string whisperErr = null;

                yield return evaluationDisplayUI.TranscribeUserAudioPcm16WithWhisper(
                    pcm,
                    16000,
                    apiKey,
                    t => { whisperText = t; done = true; },
                    e => { whisperErr = e; done = true; }
                );

                // If the coroutine returned without invoking callbacks, ensure we don't hang.
                if (!done) done = true;

                if (!string.IsNullOrWhiteSpace(whisperText))
                {
                    _whisperUserTranscript = whisperText;
                            // Restore volume back to 1 for the final (fine-tuned) spoken feedback.
                            RestoreAiAudioVolumeAfterRealtimeEvaluationIfNeeded(true);
                    Debug.Log($"[MedicalExamManager] 🎙️ User audio transcript captured ({_whisperUserTranscript.Length} chars). Will include in GPT evaluation.");
                }
                else
                {
                    RestoreAiAudioVolumeAfterRealtimeEvaluationIfNeeded(false);
                    Debug.LogWarning($"[MedicalExamManager] User audio transcription not available: {whisperErr}");
                }
            }
            else
            {
                Debug.LogWarning($"[MedicalExamManager] Whisper transcription skipped (pcm bytes={(pcm?.Length ?? 0)}). If you spoke, check mic permissions/streaming.");
            }


            // Build the transcript we send to GPT: full conversation log + optional user audio transcript
            string transcriptForGpt = _fullConversationLog;
            if (!string.IsNullOrWhiteSpace(_whisperUserTranscript))
            {
                transcriptForGpt += "\n\n[USER AUDIO TRANSCRIPT (post-hoc, from mic audio)]:\n" + _whisperUserTranscript + "\n";
            }

            string pronunciationTurnNotes = BuildTurnNotesSection("PRONUNCIATION_TRACKING_NOTES_PER_TURN", _pronunciationNotesLog);
            if (!string.IsNullOrWhiteSpace(pronunciationTurnNotes))
                transcriptForGpt += pronunciationTurnNotes;

            string structuredSpeechTurnNotes = BuildTurnNotesSection("STRUCTURED_SPEECH_ERROR_NOTES_PER_TURN", _speechTrackingNotesLog);
            if (!string.IsNullOrWhiteSpace(structuredSpeechTurnNotes))
                transcriptForGpt += structuredSpeechTurnNotes;

            // If a configured whisper-start prompt exists for this run, prepend it so the evaluator
            // sees the scenario/patient instructions that were active when the conversation began.
            if (!string.IsNullOrWhiteSpace(_startPromptUsedForRun))
            {
                transcriptForGpt = "[START_PROMPT]\n" + _startPromptUsedForRun + "\n[END_START_PROMPT]\n\n" + transcriptForGpt;
                Debug.Log("[MedicalExamManager] Prepended whisper start prompt to transcript for evaluation.");
            }

            // Persist the evaluation payload transcript for debugging and later analysis.
            try
            {
                EnsureEvidenceLoggerSessionStarted("FinalEvaluation");
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("FINAL_EVALUATION_TRANSCRIPT_PAYLOAD", transcriptForGpt);
            }
            catch { }

            // If configured, prepend a large prompt/answer bank so the fine-tuned evaluator sees the full scenario answers.
            if (useWhisperWithCustomFineTuned)
            {
                string bank = null;
                try
                {
                    if (bigPromptAsset != null && !string.IsNullOrWhiteSpace(bigPromptAsset.text))
                        bank = bigPromptAsset.text;
                }
                catch { }

                if (string.IsNullOrWhiteSpace(bank) && !string.IsNullOrWhiteSpace(bigPromptInline))
                    bank = bigPromptInline;

                if (!string.IsNullOrWhiteSpace(bank))
                {
                    // Mark the bank region so downstream parsers can ignore it if necessary.
                    transcriptForGpt = "[PROMPT_BANK_START]\n" + bank + "\n[PROMPT_BANK_END]\n\n" + transcriptForGpt;
                    Debug.Log($"[MedicalExamManager] Prepended big prompt bank ({Mathf.Min(bank.Length,200)} chars preview) to transcript for fine-tuned eval.");
                }
                else
                {
                    Debug.LogWarning("[MedicalExamManager] useWhisperWithCustomFineTuned is enabled but no big prompt provided (TextAsset or inline).");
                }
            }

            // Intentionally do NOT include the question plan in the final evaluator transcript.
            // The final evaluation should be based on what was actually said (plus optional Whisper transcript).

            // Skip fine-tuned model paths — use the general LLM (EvaluationDisplayUI) with 5D eval prompts.
            // The fine-tuned models don't match the 5D UI schema yet. When a proper fine-tuned model
            // is available, re-enable the fineTunedEvalService block below.

            // If both transcript and realtime feedback are empty, avoid dropping the request.
            if (string.IsNullOrWhiteSpace(transcriptForGpt) && string.IsNullOrWhiteSpace(_realtimeAIFeedback))
            {
                Debug.LogWarning("[MedicalExamManager] No transcript or realtime feedback available. Cannot evaluate.");
                yield break;
            }

            /* ── FINE-TUNED EVALUATOR (DISABLED — 3D model doesn't match 5D UI) ──
            if (fineTunedEvalService != null)
            {
                MedicalExamScenario tempScenario = null;
                
                Debug.Log($"[MedicalExamManager] 📤 Sending to FineTunedGPT4EvaluationService | transcriptLen={transcriptForGpt?.Length ?? 0} | realtimeFeedbackLen={_realtimeAIFeedback?.Length ?? 0}");
                Debug.Log($"[MedicalExamManager] Transcript preview (first 500 chars): {(transcriptForGpt?.Length > 500 ? transcriptForGpt.Substring(0, 500) + "..." : transcriptForGpt)}");

                bool done = false;
                FineTunedGPT4EvaluationService.EvaluationResponse result = null;
                string err = null;

                fineTunedEvalService.EvaluateConversation(
                    transcriptForGpt,
                    _realtimeAIFeedback,
                    null,
                    evalRoleType,
                    _questionPlanUsedForRun,
                    r => { result = r; done = true; },
                    e => { err = e; done = true; }
                );

                float start = Time.time;
                float timeout = 60f;
                while (!done && Time.time - start < timeout)
                    yield return null;

                if (!done)
                {
                    Debug.LogWarning("[MedicalExamManager] Fine-tuned evaluation timed out. Falling back to generic evaluator.");
                    if (tempScenario != null) Destroy(tempScenario);
                }
                else if (result != null)
                {
                    // ... mapping code ...
                    OnEvaluationParsed(_currentEvaluation);
                    if (tempScenario != null) Destroy(tempScenario);
                    yield break;
                }
                else
                {
                    Debug.LogWarning($"[MedicalExamManager] Fine-tuned evaluation failed: {err}. Falling back to generic evaluator.");
                    if (tempScenario != null) Destroy(tempScenario);
                }
            }
            ── END DISABLED FINE-TUNED BLOCK ── */

            Debug.Log($"[MedicalExamManager] 📤 Using general LLM (5D eval) via EvaluationDisplayUI | transcriptLen={transcriptForGpt?.Length ?? 0} | realtimeFeedbackLen={_realtimeAIFeedback?.Length ?? 0}");

            if (evaluationDisplayUI == null)
            {
                Debug.LogError("[MedicalExamManager] evaluationDisplayUI is not assigned. Cannot run generic final evaluation or display sliders.");
                yield break;
            }

            evaluationDisplayUI.ParseAndDisplayConversationAndRealtimeFeedback(
                transcriptForGpt,
                _realtimeAIFeedback,
                apiKey,
                GetScenarioName(),
                evalRoleType,
                OnEvaluationParsed
            );
        }

        private IEnumerator GenerateEvaluationFromTranscript(string apiKey, ExamEvaluation.RoleType roleType, string transcript)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[MedicalExamManager] No API key for single-GPT evaluation.");
                yield break;
            }

            string systemMsg = ResolveFinalEvaluationPromptHeader(roleType);
            string userTemplate = ResolveFinalEvaluationUserTemplate(roleType);

            if (strictOnlineCsvOnly && (string.IsNullOrWhiteSpace(systemMsg) || string.IsNullOrWhiteSpace(userTemplate)))
            {
                string roleName = roleType == ExamEvaluation.RoleType.DoctorToDoctor ? "d2d" : "d2p";
                Debug.LogError($"[MedicalExamManager] Missing required evaluation prompts in strictOnlineCsvOnly mode for role={roleName}. Expected keys: eval.one_call.{roleName}.system and eval.one_call.{roleName}.user_template");
                yield break;
            }

            // Legacy fallback for older setups that only provide one inspector asset.
            if (!strictOnlineCsvOnly && string.IsNullOrWhiteSpace(userTemplate) && whisperEvalPromptAsset != null && !string.IsNullOrWhiteSpace(whisperEvalPromptAsset.text))
                userTemplate = whisperEvalPromptAsset.text;

            if (!strictOnlineCsvOnly && string.IsNullOrWhiteSpace(systemMsg))
                systemMsg = RemotePromptManager.Get("eval.system_message", "You are an expert FSP medical examiner. Respond ONLY with valid JSON as instructed in the user prompt. No markdown, no extra text.");
            systemMsg = ApplyPromptPlaceholders(systemMsg);

            string caseReference = BuildActiveCaseEvaluationReference(roleType);
            string enrichedTranscript = string.IsNullOrWhiteSpace(caseReference)
                ? (transcript ?? string.Empty)
                : caseReference + "\n\n" + (transcript ?? string.Empty);

            string fullPrompt = !string.IsNullOrWhiteSpace(userTemplate)
                ? userTemplate.Replace("{COMBINED_INPUT}", enrichedTranscript)
                : "CONVERSATION TRANSCRIPT:\n" + enrichedTranscript + "\n";

            string url = "https://api.openai.com/v1/chat/completions";

                        // Build JSON body using Newtonsoft to avoid brace/escaping issues
                        var messagesArray = new JArray();
                        messagesArray.Add(new JObject(new JProperty("role", "system"), new JProperty("content", systemMsg)));
                        messagesArray.Add(new JObject(new JProperty("role", "user"), new JProperty("content", fullPrompt)));

                        var bodyObj = new JObject(
                            new JProperty("model", "ft:gpt-4.1-2025-04-14:personal:llmgo:D0CIsr5E"),
                                new JProperty("messages", messagesArray),
                                new JProperty("temperature", 0.2),
                                new JProperty("max_tokens", 1000)
                        );

                        string jsonBody = bodyObj.ToString(Newtonsoft.Json.Formatting.None);
                        byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);

            using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                Debug.Log("[MedicalExamManager] Sending single GPT evaluation request (Whisper->GPT mode)...");
                yield return request.SendWebRequest();

                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    string response = request.downloadHandler.text;
                    Debug.Log($"[MedicalExamManager] Single-GPT response received (len={response.Length})");

                    // DUMP RAW RESPONSE TO FILE FOR DEBUGGING
                    try
                    {
                        string logsDir = System.IO.Path.Combine(Application.dataPath, "Logs");
                        if (!System.IO.Directory.Exists(logsDir))
                            System.IO.Directory.CreateDirectory(logsDir);
                        
                        string dumpPath = System.IO.Path.Combine(logsDir, "EvaluationGPTResponse.txt");
                        System.IO.File.WriteAllText(dumpPath, response);
                        Debug.Log($"[MedicalExamManager] ✓ GPT evaluation response dumped to: {dumpPath}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[MedicalExamManager] Could not dump GPT response to file: {ex.Message}");
                    }

                    try
                    {
                        // Extract assistant content
                        var root = JObject.Parse(response);
                        string content = (string)(root["choices"]?[0]?["message"]?["content"] ?? "");
                        if (string.IsNullOrWhiteSpace(content))
                        {
                            Debug.LogError("[MedicalExamManager] Empty choices[0].message.content in single-GPT response.");
                            yield break;
                        }

                        // DUMP EXTRACTED CONTENT FOR DEBUGGING
                        try
                        {
                            string logsDir = System.IO.Path.Combine(Application.dataPath, "Logs");
                            string contentPath = System.IO.Path.Combine(logsDir, "EvaluationGPTContent.txt");
                            System.IO.File.WriteAllText(contentPath, content);
                            Debug.Log($"[MedicalExamManager] ✓ Extracted content dumped to: {contentPath}");
                        }
                        catch { }

                        // Pass raw assistant content to unified parser (handles 5D JSON and legacy)
                        var evaluation = ExamEvaluation.ParseFromAIResponse(content, _fullConversationLog, GetScenarioName(), roleType);

                        if (evaluation == null)
                        {
                            Debug.LogError("[MedicalExamManager] ❌ Evaluation parsing returned null.");
                            yield break;
                        }

                        Debug.Log($"[MedicalExamManager] Single-GPT evaluation parsed (5D aware): K={evaluation.kommunikation} H={evaluation.hoerverstehen} G={evaluation.gespraechsfuehrung} E={evaluation.empathie} V={evaluation.vollstaendigkeit} | Legacy T={evaluation.terminologie} V={evaluation.verstaendlichkeit} A={evaluation.aussprache} | Overall={evaluation.overallScore}");
                        OnEvaluationParsed(evaluation);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[MedicalExamManager] Error parsing single-GPT response: {e.Message}");
                    }
                }
                else
                {
                    Debug.LogError($"[MedicalExamManager] Single-GPT API error: {request.error} | code={request.responseCode}");
                }
            }
        }

        // Force parsing immediately (used by watchdog quiet timer)
        private void ForceParseNow()
        {
            Debug.Log("[MedicalExamManager] ⚡ Forcing parse now (quiet timer reached).");
            _processingEvaluation = false; // ensure not blocked
            StartParsingFeedback();
        }
        
        public void RequestEvaluation()
        {
            // Route all "end exam" paths through the same evaluation pipeline.
            // Otherwise, ending via voice phrases ("this is all") would skip realtime draft feedback.
            InterruptAndEvaluate();
        }
        
        private void GenerateAndShowEvaluation()
        {
            Debug.Log("[MedicalExamManager] === STARTING EVALUATION GENERATION ===");
            Debug.Log($"[MedicalExamManager] Conversation log length: {_fullConversationLog.Length} characters");
            Debug.Log($"[MedicalExamManager] Conversation preview: {(_fullConversationLog.Length > 100 ? _fullConversationLog.Substring(0, 100) + "..." : _fullConversationLog)}");
            
            // Check if conversation log is empty
            if (string.IsNullOrEmpty(_fullConversationLog))
            {
                Debug.LogError("[MedicalExamManager] ❌ Conversation log is EMPTY! No evaluation can be generated.");
                Debug.LogError("[MedicalExamManager] Make sure onAgentTranscript and onUserTranscript events are working!");
                return;
            }
            
            // Get OpenAI API key
            string apiKey = GetAPIKey();
            
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[MedicalExamManager] ❌ No API key found - cannot generate evaluation");
                return;
            }
            
            Debug.Log($"[MedicalExamManager] ✓ API key found (length: {apiKey.Length})");
            
            // Determine role type
            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor 
                ? ExamEvaluation.RoleType.DoctorToDoctor 
                : ExamEvaluation.RoleType.DoctorToPatient;
            
            Debug.Log($"[MedicalExamManager] ✓ Role type: {evalRoleType}");
            Debug.Log("[MedicalExamManager] 📤 Sending evaluation request to GPT-4...");
            StartCoroutine(GenerateEvaluationFromConversation(apiKey, evalRoleType));
        }
        
        private string GetAPIKey()
        {
            string apiKey = "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";
            
            if (false) // conversationManager has been removed
            {
                // var configField = conversationManager.GetType().GetField("openAIConfig",
                //     System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                // if (configField != null)
                // {
                //     var config = configField.GetValue(conversationManager);
                //     if (config != null)
                //     {
                //         var apiKeyField = config.GetType().GetField("apiKey",
                //             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                //         if (apiKeyField != null)
                //         {
                //             string key = (string)apiKeyField.GetValue(config);
                //             if (!string.IsNullOrEmpty(key))
                //                 apiKey = key;
                //         }
                //     }
                // }
            }
            
            return apiKey;
        }
        
        private void ProcessEvaluation(string evaluationResponse)
        {
            Debug.Log($"[MedicalExamManager] Processing evaluation:\n{evaluationResponse}");
            
            // conversationManager has been removed
            string apiKey = "";
            
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogWarning("[MedicalExamManager] API key not found, using regex fallback parsing");
                ParseEvaluationWithRegex(evaluationResponse);
                return;
            }
            
            // Use GPT-4 to parse evaluation reliably
            Debug.Log("[MedicalExamManager] Using GPT-4 to parse evaluation for accurate results...");
            
            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor 
                ? ExamEvaluation.RoleType.DoctorToDoctor 
                : ExamEvaluation.RoleType.DoctorToPatient;
            
            StartCoroutine(ExamEvaluation.ParseWithGPT(
                evaluationResponse,
                _fullConversationLog,
                GetScenarioName(),
                evalRoleType,
                apiKey,
                OnEvaluationParsed
            ));
        }

        /// <summary>
        /// NEW: Uses fine-tuned GPT-4 for evaluation instead of realtime AI feedback
        /// </summary>
        public void EvaluateWithFineTunedGPT4()
        {
            if (!fineTunedEvalService)
            {
                Debug.LogError("[MedicalExamManager] FineTunedGPT4EvaluationService not assigned!");
                return;
            }

            Debug.Log("[MedicalExamManager] === CALLING FINE-TUNED GPT-4 EVALUATION ===");
            Debug.Log($"[MedicalExamManager] Conversation log length: {_fullConversationLog.Length} characters");

            // Show loading indicator
            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(true);

            // Call fine-tuned service
            fineTunedEvalService.EvaluateConversation(
                _fullConversationLog,
                _realtimeAIFeedback,
                null,
                selectedRole == RoleType.DoctorToDoctor ? ExamEvaluation.RoleType.DoctorToDoctor : ExamEvaluation.RoleType.DoctorToPatient,
                _questionPlanUsedForRun,
                OnFinalEvaluationSuccess,
                OnFinalEvaluationError
            );
        }

        private void OnFinalEvaluationSuccess(FineTunedGPT4EvaluationService.EvaluationResponse evaluationResponse)
        {
            Debug.Log($"[MedicalExamManager] ✓ Fine-tuned evaluation received!");
            Debug.Log($"[MedicalExamManager] Scores - T:{evaluationResponse.terminologie}, V:{evaluationResponse.verstaendlichkeit}, A:{evaluationResponse.aussprache}, Overall:{evaluationResponse.overallScore}");

            // Convert to ExamEvaluation object
            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor 
                ? ExamEvaluation.RoleType.DoctorToDoctor 
                : ExamEvaluation.RoleType.DoctorToPatient;

            _currentEvaluation = new ExamEvaluation
            {
                scenarioName = GetScenarioName(),
                roleType = evalRoleType,
                conversationTranscript = _fullConversationLog,
                terminologie = evaluationResponse.terminologie,
                verstaendlichkeit = evaluationResponse.verstaendlichkeit,
                aussprache = evaluationResponse.aussprache,
                overallScore = evaluationResponse.overallScore,
                feedbackText = evaluationResponse.generalFeedback
            };

            // Display evaluation results
            OnEvaluationParsed(_currentEvaluation);
        }

        private void OnFinalEvaluationError(string error)
        {
            Debug.LogError($"[MedicalExamManager] ❌ Fine-tuned evaluation error: {error}");
            
            // Hide loading indicator
            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(false);

            // Fallback to regex parsing of realtime feedback
            if (!string.IsNullOrEmpty(_realtimeAIFeedback))
            {
                Debug.Log("[MedicalExamManager] Falling back to regex parsing of realtime feedback...");
                ParseEvaluationWithRegex(_realtimeAIFeedback);
            }
        }
        
        private void ParseEvaluationWithRegex(string evaluationResponse)
        {
            // Fallback to regex parsing
            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor 
                ? ExamEvaluation.RoleType.DoctorToDoctor 
                : ExamEvaluation.RoleType.DoctorToPatient;
                
            _currentEvaluation = ExamEvaluation.ParseFromAIResponse(
                evaluationResponse,
                _fullConversationLog,
                GetScenarioName(),
                evalRoleType
            );
            
            OnEvaluationParsed(_currentEvaluation);
        }
        
        private void OnEvaluationParsed(ExamEvaluation evaluation)
        {
            Debug.Log("[MedicalExamManager] === EVALUATION PARSED ===");

            if (evaluation == null)
            {
                Debug.LogError("[MedicalExamManager] ❌ Evaluation is NULL — showing error to user.");
                var errorEval = new ExamEvaluation
                {
                    scenarioName = GetScenarioName(),
                    roleType = selectedRole == RoleType.DoctorToDoctor
                        ? ExamEvaluation.RoleType.DoctorToDoctor
                        : ExamEvaluation.RoleType.DoctorToPatient,
                    overallFeedback = "Auswertung konnte nicht geladen werden. Bitte prüfen Sie die Netzwerkverbindung und die API-Konfiguration, dann versuchen Sie es erneut.",
                    feedbackText = "Evaluation error: AI response could not be parsed. Check Unity Console for details.",
                    passed = false,
                    overallScore = 0f
                };
                if (evaluationPanel != null) evaluationPanel.SetActive(true);
                if (feedbackPanel != null) feedbackPanel.SetActive(true);
                if (evaluationDisplayUI != null) evaluationDisplayUI.gameObject.SetActive(true);
                if (evaluationLoadingIndicator != null) evaluationLoadingIndicator.SetActive(false);
                if (evaluationResultsContent != null) evaluationResultsContent.SetActive(true);
                evaluationDisplayUI?.DisplayEvaluation(errorEval);
                _evaluationUIShown = true;
                return;
            }

            Debug.Log($"[MedicalExamManager] ✓ Evaluation received: 5D[K={evaluation.kommunikation} H={evaluation.hoerverstehen} G={evaluation.gespraechsfuehrung} E={evaluation.empathie} V={evaluation.vollstaendigkeit}] Legacy[T={evaluation.terminologie} V={evaluation.verstaendlichkeit} A={evaluation.aussprache}] Overall={evaluation.overallScore}");

            // Guardrail: if the conversation contains too little actual medical/structured content,
            // clamp scores so random speech can't score highly.
            if (enforceLowScoresWhenMedicalSignalIsLow)
            {
                if (!HasEnoughMedicalSignal(out int distinctHits))
                {
                    float maxOverall = Mathf.Clamp(maxOverallScoreWhenMedicalSignalIsLow, 0f, 100f);
                    float maxSub = Mathf.Clamp(maxSubscoreWhenMedicalSignalIsLow, 0f, 5f);

                    evaluation.overallScore = Mathf.Min(evaluation.overallScore, maxOverall);
                    evaluation.terminologie = Mathf.Min(evaluation.terminologie, maxSub);
                    evaluation.verstaendlichkeit = Mathf.Min(evaluation.verstaendlichkeit, maxSub);
                    evaluation.aussprache = Mathf.Min(evaluation.aussprache, maxSub);

                    string msg = $"Zu wenig medizinischer/strukturierter Inhalt für eine echte FSP-Bewertung (Signal-Keywords: {distinctHits}). Random/unklarer Inhalt muss sehr niedrig bewertet werden. Bitte: klare Anamnese-Struktur, Red Flags, DD/Diagnostik/Therapie.";
                    evaluation.overallFeedback = msg;
                    evaluation.feedbackText = msg;
                    if (string.IsNullOrWhiteSpace(evaluation.terminologieFeedback)) evaluation.terminologieFeedback = "Zu wenig verwertbare Terminologie.";
                    if (string.IsNullOrWhiteSpace(evaluation.verstaendlichkeitFeedback)) evaluation.verstaendlichkeitFeedback = "Zu wenig Struktur/Klarheit (unvollständig/unklar).";
                    if (string.IsNullOrWhiteSpace(evaluation.ausspracheFeedback)) evaluation.ausspracheFeedback = "Zu wenig verwertbare Aussprachebeobachtung.";

                    Debug.LogWarning($"[MedicalExamManager] Medical-signal guard applied -> clamped scores (distinctHits={distinctHits}).");
                }
            }

            _currentEvaluation = evaluation;

            // From here on, ignore any additional realtime evaluation transcript to prevent feedback loops.
            _evaluationCompleted = true;

            // Stop any pending evaluation-delay coroutine (not needed anymore)
            if (_evaluationProcessCoroutine != null)
            {
                StopCoroutine(_evaluationProcessCoroutine);
                _evaluationProcessCoroutine = null;
            }
            _processingEvaluation = false;

            // Restore original mute state bookkeeping (we will unmute explicitly if we speak the final result)
            RestoreAiAudioMuteStateIfNeeded();

            // Do NOT activate evaluationPanel here — it will be activated 2s after the animation
            // in ShowEvaluationUIAfterDelay / SpeakFinalEvaluationViaRealtime.

            // Keep loading indicator visible until TTS starts playing
            // (display will happen inside SpeakFinalEvaluationViaRealtime when audio actually starts)
            
            // Speak the evaluation feedback using TTS (OpenAI voices via ElevenLabsTTS wrapper)
            // Display will happen inside this coroutine when audio starts playing
            if (speakFinalEvaluationViaElevenLabs && _currentEvaluation != null)
            {
                Debug.Log("[MedicalExamManager] 🔊 Starting TTS narration of evaluation (display will show when audio plays)...");
                _evaluationPlaybackCoroutine = StartCoroutine(SpeakFinalEvaluationViaRealtime(_currentEvaluation));
            }
            else
            {
                // No TTS — still delay 1.5s for the animation before showing scores
                StartCoroutine(ShowEvaluationUIAfterDelay(_currentEvaluation, 1.5f));
            }

            // Also print general feedback into the optional dedicated text field.
            if (generalFeedbackText != null)
            {
                string overallFeedback = !string.IsNullOrWhiteSpace(evaluation.overallFeedback)
                    ? evaluation.overallFeedback
                    : evaluation.feedbackText;

                generalFeedbackText.text = overallFeedback ?? string.Empty;
            }

            // Log results
            ParseAndLogScores(_currentEvaluation);

        }
        
        private IEnumerator ShowEvaluationUIAfterDelay(ExamEvaluation evaluation, float delay)
        {
            yield return new WaitForSeconds(delay);

            // Show evaluationPanel 2s after animation trigger
            if (feedbackPanel != null)
                feedbackPanel.SetActive(true);
            if (evaluationPanel != null)
            {
                evaluationPanel.SetActive(true);
                Debug.Log("[MedicalExamManager] ✓ Evaluation panel shown (after animation delay)");
            }

            if (evaluationLoadingIndicator != null)
            {
                evaluationLoadingIndicator.SetActive(false);
                Debug.Log("[MedicalExamManager] ✓ Loading indicator hidden (after delay)");
            }

            if (evaluationResultsContent != null)
            {
                evaluationResultsContent.SetActive(true);
                Debug.Log("[MedicalExamManager] ✓ Results content shown (after delay)");
            }

            if (evaluationDisplayUI != null)
            {
                evaluationDisplayUI.DisplayEvaluation(evaluation);
                Debug.Log("[MedicalExamManager] ✓ Evaluation displayed in UI (after delay)");
            }
        }

        private void ParseAndLogScores(ExamEvaluation evaluation)
        {
            Debug.Log($"[MedicalExamManager] === EVALUATION RESULTS ===");
            Debug.Log($"Scenario: {evaluation.scenarioName}");
            Debug.Log($"Role: {evaluation.roleType}");
            Debug.Log($"Terminologie: {evaluation.terminologie}/5");
            Debug.Log($"Grammatik: {evaluation.verstaendlichkeit}/5");
            Debug.Log($"Aussprache: {evaluation.aussprache}/5");
            Debug.Log($"Overall Score: {evaluation.overallScore}/100");
            Debug.Log($"Feedback: {evaluation.feedbackText}");
            Debug.Log($"=================================");
        }
        public void RestartGame()
        {
            // Reload the currently active scene.
            // Note: the scene must be added to Build Settings for buildIndex reload to work.
            try
            {
                Time.timeScale = 1f;
            }
            catch { }
            var scene = SceneManager.GetActiveScene();
            Debug.Log($"[MedicalExamManager] RestartGame() reloading scene: '{scene.name}' (buildIndex={scene.buildIndex})");
            SceneManager.LoadScene(scene.buildIndex);
        }
        public void RestartExam()
        {
            // Reset state
            _examActive = false;
            _evaluationRequested = false;
            selectedRole = RoleType.None;

            _examTimerStarted = false;
            _examStartTime = -1f;
            _realtimePauseStartedAt = -1f;
            
            // Hide evaluation
            if (evaluationPanel != null)
                evaluationPanel.SetActive(false);
            
            // Reset evaluation UI
            if (evaluationLoadingIndicator != null)
                evaluationLoadingIndicator.SetActive(false);
            if (evaluationResultsContent != null)
                evaluationResultsContent.SetActive(false);
            
            // Reset timer color
            if (timerText != null)
                timerText.color = Color.white;
            
            // Unsubscribe from events (conversationManager has been removed)
            // if (conversationManager != null)
            // {
            //     if (conversationManager.onAgentTranscript != null)
            //         conversationManager.onAgentTranscript.RemoveListener(OnAgentSpoke);
            //     if (conversationManager.onUserTranscript != null)
            //         conversationManager.onUserTranscript.RemoveListener(OnUserSpoke);
            // }
            
            // Reset conversation state
            _conversationStarted = false;
            _aiWasSpeaking = false;
            _lastAudioStopTime = 0f;
            _currentAISentenceBuffer = "";
            _lastAITranscriptTime = 0f;
            
            // Hide all conversation indicators
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);
            if (startConversationIndicator != null)
                startConversationIndicator.SetActive(false);
            if (startConversationLoadingIndicator != null)
                startConversationLoadingIndicator.SetActive(false);

            // Hide Start UI on restart; we will only show it again after a role is selected (and only if allowed).
            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: false);
            
            // Hide End Conversation button
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(false);
            
            // Show role selection again
            ShowRoleSelection();

            // Ensure whisper recorder/mic are stopped when restarting
            try { whisperRecorder?.Unsubscribe(); } catch { }
            try { if (whisperRecorder != null) whisperRecorder.OnSegmentComplete.RemoveListener(OnWhisperSegmentComplete); } catch { }
            try { whisperMicrophoneStreamer?.StopStreaming(); } catch { }
            _autoMicSegmentsRunning = false;
            
            Debug.Log("[MedicalExamManager] Exam restarted");
        }
        
        // Public method to manually trigger evaluation (can be called from UI button)
        public void ManualEvaluationTrigger()
        {
            if (_evaluationRequested)
            {
                Debug.LogWarning("[MedicalExamManager] Evaluation already requested");
                return;
            }
            
            Debug.Log("[MedicalExamManager] ========================================");
            Debug.Log("[MedicalExamManager] MANUAL EVALUATION TRIGGERED");
            Debug.Log("[MedicalExamManager] ========================================");
            // Bypass the minimum-evidence guard for this manual debug trigger so we always send the request.
            _bypassEvidenceGuardOnce = true;
            InterruptAndEvaluate();
        }

        public void Debug_SendEvaluationDemo_GoodConversation()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[MedicalExamManager] Debug demo evaluation can only run in Play Mode.");
                return;
            }

            if (_evaluationRequested)
            {
                Debug.LogWarning("[MedicalExamManager] Evaluation already requested");
                return;
            }

            // Use the currently selected role when possible; otherwise default to Doctor->Patient.
            ExamEvaluation.RoleType evalRoleType = selectedRole == RoleType.DoctorToDoctor
                ? ExamEvaluation.RoleType.DoctorToDoctor
                : ExamEvaluation.RoleType.DoctorToPatient;

            string apiKey = GetAPIKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Debug.LogError("[MedicalExamManager] ❌ No API key found - cannot run demo evaluation");
                return;
            }

            // Inject a strong, clean transcript so we can test the evaluator on a 'good conversation'.
            string transcriptForGpt = BuildGoodConversationDemoTranscript(evalRoleType);
            _fullConversationLog = transcriptForGpt;
            _realtimeAIFeedback = "";
            _whisperUserTranscript = "";

            // IMPORTANT: stop any mic capture so no new Whisper segment can trigger an extra GPT turn.
            try
            {
                // Stop manual debug recording if it was running.
                _manualWhisperRecordingActive = false;
                _manualWhisperBuffer?.Clear();
                if (whisperMicrophoneStreamer != null)
                {
                    whisperMicrophoneStreamer.OnAudioChunk -= OnManualWhisperAudioChunk;
                }
            }
            catch { }

            if (enableAutoMicSegments)
            {
                try { whisperRecorder?.Unsubscribe(); } catch { }
                try { if (whisperRecorder != null) whisperRecorder.OnSegmentComplete.RemoveListener(OnWhisperSegmentComplete); } catch { }
                try { whisperMicrophoneStreamer?.StopStreaming(); } catch { }
                _autoMicSegmentsRunning = false;
            }
            else
            {
                // Even if auto segments are disabled, ensure we stop any active streamer.
                try { whisperMicrophoneStreamer?.StopStreaming(); } catch { }
            }

            // Mark state like a normal evaluation run so UI behaves consistently.
            _evaluationRequested = true;
            _examActive = false;
            _conversationStarted = false;

            if (startConversationPanel != null)
                startConversationPanel.SetActive(false);
            SetStartConversationUiState(showStart: false, showLoading: false);

            if (!_evaluationUIShown)
                ShowEvaluationLoading();
            if (endConversationButton != null)
                endConversationButton.gameObject.SetActive(false);
            if (recordingIndicator != null)
                recordingIndicator.SetActive(false);
            if (aiTalkingIndicator != null)
                aiTalkingIndicator.SetActive(false);

            try
            {
                EnsureEvidenceLoggerSessionStarted("DemoEvaluation");
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("DEMO_EVALUATION_TRANSCRIPT_PAYLOAD", transcriptForGpt);
            }
            catch { }

            Debug.Log("[MedicalExamManager] ▶️ Running DEMO evaluation (Good Conversation) via single GPT request (no Whisper).");
            StartCoroutine(GenerateEvaluationFromTranscript(apiKey, evalRoleType, transcriptForGpt));
        }

        private static string BuildGoodConversationDemoTranscript(ExamEvaluation.RoleType evalRoleType)
        {
            // Note: Evaluation expects lines prefixed with "User:" and "AI:".
            // This demo is written as Doctor (User) talking to Patient (AI).
            // For DoctorToDoctor we keep the structure but adjust wording slightly.

            bool isDoctorToDoctor = evalRoleType == ExamEvaluation.RoleType.DoctorToDoctor;
            string aiRole = isDoctorToDoctor ? "Kollege" : "Patient";

            return
$@"AI: Guten Tag.
User: Guten Tag, mein Name ist Dr. Müller. Ich führe heute das Gespräch. Wie darf ich Sie ansprechen?
AI: Ich heiße Schneider.
User: Danke, Herr/Frau Schneider. Was führt Sie heute zu mir?
AI: Ich habe seit zwei Tagen starke Kopfschmerzen.

User: Verstehe. Können Sie mir sagen, wo genau die Schmerzen sitzen und wie sie sich anfühlen (drückend, stechend, pulsierend)?
AI: Vor allem vorne an der Stirn, eher pulsierend.

User: Seit wann genau haben Sie die Beschwerden, sind sie plötzlich oder langsam aufgetreten, und wie stark sind sie auf einer Skala von 0 bis 10?
AI: Es hat vorgestern Nachmittag angefangen und wurde langsam stärker. Jetzt so 7 von 10.

User: Gibt es Begleitsymptome wie Übelkeit, Erbrechen, Licht- oder Lärmempfindlichkeit, Sehstörungen, Fieber oder Nackensteifigkeit?
AI: Mir ist etwas übel und Licht stört. Kein Fieber und der Nacken ist nicht steif.

User: Hatten Sie so etwas schon einmal? Gibt es Auslöser wie Stress, wenig Schlaf, zu wenig trinken oder neue Medikamente?
AI: Ähnlich kenne ich das von früher, wenn ich viel Stress habe. Ich habe auch schlecht geschlafen.

User: Danke. Haben Sie Vorerkrankungen (z.B. Bluthochdruck, Diabetes), nehmen Sie regelmäßig Medikamente, und bestehen Allergien?
AI: Ich habe keinen Diabetes. Blutdruck ist manchmal etwas erhöht. Ich nehme sonst nichts regelmäßig. Keine Allergien.

User: Rauchen oder Alkohol? Und gibt es in der Familie Migräne oder andere neurologische Erkrankungen?
AI: Ich rauche nicht, Alkohol selten. Meine Mutter hat Migräne.

User: Danke. Ich fasse kurz zusammen: Seit zwei Tagen pulsierende Stirnkopfschmerzen, Stärke 7/10, mit Übelkeit und Lichtempfindlichkeit, ohne Fieber oder Nackensteifigkeit; Stress und Schlafmangel als möglicher Auslöser, positive Familienanamnese für Migräne. Das klingt am ehesten nach Migräne oder Spannungskopfschmerz; Warnzeichen sehe ich aktuell nicht, aber ich werde Ihren Blutdruck messen und eine neurologische Kurzuntersuchung machen. Danach besprechen wir die Therapie, z.B. ausreichend Flüssigkeit, Ruhe, ggf. Ibuprofen, und bei Migräne ggf. ein Triptan. Ist das für Sie in Ordnung?
AI: Ja, das ist in Ordnung."
            ;
        }
        
        private IEnumerator GenerateEvaluationFromConversation(string apiKey, ExamEvaluation.RoleType roleType)
        {
            // Include both conversation and any feedback the Realtime AI gave
            string realtimeFeedbackSection = "";
            if (!string.IsNullOrEmpty(_realtimeAIFeedback))
            {
                realtimeFeedbackSection = $@"

REALTIME AI FEEDBACK (given during conversation):
{_realtimeAIFeedback}

IMPORTANT: The Realtime AI may have already commented on pronunciation, speech quality, or mistakes. 
Consider this feedback when scoring Aussprache (pronunciation) and overall performance.";
            }

            // Include per-turn pronunciation tracking notes (from the separate pronunciation evaluator).
            string pronunciationNotesSection = "";
            try
            {
                pronunciationNotesSection = BuildTurnNotesSection("PRONUNCIATION_TRACKING_NOTES_PER_TURN", _pronunciationNotesLog);
            }
            catch { }

            string structuredSpeechNotesSection = "";
            try
            {
                structuredSpeechNotesSection = BuildTurnNotesSection("STRUCTURED_SPEECH_ERROR_NOTES_PER_TURN", _speechTrackingNotesLog);
            }
            catch { }

                        // Role-specific evaluation prompt: prefer configured role-specific evaluation prompts if present.
                        string evaluationPrompt;
                        string roleSpecificEval = ResolveFinalEvaluationPromptHeader(roleType);

                        if (!string.IsNullOrWhiteSpace(roleSpecificEval))
                        {
                                // Use the custom role-specific evaluation prompt as header and append the conversation log.
                                evaluationPrompt = roleSpecificEval + "\n\n";
                                if (!string.IsNullOrWhiteSpace(_startPromptUsedForRun))
                                        evaluationPrompt += "START_PROMPT:\n" + _startPromptUsedForRun + "\nEND_START_PROMPT\n\n";
                            evaluationPrompt += "CONVERSATION LOG:\n" + _fullConversationLog + "\n" + realtimeFeedbackSection;
                            if (!string.IsNullOrWhiteSpace(pronunciationNotesSection))
                                evaluationPrompt += "\n" + pronunciationNotesSection;
                            if (!string.IsNullOrWhiteSpace(structuredSpeechNotesSection))
                                evaluationPrompt += "\n" + structuredSpeechNotesSection;
                        }
                        else
                        {
                                // Fallback to built-in evaluation template
                                evaluationPrompt = $@"You are an expert medical examiner. Analyze the following medical exam conversation and provide a strict, honest evaluation.

SCENARIO: {GetScenarioName()}
ROLE: {(selectedRole == RoleType.DoctorToDoctor ? "Doctor examining another doctor" : "Patient examining doctor's consultation skills")}

CONVERSATION LOG:
{_fullConversationLog}
{realtimeFeedbackSection}
{pronunciationNotesSection}
{structuredSpeechNotesSection}

EVALUATION CRITERIA:
- Terminologie (0-5): Accuracy of medical terminology, correct usage of terms
- Grammatik (0-5): Grammar/sentence structure and clarity of communication
- Aussprache (0-5): Pronunciation quality, speech clarity, vocal mistakes (consider any comments from Realtime AI feedback)
- Overall Score (0-100): Comprehensive performance including knowledge, communication, and professionalism

SCORING GUIDELINES:
- Be STRICT and HONEST
- If there were speech/pronunciation mistakes, reflect this in Aussprache score
- If medical knowledge was poor or incorrect, reflect this in Terminologie
- If communication was unclear or disorganized, reflect this in Verständlichkeit
- Overall score should match the severity: 0-40=Failed, 41-60=Poor, 61-75=Adequate, 76-85=Good, 86-95=Very Good, 96-100=Excellent

Return ONLY a valid JSON object:
{{
    ""terminologie"": [score 0-5],
    ""verstaendlichkeit"": [score 0-5],
    ""aussprache"": [score 0-5],
    ""overallScore"": [score 0-100],
    ""feedback"": ""Detailed feedback mentioning specific strengths, weaknesses, mistakes, and areas for improvement""
}}
Be strict and honest. If the user made mistakes or gave incorrect answers, reflect this in LOW scores.";

            try
            {
                EnsureEvidenceLoggerSessionStarted("GenerateEvaluationFromConversation");
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("DEBUG_EVAL_REQUEST_PROMPT", evaluationPrompt);
            }
            catch { }

            string url = "https://api.openai.com/v1/chat/completions";

            string jsonBody = $@"{{
  ""model"": ""ft:gpt-4.1-2025-04-14:personal:llmgo:D0CIsr5E"",
  ""messages"": [
    {{""role"": ""system"", ""content"": ""You are a medical exam evaluator. Provide honest, strict evaluation scores in JSON format.""}},
    {{""role"": ""user"", ""content"": {ExamEvaluation.EscapeJsonString(evaluationPrompt)}}}
  ],
  ""temperature"": 0.3,
  ""max_tokens"": 1000
}}";

            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
            
            using (UnityEngine.Networking.UnityWebRequest request = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                
                Debug.Log("[MedicalExamManager] Sending evaluation request to fine-tuned model...");
                yield return request.SendWebRequest();
                
                Debug.Log($"[MedicalExamManager] Request completed. Result: {request.result}");
                
                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string response = request.downloadHandler.text;
                        Debug.Log($"[MedicalExamManager] ✓ Fine-tuned model response received (length: {response.Length})");
                        Debug.Log($"[MedicalExamManager] Response preview: {(response.Length > 200 ? response.Substring(0, 200) + "..." : response)}");

                        try
                        {
                            if (_evidenceLogger != null)
                                _evidenceLogger.AppendSection("DEBUG_EVAL_RAW_RESPONSE", response);
                        }
                        catch { }
                        
                        // Parse the response using the existing GPT parsing
                        var gptResponse = JsonUtility.FromJson<ExamEvaluation.GPTResponse>(response);
                        Debug.Log($"[MedicalExamManager] GPT Response parsed. Choices count: {(gptResponse?.choices?.Length ?? 0)}");
                        
                        if (gptResponse?.choices != null && gptResponse.choices.Length > 0)
                        {
                            string evaluationJson = gptResponse.choices[0].message.content.Trim();
                            evaluationJson = evaluationJson.Replace("```json", "").Replace("```", "").Trim();
                            
                            Debug.Log($"[MedicalExamManager] Evaluation JSON extracted: {evaluationJson}");
                            
                            var evalData = JsonUtility.FromJson<ExamEvaluation.EvaluationData>(evaluationJson);
                            Debug.Log($"[MedicalExamManager] ✓ Parsed evaluation data: T={evalData.terminologie}, V={evalData.verstaendlichkeit}, A={evalData.aussprache}");
                            
                            ExamEvaluation evaluation = new ExamEvaluation
                            {
                                scenarioName = GetScenarioName(),
                                roleType = roleType,
                                conversationTranscript = _fullConversationLog,
                                terminologie = evalData.terminologie,
                                verstaendlichkeit = evalData.verstaendlichkeit,
                                aussprache = evalData.aussprache,
                                overallScore = evalData.overallScore,
                                feedbackText = evalData.feedback
                            };
                            
                            Debug.Log("[MedicalExamManager] ✓ Evaluation object created, calling OnEvaluationParsed...");
                            OnEvaluationParsed(evaluation);
                        }
                        else
                        {
                            Debug.LogError("[MedicalExamManager] ❌ No choices in GPT response!");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[MedicalExamManager] ❌ Error parsing GPT evaluation: {e.Message}");
                        Debug.LogError($"[MedicalExamManager] Stack trace: {e.StackTrace}");

                        try
                        {
                            if (_evidenceLogger != null)
                                _evidenceLogger.AppendLine("DEBUG_EVAL_PARSE_ERROR: " + e.Message);
                        }
                        catch { }
                    }
                }
                else
                {
                    Debug.LogError($"[MedicalExamManager] ❌ GPT-4 API error: {request.error}");
                    Debug.LogError($"[MedicalExamManager] Response code: {request.responseCode}");
                    if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                    {
                        Debug.LogError($"[MedicalExamManager] Error response: {request.downloadHandler.text}");
                    }

                    try
                    {
                        if (_evidenceLogger != null)
                        {
                            _evidenceLogger.AppendLine("DEBUG_EVAL_HTTP_ERROR: " + request.error + " code=" + request.responseCode);
                            if (!string.IsNullOrEmpty(request.downloadHandler?.text))
                                _evidenceLogger.AppendSection("DEBUG_EVAL_HTTP_ERROR_BODY", request.downloadHandler.text);
                        }
                    }
                    catch { }
                }
            }
        }}
        // --- Manual Whisper Recording for Debugger ---
    private bool _manualWhisperRecordingActive = false;
    private List<byte> _manualWhisperBuffer = new List<byte>();

    public void StartWhisperManualRecording()
    {
        _manualWhisperRecordingActive = true;
        _manualWhisperBuffer.Clear();
        if (whisperMicrophoneStreamer != null)
        {
            whisperMicrophoneStreamer.OnAudioChunk += OnManualWhisperAudioChunk;
            whisperMicrophoneStreamer.StartStreaming();
        }
        Debug.Log("[MedicalExamManager] Manual Whisper recording started.");
}

    public void StopWhisperManualRecordingAndSend()
    {
        _manualWhisperRecordingActive = false;
        if (whisperMicrophoneStreamer != null)
        {
            whisperMicrophoneStreamer.OnAudioChunk -= OnManualWhisperAudioChunk;
            whisperMicrophoneStreamer.StopStreaming();
        }
        Debug.Log($"[MedicalExamManager] Manual Whisper recording stopped. Sending { _manualWhisperBuffer.Count } bytes to Whisper...");
        if (_manualWhisperBuffer.Count > 0)
        {
            StartCoroutine(HandleWhisperSegment(_manualWhisperBuffer.ToArray()));
        }
        _manualWhisperBuffer.Clear();
    }

    private void OnManualWhisperAudioChunk(string b64)
    {
        if (!_manualWhisperRecordingActive || string.IsNullOrEmpty(b64)) return;
        try
        {
            var pcm = Convert.FromBase64String(b64);
            _manualWhisperBuffer.AddRange(pcm);
        }
        catch { }
    }

    // --- PHASE MANAGEMENT ---

    private void UpdatePhaseLogic()
    {
            phaseTimer += Time.deltaTime;
            if (phaseConfigs == null) return;
            
            PhaseConfig currentConfig = phaseConfigs.Find(p => p.phase == currentPhase);
            
            if (currentConfig != null && phaseTimer >= currentConfig.durationSeconds)
            {
                AdvancePhase();
            }
    }

    private void AdvancePhase()
    {
        if (phaseConfigs == null) return;

        int checkIndex = phaseConfigs.FindIndex(p => p.phase == currentPhase);
        if (checkIndex == -1)
        {
            Debug.LogError($"[MedicalExamManager] AdvancePhase: currentPhase '{currentPhase}' not found in phaseConfigs — aborting phase advance to prevent premature exam end.");
            return;
        }
        if (checkIndex != -1 && checkIndex < phaseConfigs.Count - 1)
        {
            var previousPhase = currentPhase;
            currentPhase = phaseConfigs[checkIndex + 1].phase;
            phaseTimer = 0f;
            Debug.Log($"[MedicalExamManager] Advancing to phase: {currentPhase} (from {previousPhase})");
            
            // Log phase change to evidence
            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("PHASE_TRANSITION", $"{previousPhase} -> {currentPhase} at {GetElapsedExamTimeFormatted()}");
            }
            catch { }

            // Update Realtime Session with new instructions
            if (useOpenAIRealtime && realtimeClient != null)
            {
                PhaseConfig newConfig = phaseConfigs[checkIndex + 1];

                string instructionText = ResolvePhaseInstructionText(newConfig);
                string baseInstructions = !string.IsNullOrEmpty(_baseSystemInstructions) ? _baseSystemInstructions : "";
                string elapsedTime = GetElapsedExamTimeFormatted();
                string phaseInstructions = $"{baseInstructions}\n\nAKTUELLE PHASE: {newConfig.phase}\nANWEISUNG: {instructionText}";
                phaseInstructions = EnsureRoleIdentityInInstructions(phaseInstructions);
                
                // Update pending so if we reconnect we use the latest
                _pendingRealtimeInstructions = phaseInstructions;

                // 1) Update the system prompt (session.update) — get voice from scenario
                string phaseVoice = GetRealtimeVoiceForRole();
                realtimeClient.SendSessionUpdate(phaseInstructions, GetRealtimeTools(newConfig.useTools), phaseVoice);

                // 2) Send an explicit text message telling the AI the phase changed,
                //    so it actually acts on it (session.update alone is silent).
                string transitionMessage = BuildPhaseTransitionMessage(previousPhase, newConfig.phase, elapsedTime, instructionText);
                if (realtimeClient != null) realtimeClient.SendText(transitionMessage);

                Debug.Log($"[MedicalExamManager] Phase transition message sent to AI: {newConfig.phase} at {elapsedTime}");
            }
        }
        else
        {
            Debug.Log("[MedicalExamManager] All phases complete — initiating exam end.");
            try
            {
                if (_evidenceLogger != null)
                    _evidenceLogger.AppendSection("PHASE_TRANSITION", $"All phases complete at {GetElapsedExamTimeFormatted()}");
            }
            catch { }

            TriggerExamCompletion();
        }
    }

    /// <summary>
    /// Called when all phases are done (either by AI calling finish_phase on the last phase,
    /// or by the phase timer expiring on the last phase). Sends the AI a closing message,
    /// waits for it to finish speaking, then fires InterruptAndEvaluate automatically.
    /// </summary>
    private void TriggerExamCompletion()
    {
        if (_examCompletionTriggered || _evaluationRequested) return;
        _examCompletionTriggered = true;

        Debug.Log("[MedicalExamManager] TriggerExamCompletion — sending AI goodbye message, then auto-evaluating.");

        if (useOpenAIRealtime && realtimeClient != null)
        {
            string closingMsg = selectedRole == RoleType.DoctorToDoctor
                ? "[SYSTEM: Alle Prüfungsphasen sind abgeschlossen. Beende das Gespräch jetzt mit EINEM kurzen, professionellen Abschlusssatz (z.B. 'Vielen Dank, das war die Prüfung.'). Sage danach nichts mehr — die Bewertung startet automatisch.]"
                : "[SYSTEM: Die Untersuchung ist abgeschlossen. Verabschiede dich mit EINEM kurzen Satz als Patientin (z.B. 'Danke, auf Wiedersehen.'). Sage danach nichts mehr — die Bewertung startet automatisch.]";
            realtimeClient.SendText(closingMsg);
        }

        StartCoroutine(WaitForAISpeechThenEvaluate());
    }

    private IEnumerator WaitForAISpeechThenEvaluate()
    {
        // Give the AI time to begin generating the closing sentence
        yield return new WaitForSeconds(2f);

        // Wait until the AI finishes speaking, capped at 15 s total
        float waited = 0f;
        while (_realtimeAgentSpeaking && waited < 13f)
        {
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }

        // Short pause after AI goes silent so the user hears the full goodbye
        yield return new WaitForSeconds(1.5f);

        if (!_evaluationRequested)
        {
            Debug.Log("[MedicalExamManager] Auto-triggering evaluation after all phases complete.");
            InterruptAndEvaluate();
        }
    }

    /// <summary>
    /// Build a short, direct message to the AI telling it to transition to the next phase.
    /// This is sent as a user text message so the AI must respond and act on it.
    /// </summary>
    private string BuildPhaseTransitionMessage(ExamPhase fromPhase, ExamPhase toPhase, string elapsedTime, string phaseInstruction)
    {
        // Use actual patient name from case data; fall back to generic if not available.
        string patientLabel = string.IsNullOrWhiteSpace(_selectedCaseTitle)
            ? "die Patientin"
            : _selectedCaseTitle.Split('-')[0].Trim(); // e.g. "Frau Huber" from "Frau Huber - Akute..."
        string roleContext = selectedRole == RoleType.DoctorToDoctor
            ? "Du bist der FSP-Prüfer (Oberarzt)."
            : $"Du bist {patientLabel} (die Patientin). Bleib vollständig in Rolle.";

        switch (toPhase)
        {
            case ExamPhase.Presentation:
                return $"[SYSTEM: Phasenuebergang. {roleContext} Die BEGRUESSUNGS-Phase ist beendet. Die PRESENTATION-Phase beginnt jetzt. Lassen Sie den Kandidaten den Fall strukturiert vorstellen und unterbrechen Sie nur, wenn er komplett vom Thema abweicht.]";

            case ExamPhase.Discussion:
                return $"[SYSTEM: Phasenuebergang. {roleContext} Die PRESENTATION-Phase ist beendet. Die DISCUSSION-Phase beginnt jetzt. Stelle EINE gezielte Rueckfrage auf einmal zu Befunden, DD, Priorisierung oder klinischem Vorgehen. Halte jede Frage kurz (max. 1-2 Saetze). Warte auf die vollstaendige Antwort, bevor du die naechste Frage stellst.]";

            case ExamPhase.Terms:
            {
                string caseTerms = GetScenarioTermsString();
                string termsList = string.IsNullOrWhiteSpace(caseTerms)
                    ? "NSTEMI, Troponin, Differentialdiagnose, Antikoagulation, Echokardiographie"
                    : caseTerms;
                return $"[SYSTEM: Phasenuebergang. {roleContext} Die DISCUSSION-Phase ist beendet. Die FACHBEGRIFFE-Phase beginnt JETZT. " +
                       $"Pruefe GENAU 5 Fachbegriffe einzeln nacheinander aus dieser Liste: {termsList}. " +
                       $"Nenne einen Begriff, warte auf die Erklaerung des Kandidaten, gib kurzes Feedback (1 Satz), dann naechster Begriff. " +
                       $"Nachdem du alle 5 Begriffe geprueft hast: Sage einen kurzen Abschlusssatz (z.B. 'Vielen Dank, das war die Pruefung.') und rufe dann finish_phase auf. " +
                       $"Das Aufrufen von finish_phase beendet die Pruefung und startet automatisch die Bewertung. " +
                       $"Beginne SOFORT mit dem ersten Begriff.]";
            }

            case ExamPhase.Anamnesis:
                if (selectedRole == RoleType.DoctorToDoctor)
                    return $"[SYSTEM: Phasenübergang. {roleContext} Die BEGRÜßUNGS-Phase ist beendet. Der Kandidat soll jetzt seinen Patientenfall vorstellen. Höre aktiv der Fallvorstellung zu, ohne zu unterbrechen. Falls der Kandidat noch nicht begonnen hat, bitte ihn höflich zu beginnen.]";
                else
                    return $"[SYSTEM: Phasenübergang. {roleContext} Die BEGRÜßUNGS-Phase ist beendet. Die ANAMNESE-Phase beginnt jetzt. Der Arzt wird dir jetzt detaillierte Fragen zu deinen Symptomen, Vorgeschichte und Medikation stellen. Antworte nur auf Nachfrage. Gib keine Informationen von dir aus preis.]";

            case ExamPhase.Summary:
                if (selectedRole == RoleType.DoctorToDoctor)
                    return $"[SYSTEM: Phasenübergang. {roleContext} Die ANAMNESE/FALLVORSTELLUNGS-Phase ist beendet. Fordere den Kandidaten jetzt zu einer kurzen strukturierten Zusammenfassung auf: Hauptproblem, wichtigste Befunde, Verdachtsdiagnose, nächste Schritte. Stelle danach 1-2 kurze Rückfragen zur Klärung. Wenn die Zusammenfassung abgeschlossen ist, beende das Gespräch sachlich.]";
                else
                    return $"[SYSTEM: Phasenübergang. {roleContext} Die ANAMNESE-Phase endet. Der Arzt wird jetzt das Gespräch zusammenfassen. Bestätige zutreffende Aussagen knapp, korrigiere falsche behutsam. Stelle höchstens eine kurze Abschlussfrage wie 'Was passiert als Nächstes?'. " +
                           $"Sobald die Zusammenfassung und alle Abschlussfragen abgeschlossen sind, verabschiede dich mit einem Satz und rufe dann finish_phase auf. " +
                           $"Das Aufrufen von finish_phase beendet die Pruefung und startet automatisch die Bewertung.]";

            default:
                return $"[SYSTEM: Phasenübergang. Wechsel zur Phase: {toPhase}. {roleContext} {phaseInstruction}]";
        }
    }

    /// <summary>
    /// Returns the elapsed exam time as a formatted string like "05:30" or "12 min 30 sec".
    /// </summary>
    private string GetElapsedExamTimeFormatted()
    {
        if (!_examTimerStarted) return "00:00";
        float elapsed = Time.time - _examStartTime;
        int minutes = Mathf.FloorToInt(elapsed / 60f);
        int seconds = Mathf.FloorToInt(elapsed % 60f);
        return $"{minutes:00}:{seconds:00}";
    }

    private object GetRealtimeTools(bool enableTools)
    {
        if (!enableTools) return null;
        
            var toolDesc = RemotePromptManager.Get("realtime.tool.log_mistake.description", "Einen Fehler des Benutzers während der medizinischen Prüfung protokollieren.");
            var paramDesc = RemotePromptManager.Get("realtime.tool.log_mistake.param.description", "Detaillierte Beschreibung des Fehlers.");
            var severityDesc = RemotePromptManager.Get("realtime.tool.log_mistake.param.severity", "Der Schweregrad des Fehlers.");

            var finishDesc = RemotePromptManager.Get("realtime.tool.finish_phase.description",
                "Rufe diese Funktion auf, wenn die aktuelle Phase der Prüfung eindeutig abgeschlossen ist. " +
                "Beispiele: Begrüßung abgeschlossen → Präsentation kann beginnen; " +
                "Fallvorstellung beendet → Diskussion kann beginnen; " +
                "alle Diskussionsfragen gestellt und beantwortet → Fachbegriffe-Phase; " +
                "alle Fachbegriffe (D2D) oder die Zusammenfassung (D2P) vollständig abgeschlossen → Prüfung beenden. " +
                "WICHTIG: In der letzten Phase (Fachbegriffe bei D2D / Zusammenfassung bei D2P) beendet das Aufrufen dieser Funktion die gesamte Prüfung und startet automatisch die Bewertung. " +
                "Rufe diese Funktion NICHT bei kurzen Pausen auf — nur wenn das Ziel der Phase tatsächlich erreicht wurde.");

            object[] tools = new object[] {
                new
                {
                    type = "function",
                    name = "log_mistake",
                    description = toolDesc,
                    parameters = new
                    {
                        type = "object",
                        properties = new
                        {
                            description = new { type = "string", description = paramDesc },
                            severity = new { type = "string", @enum = new[] { "low", "medium", "critical" }, description = severityDesc }
                        },
                        required = new[] { "description", "severity" }
                    }
                },
                new
                {
                    type = "function",
                    name = "finish_phase",
                    description = finishDesc,
                    parameters = new
                    {
                        type = "object",
                        properties = new { }, // No params
                        required = new string[] { }
                    }
                }
            };
        return tools;
    }
    }
}
