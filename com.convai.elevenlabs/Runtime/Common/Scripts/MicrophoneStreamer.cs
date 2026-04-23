using System;
using UnityEngine;

/// <summary>
/// Streams microphone audio as Base64-encoded 16-bit mono PCM chunks.
/// Each chunk is 1,024 samples (~64 ms at 16 kHz).
/// </summary>
public class MicrophoneStreamer : MonoBehaviour
{
    public Action<string> OnAudioChunk;

    [Header("Noise Gate (Optional)")]
    [Tooltip("If enabled, audio chunks below the RMS threshold are not sent. Helps reduce background noise triggers.")]
    [SerializeField] private bool enableNoiseGate = false;

    [Tooltip("RMS amplitude threshold (0..1). Typical voice is often ~0.02-0.08 depending on mic gain.")]
    [Range(0f, 0.2f)]
    [SerializeField] private float noiseGateRmsThreshold = 0.03f;

    [Tooltip("How many consecutive chunks must be above threshold before opening the gate.")]
    [SerializeField] private int noiseGateAttackChunks = 2;

    [Tooltip("After falling below threshold, keep sending audio for this long (ms) to avoid choppy speech.")]
    [SerializeField] private int noiseGateHoldMs = 250;

    private const int SampleRateOut   = 16_000; 
    private const int ChunkSamplesOut = 1_024;

    private AudioClip _microphoneClip;
    private string _micDevice;
    private int _micSampleRate;   
    private int _chunkSamplesIn;  
    private int _lastSamplePos;

    private bool _gateOpen;
    private int _gateAboveCount;
    private double _gateHoldUntil;

    #region Unity Lifecycle

    private void Start()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("MicrophoneStreamer: no microphone devices.");
            enabled = false;
            return;
        }

        _micDevice = Microphone.devices[0];
    }

    private void Update()
    {
        if (!_microphoneClip) return;

        var currentPos       = Microphone.GetPosition(_micDevice);
        var samplesAvailable = currentPos - _lastSamplePos;
        if (samplesAvailable < 0) 
            samplesAvailable += _microphoneClip.samples;

        if (samplesAvailable < _chunkSamplesIn) return;
    
        var inBuf = new float[_chunkSamplesIn];
        ReadCircular(_microphoneClip, _lastSamplePos, inBuf);
        _lastSamplePos = (_lastSamplePos + _chunkSamplesIn) % _microphoneClip.samples;

        if (enableNoiseGate && !ShouldSend(inBuf))
            return;

        var pcm16 = DownsampleAndConvert(inBuf, _micSampleRate, SampleRateOut);
        OnAudioChunk?.Invoke(Convert.ToBase64String(pcm16));
    }

    #endregion

    #region Public Methods

    public void StartStreaming()
    {
        _microphoneClip = Microphone.Start(_micDevice, true, 1, SampleRateOut);
        _micSampleRate  = _microphoneClip.frequency;
        _chunkSamplesIn = Mathf.RoundToInt(ChunkSamplesOut * (float)_micSampleRate / SampleRateOut);
        _lastSamplePos  = 0;

        _gateOpen = false;
        _gateAboveCount = 0;
        _gateHoldUntil = 0;

        Debug.Log($"[MicrophoneStreamer] device={_micDevice}, " +
                  $"realRate={_micSampleRate} Hz, chunkIn={_chunkSamplesIn} samples");
    }

    public void StopStreaming()
    {
        if (Microphone.IsRecording(_micDevice))
            Microphone.End(_micDevice);

        _microphoneClip = null;
    }

    #endregion

    #region Helpers

    private bool ShouldSend(float[] buffer)
    {
        var rms = ComputeRms(buffer);
        var now = Time.realtimeSinceStartupAsDouble;

        bool above = rms >= Mathf.Max(0f, noiseGateRmsThreshold);
        if (above)
        {
            _gateAboveCount++;
            _gateHoldUntil = now + (Mathf.Max(0, noiseGateHoldMs) / 1000.0);
            if (_gateAboveCount >= Mathf.Max(1, noiseGateAttackChunks))
                _gateOpen = true;
        }
        else
        {
            _gateAboveCount = 0;
            if (now > _gateHoldUntil)
                _gateOpen = false;
        }

        return _gateOpen;
    }

    private static float ComputeRms(float[] buffer)
    {
        if (buffer == null || buffer.Length == 0) return 0f;

        double sumSq = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            var s = buffer[i];
            sumSq += (double)s * s;
        }
        return Mathf.Sqrt((float)(sumSq / buffer.Length));
    }

    private static void ReadCircular(AudioClip clip, int start, float[] buffer)
    {
        var len         = buffer.Length;
        var clipSamples = clip.samples;
        var tail        = clipSamples - start;

        if (len <= tail)
        {
            clip.GetData(buffer, start);
        }
        else
        {
            var tempTail = new float[tail];
            var tempHead = new float[len - tail];

            clip.GetData(tempTail, start);
            clip.GetData(tempHead, 0);

            Array.Copy(tempTail, 0, buffer, 0, tail);
            Array.Copy(tempHead, 0, buffer, tail, tempHead.Length);
        }
    }

    private static byte[] DownsampleAndConvert(float[] inBuf, int inRate, int outRate)
    {
        if (inRate == outRate) 
            return ConvertToPcm16(inBuf);

        var ratio   = (float)inRate / outRate;
        var outLen    = Mathf.RoundToInt(inBuf.Length / ratio);
        var pcmOut = new byte[outLen * 2];

        var pos = 0f;
        for (var o = 0; o < outLen; o++, pos += ratio)
        {
            var i0 = Mathf.Clamp((int)pos, 0, inBuf.Length - 1);
            var i1 = Mathf.Min(i0 + 1, inBuf.Length - 1);
            var frac = pos - i0;

            var sample = Mathf.Lerp(inBuf[i0], inBuf[i1], frac);
            var s16 = (short)Mathf.Clamp(sample * 32767f, short.MinValue, short.MaxValue);

            pcmOut[o * 2]     = (byte)(s16 & 0xFF);
            pcmOut[o * 2 + 1] = (byte)((s16 >> 8) & 0xFF);
        }

        return pcmOut;
    }

    private static byte[] ConvertToPcm16(float[] buf)
    {
        var pcm = new byte[buf.Length * 2];
        for (var i = 0; i < buf.Length; i++)
        {
            var s = (short)Mathf.Clamp(buf[i] * 32767f, short.MinValue, short.MaxValue);
            pcm[i * 2]     = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }

    #endregion
}
