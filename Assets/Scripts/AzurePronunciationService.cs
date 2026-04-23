using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json.Linq;

namespace MedicalExam
{
    [Serializable]
    public class AzurePronunciationResult
    {
        public float accuracyScore;      // 0–100
        public float fluencyScore;       // 0–100
        public float completenessScore;  // 0–100
        public float prosodyScore;       // 0–100
        public float overallScore;       // weighted average
        public string feedback;          // German summary sentence
        public string[] problematicWords;
    }

    /// <summary>
    /// Per-turn pronunciation assessment using Azure Cognitive Services Speech REST API.
    /// Taps the audio already being captured by RealtimeMicrophone — no second Microphone.Start() needed.
    ///
    /// Setup:
    ///   1. Add this component to the same GameObject as RealtimeMicrophone.
    ///   2. Assign the RealtimeMicrophone reference in the Inspector.
    ///   3. Fill in your Azure subscriptionKey and region.
    ///   4. In MedicalExamManager, enable useAzurePronunciation and assign this component.
    /// </summary>
    public class AzurePronunciationService : MonoBehaviour
    {
        [Header("Azure Speech Service")]
        [Tooltip("Azure Cognitive Services Speech subscription key.")]
        [SerializeField] private string subscriptionKey = "";

        [Tooltip("Azure region, e.g. westeurope, germanywestcentral.")]
        [SerializeField] private string region = "westeurope";

        [Tooltip("BCP-47 language tag for the exam language.")]
        [SerializeField] private string language = "de-DE";

        [Tooltip("HTTP timeout for Azure requests.")]
        [SerializeField] private int timeoutSeconds = 30;

        [Header("Audio Source")]
        [Tooltip("The RealtimeMicrophone that is already capturing audio. We read from its clip.")]
        [SerializeField] private RealtimeMicrophone realtimeMicrophone;

        // Target sample rate Azure Speech expects
        private const int AZURE_SAMPLE_RATE = 16000;

        // Position in the mic clip at the start of the current turn (in mic-native samples)
        private int _turnStartSample;
        private bool _turnMarked;

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(subscriptionKey) &&
            !string.IsNullOrWhiteSpace(region) &&
            realtimeMicrophone != null;

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Call this when the user starts speaking (before their turn audio begins).
        /// MedicalExamManager should call this at the start of each pronunciation request.
        /// </summary>
        public void MarkTurnStart()
        {
            if (!IsConfigured || !realtimeMicrophone.IsActivelyRecording) return;
            _turnStartSample = realtimeMicrophone.CurrentSamplePosition;
            _turnMarked = true;
            Debug.Log($"[Azure] Turn start marked at sample {_turnStartSample}.");
        }

        /// <summary>
        /// Call this when the user's turn transcript arrives.
        /// Extracts audio from the marked start to now and posts it to Azure.
        /// </summary>
        public void EvaluateTurn(
            string referenceTranscript,
            Action<AzurePronunciationResult> onResult,
            Action<string> onError)
        {
            if (!IsConfigured)
            {
                onError?.Invoke("[Azure] Not configured — check subscriptionKey, region, and RealtimeMicrophone reference.");
                return;
            }

            if (!_turnMarked)
            {
                onError?.Invoke("[Azure] MarkTurnStart() was not called before EvaluateTurn().");
                return;
            }

            AudioClip clip = realtimeMicrophone.RecordingClip;
            int micFreq    = realtimeMicrophone.RecordingFrequency;
            int endSample  = realtimeMicrophone.CurrentSamplePosition;

            if (clip == null)
            {
                onError?.Invoke("[Azure] RealtimeMicrophone has no active recording clip.");
                return;
            }

            float[] samples = ExtractSamples(clip, _turnStartSample, endSample);
            _turnMarked = false;

            if (samples == null || samples.Length < micFreq / 4)  // < 250ms = skip
            {
                onError?.Invoke("[Azure] Turn audio too short to assess.");
                return;
            }

            float[] resampled = Resample(samples, micFreq, AZURE_SAMPLE_RATE);
            byte[] wav = FloatToWav(resampled, AZURE_SAMPLE_RATE);

            StartCoroutine(PostToAzure(wav, referenceTranscript, onResult, onError));
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        private float[] ExtractSamples(AudioClip clip, int start, int end)
        {
            int total = clip.samples * clip.channels;
            int count;

            if (end >= start)
                count = end - start;
            else
                count = clip.samples - start + end;   // wrapped around

            if (count <= 0) return null;

            float[] all = new float[total];
            clip.GetData(all, 0);

            float[] turn = new float[count];
            for (int i = 0; i < count; i++)
                turn[i] = all[(start + i) % clip.samples];

            return turn;
        }

        private static float[] Resample(float[] input, int inRate, int outRate)
        {
            if (inRate == outRate) return input;

            int outCount = Mathf.FloorToInt(input.Length * (float)outRate / inRate);
            float[] output = new float[outCount];
            float ratio = (float)inRate / outRate;

            for (int i = 0; i < outCount; i++)
            {
                float srcIdx = i * ratio;
                int i0 = Mathf.Clamp((int)srcIdx, 0, input.Length - 1);
                int i1 = Mathf.Clamp(i0 + 1,      0, input.Length - 1);
                output[i] = Mathf.Lerp(input[i0], input[i1], srcIdx - i0);
            }

            return output;
        }

        private static byte[] FloatToWav(float[] samples, int sampleRate)
        {
            int byteCount = samples.Length * 2;  // 16-bit PCM = 2 bytes per sample
            byte[] wav = new byte[44 + byteCount];

            // RIFF header
            Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
            BitConverter.GetBytes(36 + byteCount).CopyTo(wav, 4);
            Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
            Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
            BitConverter.GetBytes(16).CopyTo(wav, 16);           // PCM chunk size
            BitConverter.GetBytes((short)1).CopyTo(wav, 20);     // PCM format
            BitConverter.GetBytes((short)1).CopyTo(wav, 22);     // mono
            BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
            BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28); // byte rate
            BitConverter.GetBytes((short)2).CopyTo(wav, 32);     // block align
            BitConverter.GetBytes((short)16).CopyTo(wav, 34);    // bits per sample
            Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
            BitConverter.GetBytes(byteCount).CopyTo(wav, 40);

            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)Mathf.Clamp(samples[i] * 32767f, -32768f, 32767f);
                wav[44 + i * 2]     = (byte)(s & 0xFF);
                wav[44 + i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            return wav;
        }

        private IEnumerator PostToAzure(
            byte[] wav,
            string referenceTranscript,
            Action<AzurePronunciationResult> onResult,
            Action<string> onError)
        {
            string url = $"https://{region}.stt.speech.microsoft.com/speech/recognition/conversation" +
                         $"/cognitiveservices/v1?language={language}&format=detailed";

            // Pronunciation Assessment config (HundredMark, Word-level, Comprehensive)
            var cfg = new JObject
            {
                ["GradingSystem"] = "HundredMark",
                ["Granularity"]   = "Word",
                ["Dimension"]     = "Comprehensive",
            };
            // Enable miscue analysis only when we have a reference text
            if (!string.IsNullOrWhiteSpace(referenceTranscript))
            {
                cfg["ReferenceText"]  = referenceTranscript;
                cfg["EnableMiscue"]   = true;
            }

            string cfgBase64 = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(cfg.ToString(Newtonsoft.Json.Formatting.None)));

            using var req = new UnityWebRequest(url, "POST");
            req.uploadHandler   = new UploadHandlerRaw(wav);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type",             "audio/wav; codecs=audio/pcm; samplerate=16000");
            req.SetRequestHeader("Ocp-Apim-Subscription-Key", subscriptionKey);
            req.SetRequestHeader("Pronunciation-Assessment",  cfgBase64);
            req.timeout = Mathf.Clamp(timeoutSeconds, 10, 60);

            Debug.Log($"[Azure] Posting {wav.Length / 1024} KB audio for pronunciation assessment.");
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                onError?.Invoke($"[Azure] HTTP {req.responseCode}: {req.error}. {req.downloadHandler?.text}");
                yield break;
            }

            try
            {
                var resp  = JObject.Parse(req.downloadHandler.text);
                var nBest = resp["NBest"]?[0];
                var pa    = nBest?["PronunciationAssessment"];

                if (pa == null)
                {
                    onError?.Invoke("[Azure] No PronunciationAssessment field in response.");
                    yield break;
                }

                float acc  = pa["AccuracyScore"]?.Value<float>()      ?? 0f;
                float flu  = pa["FluencyScore"]?.Value<float>()        ?? 0f;
                float comp = pa["CompletenessScore"]?.Value<float>()   ?? 0f;
                float pros = pa["ProsodyScore"]?.Value<float>()        ?? 0f;
                // Weighted: accuracy 40%, fluency 30%, completeness 15%, prosody 15%
                float overall = acc * 0.4f + flu * 0.3f + comp * 0.15f + pros * 0.15f;

                // Collect words with low accuracy (< 60) as problem words
                var wordNodes = nBest?["Words"] as JArray;
                var bad = new List<string>();
                if (wordNodes != null)
                {
                    foreach (var w in wordNodes)
                    {
                        float wacc = w["PronunciationAssessment"]?["AccuracyScore"]?.Value<float>() ?? 100f;
                        if (wacc < 60f)
                            bad.Add(w["Word"]?.ToString() ?? "");
                    }
                }

                string fb = $"Aussprache: {acc:0}% Genauigkeit, {flu:0}% Flüssigkeit, " +
                            $"{comp:0}% Vollständigkeit, {pros:0}% Prosodie.";
                if (bad.Count > 0)
                    fb += $" Problematische Wörter: {string.Join(", ", bad)}.";

                var result = new AzurePronunciationResult
                {
                    accuracyScore      = acc,
                    fluencyScore       = flu,
                    completenessScore  = comp,
                    prosodyScore       = pros,
                    overallScore       = overall,
                    feedback           = fb,
                    problematicWords   = bad.ToArray()
                };

                Debug.Log($"[Azure] Scores — acc={acc:0} flu={flu:0} comp={comp:0} pros={pros:0} overall={overall:0.0}");
                onResult?.Invoke(result);
            }
            catch (Exception e)
            {
                onError?.Invoke($"[Azure] Parse error: {e.Message}\nBody: {req.downloadHandler?.text}");
            }
        }
    }
}
