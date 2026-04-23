using System;
using UnityEngine;

/// <summary>
/// Captures microphone audio, resamples it to 24kHz PCM16, 
/// and sends it to the OpenAIRealtimeClient.
/// </summary>
public class RealtimeMicrophone : MonoBehaviour
{
    [Header("Dependencies")]
    [Tooltip("Reference to the OpenAIRealtimeClient that will receive audio data.")]
    [SerializeField] private OpenAIRealtimeClient realtimeClient;

    [Header("Microphone Settings")]
    [Tooltip("Target sample rate for OpenAI Realtime API (default 24000Hz).")]
    [SerializeField] private int targetSampleRate = 24000;
    
    [Tooltip("Frequency to request from the microphone (default 44100Hz or 48000Hz usually works best).")]
    [SerializeField] private int microphoneRecordingRate = 48000;

    [Tooltip("Length of the recording buffer in seconds.")]
    [SerializeField] private int bufferLengthSec = 10;

    [Tooltip("Microphone sensitivity / gain (0 = muted, 1 = full volume). Lower this if the mic is too loud.")]
    [Range(0f, 1f)]
    [SerializeField] private float micSensitivity = 0.5f;

    // Internal state
    private AudioClip _microphoneClip;
    private string _device;
    private int _lastSamplePos;
    private bool _isRecording;

    // Public accessors for Azure Pronunciation Assessment
    public AudioClip RecordingClip        => _microphoneClip;
    public int       RecordingFrequency   => microphoneRecordingRate;
    public string    RecordingDevice      => _device;
    public bool      IsActivelyRecording  => _isRecording;
    public int       CurrentSamplePosition => _isRecording ? Microphone.GetPosition(_device) : 0;

    // Buffer for reading from Unity's microphone
    private float[] _tempBuffer;

    private void Start()
    {
        // Auto-find client if not assigned
        if (realtimeClient == null)
        {
            realtimeClient = GetComponent<OpenAIRealtimeClient>();
            if (realtimeClient == null)
            {
#if UNITY_2023_1_OR_NEWER
                realtimeClient = FindFirstObjectByType<OpenAIRealtimeClient>();
#else
                realtimeClient = FindObjectOfType<OpenAIRealtimeClient>();
#endif
            }
        }
    }

    private void Update()
    {
        if (!_isRecording || _microphoneClip == null || realtimeClient == null) return;

        int currentPos = Microphone.GetPosition(_device);
        int samplesAvailable = GetSamplesAvailable(currentPos);

        // Process in chunks to avoid latency
        // 24kHz * 0.1s = 2400 samples. 
        // We can process smaller chunks if available, e.g., > 1024 samples.
        int threshold = 1024; 

        if (samplesAvailable > threshold)
        {
            ProcessAudioChunk(samplesAvailable);
        }
    }

    /// <summary>
    /// Starts the microphone capture.
    /// Note: This will restart the microphone. If other scripts are using it, they will lose access.
    /// </summary>
    public void StartStreaming()
    {
        if (_isRecording) return;

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[RealtimeMicrophone] No microphone devices found.");
            return;
        }

        _device = Microphone.devices[0]; // Use default device or expose a selector
        _microphoneClip = Microphone.Start(_device, true, bufferLengthSec, microphoneRecordingRate);
        
        // Wait until recording actually starts
        int attempts = 0;
        while (Microphone.GetPosition(_device) <= 0 && attempts++ < 1000) { }

        _lastSamplePos = 0;
        _isRecording = true;
        
        // Initialize temp buffer to a reasonable max size to avoid allocations in Update
        _tempBuffer = new float[microphoneRecordingRate]; 

        Debug.Log($"[RealtimeMicrophone] Started recording on {_device} at {microphoneRecordingRate}Hz. Target: {targetSampleRate}Hz");
    }

    /// <summary>
    /// Stops the microphone capture.
    /// </summary>
    public void StopStreaming()
    {
        if (!_isRecording) return;

        if (Microphone.IsRecording(_device))
        {
            Microphone.End(_device);
        }

        _isRecording = false;
        _microphoneClip = null;
        Debug.Log("[RealtimeMicrophone] Stopped recording.");
    }

    private int GetSamplesAvailable(int currentPos)
    {
        if (currentPos < _lastSamplePos)
        {
            // Wrapped around
            return (_microphoneClip.samples - _lastSamplePos) + currentPos;
        }
        return currentPos - _lastSamplePos;
    }

    private void ProcessAudioChunk(int samplesToRead)
    {
        if (_microphoneClip == null) return;

        // Resize buffer if needed (unlikely if allocated generously)
        if (_tempBuffer == null || _tempBuffer.Length < samplesToRead)
        {
            _tempBuffer = new float[samplesToRead];
        }

        // Read data from AudioClip
        if (_microphoneClip.GetData(_tempBuffer, _lastSamplePos))
        {
            // If we wrapped around, GetData logic in Unity is tricky. 
            // However, GetData using offset usually handles wrapping if we request valid range?
            // Actually, Unity's GetData acts on the start offset. It does NOT wrap automatically for single calls exceeding buffer end.
            // We need manual circular reading if wrapping happens.
            
            // Simplified circular read:
            ReadCircular(_microphoneClip, _lastSamplePos, _tempBuffer, samplesToRead);
        }

        // Processing: Resample + Convert to PCM16
        byte[] pcmData = ResampleAndConvert(_tempBuffer, samplesToRead, _microphoneClip.frequency, targetSampleRate);

        // Send to Client
        if (pcmData != null && pcmData.Length > 0)
        {
            realtimeClient.SendAudio(pcmData);
        }

        // Advance pointer
        _lastSamplePos = (_lastSamplePos + samplesToRead) % _microphoneClip.samples;
    }

    private void ReadCircular(AudioClip clip, int startPos, float[] buffer, int length)
    {
        if (startPos + length <= clip.samples)
        {
            clip.GetData(buffer, startPos);
        }
        else
        {
            // Split into two reads
            int endPart = clip.samples - startPos;
            float[] tempEnd = new float[endPart];
            clip.GetData(tempEnd, startPos);
            Array.Copy(tempEnd, 0, buffer, 0, endPart);

            int startPart = length - endPart;
            float[] tempStart = new float[startPart];
            clip.GetData(tempStart, 0);
            Array.Copy(tempStart, 0, buffer, endPart, startPart);
        }
    }

    /// <summary>
    /// Simple linear interpolation resampling and PCM16 conversion.
    /// </summary>
    private byte[] ResampleAndConvert(float[] inputSamples, int inputCount, int inRate, int outRate)
    {
        // 1. Calculate ratio
        float ratio = (float)inRate / outRate;
        int outputCount = Mathf.FloorToInt(inputCount / ratio);

        if (outputCount <= 0) return null;

        byte[] outputBytes = new byte[outputCount * 2]; // 2 bytes per sample (PCM16)

        for (int i = 0; i < outputCount; i++)
        {
            float inputIndex = i * ratio;
            int index0 = (int)inputIndex;
            int index1 = index0 + 1;
            float t = inputIndex - index0;

            // Clamping indices
            if (index0 >= inputCount) index0 = inputCount - 1;
            if (index1 >= inputCount) index1 = inputCount - 1;

            // Linear Interpolation
            float sampleValue = Mathf.Lerp(inputSamples[index0], inputSamples[index1], t);

            // Apply sensitivity/gain
            sampleValue *= micSensitivity;

            // Convert float (-1.0 to 1.0) to short (-32768 to 32767)
            short shortSample = (short)(Mathf.Clamp(sampleValue, -1f, 1f) * short.MaxValue);

            // To Byte Array (Little Endian)
            outputBytes[i * 2] = (byte)(shortSample & 0x00ff);
            outputBytes[i * 2 + 1] = (byte)((shortSample & 0xff00) >> 8);
        }

        return outputBytes;
    }
}
