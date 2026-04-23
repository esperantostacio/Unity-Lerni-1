using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[Serializable]
public class PcmSegmentEvent : UnityEvent<byte[]> { }

/// <summary>
/// Listens to Base64 PCM chunks from MicrophoneStreamer, buffers a speech segment
/// and fires OnSegmentComplete when silence >= RequiredSilenceSeconds.
/// Intended for Whisper-style post-hoc transcription triggers.
/// </summary>
public class WhisperRecorder : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("Assign the MicrophoneStreamer that produces OnAudioChunk events.")]
    [SerializeField] private MicrophoneStreamer micStreamer;

    [Header("Silence Detection")]
    [Tooltip("RMS threshold to consider a chunk as 'speech'.")]
    [SerializeField, Range(0f, 0.2f)] private float rmsThreshold = 0.03f;

    [Tooltip("Seconds of continuous silence to mark end of segment.")]
    [SerializeField] private float requiredSilenceSeconds = 2.0f;

    [Tooltip("Maximum seconds to allow per captured segment (safety cap).")]
    [SerializeField] private float maxSegmentSeconds = 10f;

    [Header("Behavior")]
    [Tooltip("If true, subscribes automatically on Start().")]
    [SerializeField] private bool autoSubscribe = true;

    [Header("Events")]
    public PcmSegmentEvent OnSegmentComplete;

    private List<byte> _buffer = new List<byte>();
    private bool _recording = false;
    private double _lastLoudTime = 0;
    private double _segmentStartTime = 0;
    private float _chunkDurationSeconds = 0.064f; // approx 1024 samples @ 16kHz

    private bool _subscribed;
    private double _lastChunkTime;
    private double _nextIdleRmsLogTime;

    private void Start()
    {
        if (autoSubscribe) Subscribe();
    }

    private void OnEnable() { if (autoSubscribe) Subscribe(); }
    private void OnDisable() { Unsubscribe(); }

    public void Subscribe()
    {
        if (micStreamer == null)
        {
            Debug.LogWarning("WhisperRecorder: micStreamer not assigned.");
            return;
        }
        if (_subscribed)
            return;

        micStreamer.OnAudioChunk += OnAudioChunk;
        _subscribed = true;
        _lastChunkTime = Time.realtimeSinceStartupAsDouble;
        _nextIdleRmsLogTime = 0;
        Debug.Log("WhisperRecorder: Subscribed to MicrophoneStreamer.");
    }

    public void Unsubscribe()
    {
        if (micStreamer == null) return;
        if (!_subscribed)
            return;

        micStreamer.OnAudioChunk -= OnAudioChunk;
        _subscribed = false;
        Debug.Log("WhisperRecorder: Unsubscribed from MicrophoneStreamer.");
    }

    public void SetMicrophoneStreamer(MicrophoneStreamer streamer, bool resubscribe = true)
    {
        if (streamer == micStreamer)
            return;

        if (_subscribed)
            Unsubscribe();

        micStreamer = streamer;

        if (resubscribe && isActiveAndEnabled)
            Subscribe();
    }

    private void OnAudioChunk(string b64)
    {
        if (string.IsNullOrEmpty(b64)) return;

        byte[] pcm;
        try { pcm = Convert.FromBase64String(b64); }
        catch { return; }

        float rms = ComputeRmsFromPcm16(pcm);
        double now = Time.realtimeSinceStartupAsDouble;

        _lastChunkTime = now;

        // Lightweight debug to help tune rmsThreshold if nothing ever triggers.
        if (!_recording && now >= _nextIdleRmsLogTime)
        {
            _nextIdleRmsLogTime = now + 1.0;
           // Debug.Log($"WhisperRecorder: idle rms={rms:F3} (threshold={rmsThreshold:F3})");
        }

        if (!_recording)
        {
            if (rms >= rmsThreshold)
            {
                // start recording
                _recording = true;
                _buffer.Clear();
                _segmentStartTime = now;
                _lastLoudTime = now;
                _buffer.AddRange(pcm);
                Debug.Log($"WhisperRecorder: Started segment (rms={rms:F3})");
            }
            // else ignore until we detect loud enough chunk
        }
        else
        {
            // append
            _buffer.AddRange(pcm);
            if (rms >= rmsThreshold)
            {
                // Extend silence timer by 2 seconds from now
                _lastLoudTime = now;
            }

            // safety cap: never exceed maxSegmentSeconds
            if (now - _segmentStartTime > maxSegmentSeconds)
            {
                FinishSegment();
                return;
            }

            // check silence timer: finish if 2 seconds of silence
            if (now - _lastLoudTime >= requiredSilenceSeconds)
            {
                FinishSegment();
            }
        }
    }

    private void FinishSegment()
    {
        if (!_recording) return;
        _recording = false;

        var outBytes = _buffer.ToArray();
        Debug.Log($"WhisperRecorder: Segment complete ({outBytes.Length} bytes). Raising event.");
        OnSegmentComplete?.Invoke(outBytes);
        _buffer.Clear();
    }

    private static float ComputeRmsFromPcm16(byte[] pcm)
    {
        if (pcm == null || pcm.Length < 2) return 0f;
        int samples = pcm.Length / 2;
        double sumSq = 0;
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
            float f = s / 32767f;
            sumSq += (double)f * f;
        }
        return Mathf.Sqrt((float)(sumSq / samples));
    }

    // Optional runtime helpers for quick checks
    public void TestMicAvailable()
    {
        if (Microphone.devices.Length == 0)
            Debug.LogError("No microphone devices detected.");
        else
            Debug.Log($"Microphone devices: {string.Join(", ", Microphone.devices)}");
    }
}
