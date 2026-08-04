using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using NativeWebSocket;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using MedicalExam;

public class OpenAIRealtimeClient : MonoBehaviour
{
    private const string DefaultRealtimeModel = "gpt-realtime-2.1";

    private static readonly HashSet<string> SupportedRealtimeVoices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"
    };

    [Header("Configuration")]
    [SerializeField] private string apiKey;
    [SerializeField] private string model = DefaultRealtimeModel;
    [SerializeField] private string reasoningEffort = "low"; // Realtime 2: low/medium/high
    
    [Header("Audio Output")]
    [Tooltip("Uses PcmAudioPlayer if assigned. Otherwise uses internal decoding (OnAudioFilterRead).")]
    [SerializeField] private PcmAudioPlayer pcmAudioPlayer;

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;
        public event Action<string> OnTranscriptDelta; // Fired when AI text is received
        public event Action OnAudioStarted; // Fired when AI starts playing audio
        public event Action OnAudioFinished; // Fired when AI finishes audio
        public event Action<string, string> OnMistakeLogged; // description, severity
        public event Action OnFinishPhase;
        public event Action<string> OnUserTranscriptCompleted; // Fired when user speech transcription is done
        public event Action<string> OnAITranscriptCompleted;   // Fired when AI speech transcript is fully done
        public event Action OnResponseCreated; // Fired when a new AI response begins (before audio/transcript deltas)
        public event Action OnSessionUpdated; // Fired when session.updated is confirmed by the server
        public event Action OnUserSpeechStarted; // Fired when VAD detects start of user speech
        private WebSocket _webSocket; // NativeWebSocket — works on Quest/Android/PC/Editor
        private bool _isConnecting;
        private bool _shouldReconnect;
        // Guard: only commit+create after session.updated confirms instructions are live.
        // Prevents AI responding as plain GPT before persona is applied.
        private bool _sessionUpdateApplied = false;

        // Audio Handling
        private int _outputSampleRate;
        // Audio buffer — NativeWebSocket callbacks are already on the main thread, so no need for ConcurrentQueue,
        // but we keep it for compatibility with OnAudioFilterRead fallback path.
        private ConcurrentQueue<float> _audioBuffer = new ConcurrentQueue<float>();
        private AudioSource _audioSource;
        private Dictionary<string, string> _functionNames = new Dictionary<string, string>(); // call_id -> name
        private bool _audioIsPlaying = false;
        private bool _outputMuted = false;
        private float _lastAudioEnqueueTime = 0f; // Set on main thread only
        private bool _audioChunkReceivedFlag = false; // Thread-safe flag from socket thread
        private const float AUDIO_TIMEOUT = 1.0f; // If no audio for 1s, consider it finished
        private int _audioChunkCount = 0; // Debug counter
        private int _pendingAudioBytes = 0; // Bytes appended since last commit
        private const int MinCommitBytes = 4800; // 100ms at 24kHz PCM16 (24000 * 0.1 * 2)
        // Set in speech_stopped so committed handler knows whether to fire response.create
        private bool _shouldCreateResponseAfterCommit = false;

        // Tool Definitions
        [Serializable]
        public class MistakeStructure
        {
            public string description;
            public string severity;
        }

        private void Awake()
        {
            _outputSampleRate = AudioSettings.outputSampleRate;

            // Auto-find PcmAudioPlayer if not assigned
            if (pcmAudioPlayer == null)
            {
                pcmAudioPlayer = GetComponentInChildren<PcmAudioPlayer>();
                if (pcmAudioPlayer == null)
                {
#if UNITY_2023_1_OR_NEWER
                    pcmAudioPlayer = FindFirstObjectByType<PcmAudioPlayer>();
#else
                    pcmAudioPlayer = FindObjectOfType<PcmAudioPlayer>();
#endif
                }
                if (pcmAudioPlayer != null)
                    Debug.Log($"[OpenAIRealtimeClient] Auto-found PcmAudioPlayer: {pcmAudioPlayer.gameObject.name}");
            }

            // If using PcmAudioPlayer, it handles its own AudioSource. Otherwise, set up fallback.
            if (pcmAudioPlayer == null)
            {
                Debug.LogWarning("[OpenAIRealtimeClient] No PcmAudioPlayer found! Using OnAudioFilterRead fallback.");
                // Setup AudioSource for playback (fallback method using OnAudioFilterRead)
                _audioSource = GetComponent<AudioSource>();
                if (_audioSource == null)
                {
                    _audioSource = gameObject.AddComponent<AudioSource>();
                }

                // Create a dummy clip to ensure the AudioSource is active and OnAudioFilterRead is called
                // 1 second of silence, looped. This is required because OnAudioFilterRead is only called when the AudioSource is playing.
                if (_audioSource.clip == null)
                {
                    _audioSource.clip = AudioClip.Create("RealtimeOutput", _outputSampleRate, 1, _outputSampleRate, false);
                    float[] data = new float[_outputSampleRate]; // Silence
                    _audioSource.clip.SetData(data, 0);
                }
                
                _audioSource.loop = true;
                _audioSource.spatialBlend = 0f; // Force 2D sound so distance doesn't matter
                _audioSource.playOnAwake = true;
            }
        }

        private void OnEnable()
        {
            if (_audioSource != null && !_audioSource.isPlaying)
            {
                _audioSource.Play();
            }
        }

        private void Update()
        {
            // Transfer audio chunk flag from socket thread to main thread timing
            if (_audioChunkReceivedFlag)
            {
                _audioChunkReceivedFlag = false;
                _lastAudioEnqueueTime = Time.time;

                if (!_audioIsPlaying)
                {
                    _audioIsPlaying = true;
                    Debug.Log("[OpenAIRealtimeClient] Audio playback started");
                    OnAudioStarted?.Invoke();
                }
            }

            // Check if audio has timed out (no new chunks received)
            if (_audioIsPlaying && _lastAudioEnqueueTime > 0f)
            {
                if (Time.time - _lastAudioEnqueueTime > AUDIO_TIMEOUT)
                {
                    _audioIsPlaying = false;
                    _lastAudioEnqueueTime = 0f;
                    Debug.Log("[OpenAIRealtimeClient] Audio playback finished");
                    OnAudioFinished?.Invoke();
                }
            }

            // NativeWebSocket REQUIRES DispatchMessageQueue() to be called every frame
            // so that callbacks (OnMessage, OnOpen, etc.) are dispatched on the main thread.
            // Without this call, no messages are delivered on Android/Quest.
#if !UNITY_WEBGL || UNITY_EDITOR
            _webSocket?.DispatchMessageQueue();
#endif
        }

        // Public property to check if AI audio is currently playing
        public bool IsAudioPlaying => _audioIsPlaying;

        // Exposes the API key so MedicalExamManager can reuse it for the lightweight
        // per-turn pronunciation tracking coroutine in Realtime mode.
        // The shared APIKeyConfig asset wins when set, so rotating one key there fixes every
        // component even if a stale key is still serialized on this one.
        public string ApiKey => !string.IsNullOrWhiteSpace(APIKeyConfig.Instance?.OpenAIApiKey) ? APIKeyConfig.Instance.OpenAIApiKey : apiKey;

        private void OnDestroy()
        {
            Disconnect();
        }

        public void Connect()
        {
            if (_isConnecting) return;
            if (_webSocket != null && _webSocket.State == WebSocketState.Open) return;

            _sessionUpdateApplied = false; // reset so no response fires before session.updated
            _pendingAudioBytes = 0;
            _shouldCreateResponseAfterCommit = false;
            StartCoroutine(ConnectCoroutine());
        }

        private IEnumerator ConnectCoroutine()
        {
            _isConnecting = true;

            model = NormalizeRealtimeModel(model);

            // GA Realtime API: do not send the deprecated OpenAI-Beta realtime header.
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + ApiKey }
            };

            string url = $"wss://api.openai.com/v1/realtime?model={Uri.EscapeDataString(model)}";
            _webSocket = new WebSocket(url, headers);

            _webSocket.OnOpen += () =>
            {
                _isConnecting = false;
                Debug.Log("[OpenAIRealtimeClient] WebSocket connected (NativeWebSocket).");
                OnConnected?.Invoke();
            };

            _webSocket.OnError += (err) =>
            {
                _isConnecting = false;
                Debug.LogError($"[OpenAIRealtimeClient] WebSocket error: {err}");
                OnError?.Invoke($"Connection error: {err}");
            };

            _webSocket.OnClose += (code) =>
            {
                _isConnecting = false;
                Debug.Log($"[OpenAIRealtimeClient] WebSocket closed. Code={code}");
                OnDisconnected?.Invoke();
            };

            _webSocket.OnMessage += (data) =>
            {
                string json = System.Text.Encoding.UTF8.GetString(data);
                HandleMessage(json);
            };

            // NOTE: Do NOT send session update here. OnConnected handler in MedicalExamManager
            // will send the real session update with instructions, tools, and modalities.
            yield return _webSocket.Connect();
        }

        public async void Disconnect()
        {
            _sessionUpdateApplied = false;
            if (_webSocket != null)
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    try { await _webSocket.Close(); }
                    catch { /* Best effort close */ }
                }
                _webSocket = null;
            }
        }

        /// <summary>
        /// Stop any queued AI audio playback immediately and disconnect the WebSocket.
        /// Call this when evaluation starts so the AI stops talking.
        /// </summary>
        public void StopPlaybackAndDisconnect()
        {
            // Stop audio playback immediately
            if (pcmAudioPlayer != null)
            {
                try { pcmAudioPlayer.StopImmediately(); }
                catch (System.Exception ex) { Debug.LogWarning($"[OpenAIRealtimeClient] Could not stop pcmAudioPlayer: {ex.Message}"); }
            }

            if (_audioSource != null)
            {
                _audioSource.Stop();
                _audioSource.mute = true;
            }

            _audioIsPlaying = false;
            _lastAudioEnqueueTime = 0f;

            // Clear any queued audio data
            while (_audioBuffer.TryDequeue(out _)) { }

            Debug.Log("[OpenAIRealtimeClient] Stopped audio playback and clearing buffers.");

            // Disconnect WebSocket
            Disconnect();
        }

        /// <summary>
        /// Mute or unmute realtime AI output audio while keeping the websocket session alive.
        /// </summary>
        public void SetOutputMuted(bool muted)
        {
            _outputMuted = muted;

            if (muted)
            {
                if (pcmAudioPlayer != null)
                {
                    try { pcmAudioPlayer.StopImmediately(); } catch { }
                }

                if (_audioSource != null)
                {
                    _audioSource.Stop();
                    _audioSource.mute = true;
                }

                _audioIsPlaying = false;
                _lastAudioEnqueueTime = 0f;
                while (_audioBuffer.TryDequeue(out _)) { }
            }
            else
            {
                if (_audioSource != null)
                    _audioSource.mute = false;
            }
        }

        // ReceiveLoop removed — NativeWebSocket delivers messages via OnMessage callback + DispatchMessageQueue().

        private void HandleMessage(string json)
        {
            try
            {
                JObject response = JObject.Parse(json);
                string type = response["type"]?.ToString();
                
                if (string.IsNullOrEmpty(type)) return;

                // Log ALL events except high-frequency audio deltas for debugging
                if (type != "response.audio.delta" && type != "response.output_audio.delta" && type != "input_audio_buffer.speech_started" && type != "input_audio_buffer.committed")
                {
                    // Truncate json for readability (NativeWebSocket: already on main thread)
                    string shortJson = json.Length > 500 ? json.Substring(0, 500) + "..." : json;
                    Debug.Log($"[OpenAIRealtimeClient] EVENT: {type} => {shortJson}");
                }

                switch (type)
                {
                    case "response.audio.delta":
                    case "response.output_audio.delta":
                        string deltaData = response["delta"]?.ToString() ?? response.SelectToken("audio.delta")?.ToString();
                        if (!string.IsNullOrEmpty(deltaData))
                        {
                            if (_outputMuted)
                                break;

                            _audioChunkCount++;
                            // Set flag for main thread to pick up (avoid Time.time off main thread)
                            _audioChunkReceivedFlag = true;

                            // Log first few chunks and then every 50th
                            if (_audioChunkCount <= 3 || _audioChunkCount % 50 == 0)
                            {
                                Debug.Log($"[OpenAIRealtimeClient] Audio chunk #{_audioChunkCount} received, size={deltaData.Length} chars, pcmAudioPlayer={(pcmAudioPlayer != null ? "assigned" : "NULL")}");
                            }

                            if (pcmAudioPlayer != null)
                            {
                                // NativeWebSocket: already on main thread, call directly
                                pcmAudioPlayer.EnqueueBase64Audio(deltaData);
                            }
                            else
                            {
                                // Route to internal decoder (OnAudioFilterRead fallback)
                                try {
                                    ProcessAudioDelta(deltaData);
                                } catch (Exception e) {
                                    Debug.LogError($"Audio decode error: {e.Message}");
                                }
                            }
                        }
                        break;

                    case "response.audio_transcript.delta":
                    case "response.output_audio_transcript.delta":
                    {
                        string transcriptDelta = response["delta"]?.ToString() ?? response.SelectToken("transcript.delta")?.ToString();
                        if (!string.IsNullOrEmpty(transcriptDelta))
                            OnTranscriptDelta?.Invoke(transcriptDelta);
                        break;
                    }

                    case "response.audio_transcript.done":
                    case "response.output_audio_transcript.done":
                    {
                        string fullTranscript = response["transcript"]?.ToString() ?? response.SelectToken("transcript.text")?.ToString();
                        if (!string.IsNullOrEmpty(fullTranscript))
                        {
                            Debug.Log($"[OpenAIRealtimeClient] AI said: {fullTranscript}");
                            OnAITranscriptCompleted?.Invoke(fullTranscript);
                        }
                        break;
                    }

                    case "response.audio.done":
                    case "response.output_audio.done":
                        Debug.Log("[OpenAIRealtimeClient] Audio stream done event received.");
                        break;

                    case "conversation.item.input_audio_transcription.completed":
                    {
                        string userTranscript = response["transcript"]?.ToString();
                        if (!string.IsNullOrEmpty(userTranscript))
                        {
                            Debug.Log($"[OpenAIRealtimeClient] User said: {userTranscript}");
                            OnUserTranscriptCompleted?.Invoke(userTranscript);
                        }
                        break;
                    }

                    case "response.output_item.added":
                    {
                        // Track function calls to map call_id to function name
                        // This helps us know WHICH function is being called when args arrive
                        var item = response["item"];
                        if (item != null && item["type"]?.ToString() == "function_call")
                        {
                            string callId = item["call_id"]?.ToString();
                            string name = item["name"]?.ToString();
                            if (!string.IsNullOrEmpty(callId) && !string.IsNullOrEmpty(name))
                            {
                                lock (_functionNames)
                                {
                                    _functionNames[callId] = name;
                                }
                            }
                        }
                        break;
                    }

                    case "response.function_call_arguments.done":
                    {
                        string callId = response["call_id"]?.ToString();
                        string args = response["arguments"]?.ToString();
                        
                        string functionName = null;
                        if (!string.IsNullOrEmpty(callId))
                        {
                            lock (_functionNames)
                            {
                                if (_functionNames.ContainsKey(callId))
                                {
                                    functionName = _functionNames[callId];
                                    _functionNames.Remove(callId); // Cleanup
                                }
                            }
                        }

                        if (functionName == "log_mistake" && !string.IsNullOrEmpty(args))
                        {
                            try
                            {
                                var mistakeData = JsonConvert.DeserializeObject<MistakeStructure>(args);
                                OnMistakeLogged?.Invoke(mistakeData.description, mistakeData.severity);
                            }
                            catch (Exception e) { Debug.LogWarning($"Failed to parse log_mistake arguments: {e.Message}"); }
                        }
                        else if (functionName == "finish_phase")
                        {
                            OnFinishPhase?.Invoke();
                        }
                        break;
                    }
                    
                    case "error":
                    {
                        string errorCode = response["error"]?["code"]?.ToString();
                        // Suppress the benign double-commit noise that occurs when server-side VAD
                        // has already committed the buffer before our handler runs.
                        if (errorCode == "input_audio_buffer_commit_empty")
                        {
                            _shouldCreateResponseAfterCommit = false;
                            break;
                        }
                        string errorMsg = response["error"]?.ToString();
                        Debug.LogError($"[OpenAIRealtimeClient] API ERROR: {errorMsg}");
                        OnError?.Invoke($"API Error: {errorMsg}");
                        break;
                    }

                    case "session.created":
                        Debug.Log("[OpenAIRealtimeClient] Session created successfully");
                        break;

                    case "session.updated":
                        _sessionUpdateApplied = true; // persona is live — safe to create responses
                        Debug.Log("[OpenAIRealtimeClient] Session updated — instructions applied, user can speak now");
                        OnSessionUpdated?.Invoke();
                        break;

                    case "response.created":
                        _audioChunkCount = 0;
                        Debug.Log("[OpenAIRealtimeClient] Response generation started");
                        OnResponseCreated?.Invoke();
                        break;

                    case "response.done":
                    {
                        var statusCode = response["response"]?["status"]?.ToString();
                        var statusDetails = response["response"]?["status_details"]?.ToString();
                        Debug.Log($"[OpenAIRealtimeClient] Response done. Status={statusCode} Details={statusDetails}");
                        break;
                    }

                    case "input_audio_buffer.speech_started":
                        Debug.Log("[OpenAIRealtimeClient] VAD: User speech started");
                        _pendingAudioBytes = 0; // reset counter for this speech segment
                        OnUserSpeechStarted?.Invoke();
                        break;

                    case "input_audio_buffer.speech_stopped":
                        Debug.Log("[OpenAIRealtimeClient] VAD: User speech stopped");
                        // With server-side VAD (semantic_vad) the server auto-commits the buffer
                        // and fires input_audio_buffer.committed. We must NOT send a manual commit
                        // here — that would double-commit an already-empty buffer and produce the
                        // "input_audio_buffer_commit_empty" error. Instead, set a flag and let the
                        // committed handler fire response.create once the server confirms the commit.
                        // We trust semantic_vad to filter background noise; do not gate on
                        // _pendingAudioBytes here because speech_started may reset the counter to 0
                        // in the same DispatchMessageQueue batch, before SendAudio() can accumulate bytes.
                        _shouldCreateResponseAfterCommit = _sessionUpdateApplied;
                        if (!_shouldCreateResponseAfterCommit)
                        {
                            Debug.LogWarning("[OpenAIRealtimeClient] Speech stopped but session.updated not yet confirmed — discarding.");
                        }
                        _pendingAudioBytes = 0;
                        break;

                    case "input_audio_buffer.committed":
                        if (_shouldCreateResponseAfterCommit)
                        {
                            _shouldCreateResponseAfterCommit = false;
                            SendJson(new { type = "response.create" });
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Error parsing message: {ex.Message}");
            }
        }

        #region Audio Input/Output

        public void SendAudio(byte[] pcmData)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

            string base64Audio = Convert.ToBase64String(pcmData);
            var eventData = new { type = "input_audio_buffer.append", audio = base64Audio };
            SendJson(eventData);
            _pendingAudioBytes += pcmData.Length;
        }
        
        // Helper to commit the buffer if needed (realtime API commits automatically usually, but "input_audio_buffer.commit" exists)
        public void CommitAudio()
        {
            SendJson(new { type = "input_audio_buffer.commit" });
            // Usually followed by response.create if we want an answer immediately
            SendJson(new { type = "response.create" });
        }

        public void SendText(string text)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open) return;

            var eventData = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "user",
                    content = new[]
                    {
                        new { type = "input_text", text = text }
                    }
                }
            };

            SendJson(eventData);
            SendJson(new { type = "response.create" });
        }

        private void ProcessAudioDelta(string base64Data)
        {
            byte[] pcmData = Convert.FromBase64String(base64Data);
            // OpenAI Realtime returns PCM16 24kHz Mono regardless of input format currently (as of Oct 2024 preview)
            // We need to convert bytes to shorts, then shorts to floats (-1 to 1)

            short[] samplesShort = new short[pcmData.Length / 2];
            Buffer.BlockCopy(pcmData, 0, samplesShort, 0, pcmData.Length);

            float[] samplesFloat = new float[samplesShort.Length];
            for (int i = 0; i < samplesShort.Length; i++)
            {
                samplesFloat[i] = samplesShort[i] / 32768f;
            }

            // Resample from 24000Hz to _outputSampleRate (Unity's DSP clock)
            lock (_audioBuffer) 
            {
                ResampleAndPush(samplesFloat, 24000); 
            }
        }

        private void ResampleAndPush(float[] inputSamples, int inputRate)
        {
            if (inputSamples == null || inputSamples.Length == 0) return;
            
            // Debug once per second or so if buffer is growing
            if (_audioBuffer.Count < 100) 
            {
                 // Uncomment to debug if audio is arriving: 
                 // Debug.Log($"[OpenAIRealtimeClient] Enqueuing {inputSamples.Length} samples. Buffer size: {_audioBuffer.Count}");
            }

            // Simple Linear Interpolation Resampling
            float ratio = (float)inputRate / _outputSampleRate;
            int newLength = (int)(inputSamples.Length / ratio);
            
            for (int i = 0; i < newLength; i++)
            {
                float position = i * ratio;
                int index = (int)position;
                float fraction = position - index;

                float val;
                if (index >= inputSamples.Length - 1)
                {
                    val = inputSamples[inputSamples.Length - 1]; // Clamp to last sample
                }
                else
                {
                    val = inputSamples[index] * (1.0f - fraction) + inputSamples[index + 1] * fraction;
                }
                
                _audioBuffer.Enqueue(val);
            }
        }

        // Unity Audio Thread Callback
        // This is called on a high priority thread. Keep it fast.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            // data contains interleaved samples: L, R, L, R... (if stereo)
            // We need to fill it with our mono signal
            
            for (int i = 0; i < data.Length; i += channels)
            {
                float sample = 0;
                
                // Try to get next sample
                if (_audioBuffer.TryDequeue(out float val))
                {
                    sample = val;
                }
                // If buffer is empty, it stays 0 (silence)

                // Copy mono sample to all channels
                for (int c = 0; c < channels; c++)
                {
                    data[i + c] = sample;
                }
            }
        }

        #endregion

        #region Session Management

        public void SendSessionUpdate(string instructions, object tools = null, string voice = "alloy")
        {
            string normalizedVoice = NormalizeRealtimeVoice(voice);
            string normalizedReasoningEffort = NormalizeReasoningEffort(reasoningEffort);

            // Enforce German language for all responses + natural-turn-taking rules
            const string germanDirective =
                "WICHTIG: Du MUSST ausschließlich auf Deutsch antworten. Sprich niemals Englisch. " +
                "Alle Antworten, Erklärungen und Gespräche müssen vollständig auf Deutsch sein. " +
                "Verwende korrekte deutsche medizinische Fachbegriffe.\n\n" +
                "GESPRÄCHSREGEL: Unterbreche den Kandidaten NIEMALS — auch nicht bei Denkpausen, Atemzügen oder kurzen Zögerern. " +
                "Warte immer, bis der Kandidat seinen Gedanken vollständig abgeschlossen hat und mehrere Sekunden geschwiegen hat, " +
                "bevor du antwortest. Denkpausen von 1–3 Sekunden sind normal und kein Gesprächsende.\n\n";
            string fullInstructions = germanDirective + instructions;

            // Build session config — gpt-realtime-2 nested audio object structure
            var session = new JObject
            {
                ["type"] = "realtime",
                ["output_modalities"] = new JArray("audio"),
                ["instructions"] = fullInstructions,
                ["audio"] = new JObject
                {
                    ["input"] = new JObject
                    {
                        ["format"] = new JObject
                        {
                            ["type"] = "audio/pcm",
                            ["rate"] = 24000
                        },
                        ["transcription"] = new JObject
                        {
                            ["model"] = "gpt-realtime-whisper",
                            ["language"] = "de"
                        },
                        ["turn_detection"] = new JObject
                        {
                            ["type"] = "semantic_vad",
                            ["eagerness"] = "medium",
                            ["create_response"] = false,
                            ["interrupt_response"] = true
                        }
                    },
                    ["output"] = new JObject
                    {
                        ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                        ["voice"] = normalizedVoice
                    }
                },
                ["tool_choice"] = "auto"
            };

            if (tools != null)
            {
                session["tools"] = JToken.FromObject(tools);
            }

            var eventData = new JObject
            {
                ["type"] = "session.update",
                ["session"] = session
            };

            Debug.Log($"[OpenAIRealtimeClient] Sending session.update with output_modalities=[audio], semantic_vad");
            SendJson(eventData);
        }

        // True once server chunks have stopped AND PcmAudioPlayer has drained its buffer.
        public bool IsPlaybackFullyDone =>
            !_audioIsPlaying && (pcmAudioPlayer == null || !pcmAudioPlayer.IsPlaying);

        /// <summary>
        /// One-shot welcome: connect → speak text in realtime voice → disconnect → call onDone.
        /// Safe to call before StartConversation — MedicalExamManager has not subscribed yet.
        /// </summary>
        public void PlayWelcomeAndDisconnect(string text, string voice, Action onDone)
        {
            StartCoroutine(WelcomeSpeakCoroutine(text, voice, onDone));
        }

        private IEnumerator WelcomeSpeakCoroutine(string text, string voice, Action onDone)
        {
            // 1 — Connect
            bool connected = false;
            bool failed = false;
            Action onConn = () => connected = true;
            Action<string> onErr = _ => failed = true;
            OnConnected += onConn;
            OnError += onErr;
            Connect();
            float deadline = Time.time + 10f;
            while (!connected && !failed && Time.time < deadline)
                yield return null;
            OnConnected -= onConn;
            OnError -= onErr;

            if (!connected)
            {
                Debug.LogError("[OpenAIRealtimeClient] Welcome: connection timed out, skipping welcome.");
                onDone?.Invoke();
                yield break;
            }

            // 2 — Minimal session: narrator voice, VAD disabled, no tools
            string instruction = "Lies jetzt sofort und WÖRTLICH diesen Text vor, ohne Ergänzungen:\n\n" + text;
            var sessionUpdate = new JObject
            {
                ["type"] = "session.update",
                ["session"] = new JObject
                {
                    ["type"] = "realtime",
                    ["output_modalities"] = new JArray("audio"),
                    ["instructions"] = instruction,
                    ["audio"] = new JObject
                    {
                        ["input"] = new JObject
                        {
                            ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                            ["turn_detection"] = JValue.CreateNull()
                        },
                        ["output"] = new JObject
                        {
                            ["format"] = new JObject { ["type"] = "audio/pcm", ["rate"] = 24000 },
                            ["voice"] = NormalizeRealtimeVoice(voice)
                        }
                    }
                }
            };
            SendJson(sessionUpdate);

            // 3 — Wait for session.updated confirmation (max 5 s)
            bool sessionReady = false;
            Action onSessUpd = () => sessionReady = true;
            OnSessionUpdated += onSessUpd;
            deadline = Time.time + 5f;
            while (!sessionReady && Time.time < deadline)
                yield return null;
            OnSessionUpdated -= onSessUpd;

            // 4 — Trigger response (AI reads from instructions — no user message needed)
            SendJson(new JObject { ["type"] = "response.create" });

            // 5 — Wait for audio to start (up to 8 s)
            bool audioFinished = false;
            Action onAudioDone = () => audioFinished = true;
            OnAudioFinished += onAudioDone;
            deadline = Time.time + 8f;
            while (!_audioIsPlaying && !audioFinished && Time.time < deadline)
                yield return null;

            // 6 — Wait for last server chunk + PcmAudioPlayer buffer drain
            deadline = Time.time + 30f;
            while (!audioFinished && Time.time < deadline)
                yield return null;
            OnAudioFinished -= onAudioDone;

            deadline = Time.time + 10f;
            while (!IsPlaybackFullyDone && Time.time < deadline)
                yield return null;

            // 7 — Disconnect cleanly then notify caller
            Disconnect();
            yield return new WaitForSeconds(0.3f);
            onDone?.Invoke();
        }

        private string NormalizeRealtimeVoice(string voice)
        {
            string requested = string.IsNullOrWhiteSpace(voice) ? "alloy" : voice.Trim().ToLowerInvariant();

            if (SupportedRealtimeVoices.Contains(requested))
                return requested;

            // Map common legacy voice IDs to current realtime voices.
            string mapped = requested switch
            {
                "nova" => "verse",
                "onyx" => "ash",
                "fable" => "sage",
                _ => "alloy"
            };

            Debug.LogWarning($"[OpenAIRealtimeClient] Unsupported realtime voice '{voice}'. Using '{mapped}' instead.");
            return mapped;
        }

        private string NormalizeReasoningEffort(string effort)
        {
            string requested = string.IsNullOrWhiteSpace(effort) ? "low" : effort.Trim().ToLowerInvariant();
            if (requested == "low" || requested == "medium" || requested == "high")
                return requested;

            Debug.LogWarning($"[OpenAIRealtimeClient] Unsupported reasoning effort '{effort}'. Using 'low' instead.");
            return "low";
        }

        private string NormalizeRealtimeModel(string configuredModel)
        {
            string requested = string.IsNullOrWhiteSpace(configuredModel)
                ? DefaultRealtimeModel
                : configuredModel.Trim();

            // Old preview IDs can remain serialized in scenes/prefabs and cause model_not_found.
            if (requested.StartsWith("gpt-4o-realtime-preview", StringComparison.OrdinalIgnoreCase)
                || requested.Equals("gpt-4o-realtime", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[OpenAIRealtimeClient] Legacy realtime model '{requested}' detected. Using '{DefaultRealtimeModel}' instead.");
                return DefaultRealtimeModel;
            }

            // gpt-realtime-2 still works but has been superseded by gpt-realtime-2.1 (better
            // alphanumeric recognition, noise handling, and interruption behavior).
            if (requested.Equals("gpt-realtime-2", StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning($"[OpenAIRealtimeClient] Realtime model '{requested}' detected. Using '{DefaultRealtimeModel}' instead.");
                return DefaultRealtimeModel;
            }

            return requested;
        }

        // Trigger the AI to generate the first response
        public void CreateResponse()
        {
            SendJson(new { type = "response.create" });
        }



        private async void SendJson(object data)
        {
            if (_webSocket == null || _webSocket.State != WebSocketState.Open)
            {
                Debug.LogWarning($"[OpenAIRealtimeClient] SendJson called but WebSocket not open. State={_webSocket?.State}");
                return;
            }

            try
            {
                string json;
                if (data is JObject jObj)
                    json = jObj.ToString(Formatting.None);
                else
                    json = JsonConvert.SerializeObject(data, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                if (!json.Contains("input_audio_buffer.append"))
                {
                    string shortJson = json.Length > 500 ? json.Substring(0, 500) + "..." : json;
                    Debug.Log($"[OpenAIRealtimeClient] SENDING: {shortJson}");
                }

                byte[] bytes = Encoding.UTF8.GetBytes(json);
                // NativeWebSocket.SendText is the correct method for UTF-8 string messages
                await _webSocket.SendText(json);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"Send error: {ex.Message}");
            }
        }

        #endregion
    }

