using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

namespace MedicalExam
{
    /// <summary>
    /// Simple OpenAI TTS player for non-streaming use cases (welcome messages, evaluation readout).
    /// Downloads the full audio from OpenAI's TTS API, then plays it on a dedicated AudioSource.
    /// This is separate from the realtime API PCM streaming used during conversations.
    /// </summary>
    public class OpenAITTSPlayer : MonoBehaviour
    {
        [Header("OpenAI Config")]
        [Tooltip("If assigned, uses apiKey from this config. Otherwise set apiKey below.")]
        [SerializeField] private OpenAI.OpenAIConfig openAIConfig;

        [Tooltip("OpenAI API key (used if OpenAIConfig is not assigned)")]
        [SerializeField] private string apiKey = "";

        [Header("Voice Settings")]
        [Tooltip("Fixed female voice for Doctor-to-Doctor output")]
        [SerializeField] private string voiceDoctorToDoctor = "shimmer";

        [Tooltip("Fixed female voice for Doctor-to-Patient output")]
        [SerializeField] private string voiceDoctorToPatient = "nova";

        [Header("Model")]
        [Tooltip("Preferred: gpt-audio-1.5. Legacy options: gpt-4o-mini-tts, tts-1, tts-1-hd")]
        [SerializeField] private string model = "gpt-audio-1.5";

        [Header("Audio")]
        [Tooltip("Dedicated AudioSource for TTS playback (should NOT be the realtime PCM AudioSource)")]
        [SerializeField] private AudioSource audioSource;

        [Tooltip("PCM sample rate (OpenAI TTS default is 24000 Hz)")]
        [SerializeField] private int sampleRateHz = 24000;

        /// <summary>Returns the AudioSource used for playback.</summary>
        public AudioSource PlaybackAudioSource => audioSource;

        /// <summary>True while audio is currently playing.</summary>
        public bool IsPlaying => audioSource != null && audioSource.isPlaying;

        public enum VoiceType { DoctorToDoctor, DoctorToPatient }

        private Coroutine _currentCoroutine;

        private void Start()
        {
            if (audioSource == null)
            {
                Debug.LogWarning("[OpenAITTSPlayer] No AudioSource assigned. TTS will not play.");
            }
        }

        /// <summary>
        /// Speak text using OpenAI TTS. Non-blocking (starts coroutine).
        /// </summary>
        public void Speak(string text, VoiceType voiceType = VoiceType.DoctorToDoctor)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[OpenAITTSPlayer] Empty text, skipping.");
                return;
            }

            string key = GetAPIKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("[OpenAITTSPlayer] No API key available!");
                return;
            }

            string voice = GetVoiceForType(voiceType);
            _currentCoroutine = StartCoroutine(SpeakCoroutine(text, voice, key));
        }

        /// <summary>
        /// Speak text and wait for completion. Use with yield return StartCoroutine(...).
        /// </summary>
        public IEnumerator SpeakAndWait(string text, VoiceType voiceType = VoiceType.DoctorToDoctor)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[OpenAITTSPlayer] Empty text, skipping.");
                yield break;
            }

            string key = GetAPIKey();
            if (string.IsNullOrWhiteSpace(key))
            {
                Debug.LogError("[OpenAITTSPlayer] No API key available!");
                yield break;
            }

            string voice = GetVoiceForType(voiceType);
            yield return SpeakCoroutine(text, voice, key);

            // Wait for playback to finish
            while (audioSource != null && audioSource.isPlaying)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Stop any currently playing TTS audio.
        /// </summary>
        public void Stop()
        {
            if (_currentCoroutine != null)
            {
                StopCoroutine(_currentCoroutine);
                _currentCoroutine = null;
            }

            if (audioSource != null && audioSource.isPlaying)
            {
                audioSource.Stop();
            }
        }

        private IEnumerator SpeakCoroutine(string text, string voice, string key)
        {
            string url = "https://api.openai.com/v1/audio/speech";

            string truncatedPreview = text.Length > 60 ? text.Substring(0, 60) + "..." : text;
            Debug.Log($"[OpenAITTSPlayer] Requesting TTS: voice={voice}, text=\"{truncatedPreview}\"");

            var requestBody = new TTSRequest
            {
                model = this.model,
                input = text,
                voice = voice,
                response_format = "pcm",
                speed = 1.0f,
                language = "de"
            };
            string jsonBody = JsonUtility.ToJson(requestBody);

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
                    Debug.Log($"[OpenAITTSPlayer] Audio received in {latency:F2}s ({request.downloadHandler.data.Length} bytes)");
                    PlayPCM16(request.downloadHandler.data);
                }
                else
                {
                    Debug.LogError($"[OpenAITTSPlayer] API error: {request.error}\nResponse: {request.downloadHandler?.text}");
                }
            }

            _currentCoroutine = null;
        }

        private void PlayPCM16(byte[] pcm16le)
        {
            if (audioSource == null)
            {
                Debug.LogError("[OpenAITTSPlayer] No AudioSource assigned!");
                return;
            }

            if (pcm16le == null || pcm16le.Length < 2)
            {
                Debug.LogError("[OpenAITTSPlayer] PCM data is empty.");
                return;
            }

            int rate = sampleRateHz > 0 ? sampleRateHz : 24000;

            // Convert signed 16-bit little-endian PCM to Unity float samples
            int sampleCount = pcm16le.Length / 2;
            float[] samples = new float[sampleCount];
            for (int i = 0, si = 0; si < sampleCount; si++, i += 2)
            {
                short s = (short)(pcm16le[i] | (pcm16le[i + 1] << 8));
                samples[si] = Mathf.Clamp(s / 32768f, -1f, 1f);
            }

            // Create mono AudioClip and play
            AudioClip clip = AudioClip.Create("openai_tts", sampleCount, 1, rate, false);
            clip.SetData(samples, 0);
            audioSource.clip = clip;
            audioSource.Play();

            Debug.Log($"[OpenAITTSPlayer] Playing {sampleCount} samples ({(float)sampleCount / rate:F1}s) at {rate}Hz");
        }

        private string GetAPIKey()
        {
            // 1. Shared APIKeyConfig asset — wins when set, so rotating one key there fixes
            //    every component even if a stale key is still serialized on this one.
            if (APIKeyConfig.Instance != null && !string.IsNullOrWhiteSpace(APIKeyConfig.Instance.OpenAIApiKey))
                return APIKeyConfig.Instance.OpenAIApiKey;

            // 2. OpenAIConfig ScriptableObject
            if (openAIConfig != null && !string.IsNullOrWhiteSpace(openAIConfig.apiKey))
                return openAIConfig.apiKey;

            // 3. Inspector field
            if (!string.IsNullOrWhiteSpace(apiKey))
                return apiKey;

            // 4. Environment variable
            string envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(envKey))
                return envKey;

            // 5. Find OpenAIConfig in scene
            var config = FindFirstObjectByType<OpenAI.OpenAIConfig>();
            if (config != null && !string.IsNullOrWhiteSpace(config.apiKey))
                return config.apiKey;

            return null;
        }

        /// <summary>
        /// Gets the fixed female voice for the given role.
        /// We intentionally ignore scenario CSV voice overrides to keep output stable.
        /// </summary>
        private string GetVoiceForType(VoiceType voiceType)
        {
            return voiceType == VoiceType.DoctorToDoctor ? voiceDoctorToDoctor : voiceDoctorToPatient;
        }

        /// <summary>
        /// Splits long text into chunks for sequential TTS calls.
        /// </summary>
        public static List<string> SplitTextForTts(string text, int maxChars = 900)
        {
            var chunks = new List<string>();
            if (string.IsNullOrWhiteSpace(text)) return chunks;

            string normalized = text.Replace("\r\n", "\n").Trim();
            if (normalized.Length <= maxChars)
            {
                chunks.Add(normalized);
                return chunks;
            }

            // Split on sentence boundaries
            string[] sentences = normalized.Split(new[] { ". ", "! ", "? ", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var current = new System.Text.StringBuilder(maxChars + 64);

            foreach (string raw in sentences)
            {
                string s = raw.Trim();
                if (s.Length == 0) continue;

                if (!s.EndsWith(".") && !s.EndsWith("!") && !s.EndsWith("?"))
                    s += ".";

                if (current.Length + s.Length + 1 > maxChars && current.Length > 0)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Length = 0;
                }

                if (current.Length > 0) current.Append(' ');
                current.Append(s);
            }

            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());

            return chunks;
        }

        [Serializable]
        private class TTSRequest
        {
            public string model;
            public string input;
            public string voice;
            public string response_format;
            public float speed;
            public string language;
        }
    }
}
