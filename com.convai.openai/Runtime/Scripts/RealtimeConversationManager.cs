using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace OpenAI
{
    public class RealtimeConversationManager : MonoBehaviour
    {
        // NOTE: This must be a realtime-capable model. If this is wrong, you may see
        // transcription failures and/or no audio output.
        private const string DefaultRealtimeModel = "gpt-realtime-2";

        #region Inspector

        [Header("Model")]
        [Tooltip("Realtime model name used as the ?model= query param.")]
        [SerializeField] private string model = DefaultRealtimeModel;

        [Header("Transcription")]
        [Tooltip("Realtime input transcription model.")]
        [SerializeField] private string inputTranscriptionModel = "gpt-4o-mini-transcribe";

        [Header("VAD (Mic Activation / Noise Filtering)")]
        [Tooltip("Higher = needs louder voice to count as speech. Helps avoid triggering from background noise.")]
        [Range(0f, 1f)]
        [SerializeField] private float serverVadThreshold = 0.6f;

        [Tooltip("How long (ms) of silence before the server emits speech_stopped. Larger = fewer false stops from breathing/pauses.")]
        [SerializeField] private int serverVadSilenceDurationMs = 700;

        [Tooltip("How much audio (ms) to include before detected speech. Small value helps keep first syllables.")]
        [SerializeField] private int serverVadPrefixPaddingMs = 300;

        [Tooltip("Extra delay (ms) after speech_stopped before we commit+create a response. If you speak again during this delay, it won't commit yet.")]
        [SerializeField] private int speechStopCommitDelayMs = 1000;

        private enum Voice { Alloy, Verse, Ash, Ballad, Coral, Sage, Custom }

        [Header("Voice")]
        [SerializeField] private Voice voice = Voice.Alloy;
        [SerializeField] private string customVoice = "";

        [Header("System prompt")]
        [TextArea(2, 8)]
        [SerializeField] private string systemPrompt = 
            "You are participating in a medical oral exam conversation.\n\n" +
            "PRIMARY ROLE:\n" +
            "- Act as a patient or doctor naturally (based on exam context).\n" +
            "- Keep conversation flowing naturally.\n\n" +
            "SECONDARY HIDDEN ROLE:\n" +
            "- Observe the student's spoken performance in real time.\n" +
            "- Track: pronunciation quality of medical terms (word-level), hesitations/pauses, filler words, sentence clarity, light terminology misuse.\n" +
            "- Capture mispronounced terms with short notes so the evaluation API can grade pronunciation harshly.\n" +
            "- Do NOT interrupt or correct during the exam.\n" +
            "- Do NOT evaluate medical correctness, completeness, or safety - that's for the final evaluation model.\n\n" +
            "IMPORTANT: Continue the conversation naturally. Do not reveal you are evaluating speech.";

        [Header("Initial Greeting")]
        [SerializeField] private bool speakFirstOnStart = false;
        [TextArea(1, 4)]
        [SerializeField] private string initialGreeting = "Guten Tag. Wir starten jetzt mit der medizinischen Prufungsvorbereitung. Wie kann ich Ihnen helfen?";

        [Header("Startup")]
        [SerializeField] private bool startOnAwake = false; // disabled by default to avoid auto-starting realtime

        [Header("Dependencies")]
        [SerializeField] private OpenAIConfig       config;
        [SerializeField] private MicrophoneStreamer micStreamer;
        [SerializeField] private PcmAudioPlayer     audioPlayer;

        [Header("Thinking SFX")]
        [SerializeField] private AudioSource thinkingAudioSource;
        [SerializeField] private AudioClip thinkingLoopClip;
        [Range(0f, 1f)]
        [SerializeField] private float thinkingLoopVolume = 0.25f;

        [Header("Unity Events")]
        public UnityEvent<string> onAgentTranscript;
        public UnityEvent<string> onUserTranscript;
        public UnityEvent<bool>   onUserSpeaking;
        public UnityEvent<bool>   onAgentSpeaking;
        public UnityEvent         onSessionReady;

        #endregion

        #region Internals

        private ClientWebSocket _ws;
        private bool      _sessionReady;
        private bool      _microphoneEnabled = true;
        private bool      _upstreamAudioEnabled = true;
        private bool      _agentCurrentlySpeaking;
        private bool      _responseActive;
        private bool      _responseCreateSent;
        private double    _lastCreateSentTime;
        private const double MinCreateIntervalSec = 0.2;
        private int _receivedAudioDeltas;
        private int _receivedAudioBytes;

        private int _speechStopGeneration;

        // Debug: log only the first few verbose events per session
        private int _verboseResponseLogs;

        // Debug counters
        private int _sentMicChunks = 0;

        // === Background capture for post-hoc transcription (user mic only) ===
        public const int UserMicSampleRateHz = 16000;

        // Realtime assistant voice audio is typically PCM16 mono 24kHz.
        public const int AssistantAudioSampleRateHz = 24000;

        private byte[] _userMicRing;
        private int _userMicRingWrite;
        private int _userMicRingCount;
        private bool _userMicCaptureEnabled;
        private readonly object _userMicRingLock = new object();

        private byte[] _assistantAudioRing;
        private int _assistantAudioRingWrite;
        private int _assistantAudioRingCount;
        private bool _assistantAudioCaptureEnabled;
        private readonly object _assistantAudioRingLock = new object();

        private readonly ToolCallState _toolCalls = new ();
        private int _thinkingDepth;
        
        // Conversation tracking for evaluation
        private readonly List<string> _conversationQuestions = new ();
        private readonly List<string> _conversationAnswers = new ();
        private string _currentQuestion = "";
        private string _currentAnswer = "";
        private bool _isCollectingAnswer = false;

        #endregion

        #region Unity lifecycle

        private void Awake()
        {
            // User (student) always speaks first. Serialized scene value might be stale (true from old default).
            speakFirstOnStart = false;
        }

        private void Start()
        {
            if (startOnAwake) StartAgent();
        }

        /// <summary>
        /// Enable/disable local microphone capture + streaming to the realtime session.
        /// Useful for pausing input while you run post-hoc evaluation.
        /// </summary>
        public void SetMicrophoneEnabled(bool enabled)
        {
            _microphoneEnabled = enabled;

            if (micStreamer == null) return;

            if (enabled)
            {
                // Only start streaming if we're connected; otherwise StartAgent will start it on connect.
                if (_ws != null)
                    micStreamer.StartStreaming();
            }
            else
            {
                micStreamer.StopStreaming();
            }
        }

        /// <summary>
        /// Enable/disable sending mic audio upstream to the realtime session.
        /// When disabled, the local mic can still run (if enabled elsewhere), but no audio is appended to the server.
        /// This is useful during evaluation so user speech won't interrupt the agent's feedback.
        /// </summary>
        public void SetUpstreamAudioEnabled(bool enabled)
        {
            _upstreamAudioEnabled = enabled;
        }

        private void Update()
        {
            
            // Check for evaluation trigger (T key)
            if (Input.GetKeyDown(KeyCode.T))
            {
                TriggerEvaluation();
            }
        }

        private async void OnDisable()         => await Shutdown();
        private async void OnApplicationQuit() => await Shutdown();

        #endregion

        #region Public API

        /// <summary>
        /// Sets the assistant voice preset and immediately applies it to the active session (via session.update) if connected.
        /// Valid presets: "alloy", "verse", "custom".
        /// </summary>
        public void ApplyVoicePreset(string preset, string custom = "")
        {
            if (string.IsNullOrWhiteSpace(preset))
                preset = "alloy";

            string p = preset.Trim().ToLowerInvariant();
            switch (p)
            {
                case "alloy":
                    voice = Voice.Alloy;
                    break;
                case "verse":
                    voice = Voice.Verse;
                    break;
                case "custom":
                    voice = Voice.Custom;
                    customVoice = custom ?? "";
                    break;
                default:
                    Debug.LogWarning($"[OpenAI] Unknown voice preset '{preset}'. Using Alloy.");
                    voice = Voice.Alloy;
                    break;
            }

            // If already connected, push the change immediately.
            if (_ws != null)
                SendSessionUpdate();
        }
        
        /// <summary>
        /// Call this from any GameObject to start talking to the agent
        /// Normally this is called on start if `startOnAwake` is set to true
        /// </summary>
        public async void StartAgent()
        {
            try
            {
                // Validate required dependencies
                if (config == null)
                {
                    Debug.LogError("[OpenAI] OpenAIConfig is not assigned! Please assign it in the Inspector.");
                    enabled = false;
                    return;
                }
                
                if (string.IsNullOrWhiteSpace(config.apiKey))
                {
                    Debug.LogError("[OpenAI] API Key is not set! Please set your OpenAI API key in the OpenAIConfig asset.");
                    enabled = false;
                    return;
                }
                
                if (micStreamer == null)
                {
                    Debug.LogError("[OpenAI] MicrophoneStreamer is not assigned! Please assign it in the Inspector.");
                    enabled = false;
                    return;
                }
                
                if (audioPlayer == null)
                {
                    Debug.LogError("[OpenAI] PcmAudioPlayer is not assigned! Please assign it in the Inspector.");
                    enabled = false;
                    return;
                }

                if (string.IsNullOrWhiteSpace(model))
                {
                    model = DefaultRealtimeModel;
                    Debug.LogWarning($"[OpenAI] Realtime model was empty. Defaulting to '{model}'.");
                }

                Debug.Log("[OpenAI] Starting realtime agent...");
                await InitializeWebSocketAsync();
                micStreamer.OnAudioChunk += SendMicChunk;
                Debug.Log("[OpenAI] WS connected.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI] Startup failed: {ex}");
                enabled = false;
            }
        }

        /// <summary>
        /// Stops mic streaming, cancels any active response, and closes the websocket.
        /// Safe to call even if not connected.
        /// </summary>
        public async void StopAgent()
        {
            await Shutdown();
        }

        /// <summary>
        /// Force stop agent
        /// Used it to cancel any interaction and close the socket immediately
        /// </summary>
        public void InterruptNow()
        {
            TryCancelAssistantResponse();
        }

        /// <summary>
        /// Update the system prompt during an active session and immediately send a session.update.
        /// Useful for switching the realtime agent into an explicit evaluator mode at the end of an exam.
        /// </summary>
        public void ApplySystemPrompt(string newSystemPrompt)
        {
            systemPrompt = newSystemPrompt ?? string.Empty;

            if (_ws == null)
            {
                Debug.LogWarning("[OpenAI] Cannot apply system prompt - not connected yet.");
                return;
            }

            SendSessionUpdate();
        }
        
        /// <summary>
        /// Send a text message to the AI as if the user said it (programmatically)
        /// </summary>
        public async void SendTextMessage(string text)
        {
            if (_ws == null)
            {
                Debug.LogWarning("[OpenAI] Cannot send text - not connected!");
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[OpenAI] Cannot send empty text message!");
                return;
            }

            Debug.Log($"[OpenAI] Sending text message: {text}");

            var textMessage = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "input_text",
                            ["text"] = text
                        }
                    }
                }
            };

            await SendWebSocketMessage(textMessage.ToString(Formatting.None));
            // Trigger AI response
            var create = BuildResponseCreate();
            await SendWebSocketMessage(create.ToString(Formatting.None));
        }

        // If we send a text message while a response is active, some servers will reject response.create.
        // This queue lets us keep the same realtime session without interrupting: we create the user message now,
        // and trigger response.create as soon as the current response finishes.
        private bool _queuedCreateAfterResponse = false;

        public async void SendTextMessageQueued(string text)
        {
            if (_ws == null)
            {
                Debug.LogWarning("[OpenAI] Cannot send text - not connected!");
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[OpenAI] Cannot send empty text message!");
                return;
            }

            var textMessage = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "input_text",
                            ["text"] = text
                        }
                    }
                }
            };

            await SendWebSocketMessage(textMessage.ToString(Formatting.None));

            if (_responseActive || _agentCurrentlySpeaking)
            {
                _queuedCreateAfterResponse = true;
                Debug.Log("[OpenAI] Queued response.create (waiting for current response to finish)");
                return;
            }

            var create = BuildResponseCreate();
            await SendWebSocketMessage(create.ToString(Formatting.None));
        }
        
        /// <summary>
        /// Trigger evaluation of the conversation (call this or press T key)
        /// </summary>
        public void TriggerEvaluation()
        {
            // Integration: if a MedicalExamManager is present in the scene, route evaluation through it.
            // This ensures we capture the realtime draft evaluation transcript and include it in the final payload.
            try
            {
                Type mgrType = null;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length && mgrType == null; i++)
                {
                    try { mgrType = assemblies[i].GetType("MedicalExam.MedicalExamManager"); }
                    catch { }
                }

                if (mgrType != null)
                {
                    // Avoid generic FindFirstObjectByType<T>() because this package shouldn't reference user assemblies.
#if UNITY_2023_1_OR_NEWER || UNITY_2022_2_OR_NEWER
                    var mgrObj = UnityEngine.Object.FindFirstObjectByType(mgrType);
#else
                    #pragma warning disable CS0618
                    var mgrObj = UnityEngine.Object.FindObjectOfType(mgrType);
                    #pragma warning restore CS0618
#endif
                    if (mgrObj != null)
                    {
                        var mi = mgrType.GetMethod("RequestEvaluation", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (mi != null)
                        {
                            Debug.Log("[OpenAI] Routing TriggerEvaluation() to MedicalExamManager.RequestEvaluation() (reflection)");
                            mi.Invoke(mgrObj, null);
                            return;
                        }
                    }
                }
            }
            catch { }

            if (_conversationQuestions.Count == 0)
            {
                Debug.LogWarning("[OpenAI] No conversation to evaluate yet!");
                return;
            }
            
            // Save current answer if collecting
            if (_isCollectingAnswer && !string.IsNullOrWhiteSpace(_currentAnswer))
            {
                _conversationAnswers.Add(_currentAnswer.Trim());
                _currentAnswer = "";
                _isCollectingAnswer = false;
            }
            
            SendEvaluationRequest();
        }

        #endregion

        #region Shutdown

        private async Task Shutdown()
        {
            try
            {
                micStreamer.OnAudioChunk -= SendMicChunk;
                micStreamer.StopStreaming();
                audioPlayer.StopImmediately();
                StopThinkingSfx(true);

                if (_ws != null)
                {
                    if (_ws.State == WebSocketState.Open)
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
                    _ws?.Dispose();
                    _ws = null;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI] Shutdown error: {ex}");
            }
        }

        #endregion

        #region WebSocket receive

        private void OnSocketMessage(byte[] raw)
        {
            var text = Encoding.UTF8.GetString(raw);

            BaseEvent baseEvt;
            try { baseEvt = JsonConvert.DeserializeObject<BaseEvent>(text); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] Failed to parse event: {ex}\n{text}");
                return;
            }

            // Some servers/clients can include trailing whitespace/newlines in the type field.
            // Trim so we don't miss known events and fall into the default handler.
            var eventType = baseEvt?.Type?.Trim();

            switch (eventType)
            {
                case "session.created":
                    HandleSessionCreated();
                    break;

                case "session.updated":
                    HandleSessionUpdated();
                    break;

                case "input_audio_buffer.speech_started":
                    HandleSpeechStarted();
                    break;

                case "input_audio_buffer.speech_stopped":
                    HandleSpeechStopped();
                    break;

                case "input_audio_buffer.committed":
                    HandleInputCommitted();
                    break;

                case "conversation.item.input_audio_transcription.delta":
                    HandleInputTranscriptDelta(text);
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    HandleInputTranscriptCompleted(text);
                    break;

                case "conversation.item.created":
                    HandleConversationItemCreated();
                    break;

                case "response.audio.delta":
                case "response.output_audio.delta":
                    HandleResponseAudioDelta(text);
                    break;

                case "response.audio.done":
                case "response.output_audio.done":
                    HandleResponseAudioDone();
                    break;

                case "response.audio_transcript.delta":
                case "response.output_audio_transcript.delta":
                    HandleResponseTranscriptDelta(text);
                    break;

                // Some realtime models emit explicit text-delta events.
                case "response.output_text.delta":
                case "response.text.delta":
                    HandleResponseTextDelta(text);
                    break;

                case "response.audio_transcript.done":
                case "response.content_part.added":
                case "response.content_part.done":
                    HandleResponseContentEvent(eventType, text);
                    break;

                case "response.created":
                    HandleResponseCreated();
                    break;

                case "response.done":
                case "response.cancelled":
                    HandleResponseFinished(text, eventType);
                    break;

                case "response.function_call_arguments.delta":
                    HandleFunctionArgsDelta(text);
                    break;

                case "response.function_call_arguments.done":
                    HandleNoop();
                    break;

                case "response.output_item.added":
                    HandleOutputItemAdded(text);
                    break;

                case "response.output_item.done":
                    HandleOutputItemDone(text);
                    break;

                case "rate_limits.updated":
                    HandleNoop();
                    break;

                case "error":
                    HandleError(text);
                    break;

                case "conversation.item.input_audio_transcription.failed":
                    try
                    {
                        var jo = JObject.Parse(text);
                        var err = jo["error"] as JObject;
                        var code = err?["code"]?.ToString() ?? "(no code)";
                        var msg  = err?["message"]?.ToString() ?? "(no message)";
                        Debug.LogWarning($"[OpenAI] input_audio_transcription.failed code={code} msg={msg}");
                    }
                    catch
                    {
                        Debug.LogWarning($"[OpenAI] Transcription failed for an audio item. Payload: {text}");
                    }
                    break;

                default:
                    Debug.Log($"[OpenAI] Unhandled event type: {eventType}");
                    break;
            }
        }

        #endregion

        #region WebSocket send

        private async void SendMicChunk(string b64)
        {
            if (!_sessionReady || _ws == null || _ws.State != WebSocketState.Open) return;

            // Optional background capture of user audio for later Whisper transcription.
            if (_userMicCaptureEnabled && !string.IsNullOrEmpty(b64))
            {
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    AppendUserMicBytesToRing(bytes);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OpenAI] Failed to decode mic chunk for background capture: {ex.Message}");
                }
            }

            // If upstream audio is muted, do not send mic audio to the realtime session.
            if (!_upstreamAudioEnabled) return;

            var payload = new Dictionary<string, object>
            {
                { "type",  "input_audio_buffer.append" },
                { "audio", b64 }
            };

            try
            {
                var json = JsonConvert.SerializeObject(payload);
                var bytes = Encoding.UTF8.GetBytes(json);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] SendMicChunk error: {ex.Message}");
            }
        }

        /// <summary>
        /// Start capturing the user's microphone PCM16 (16 kHz mono) into an in-memory ring buffer.
        /// This does NOT start/stop the microphone itself; it just records what we already stream.
        /// </summary>
        public void BeginBackgroundUserAudioCapture(int maxSeconds = 180)
        {
            maxSeconds = Mathf.Clamp(maxSeconds, 1, 600);
            int capacityBytes = UserMicSampleRateHz * maxSeconds * 2; // 16-bit mono

            lock (_userMicRingLock)
            {
                _userMicRing = new byte[capacityBytes];
                _userMicRingWrite = 0;
                _userMicRingCount = 0;
                _userMicCaptureEnabled = true;
            }

            Debug.Log($"[OpenAI] Background user-audio capture enabled ({maxSeconds}s, cap={capacityBytes} bytes)." );
        }

        /// <summary>
        /// Stop capturing user microphone audio into the ring buffer.
        /// Captured data remains available via GetBackgroundUserAudioPcm16().
        /// </summary>
        public void StopBackgroundUserAudioCapture()
        {
            lock (_userMicRingLock)
            {
                _userMicCaptureEnabled = false;
            }
            Debug.Log("[OpenAI] Background user-audio capture disabled.");
        }

        /// <summary>
        /// Returns a copy of the captured user mic PCM16 bytes (chronological). May be empty.
        /// </summary>
        public byte[] GetBackgroundUserAudioPcm16()
        {
            lock (_userMicRingLock)
            {
                if (_userMicRing == null || _userMicRingCount <= 0) return Array.Empty<byte>();

                int cap = _userMicRing.Length;
                var output = new byte[_userMicRingCount];

                int start = (_userMicRingWrite - _userMicRingCount + cap) % cap;
                if (start + _userMicRingCount <= cap)
                {
                    Buffer.BlockCopy(_userMicRing, start, output, 0, _userMicRingCount);
                }
                else
                {
                    int first = cap - start;
                    Buffer.BlockCopy(_userMicRing, start, output, 0, first);
                    Buffer.BlockCopy(_userMicRing, 0, output, first, _userMicRingCount - first);
                }

                return output;
            }
        }

        private void AppendUserMicBytesToRing(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            lock (_userMicRingLock)
            {
                if (_userMicRing == null || _userMicRing.Length == 0) return;

                int cap = _userMicRing.Length;
                for (int i = 0; i < bytes.Length; i++)
                {
                    _userMicRing[_userMicRingWrite] = bytes[i];
                    _userMicRingWrite++;
                    if (_userMicRingWrite >= cap) _userMicRingWrite = 0;

                    if (_userMicRingCount < cap) _userMicRingCount++;
                }
            }
        }


          private void SendSessionUpdate()
        {
            const string germanOnlyDirective =
                "WICHTIG: Antworte ausschliesslich auf Deutsch. Verwende keine englischen Antworten. ";

            // Flat field schema (same as v1), only renamed keys for gpt-realtime-2
            var session = new JObject
            {
                ["type"] = "realtime",
                ["output_modalities"] = new JArray("audio"),
                ["instructions"] = germanOnlyDirective + (systemPrompt ?? string.Empty),
                ["voice"] = ResolveVoiceString(),
                ["input_audio_format"] = "pcm16",
                ["output_audio_format"] = "pcm16",
                ["input_audio_transcription"] = new JObject { ["model"] = inputTranscriptionModel ?? "gpt-4o-mini-transcribe" },
                ["turn_detection"] = new JObject
                {
                    ["type"] = "semantic_vad",
                    ["eagerness"] = "low",
                    ["create_response"] = false,
                    ["interrupt_response"] = true
                },
                ["tool_choice"] = "auto",
                ["tools"] = AgentToolRegistry.GetToolsSpec()
            };

            var upd = new JObject { ["type"] = "session.update", ["session"] = session };
            _ = SendWebSocketMessage(upd.ToString(Formatting.None));
        }

        private static JObject BuildResponseCreate()
        {
            // Keep this minimal like the original implementation.
            // Audio/text modalities are still requested via session.update.
            return new JObject
            {
                ["type"] = "response.create"
            };
        }

        private void SendResponseCreate()
        {
            if (_ws == null) return;
            if (_responseActive || _responseCreateSent) return;

            var now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastCreateSentTime < MinCreateIntervalSec) return;

            _lastCreateSentTime = now;
            _responseCreateSent = true;

            var commit = new JObject { ["type"] = "input_audio_buffer.commit" };
            _ = SendWebSocketMessage(commit.ToString(Formatting.None));

            var create = BuildResponseCreate();
            _ = SendWebSocketMessage(create.ToString(Formatting.None));
        }

        private void TryCancelAssistantResponse()
        {
            if (_ws == null) return;

            var cancel = new JObject { ["type"] = "response.cancel" };
            _ = SendWebSocketMessage(cancel.ToString(Formatting.None));

            audioPlayer.StopImmediately();
            StopThinkingSfx(true);

            if (_agentCurrentlySpeaking)
            {
                _agentCurrentlySpeaking = false;
                onAgentSpeaking?.Invoke(false);
            }
            _responseActive = false;
            _responseCreateSent = false;
        }

        private async void SendInitialGreeting()
        {
            if (_ws == null) return;
            if (string.IsNullOrWhiteSpace(initialGreeting)) return;

            await Task.Delay(500); // Wait a bit for session to be fully ready

            var textMessage = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "input_text",
                            ["text"] = initialGreeting
                        }
                    }
                }
            };

            await SendWebSocketMessage(textMessage.ToString(Formatting.None));

            // Trigger AI response
            var create = BuildResponseCreate();
            await SendWebSocketMessage(create.ToString(Formatting.None));

            Debug.Log($"[OpenAI] Sent initial greeting: {initialGreeting}");
        }
        
        private async void SendEvaluationRequest()
        {
            if (_ws == null)
            {
                Debug.LogWarning("[OpenAI] Cannot send evaluation - not connected!");
                return;
            }
            
            // Build evaluation text
            var evalText = new StringBuilder();
            evalText.AppendLine("Please evaluate the following conversation. Provide feedback on:");
            evalText.AppendLine("1. Understanding - How well did the user understand the questions (Score /5)");
            evalText.AppendLine("2. Speaking - Clarity and fluency of responses (Score /5)");
            evalText.AppendLine("3. Grammar - Identify specific grammar mistakes and provide corrections (Score /5)");
            evalText.AppendLine();
            evalText.AppendLine("Conversation:");
            evalText.AppendLine("==============");
            
            int count = Mathf.Max(_conversationQuestions.Count, _conversationAnswers.Count);
            for (int i = 0; i < count; i++)
            {
                if (i < _conversationQuestions.Count)
                {
                    evalText.AppendLine($"\nQuestion {i + 1}: {_conversationQuestions[i]}");
                }
                
                if (i < _conversationAnswers.Count)
                {
                    evalText.AppendLine($"Answer {i + 1}: {_conversationAnswers[i]}");
                }
            }
            
            evalText.AppendLine();
            evalText.AppendLine("Please provide your evaluation now.");
            
            Debug.Log($"[OpenAI] Requesting evaluation of {_conversationQuestions.Count} questions and {_conversationAnswers.Count} answers");
            
            // Send as text message
            var textMessage = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject
                {
                    ["type"] = "message",
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "input_text",
                            ["text"] = evalText.ToString()
                        }
                    }
                }
            };
            
            await SendWebSocketMessage(textMessage.ToString(Formatting.None));
            
            // Trigger AI response
            var create = BuildResponseCreate();
            await SendWebSocketMessage(create.ToString(Formatting.None));
        }

        #endregion

        #region Tool execution

        private async Task ExecuteToolCallIfReady(string callId)
        {
            try
            {
                if (!_toolCalls.TryGetName(callId, out var toolName))
                {
                    Debug.LogWarning($"[OpenAI] function_call done without known name (call_id={callId})");
                    StopThinkingSfx();
                    return;
                }

                var argsJson = _toolCalls.GetArgsJson(callId) ?? "{}";

                if (!AgentToolRegistry.TryGetHandler(toolName, out var handler))
                {
                    Debug.LogWarning($"[OpenAI] Tool not registered: {toolName}");
                    await SendToolOutputAndContinue(callId, "");
                    return;
                }

                JObject args;
                try { args = string.IsNullOrWhiteSpace(argsJson) ? new JObject() : JObject.Parse(argsJson); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[OpenAI] Tool args parse failed for {toolName}: {ex.Message}\n{argsJson}");
                    args = new JObject();
                }

                object resultAny = await handler(args);

                string outputStr;
                if (resultAny == null) outputStr = string.Empty;
                else if (resultAny is string s) outputStr = s;
                else if (resultAny is JToken jt) outputStr = jt.ToString(Formatting.None);
                else outputStr = JsonConvert.SerializeObject(resultAny, Formatting.None);

                await SendToolOutputAndContinue(callId, outputStr);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI] Tool execution failed: {ex}");
                await SendToolOutputAndContinue(callId, JsonConvert.SerializeObject(new { ok = false, error = ex.Message }));
            }
            finally
            {
                _toolCalls.Clear(callId);
                StopThinkingSfx();
            }
        }

        private async Task SendToolOutputAndContinue(string callId, string outputStr)
        {
            if (_ws == null) return;

            var toolResultItem = new JObject
            {
                ["type"] = "conversation.item.create",
                ["item"] = new JObject
                {
                    ["type"]    = "function_call_output",
                    ["call_id"] = callId ?? string.Empty,
                    ["output"]  = outputStr ?? string.Empty
                }
            };
            await SendWebSocketMessage(toolResultItem.ToString(Formatting.None));

            var create = BuildResponseCreate();
            await SendWebSocketMessage(create.ToString(Formatting.None));
        }
        
        #endregion

        #region Helpers

        private string ResolveVoiceString()
        {
            switch (voice)
            {
                case Voice.Alloy:  return "alloy";
                case Voice.Verse:  return "verse";
                case Voice.Ash:    return "ash";
                case Voice.Ballad: return "ballad";
                case Voice.Coral:  return "coral";
                case Voice.Sage:   return "sage";
                case Voice.Custom: return string.IsNullOrWhiteSpace(customVoice) ? "alloy" : customVoice.Trim();
                default:           return "alloy";
            }
        }

        private void StartThinkingSfx()
        {
            _thinkingDepth++;
            if (thinkingAudioSource == null || thinkingLoopClip == null) return;

            if (!thinkingAudioSource.isPlaying)
            {
                thinkingAudioSource.clip   = thinkingLoopClip;
                thinkingAudioSource.loop   = true;
                thinkingAudioSource.volume = thinkingLoopVolume;
                thinkingAudioSource.Play();
            }
        }

        private void StopThinkingSfx(bool forceStopAll = false)
        {
            if (forceStopAll) _thinkingDepth = 0;
            else _thinkingDepth = Mathf.Max(0, _thinkingDepth - 1);

            if (_thinkingDepth == 0 && thinkingAudioSource != null && thinkingAudioSource.isPlaying)
            {
                thinkingAudioSource.Stop();
            }
        }

        #endregion

        #region Socket helpers

        private async Task InitializeWebSocketAsync()
        {
            var url = $"{config.realtimeConvWebsocketUrl}?model={Uri.EscapeDataString(model)}";
            _ws = new ClientWebSocket();
            _ws.Options.AddSubProtocol("realtime");
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {config.apiKey}");

            try
            {
                await _ws.ConnectAsync(new Uri(url), CancellationToken.None);
                Debug.Log($"[OpenAI] WS init url={url}");
                _ = ReceiveMessagesAsync();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI] WS connection failed: {ex}");
                throw;
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            try
            {
                byte[] buffer = new byte[4096];
                while (_ws != null && _ws.State == WebSocketState.Open)
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        OnSocketMessage(Encoding.UTF8.GetBytes(json));
                    }
                    else if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        Debug.Log("[OpenAI] WS closed.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (_ws?.State != WebSocketState.Closed)
                    Debug.LogError($"[OpenAI] WS receive error: {ex}");
            }
        }

        private async Task SendWebSocketMessage(string message)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(message);
                await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] SendWebSocketMessage error: {ex.Message}");
            }
        }

        #endregion

        #region Event handlers (parsed)

        private void HandleSessionCreated()
        {
            _sessionReady = true;
            SendSessionUpdate();

            // Notify listeners that the realtime session is ready (session.created received).
            onSessionReady?.Invoke();

            // Speak first (if requested) even if mic/transcription is failing.
            if (speakFirstOnStart)
            {
                SendInitialGreeting();
                _ = StartMicAfterDelayAsync(1000);
            }
            else
            {
                if (_microphoneEnabled)
                    micStreamer.StartStreaming();
            }
        }

        private async Task StartMicAfterDelayAsync(int delayMs)
        {
            try
            {
                await Task.Delay(Mathf.Max(0, delayMs));
                if (_microphoneEnabled)
                    micStreamer.StartStreaming();
            }
            catch { }
        }

        private void HandleSessionUpdated() { }

        private void HandleSpeechStarted()
        {
            // Invalidate any pending stop->commit debounce.
            _speechStopGeneration++;
            onUserSpeaking?.Invoke(true);
        }

        private async void HandleSpeechStopped()
        {
            onUserSpeaking?.Invoke(false);

            // Debounce commit/create so tiny pauses (breathing) don't instantly end the user's turn.
            // If speech starts again during the delay, this stop is ignored.
            var myGen = ++_speechStopGeneration;

            int delayMs = Mathf.Max(0, speechStopCommitDelayMs);
            if (delayMs > 0)
            {
                try { await Task.Delay(delayMs); } catch { return; }
            }

            if (myGen != _speechStopGeneration) return;

            SendResponseCreate();
        }

        private static void HandleInputCommitted() { }

        private void HandleInputTranscriptDelta(string json)
        {
            try
            {
                var jo = JObject.Parse(json);
                var delta = jo["delta"]?.ToString();
                // Removed duplicate invocation: deltas will accumulate into the completed transcript
                // if (!string.IsNullOrEmpty(delta)) onUserTranscript?.Invoke(delta);
            } catch {}
        }

        private void HandleInputTranscriptCompleted(string json)
        {
            try
            {
                var user = JsonConvert.DeserializeObject<InputAudioTranscriptDone>(json);
                var userText = user?.Text ?? "";
                onUserTranscript?.Invoke(userText);
                
                // Track user answer
                if (_isCollectingAnswer && !string.IsNullOrWhiteSpace(userText))
                {
                    _conversationAnswers.Add(userText.Trim());
                    _currentAnswer = "";
                    _isCollectingAnswer = false;
                }
            } catch {}
        }

        private static void HandleConversationItemCreated() { }

        private void HandleResponseAudioDelta(string json)
        {
            if (!_agentCurrentlySpeaking)
            {
                _agentCurrentlySpeaking = true;
                onAgentSpeaking?.Invoke(true);
            }
            try
            {
                var audio = JsonConvert.DeserializeObject<ResponseAudioDelta>(json);
                if (!string.IsNullOrEmpty(audio?.DeltaBase64))
                {
                    byte[] bytes = null;

                    _receivedAudioDeltas++;
                    if (_receivedAudioDeltas <= 3)
                    {
                        int bytesLen = 0;
                        try
                        {
                            bytes = Convert.FromBase64String(audio.DeltaBase64);
                            bytesLen = bytes?.Length ?? 0;
                        }
                        catch { }
                        Debug.Log($"[OpenAI] response.audio.delta #{_receivedAudioDeltas} (b64Len={audio.DeltaBase64.Length}, bytes={bytesLen})");
                    }

                    // Capture assistant audio for post-hoc evaluation (optional)
                    if (_assistantAudioCaptureEnabled)
                    {
                        try
                        {
                            if (bytes == null) bytes = Convert.FromBase64String(audio.DeltaBase64);
                            AppendAssistantAudioBytesToRing(bytes);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"[OpenAI] Failed to decode assistant audio chunk for background capture: {ex.Message}");
                        }
                    }

                    try
                    {
                        if (bytes == null) bytes = Convert.FromBase64String(audio.DeltaBase64);
                        _receivedAudioBytes += bytes?.Length ?? 0;
                    }
                    catch { }

                    audioPlayer.EnqueueBase64Audio(audio.DeltaBase64);
                }
            } catch { 
                // ignore
            }
        }

        /// <summary>
        /// Start capturing the assistant (agent) PCM16 (24 kHz mono) audio into an in-memory ring buffer.
        /// This records what we already receive from realtime (response.audio.delta).
        /// </summary>
        public void BeginBackgroundAssistantAudioCapture(int maxSeconds = 180)
        {
            maxSeconds = Mathf.Clamp(maxSeconds, 1, 600);
            int capacityBytes = AssistantAudioSampleRateHz * maxSeconds * 2; // 16-bit mono

            lock (_assistantAudioRingLock)
            {
                _assistantAudioRing = new byte[capacityBytes];
                _assistantAudioRingWrite = 0;
                _assistantAudioRingCount = 0;
                _assistantAudioCaptureEnabled = true;
            }

            Debug.Log($"[OpenAI] Background assistant-audio capture enabled ({maxSeconds}s, cap={capacityBytes} bytes)." );
        }

        /// <summary>
        /// Stop capturing assistant audio into the ring buffer.
        /// Captured data remains available via GetBackgroundAssistantAudioPcm16().
        /// </summary>
        public void StopBackgroundAssistantAudioCapture()
        {
            lock (_assistantAudioRingLock)
            {
                _assistantAudioCaptureEnabled = false;
            }
            Debug.Log("[OpenAI] Background assistant-audio capture disabled.");
        }

        /// <summary>
        /// Returns a copy of the captured assistant PCM16 bytes (chronological). May be empty.
        /// </summary>
        public byte[] GetBackgroundAssistantAudioPcm16()
        {
            lock (_assistantAudioRingLock)
            {
                if (_assistantAudioRing == null || _assistantAudioRingCount <= 0) return Array.Empty<byte>();

                int cap = _assistantAudioRing.Length;
                var output = new byte[_assistantAudioRingCount];

                int start = (_assistantAudioRingWrite - _assistantAudioRingCount + cap) % cap;
                if (start + _assistantAudioRingCount <= cap)
                {
                    Buffer.BlockCopy(_assistantAudioRing, start, output, 0, _assistantAudioRingCount);
                }
                else
                {
                    int first = cap - start;
                    Buffer.BlockCopy(_assistantAudioRing, start, output, 0, first);
                    Buffer.BlockCopy(_assistantAudioRing, 0, output, first, _assistantAudioRingCount - first);
                }

                return output;
            }
        }

        private void AppendAssistantAudioBytesToRing(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return;

            lock (_assistantAudioRingLock)
            {
                if (_assistantAudioRing == null || _assistantAudioRing.Length == 0) return;

                int cap = _assistantAudioRing.Length;
                for (int i = 0; i < bytes.Length; i++)
                {
                    _assistantAudioRing[_assistantAudioRingWrite] = bytes[i];
                    _assistantAudioRingWrite++;
                    if (_assistantAudioRingWrite >= cap) _assistantAudioRingWrite = 0;

                    if (_assistantAudioRingCount < cap) _assistantAudioRingCount++;
                }
            }
        }

        private void HandleResponseAudioDone()
        {
            if (_receivedAudioDeltas > 0)
            {
                Debug.Log($"[OpenAI] response.audio.done (deltas={_receivedAudioDeltas}, totalBytes~={_receivedAudioBytes})");
                _receivedAudioDeltas = 0;
                _receivedAudioBytes = 0;
            }

            _agentCurrentlySpeaking = false;
            onAgentSpeaking?.Invoke(false);
            
            // Save the completed question
            if (!string.IsNullOrWhiteSpace(_currentQuestion))
            {
                _conversationQuestions.Add(_currentQuestion.Trim());
                _currentQuestion = "";
            }
            
            // Start collecting user answer
            _isCollectingAnswer = true;
            _currentAnswer = "";
        }

        private void HandleResponseTranscriptDelta(string json)
        {
            try
            {
                var tx = JsonConvert.DeserializeObject<ResponseAudioTranscriptDelta>(json);
                if (!string.IsNullOrEmpty(tx?.DeltaText))
                {
                    _currentQuestion += tx.DeltaText;
                    onAgentTranscript?.Invoke(tx.DeltaText);
                }
            } catch {}
        }

        private void HandleResponseTextDelta(string json)
        {
            // Fallback: if the model is returning text events but not audio, we still want to see it.
            try
            {
                var jo = JObject.Parse(json);
                var delta = jo["delta"]?.ToString();
                if (!string.IsNullOrEmpty(delta))
                {
                    onAgentTranscript?.Invoke(delta);
                    if (_verboseResponseLogs < 6)
                    {
                        _verboseResponseLogs++;
                        var preview = delta.Length > 140 ? delta.Substring(0, 140) + "…" : delta;
                        Debug.Log($"[OpenAI] text.delta preview={preview}");
                    }
                }
            }
            catch { }
        }

        private void HandleResponseCreated()
        {
            Debug.Log("[OpenAI] response.created");
            _responseActive = true;
            _responseCreateSent = false;
        }

        private void HandleResponseFinished(string json, string eventType)
        {
            _responseActive = false;
            _responseCreateSent = false;

            if (_queuedCreateAfterResponse)
            {
                _queuedCreateAfterResponse = false;
                try
                {
                    var create = BuildResponseCreate();
                    _ = SendWebSocketMessage(create.ToString(Formatting.None));
                    Debug.Log("[OpenAI] response finished -> would send queued response.create");
                }
                catch { }
            }

            // Summarize what the server actually returned.
            try
            {
                var jo = JObject.Parse(json);
                var response = jo["response"] as JObject;
                var status = response?["status"]?.ToString();
                var output = response?["output"] as JArray;
                var outCount = output?.Count ?? 0;
                string outTypes = "";

                if (output != null && outCount > 0)
                {
                    var types = new List<string>();
                    foreach (var it in output)
                    {
                        var t = it?["type"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(t)) types.Add(t);
                    }
                    outTypes = string.Join(",", types);
                }

                if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var err = response?.SelectToken("status_details.error") as JObject;
                    var errCode = err?["code"]?.ToString();
                    var errMsg  = err?["message"]?.ToString();

                    // Fallback: some payloads use response.error
                    if (err == null)
                    {
                        err = response?.SelectToken("error") as JObject;
                        errCode ??= err?["code"]?.ToString();
                        errMsg  ??= err?["message"]?.ToString();
                    }

                    var extra = "";
                    if (!string.IsNullOrWhiteSpace(errCode) || !string.IsNullOrWhiteSpace(errMsg))
                    {
                        extra = $" errorCode={errCode ?? "(no code)"} errorMsg={errMsg ?? "(no message)"}";
                    }

                    Debug.LogWarning($"[OpenAI] {eventType} (status={status ?? "?"}, outputCount={outCount}, outputTypes={outTypes}){extra}");
                }
                else
                {
                    Debug.Log($"[OpenAI] {eventType} (status={status ?? "?"}, outputCount={outCount}, outputTypes={outTypes})");
                }
            }
            catch
            {
                Debug.Log($"[OpenAI] {eventType}");
            }
        }

        private void HandleResponseContentEvent(string eventType, string json)
        {
            // These can be spammy. Log only a few to confirm whether text is flowing.
            if (_verboseResponseLogs >= 6) return;
            _verboseResponseLogs++;

            try
            {
                var jo = JObject.Parse(json);
                var delta = jo["delta"]?.ToString();
                var partText = jo.SelectToken("part.text")?.ToString();
                var transcript = jo.SelectToken("transcript")?.ToString();

                var preview = delta ?? partText ?? transcript;
                if (!string.IsNullOrWhiteSpace(preview) && preview.Length > 140) preview = preview.Substring(0, 140) + "…";
                Debug.Log($"[OpenAI] {eventType} preview={(string.IsNullOrWhiteSpace(preview) ? "<none>" : preview)}");
            }
            catch
            {
                Debug.Log($"[OpenAI] {eventType}");
            }
        }

        private void HandleFunctionArgsDelta(string json)
        {
            try
            {
                var obj    = JObject.Parse(json);
                var callId = obj["call_id"]?.ToString();
                var delta  = obj["delta"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrEmpty(callId)) _toolCalls.AppendArgs(callId, delta);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] parse arguments.delta failed: {ex}");
            }
        }

        private void HandleOutputItemAdded(string json)
        {
            try
            {
                var obj  = JObject.Parse(json);
                var item = obj["item"] as JObject;
                if (item?["type"]?.ToString() == "function_call")
                {
                    var callId = item["call_id"]?.ToString();
                    var name   = item["name"]?.ToString();
                    if (!string.IsNullOrEmpty(callId) && !string.IsNullOrEmpty(name))
                        _toolCalls.SetToolName(callId, name);

                    StartThinkingSfx();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] parse output_item.added failed: {ex}");
            }
        }

        private void HandleOutputItemDone(string json)
        {
            try
            {
                var obj  = JObject.Parse(json);
                var item = obj["item"] as JObject;
                if (item?["type"]?.ToString() == "function_call")
                {
                    var callId = item["call_id"]?.ToString();
                    if (!string.IsNullOrEmpty(callId)) _ = ExecuteToolCallIfReady(callId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[OpenAI] parse output_item.done failed: {ex}");
            }
        }

        private void HandleError(string json)
        {
            var err = JsonConvert.DeserializeObject<ErrorEvent>(json);
            var code = err?.Error?.Code    ?? "(no code)";
            var msg  = err?.Error?.Message ?? "(no message)";
            
            // Avoid the warnings while agent is speaking.
            if (code == "input_audio_buffer_commit_empty")
            {
                return;
            }
            
            Debug.LogError($"[OpenAI] ERROR {code}: {msg}");
            if (code == "conversation_already_has_active_response")
            {
                _responseActive = true;
                _responseCreateSent = false;
            }
            StopThinkingSfx();
        }

        private static void HandleNoop() { }

        #endregion
    }
}
