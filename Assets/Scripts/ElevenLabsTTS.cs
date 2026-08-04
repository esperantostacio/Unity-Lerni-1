using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using NativeWebSocket;
using UnityEngine;
using UnityEngine.Networking;

namespace MedicalExam
{
    /// <summary>
    /// Text-to-Speech using OpenAI's TTS API (faster and cheaper than ElevenLabs)
    /// </summary>
    public class ElevenLabsTTS : MonoBehaviour
    {
        [Header("OpenAI TTS Settings")]
        [Tooltip("Your OpenAI API Key (same as for GPT)")]
        [SerializeField] private string apiKey = "";
        
        [Header("Voice Selection")]
        [Tooltip("Fixed female voice for Doctor-to-Doctor OpenAI-TTS output")]
        [SerializeField] private string femaleVoiceDoctorToDoctor = "shimmer";
        [Tooltip("Fixed female voice for Doctor-to-Patient OpenAI-TTS output")]
        [SerializeField] private string femaleVoiceDoctorToPatient = "coral";
        
        [Header("Model")]
        [Tooltip("tts-1 (faster, ~0.5s latency) or tts-1-hd (higher quality, ~1s latency)")]
        [SerializeField] private string model = "tts-1";

        [Header("Audio Format")]
        [Tooltip("Response format from OpenAI TTS. 'pcm' is fastest and avoids FMOD decoding issues.")]
        [SerializeField] private string responseFormat = "pcm"; // pcm|wav|mp3

        [Tooltip("Sample rate used by OpenAI TTS when response_format='pcm'. 24000 is the typical default.")]
        [SerializeField] private int pcmSampleRateHz = 24000;
        
        [Tooltip("AudioSource to play the TTS audio")]
        [SerializeField] private AudioSource audioSource;

        /// <summary>Returns the AudioSource used for TTS playback (read-only).</summary>
        public AudioSource PlaybackAudioSource => audioSource;

        [Tooltip("Optional PCM audio player for streaming playback (plays as chunks arrive)")]
        [SerializeField] private MonoBehaviour pcmAudioPlayer;

        [Header("Native On-Device TTS (Quest 3 / Android — Fastest, Offline)")]
        [Tooltip("Speech rate for native TTS. 1.0 = normal, 1.5 = faster, 0.75 = slower")]
        [Range(0.25f, 3.0f)]
        [SerializeField] private float nativeSpeechRate = 1.0f;

        [Tooltip("Pitch for native TTS. 1.0 = normal, 1.5 = higher, 0.5 = lower")]
        [Range(0.25f, 2.0f)]
        [SerializeField] private float nativePitch = 1.0f;

        [Tooltip("Language/locale for native TTS (e.g. en-US, fr-FR, es-ES)")]
        [SerializeField] private string nativeLocale = "de-DE";

        [Header("Wit.ai TTS Settings (Meta Cloud — Fallback)")]
        [Tooltip("Your Wit.ai Server Access Token (only needed if using WitAI cloud provider). Leave blank to use APIKeyConfig.")]
        [SerializeField] private string witAiToken = "";

        [Tooltip("Wit.ai voice name. Examples: wit$Rebecca, wit$Charlie, wit$Ana, wit$Kenji")]
        [SerializeField] private string witAiVoice = "wit$Rebecca";

        [Tooltip("Wit.ai TTS audio speed (50-400, default 100)")]
        [Range(50, 400)]
        [SerializeField] private int witAiSpeed = 100;

        [Tooltip("Wit.ai TTS pitch (25-400, default 100)")]
        [Range(25, 400)]
        [SerializeField] private int witAiPitch = 100;

        [Tooltip("Wit.ai PCM sample rate. Their raw output is typically 24000 Hz, mono, signed 16-bit LE.")]
        [SerializeField] private int witAiSampleRateHz = 24000;

        [Header("Murf.ai TTS Settings (Premium Quality Cloud)")]
        [Tooltip("Your Murf.ai API Key. Leave blank to use APIKeyConfig.")]
        [SerializeField] private string murfAiApiKey = "";

        [Tooltip("Murf.ai voice ID for German. Default is female: Erna")]
        [SerializeField] private string murfAiVoiceId = "Erna";

        [Tooltip("Murf.ai model (FALCON is recommended)")]
        [SerializeField] private string murfAiModel = "FALCON";

        [Tooltip("Murf.ai locale (de-DE for German, en-US for English)")]
        [SerializeField] private string murfAiLocale = "de-DE";

        [Tooltip("Murf.ai PCM sample rate (24000 Hz recommended)")]
        [SerializeField] private int murfAiSampleRateHz = 24000;

        [Header("ElevenLabs TTS Settings (Fastest Streaming)")]
        [Tooltip("Your ElevenLabs API Key. Leave blank to use APIKeyConfig.")]
        [SerializeField] private string elevenLabsApiKey = "";

        [Tooltip("Female Voice ID for Doctor-to-Patient. Default: Bella")]
        [SerializeField] private string elevenLabsVoiceMale = "EXAVITQu4vr4xnSDxMaL";

        [Tooltip("Female Voice ID for Doctor-to-Doctor. Default: Rachel")]
        [SerializeField] private string elevenLabsVoiceFemale = "21m00Tcm4TlvDq8ikWAM";

        [Tooltip("Model ID: eleven_flash_v2_5 (fastest) or eleven_turbo_v2_5 (high quality low latency)")]
        [SerializeField] private string elevenLabsModelId = "eleven_flash_v2_5";

        [Tooltip("Output format. pcm_24000 is best for streaming.")]
        [SerializeField] private string elevenLabsOutputFormat = "pcm_24000";

        [Header("TTS Provider Selection")]
        [Tooltip("Which TTS provider to use by default. NativeDevice = Quest 3 built-in (fastest, offline)")]
        [SerializeField] private TTSProvider defaultProvider = TTSProvider.NativeDevice;

        public enum TTSProvider { OpenAI, WitAI, NativeDevice, MurfAI, ElevenLabs }
        public enum ScenarioType { DoctorToDoctor, DoctorToPatient }

#if UNITY_ANDROID && !UNITY_EDITOR
        private AndroidJavaObject androidTTS;
        private bool nativeTTSReady = false;
#endif

        // WebSocket tracking for NativeWebSocket (must call DispatchMessageQueue in Update)
        private readonly List<WebSocket> activeWebSockets = new List<WebSocket>();
        private List<byte> murfAudioBuffer;
        private MethodInfo pcmEnqueueMethod;

        /// <summary>Resolved Murf.ai key: the shared APIKeyConfig asset wins when set, so rotating one
        /// key there fixes every component even if a stale key is still serialized on this one.</summary>
        private string ResolvedMurfAiApiKey =>
            !string.IsNullOrEmpty(APIKeyConfig.Instance?.MurfAiApiKey) ? APIKeyConfig.Instance.MurfAiApiKey : murfAiApiKey;

        /// <summary>Resolved ElevenLabs key: the shared APIKeyConfig asset wins when set.</summary>
        private string ResolvedElevenLabsApiKey =>
            !string.IsNullOrEmpty(APIKeyConfig.Instance?.ElevenLabsApiKey) ? APIKeyConfig.Instance.ElevenLabsApiKey : elevenLabsApiKey;

        /// <summary>Resolved Wit.ai token: the shared APIKeyConfig asset wins when set.</summary>
        private string ResolvedWitAiToken =>
            !string.IsNullOrEmpty(APIKeyConfig.Instance?.WitAiToken) ? APIKeyConfig.Instance.WitAiToken : witAiToken;

        private void Start()
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogWarning("[OpenAI-TTS] API key is not set! Will try to get it from environment or OpenAIConfig.");
            }

            if (audioSource == null)
            {
                Debug.LogWarning("[OpenAI-TTS] No AudioSource assigned. Assign one to hear playback.");
            }

            Debug.Log("[OpenAI-TTS] OpenAI TTS initialized (model: " + model + ")");

            if (!string.IsNullOrEmpty(ResolvedWitAiToken))
                Debug.Log($"[Wit.ai-TTS] Wit.ai TTS ready (voice: {witAiVoice})");

            if (!string.IsNullOrEmpty(ResolvedMurfAiApiKey))
                Debug.Log($"[Murf.ai-TTS] Murf.ai TTS ready (voice: {murfAiVoiceId}, locale: {murfAiLocale})");

            if (!string.IsNullOrEmpty(ResolvedElevenLabsApiKey))
                Debug.Log($"[ElevenLabs-TTS] ElevenLabs TTS ready (Voices: {elevenLabsVoiceMale} / {elevenLabsVoiceFemale})");

            InitNativeTTS();
        }

        private void OnDestroy()
        {
            ShutdownNativeTTS();
            // Close any active WebSockets
            foreach (var ws in activeWebSockets)
            {
                if (ws != null && ws.State == WebSocketState.Open)
                    _ = ws.Close();
            }
            activeWebSockets.Clear();
        }

        private void Update()
        {
            // NativeWebSocket requires dispatching received messages on the main thread
            foreach (var ws in activeWebSockets)
                ws?.DispatchMessageQueue();
        }

        // ────────────────────────────────────────────────────────────────────
        //  Native Android TTS  (Quest 3 built-in — zero latency, offline)
        // ────────────────────────────────────────────────────────────────────

        private void InitNativeTTS()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                // Create TTS with OnInitListener
                androidTTS = new AndroidJavaObject(
                    "android.speech.tts.TextToSpeech",
                    activity,
                    new AndroidTTSInitListener(this)
                );

                Debug.Log("[NativeTTS] Android TextToSpeech created, waiting for init...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NativeTTS] Failed to create Android TTS: {ex.Message}");
            }
#else
            Debug.Log("[NativeTTS] Native TTS only available on Android/Quest 3. In Editor, will use 'say' command on macOS or log only.");
#endif
        }

        private void ShutdownNativeTTS()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidTTS != null)
            {
                try
                {
                    androidTTS.Call("stop");
                    androidTTS.Call("shutdown");
                }
                catch { }
                androidTTS = null;
                nativeTTSReady = false;
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Called from the Android OnInitListener when TTS engine is ready.
        /// </summary>
        public void OnNativeTTSInitialized(int status)
        {
            if (status == 0) // TextToSpeech.SUCCESS == 0
            {
                nativeTTSReady = true;
                Debug.Log("[NativeTTS] ✅ Android TTS engine ready!");

                // Set locale
                try
                {
                    string[] parts = nativeLocale.Split('-');
                    AndroidJavaObject locale;
                    if (parts.Length >= 2)
                        locale = new AndroidJavaObject("java.util.Locale", parts[0], parts[1]);
                    else
                        locale = new AndroidJavaObject("java.util.Locale", nativeLocale);

                    int result = androidTTS.Call<int>("setLanguage", locale);
                    if (result < 0)
                        Debug.LogWarning($"[NativeTTS] Locale '{nativeLocale}' not fully supported (code={result}), using default.");
                    else
                        Debug.Log($"[NativeTTS] Locale set to {nativeLocale}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NativeTTS] Failed to set locale: {ex.Message}");
                }

                // Set rate & pitch
                androidTTS.Call<int>("setSpeechRate", nativeSpeechRate);
                androidTTS.Call<int>("setPitch", nativePitch);
            }
            else
            {
                nativeTTSReady = false;
                Debug.LogError($"[NativeTTS] ❌ Android TTS init failed with status {status}");
            }
        }
#endif

        /// <summary>
        /// Speak using the native Android/Quest 3 TTS engine. Instant, offline, no API key.
        /// </summary>
        public void SpeakNative(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("[NativeTTS] Empty text, skipping.");
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidTTS == null || !nativeTTSReady)
            {
                Debug.LogError("[NativeTTS] Android TTS not initialized yet!");
                return;
            }

            Debug.Log($"[NativeTTS] Speaking: {text.Substring(0, Math.Min(50, text.Length))}...");

            // Update rate & pitch in case they changed in Inspector
            androidTTS.Call<int>("setSpeechRate", nativeSpeechRate);
            androidTTS.Call<int>("setPitch", nativePitch);

            // QUEUE_FLUSH = 0 (stop current speech and speak this)
            // Use HashMap for params (null works on most Android versions)
            int queueFlush = 0;

            // Android API 21+ uses Bundle params; older uses HashMap.
            // Quest 3 is Android 12+, so we can use the Bundle version.
            AndroidJavaObject bundle = new AndroidJavaObject("android.os.Bundle");
            string utteranceId = "lerini_tts_" + DateTime.UtcNow.Ticks;
            androidTTS.Call<int>("speak", text, queueFlush, bundle, utteranceId);
#else
            // Editor fallback: use macOS 'say' command for testing
            Debug.Log($"[NativeTTS] (Editor) Would speak: {text}");
            #if UNITY_EDITOR_OSX
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("say", $"\"{text}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NativeTTS] macOS 'say' failed: {ex.Message}");
            }
            #endif
#endif
        }

        /// <summary>
        /// Stop any currently playing native TTS speech.
        /// </summary>
        public void StopNativeSpeech()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidTTS != null)
                androidTTS.Call<int>("stop");
#endif
        }

        /// <summary>
        /// Check if native TTS is currently speaking.
        /// </summary>
        public bool IsNativeSpeaking()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (androidTTS != null)
                return androidTTS.Call<bool>("isSpeaking");
#endif
            return false;
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Android OnInitListener proxy — receives the TTS init callback from Java.
        /// </summary>
        private class AndroidTTSInitListener : AndroidJavaProxy
        {
            private ElevenLabsTTS owner;

            public AndroidTTSInitListener(ElevenLabsTTS owner)
                : base("android.speech.tts.TextToSpeech$OnInitListener")
            {
                this.owner = owner;
            }

            // Called by Android when TTS engine finishes init
            public void onInit(int status)
            {
                // Marshal back to Unity main thread
                UnityMainThreadDispatcher.Enqueue(() => owner.OnNativeTTSInitialized(status));
            }
        }
#endif

        /// <summary>
        /// Speak text using the appropriate fixed female voice for the scenario.
        /// Doctor-to-Doctor: female voice A, Doctor-to-Patient: female voice B.
        /// Routes through the selected defaultProvider.
        /// </summary>
        public void Speak(string text, ScenarioType scenario)
        {
            if (defaultProvider == TTSProvider.NativeDevice)
            {
                SpeakNative(text);
                return;
            }

            if (defaultProvider == TTSProvider.WitAI)
            {
                SpeakWithWitAI(text);
                return;
            }

            if (defaultProvider == TTSProvider.MurfAI)
            {
                SpeakWithMurfAI(text);
                return;
            }

            if (defaultProvider == TTSProvider.ElevenLabs)
            {
                // DoctorToDoctor -> Female A (Rachel)
                // DoctorToPatient -> Female B (Bella)
                string voiceId = (scenario == ScenarioType.DoctorToDoctor) ? elevenLabsVoiceFemale : elevenLabsVoiceMale;
                SpeakWithElevenLabs(text, voiceId);
                return;
            }

            string key = GetAPIKey();
            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError("[OpenAI-TTS] API key not available!");
                return;
            }

            string voice = GetOpenAIVoiceForScenario(scenario);
            StartCoroutine(TextToSpeechCoroutine(text, voice, key));
        }

        // ────────────────────────────────────────────────────────────────────
        //  Murf.ai TTS  (Premium Cloud — High Quality, German/Multi-language)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Speak text using Murf.ai TTS. Premium quality, supports German and many languages.
        /// Returns raw PCM audio stream for minimal latency.
        /// </summary>
        public void SpeakWithMurfAI(string text)
        {
            if (string.IsNullOrEmpty(ResolvedMurfAiApiKey))
            {
                Debug.LogError("[Murf.ai-TTS] Murf.ai API key is not set!");
                return;
            }
            StartCoroutine(MurfAiTTSCoroutine(text));
        }

        private IEnumerator MurfAiTTSCoroutine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("[Murf.ai-TTS] Empty text, skipping TTS.");
                yield break;
            }

            Debug.Log($"[Murf.ai-TTS] Connecting to WebSocket: voice={murfAiVoiceId}, locale={murfAiLocale}");

            murfAudioBuffer = new List<byte>();
            bool connected = false;
            bool completed = false;
            bool hasError = false;
            bool firstChunk = true;
            int totalReceived = 0;

            string wsUrl = $"wss://global.api.murf.ai/v1/speech/stream-input?api-key={ResolvedMurfAiApiKey}&model={murfAiModel}&sample_rate={murfAiSampleRateHz}&channel_type=MONO&format=WAV";
            var ws = new WebSocket(wsUrl);
            activeWebSockets.Add(ws);

            ws.OnOpen += () =>
            {
                connected = true;
                Debug.Log("[Murf.ai-TTS] WebSocket connected!");
            };

            ws.OnError += (err) =>
            {
                Debug.LogError($"[Murf.ai-TTS] WebSocket error: {err}");
                hasError = true;
                completed = true;
            };

            ws.OnClose += (code) =>
            {
                Debug.Log($"[Murf.ai-TTS] WebSocket closed with code: {code}");
                completed = true;
            };

            ws.OnMessage += (data) =>
            {
                // Detect text (JSON) vs binary (audio) by checking first byte
                bool isJson = data.Length > 0 && data[0] == (byte)'{';

                if (!isJson)
                {
                    // Binary audio data
                    bool hasRiffHeader = data.Length > 12
                        && System.Text.Encoding.ASCII.GetString(data, 0, 4) == "RIFF"
                        && System.Text.Encoding.ASCII.GetString(data, 8, 4) == "WAVE";

                    byte[] pcmBytes = data;
                    if (hasRiffHeader && data.Length > 44)
                    {
                        pcmBytes = new byte[data.Length - 44];
                        System.Array.Copy(data, 44, pcmBytes, 0, data.Length - 44);
                    }

                    if (TryEnqueuePcm(pcmAudioPlayer, pcmBytes))
                    {
                        totalReceived += pcmBytes.Length;
                        Debug.Log($"[Murf.ai-TTS] Stream binary chunk: {pcmBytes.Length} bytes (total: {totalReceived})");
                    }
                    else
                    {
                        murfAudioBuffer.AddRange(data);
                        totalReceived += data.Length;
                        Debug.Log($"[Murf.ai-TTS] Binary chunk buffered: {data.Length} bytes (total: {totalReceived})");
                    }
                    firstChunk = false;
                }
                else
                {
                    // Text JSON response
                    string jsonResponse = System.Text.Encoding.UTF8.GetString(data);
                    Debug.Log($"[Murf.ai-TTS] Received JSON ({jsonResponse.Length} chars): {jsonResponse.Substring(0, Math.Min(200, jsonResponse.Length))}...");

                    try
                    {
                        int audioStart = jsonResponse.IndexOf("\"audio\":\"");
                        if (audioStart >= 0)
                        {
                            audioStart += 9;
                            int audioEnd = jsonResponse.IndexOf("\"", audioStart);

                            if (audioEnd > audioStart)
                            {
                                string base64Audio = jsonResponse.Substring(audioStart, audioEnd - audioStart);
                                byte[] audioBytes = System.Convert.FromBase64String(base64Audio);
                                Debug.Log($"[Murf.ai-TTS] Decoded base64 chunk: {audioBytes.Length} bytes (base64 was {base64Audio.Length} chars)");

                                bool hasRiffHeader = audioBytes.Length > 12
                                    && System.Text.Encoding.ASCII.GetString(audioBytes, 0, 4) == "RIFF"
                                    && System.Text.Encoding.ASCII.GetString(audioBytes, 8, 4) == "WAVE";

                                byte[] pcmBytes = audioBytes;
                                if (hasRiffHeader && audioBytes.Length > 44)
                                {
                                    pcmBytes = new byte[audioBytes.Length - 44];
                                    System.Array.Copy(audioBytes, 44, pcmBytes, 0, audioBytes.Length - 44);
                                }

                                if (TryEnqueuePcm(pcmAudioPlayer, pcmBytes))
                                {
                                    totalReceived += pcmBytes.Length;
                                    Debug.Log($"[Murf.ai-TTS] Stream chunk: {pcmBytes.Length} bytes (total: {totalReceived})");
                                }
                                else
                                {
                                    if (!firstChunk && hasRiffHeader && audioBytes.Length > 44)
                                    {
                                        byte[] audioWithoutHeader = new byte[audioBytes.Length - 44];
                                        System.Array.Copy(audioBytes, 44, audioWithoutHeader, 0, audioBytes.Length - 44);
                                        murfAudioBuffer.AddRange(audioWithoutHeader);
                                        totalReceived += audioWithoutHeader.Length;
                                        Debug.Log($"[Murf.ai-TTS] Chunk: stripped repeated WAV header, added {audioWithoutHeader.Length} bytes (total: {totalReceived})");
                                    }
                                    else
                                    {
                                        murfAudioBuffer.AddRange(audioBytes);
                                        totalReceived += audioBytes.Length;
                                        Debug.Log($"[Murf.ai-TTS] Chunk: added {audioBytes.Length} bytes (total: {totalReceived})");
                                    }
                                }
                                firstChunk = false;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"[Murf.ai-TTS] No 'audio' field in JSON response: {jsonResponse}");
                        }

                        if (jsonResponse.Contains("\"final\":true") || jsonResponse.Contains("\"final\": true"))
                        {
                            Debug.Log("[Murf.ai-TTS] \u2713 Received final=true from server");
                            completed = true;
                        }
                    }
                    catch (System.FormatException ex)
                    {
                        Debug.LogWarning($"[Murf.ai-TTS] Base64 decode error: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[Murf.ai-TTS] Error parsing response: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            };

            // Connect (NativeWebSocket \u2014 works on all platforms including Quest/Android)
            _ = ws.Connect();

            float elapsed = 0f;
            while (!connected && !hasError && elapsed < 5f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!connected)
            {
                Debug.LogError("[Murf.ai-TTS] WebSocket connection failed or timeout!");
                activeWebSockets.Remove(ws);
                yield break;
            }

            // Send voice configuration
            var voiceConfig = new MurfWsInit
            {
                voice_config = new MurfWsVoiceConfig
                {
                    voiceId = murfAiVoiceId,
                    multiNativeLocale = murfAiLocale,
                    style = "Conversation",
                    rate = 0,
                    pitch = 0,
                    variation = 1
                }
            };
            string configJson = JsonUtility.ToJson(voiceConfig);
            Debug.Log($"[Murf.ai-TTS] Sending voice config: {configJson}");
            _ = ws.SendText(configJson);
            yield return new WaitForSeconds(0.2f);

            // Send text to synthesize
            var textMessage = new MurfWsText
            {
                text = text,
                end = true
            };
            string textJson = JsonUtility.ToJson(textMessage);
            Debug.Log($"[Murf.ai-TTS] Sending text: {textJson}");
            _ = ws.SendText(textJson);

            // Wait for audio to arrive
            elapsed = 0f;
            while (!completed && elapsed < 15f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (elapsed >= 15f)
            {
                Debug.LogWarning("[Murf.ai-TTS] Audio receive timeout");
            }

            yield return new WaitForSeconds(0.2f);

            // Play audio if received (WAV fallback). Streaming PCM uses PcmAudioPlayer.
            if (pcmAudioPlayer == null)
            {
                if (murfAudioBuffer.Count > 0)
                {
                    Debug.Log($"[Murf.ai-TTS] Received {murfAudioBuffer.Count} bytes of audio, starting playback...");
                    byte[] audioData = murfAudioBuffer.ToArray();
                    PlayAudioFromWAV(audioData);
                }
                else
                {
                    Debug.LogWarning("[Murf.ai-TTS] No audio data received!");
                }
            }

            // Close WebSocket
            if (ws.State == WebSocketState.Open)
            {
                _ = ws.Close();
            }
            activeWebSockets.Remove(ws);
        }

        private bool TryEnqueuePcm(MonoBehaviour player, byte[] pcmBytes)
        {
            if (player == null || pcmBytes == null || pcmBytes.Length < 2)
                return false;

            if (pcmEnqueueMethod == null)
            {
                pcmEnqueueMethod = player.GetType().GetMethod(
                    "EnqueuePcm16",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(byte[]) },
                    modifiers: null
                );
            }

            if (pcmEnqueueMethod == null)
            {
                Debug.LogWarning("[Murf.ai-TTS] PCM player missing EnqueuePcm16(byte[]) method.");
                return false;
            }

            try
            {
                pcmEnqueueMethod.Invoke(player, new object[] { pcmBytes });
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Murf.ai-TTS] PCM enqueue failed: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }



        // ────────────────────────────────────────────────────────────────────
        //  ElevenLabs TTS (Fastest Streaming)
        // ────────────────────────────────────────────────────────────────────

        public void SpeakWithElevenLabs(string text, string voiceId = null)
        {
            if (string.IsNullOrEmpty(ResolvedElevenLabsApiKey))
            {
                Debug.LogError("[ElevenLabs-TTS] API Key is missing or default.");
                return;
            }
            
            if (string.IsNullOrEmpty(voiceId)) voiceId = elevenLabsVoiceMale; // Fallback

            StartCoroutine(ElevenLabsTTSCoroutine(text, voiceId));
        }

        private IEnumerator ElevenLabsTTSCoroutine(string text, string voiceId)
        {
            if (string.IsNullOrEmpty(text)) yield break;

            Debug.Log($"[ElevenLabs-TTS] Connecting to {elevenLabsModelId} (Voice: {voiceId})...");

            string url = $"wss://api.elevenlabs.io/v1/text-to-speech/{voiceId}/stream-input?model_id={elevenLabsModelId}&output_format={elevenLabsOutputFormat}";

            bool connected = false;
            bool completed = false;
            bool hasError = false;
            int totalBytes = 0;

            var ws = new WebSocket(url);
            activeWebSockets.Add(ws);

            ws.OnOpen += () =>
            {
                connected = true;
                Debug.Log("[ElevenLabs-TTS] Connected.");
            };

            ws.OnError += (err) =>
            {
                Debug.LogError($"[ElevenLabs-TTS] WebSocket error: {err}");
                hasError = true;
                completed = true;
            };

            ws.OnClose += (code) =>
            {
                Debug.Log($"[ElevenLabs] Stream finished. Total bytes: {totalBytes}");
                completed = true;
            };

            ws.OnMessage += (data) =>
            {
                string jsonResponse = System.Text.Encoding.UTF8.GetString(data);

                if (jsonResponse.Contains("\"audio\""))
                {
                    try
                    {
                        int audioStart = jsonResponse.IndexOf("\"audio\":") + 8;
                        while (audioStart < jsonResponse.Length && (jsonResponse[audioStart] == ' ' || jsonResponse[audioStart] == '"')) audioStart++;

                        int audioEnd = jsonResponse.IndexOf("\"", audioStart);
                        if (audioEnd > audioStart)
                        {
                            string b64 = jsonResponse.Substring(audioStart, audioEnd - audioStart);
                            byte[] pcmData = Convert.FromBase64String(b64);

                            if (pcmData.Length > 0)
                            {
                                if (TryEnqueuePcm(pcmAudioPlayer, pcmData))
                                {
                                    totalBytes += pcmData.Length;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[ElevenLabs] Error parsing chunk: {ex.Message}");
                    }
                }

                if (jsonResponse.Contains("\"isFinal\":true") || jsonResponse.Contains("\"isFinal\": true"))
                {
                    completed = true;
                }
            };

            // Connect (NativeWebSocket \u2014 works on all platforms including Quest/Android)
            _ = ws.Connect();

            float elapsed = 0f;
            while (!connected && !hasError && elapsed < 5f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!connected)
            {
                Debug.LogError($"[ElevenLabs-TTS] Connection failed or timeout.");
                activeWebSockets.Remove(ws);
                yield break;
            }

            // 1. Send Text + Key + Config
            // ElevenLabs allows sending the first message with "text" and "xi_api_key".
            // "try_trigger_generation": true ensures it starts immediately.
            var payload = new ElevenLabsPayload
            {
                text = text + " ", // Append space to ensure token closure
                xi_api_key = ResolvedElevenLabsApiKey,
                voice_settings = new ElevenLabsVoiceSettings { stability = 0.5f, similarity_boost = 0.75f },
                try_trigger_generation = true
            };
            string json = JsonUtility.ToJson(payload);
            _ = ws.SendText(json);
            yield return null; // Give one frame for send to process

            // 2. Send EOS (End of Stream) - explicitly needed for streaming endpoint
            var eosPayload = new ElevenLabsEOSPayload { text = "" }; // empty string signals EOS
            string eosJson = JsonUtility.ToJson(eosPayload);
            _ = ws.SendText(eosJson);

            Debug.Log("[ElevenLabs-TTS] Sent text & EOS. Receiving audio...");

            // 3. Wait for completion
            elapsed = 0f;
            while (!completed && elapsed < 20f) // 20s timeout
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (!completed) Debug.LogWarning("[ElevenLabs-TTS] Receive timeout.");

            // Close
            if (ws.State == WebSocketState.Open)
            {
                _ = ws.Close();
            }
            activeWebSockets.Remove(ws);
        }
        /// <summary>
        /// Speak text using Wit.ai TTS (Meta). Returns raw PCM for minimal latency.
        /// Free tier, very fast response, good for real-time conversation.
        /// </summary>
        public void SpeakWithWitAI(string text)
        {
            if (string.IsNullOrEmpty(ResolvedWitAiToken))
            {
                Debug.LogError("[Wit.ai-TTS] Wit.ai token is not set!");
                return;
            }
            StartCoroutine(WitAiTTSCoroutine(text));
        }

        private IEnumerator WitAiTTSCoroutine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("[Wit.ai-TTS] Empty text, skipping TTS.");
                yield break;
            }

            Debug.Log($"[Wit.ai-TTS] Requesting TTS: voice={witAiVoice}, text={text.Substring(0, Math.Min(50, text.Length))}...");

            // Wit.ai /synthesize endpoint uses POST
            string url = "https://api.wit.ai/synthesize?v=20220622";

            var requestData = new WitAiTTSRequest
            {
                q = text,
                voice = witAiVoice,
                speed = witAiSpeed,
                pitch = witAiPitch
            };
            string jsonBody = JsonUtility.ToJson(requestData);

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {ResolvedWitAiToken}");
                // Request raw PCM for fastest playback (no decode step)
                request.SetRequestHeader("Accept", "audio/raw");

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float latency = Time.realtimeSinceStartup - startTime;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[Wit.ai-TTS] Audio received in {latency:F2}s ({request.downloadHandler.data.Length} bytes)");
                    byte[] audioData = request.downloadHandler.data;
                    PlayAudioFromPCM16(audioData, witAiSampleRateHz);
                }
                else
                {
                    Debug.LogError($"[Wit.ai-TTS] Error: {request.error}\nResponse: {request.downloadHandler.text}");

                    // Fallback: try WAV format if raw PCM failed
                    Debug.Log("[Wit.ai-TTS] Retrying with audio/wav...");
                    yield return StartCoroutine(WitAiTTSFallbackWAV(text));
                }
            }
        }

        /// <summary>
        /// Fallback: request WAV from Wit.ai if raw PCM fails.
        /// </summary>
        private IEnumerator WitAiTTSFallbackWAV(string text)
        {
            string url = "https://api.wit.ai/synthesize?v=20220622";

            var requestData = new WitAiTTSRequest
            {
                q = text,
                voice = witAiVoice,
                speed = witAiSpeed,
                pitch = witAiPitch
            };
            string jsonBody = JsonUtility.ToJson(requestData);

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {ResolvedWitAiToken}");
                request.SetRequestHeader("Accept", "audio/wav");

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float latency = Time.realtimeSinceStartup - startTime;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[Wit.ai-TTS] WAV fallback received in {latency:F2}s");
                    PlayAudioFromWAV(request.downloadHandler.data);
                }
                else
                {
                    Debug.LogError($"[Wit.ai-TTS] WAV fallback also failed: {request.error}\nResponse: {request.downloadHandler.text}");
                }
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Test Methods  (call from Inspector button, console, or code)
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Quick test for OpenAI TTS — says a short sentence with the male voice.
        /// Hook this up to a UI button or call from the console.
        /// </summary>
        [ContextMenu("Test OpenAI TTS")]
        public void TestOpenAITTS()
        {
            Debug.Log("[TTS-Test] Testing OpenAI TTS...");
            Speak("Hello, this is a test of the OpenAI text to speech system.", ScenarioType.DoctorToDoctor);
        }

        /// <summary>
        /// Quick test for Native Device TTS (Quest 3) — instant, offline.
        /// </summary>
        [ContextMenu("Test Native Device TTS (Quest 3)")]
        public void TestNativeTTS()
        {
            Debug.Log("[TTS-Test] Testing Native Device TTS...");
            SpeakNative("Hello, this is a test of the native device text to speech. Zero latency, fully offline.");
        }

        /// <summary>
        /// Quick test for Wit.ai TTS — says a short sentence.
        /// Hook this up to a UI button or call from the console.
        /// </summary>
        [ContextMenu("Test Wit.ai TTS (Cloud)")]
        public void TestWitAITTS()
        {
            Debug.Log("[TTS-Test] Testing Wit.ai TTS...");
            SpeakWithWitAI("Hello, this is a test of the Wit.ai text to speech system.");
        }

        /// <summary>
        /// Quick test for Murf.ai TTS — premium quality, German language.
        /// Uses correct WebSocket protocol: voice_config, base64 audio, WAV header skip.
        /// </summary>
        [ContextMenu("Test Murf.ai TTS (German)")]
        public void TestMurfAITTS()
        {
            Debug.Log("[TTS-Test] Testing Murf.ai TTS (de-DE)...");
            SpeakWithMurfAI("Mit einer einzelnen WebSocket-Verbindung können Sie Texteingaben streamen und synthetisierte Audio kontinuierlich empfangen, ohne den Overhead wiederholter HTTP-Anfragen.");
        }

        /// <summary>
        /// Quick test for ElevenLabs TTS (Flash v2.5).
        /// </summary>
        [ContextMenu("Test ElevenLabs TTS (English)")]
        public void TestElevenLabsTTS()
        {
            Debug.Log("[TTS-Test] Testing ElevenLabs TTS...");
            SpeakWithElevenLabs("Hello! This is a streaming test using the fastest ElevenLabs model available via WebSocket.");
        }

        /// <summary>
        /// Quick A/B comparison — Native (fastest) vs OpenAI (best quality)
        /// </summary>
        [ContextMenu("Test A/B: Native vs OpenAI")]
        public void TestBothTTS()
        {
            Debug.Log("[TTS-Test] A/B comparison — Native first, then OpenAI.");
            StartCoroutine(ABCompareCoroutine());
        }

        private IEnumerator ABCompareCoroutine()
        {
            string testSentence = "The patient presents with mild fever and elevated white blood cell count.";

            // Native (instant)
            Debug.Log("[TTS-Test] >>> Native Device TTS (instant)");
            SpeakNative(testSentence);

            // Wait for native to finish
            yield return new WaitForSeconds(5f);

            // OpenAI (network)
            Debug.Log("[TTS-Test] >>> OpenAI TTS (network)");
            TTSProvider savedProvider = defaultProvider;
            defaultProvider = TTSProvider.OpenAI;
            Speak(testSentence, ScenarioType.DoctorToDoctor);

            defaultProvider = savedProvider;
        }

        private string GetAPIKey()
        {
            // The shared APIKeyConfig asset wins when set, so rotating one key there fixes
            // every component even if a stale key is still serialized on this one.
            if (APIKeyConfig.Instance != null && !string.IsNullOrEmpty(APIKeyConfig.Instance.OpenAIApiKey))
                return APIKeyConfig.Instance.OpenAIApiKey;

            // Try Inspector field
            if (!string.IsNullOrEmpty(apiKey))
                return apiKey;

            // Try environment variable
            string envKey = System.Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrEmpty(envKey))
                return envKey;

            // Try to find OpenAIConfig in scene
            var config = FindFirstObjectByType<OpenAI.OpenAIConfig>();
            if (config != null && !string.IsNullOrEmpty(config.apiKey))
                return config.apiKey;

            return null;
        }

        /// <summary>
        /// Gets fixed female OpenAI voices by scenario role.
        /// We intentionally ignore scenario CSV eval voice overrides to enforce consistent female voices.
        /// </summary>
        private string GetOpenAIVoiceForScenario(ScenarioType scenario)
        {
            return scenario == ScenarioType.DoctorToDoctor ? femaleVoiceDoctorToDoctor : femaleVoiceDoctorToPatient;
        }

        private IEnumerator TextToSpeechCoroutine(string text, string voice, string key)
        {
            if (string.IsNullOrEmpty(text))
            {
                Debug.LogWarning("[OpenAI-TTS] Empty text, skipping TTS.");
                yield break;
            }

            Debug.Log($"[OpenAI-TTS] Requesting TTS: voice={voice}, text={text.Substring(0, Math.Min(50, text.Length))}...");

            string url = "https://api.openai.com/v1/audio/speech";

            string fmt = string.IsNullOrWhiteSpace(responseFormat) ? "pcm" : responseFormat.Trim().ToLowerInvariant();

            // Prefer PCM to avoid Unity/FMOD decode failures and to avoid temp-file I/O.
            // WAV/MP3 can be used as fallback for environments where PCM isn't supported.
            var requestData = new OpenAITTSRequest
            {
                model = this.model,
                input = text,
                voice = voice,
                response_format = fmt,
                speed = 1.0f
            };
            string jsonBody = JsonUtility.ToJson(requestData);

            using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonBody);
                request.uploadHandler = new UploadHandlerRaw(bodyRaw);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {key}");

                float startTime = Time.realtimeSinceStartup;
                yield return request.SendWebRequest();
                float latency = Time.realtimeSinceStartup - startTime;

                if (request.result == UnityWebRequest.Result.Success)
                {
                    Debug.Log($"[OpenAI-TTS] Audio received in {latency:F2}s");
                    byte[] audioData = request.downloadHandler.data;
                    if (fmt == "pcm")
                        PlayAudioFromPCM16(audioData, pcmSampleRateHz);
                    else if (fmt == "wav")
                        PlayAudioFromWAV(audioData);
                    else
                        PlayAudioFromMP3(audioData);
                }
                else
                {
                    Debug.LogError($"[OpenAI-TTS] Error: {request.error}\nResponse: {request.downloadHandler.text}");
                }
            }
        }

        private void PlayAudioFromPCM16(byte[] pcm16le, int sampleRateHz)
        {
            if (audioSource == null)
            {
                Debug.LogError("[OpenAI-TTS] No AudioSource assigned!");
                return;
            }

            if (pcm16le == null || pcm16le.Length < 2)
            {
                Debug.LogError("[OpenAI-TTS] PCM payload empty.");
                return;
            }

            if (sampleRateHz <= 0) sampleRateHz = 24000;

            // Convert signed 16-bit little-endian PCM to Unity float samples.
            int sampleCount = pcm16le.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0, si = 0; si < sampleCount; si++, i += 2)
            {
                short s = (short)(pcm16le[i] | (pcm16le[i + 1] << 8));
                samples[si] = Mathf.Clamp(s / 32768f, -1f, 1f);
            }

            // Create clip (mono)
            var clip = AudioClip.Create("openai_tts_pcm", sampleCount, 1, sampleRateHz, stream: false);
            clip.SetData(samples, 0);
            audioSource.clip = clip;
            audioSource.Play();
        }

        // WAV fallback (some platforms/loaders can still fail; PCM is preferred)

        private void PlayAudioFromWAV(byte[] wavData)
        {
            if (audioSource == null)
            {
                Debug.LogError("[OpenAI-TTS] No AudioSource assigned!");
                return;
            }

            StartCoroutine(LoadAndPlayWAV(wavData));
        }

        // MP3 fallback (slowest; requires decode)
        private void PlayAudioFromMP3(byte[] mp3Data)
        {
            if (audioSource == null)
            {
                Debug.LogError("[OpenAI-TTS] No AudioSource assigned!");
                return;
            }

            StartCoroutine(LoadAndPlayMP3(mp3Data));
        }

        private IEnumerator LoadAndPlayMP3(byte[] mp3Data)
        {
            if (mp3Data == null || mp3Data.Length == 0)
            {
                Debug.LogError("[OpenAI-TTS] No MP3 data to play.");
                yield break;
            }

            string tempPath = System.IO.Path.Combine(
                Application.temporaryCachePath,
                $"openai_tts_{DateTime.UtcNow.Ticks}_{UnityEngine.Random.Range(0, 100000)}.mp3");
            System.IO.File.WriteAllBytes(tempPath, mp3Data);

            string fileUri;
            try { fileUri = new Uri(tempPath).AbsoluteUri; }
            catch { yield break; }

            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.MPEG))
            {
                yield return www.SendWebRequest();
                if (www.result == UnityWebRequest.Result.Success)
                {
                    var audioClip = DownloadHandlerAudioClip.GetContent(www);
                    if (audioClip != null)
                    {
                        audioSource.clip = audioClip;
                        audioSource.Play();
                    }
                }
                else
                {
                    Debug.LogError($"[OpenAI-TTS] Failed to load MP3: {www.error}");
                }
            }

            try { System.IO.File.Delete(tempPath); } catch { }
        }

        private IEnumerator LoadAndPlayWAV(byte[] wavData)
        {
            // OPTIMIZATION: Use WAV temp file (no MP3 decoding overhead!)
            // WAV is uncompressed PCM - Unity loads it directly without decoding
            if (wavData == null || wavData.Length == 0)
            {
                Debug.LogError("[OpenAI-TTS] No audio data to play.");
                yield break;
            }

            Debug.Log($"[OpenAI-TTS] WAV data received: {wavData.Length} bytes");

            // Validate WAV header to avoid feeding FMOD invalid bytes (e.g., HTML/JSON error bodies).
            // WAV should start with 'RIFF' and contain 'WAVE' at bytes 8-11.
            if (wavData.Length < 12)
            {
                Debug.LogError($"[OpenAI-TTS] WAV data too small ({wavData.Length} bytes)");
                yield break;
            }

            string header = System.Text.Encoding.ASCII.GetString(wavData, 0, 4);
            string format = System.Text.Encoding.ASCII.GetString(wavData, 8, 4);
            Debug.Log($"[OpenAI-TTS] WAV Header: '{header}', Format: '{format}'");

            if (header != "RIFF" || format != "WAVE")
            {
                int previewLen = Mathf.Min(64, wavData.Length);
                string hex = BitConverter.ToString(wavData, 0, previewLen);
                Debug.LogError($"[OpenAI-TTS] Invalid WAV header (len={wavData.Length}). First {previewLen} bytes: {hex}");
                yield break;
            }

            // Write WAV to a UNIQUE temp file.
            // Important: multiple TTS calls can overlap; a fixed filename causes races/overwrites and FMOD load failures.
            string tempPath = System.IO.Path.Combine(
                Application.temporaryCachePath,
                $"murf_tts_{DateTime.UtcNow.Ticks}_{UnityEngine.Random.Range(0, 100000)}.wav");
            
            System.IO.File.WriteAllBytes(tempPath, wavData);
            Debug.Log($"[OpenAI-TTS] WAV saved to: {tempPath}");

            string fileUri;
            try
            {
                // Produces a correct file:/// URI (and escapes special characters).
                fileUri = new Uri(tempPath).AbsoluteUri;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[OpenAI-TTS] Failed to build file URI for '{tempPath}': {ex.Message}");
                yield break;
            }

            bool deleteTempFile = true;

            // Load audio clip from temp file
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.WAV))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip audioClip;
                    try
                    {
                        audioClip = DownloadHandlerAudioClip.GetContent(www);
                    }
                    catch (Exception ex)
                    {
                        deleteTempFile = false;
                        Debug.LogError($"[OpenAI-TTS] WAV decoded but FMOD failed to create sound. Keeping temp file for inspection: {tempPath}. Error: {ex.Message}");
                        yield break;
                    }
                    if (audioClip != null)
                    {
                        Debug.Log($"[OpenAI-TTS] ✅ Loaded WAV clip: {audioClip.length:F2}s, {audioClip.frequency}Hz, {audioClip.channels}ch");
                        
                        if (audioSource == null)
                        {
                            Debug.LogError("[OpenAI-TTS] No AudioSource assigned!");
                            yield break;
                        }

                        audioSource.clip = audioClip;
                        audioSource.Play();
                        Debug.Log($"[OpenAI-TTS] ▶️ Playing WAV audio ({audioClip.length:F2}s)");
                    }
                    else
                    {
                        deleteTempFile = false;
                        Debug.LogError("[OpenAI-TTS] Failed to load audio clip (null).");
                    }
                }
                else
                {
                    deleteTempFile = false;
                    Debug.LogError($"[OpenAI-TTS] Failed to load WAV: {www.error}");
                }
            }

            // Clean up temp file
            if (deleteTempFile)
            {
                try
                {
                    System.IO.File.Delete(tempPath);
                }
                catch { }
            }
            else
            {
                Debug.LogWarning($"[OpenAI-TTS] Temp WAV kept for debugging: {tempPath}");
            }
        }

        [System.Serializable]
        private class OpenAITTSRequest
        {
            public string model;
            public string input;
            public string voice;
            public string response_format;
            public float speed;
        }

        [System.Serializable]
        private class WitAiTTSRequest
        {
            public string q;
            public string voice;
            public int speed;
            public int pitch;
        }

        [System.Serializable]
        private class MurfAiTTSRequest
        {
            public string text;
            public string voice_id;
            public string model;
            public string multi_native_locale;
            public int sample_rate;
        }

        [System.Serializable]
        private class MurfVoiceConfig
        {
            public string voice_id;
            public string voice_code;
            public string language_code;
            public float rate;
        }

        [System.Serializable]
        private class ElevenLabsPayload
        {
            public string text;
            public string xi_api_key;
            public ElevenLabsVoiceSettings voice_settings;
            public bool try_trigger_generation;
        }

        [System.Serializable]
        private class ElevenLabsVoiceSettings
        {
            public float stability;
            public float similarity_boost;
        }

        [System.Serializable]
        private class ElevenLabsEOSPayload
        {
            public string text;
        }

        // Murf WebSocket Serialization Helpers
        [System.Serializable]
        private class MurfWsInit { public MurfWsVoiceConfig voice_config; }
        [System.Serializable]
        private class MurfWsVoiceConfig { public string voiceId; public string multiNativeLocale; public string style; public int rate; public int pitch; public int variation; }
        [System.Serializable]
        private class MurfWsText { public string text; public bool end; }
    }
}
