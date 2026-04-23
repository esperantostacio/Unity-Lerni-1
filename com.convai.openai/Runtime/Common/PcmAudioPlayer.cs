using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Handles playback of PCM audio received as Base64-encoded strings.
/// Streams audio continuously using OnAudioFilterRead for gapless playback.
/// </summary>
public class PcmAudioPlayer : MonoBehaviour
{
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private int sampleRate = 16000; // PCM16 input sample rate
    
    // Thread-safe ring buffer for streaming audio
    private readonly Queue<float> _sampleQueue = new();
    private readonly object _lock = new();
    private int _enqueueCount = 0;
    private bool _isPlaying = false;
    private int _outputSampleRate;
    
    /// <summary>
    /// Returns true if audio samples are being played or buffered.
    /// </summary>
    public bool IsPlaying
    {
        get { lock (_lock) { return _isPlaying || _sampleQueue.Count > 0; } }
    }

    /// <summary>
    /// The AudioSource used for playback. Use this to feed lip-sync scripts.
    /// </summary>
    public AudioSource AudioSource => audioSource;
    
    #region Unity Lifecycle

    private void Awake()
    {
        _outputSampleRate = AudioSettings.outputSampleRate;
        
        if (audioSource == null)
        {
            audioSource = GetComponent<AudioSource>();
            if (audioSource == null)
            {
                audioSource = gameObject.AddComponent<AudioSource>();
                Debug.Log("[PcmAudioPlayer] Created AudioSource automatically");
            }
        }
        
        // Create a looping silence clip so OnAudioFilterRead is always called
        audioSource.clip = AudioClip.Create("PcmStream", _outputSampleRate, 1, _outputSampleRate, false);
        float[] silence = new float[_outputSampleRate];
        audioSource.clip.SetData(silence, 0);
        audioSource.loop = true;
        audioSource.pitch = 1f;
        audioSource.playOnAwake = true;
        audioSource.spatialBlend = 0f; // Force 2D
        audioSource.volume = 1f;
        audioSource.Play();
        
        Debug.Log($"[PcmAudioPlayer] Ready (streaming mode). InputRate={sampleRate}, OutputRate={_outputSampleRate}, Volume={audioSource.volume}");
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Converts Base64-encoded PCM audio and adds samples to the streaming buffer.
    /// </summary>
    public void EnqueueBase64Audio(string base64Audio)
    {
        if (string.IsNullOrEmpty(base64Audio)) return;
        
        _enqueueCount++;
        
        // Decode Base64 to raw bytes
        var bytes = System.Convert.FromBase64String(base64Audio);
        
        // PCM16 to float conversion
        int sampleCount = bytes.Length / 2;
        float[] samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            short sample = (short)((bytes[i * 2 + 1] << 8) | bytes[i * 2]);
            samples[i] = sample / 32768f;
        }
        
        // Resample from input rate (24kHz) to output rate (system DSP rate)
        float ratio = (float)sampleRate / _outputSampleRate;
        int resampledLength = Mathf.FloorToInt(sampleCount / ratio);
        
        lock (_lock)
        {
            for (int i = 0; i < resampledLength; i++)
            {
                float pos = i * ratio;
                int idx = (int)pos;
                float frac = pos - idx;
                
                float val;
                if (idx >= sampleCount - 1)
                    val = samples[sampleCount - 1];
                else
                    val = samples[idx] * (1f - frac) + samples[idx + 1] * frac;
                
                _sampleQueue.Enqueue(val);
            }
            _isPlaying = true;
        }
        
        if (_enqueueCount <= 3 || _enqueueCount % 100 == 0)
            Debug.Log($"[PcmAudioPlayer] Chunk #{_enqueueCount}, {sampleCount} samples -> {resampledLength} resampled, buffer={_sampleQueue.Count}");
    }

    /// <summary>
    /// Stops playback immediately and clears the buffer.
    /// </summary>
    public void StopImmediately()
    {
        lock (_lock)
        {
            _sampleQueue.Clear();
            _isPlaying = false;
        }
    }

    #endregion

    #region Audio Thread

    /// <summary>
    /// Called on Unity's audio thread. Fills the output buffer with queued samples.
    /// </summary>
    private void OnAudioFilterRead(float[] data, int channels)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i += channels)
            {
                float sample = 0f;
                if (_sampleQueue.Count > 0)
                {
                    sample = _sampleQueue.Dequeue();
                }
                
                // Write to all channels
                for (int c = 0; c < channels; c++)
                {
                    data[i + c] = sample;
                }
            }
            
            if (_isPlaying && _sampleQueue.Count == 0)
            {
                _isPlaying = false;
            }
        }
    }

    #endregion
}