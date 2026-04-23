using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Lip-sync + natural facial animation for an avatar driven by:
///   1. Live transcript phoneme mapping (primary — German/English character → viseme)
///   2. Procedural oscillator fallback (when no transcript is available)
///   3. Audio-analysis amplitude mode (when Auto Move is OFF)
///
/// Transcript feed comes from OpenAIRealtimeClient.OnTranscriptDelta (main-thread safe).
/// Phonemes are queued at ~70 ms each and smoothed onto blendshapes in real-time.
/// Eyes blink fully (0 → 100), support double-blinks, and have subtle saccade asymmetry.
/// Scale: ALL blendshape weights are 0 – 100  (100 = fully closed/active).
/// </summary>
public class maherlips : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    // INSPECTOR FIELDS
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Required")]
    [SerializeField] private SkinnedMeshRenderer skinnedMeshRenderer;

    [Header("Mode")]
    [Tooltip("Auto Move: phoneme/procedural lip-sync while AI speaks. OFF: classic AudioSource amplitude mode.")]
    [SerializeField] private bool autoMove = true;

    [Header("Auto Move — General")]
    [Range(1f, 12f)]  [SerializeField] private float talkSpeed   = 6f;
    [Range(0f, 100f)] [SerializeField] private float minMouth    = 2f;
    [Range(0f, 100f)] [SerializeField] private float maxMouth    = 35f;   // max mouth-open blendshape weight
    [Range(0f, 100f)] [SerializeField] private float maxVowel    = 80f;   // max vowel viseme blendshape weight
    [Range(0f, 100f)] [SerializeField] private float maxConsonant = 55f;  // max consonant viseme blendshape weight
    [Range(1f, 30f)]  [SerializeField] private float closeSpeed  = 14f;

    [Header("Transcript Phoneme Lip Sync")]
    [Tooltip("How long (seconds) each phoneme is held before the next one plays.")]
    [Range(0.03f, 0.18f)] [SerializeField] private float phonemeDuration = 0.07f;
    [Tooltip("Speed at which viseme blendshapes snap TO the new phoneme target.")]
    [Range(4f, 60f)] [SerializeField] private float phonemeSmoothIn  = 28f;
    [Tooltip("Speed at which viseme blendshapes decay AWAY from the previous phoneme.")]
    [Range(2f, 30f)] [SerializeField] private float phonemeSmoothOut = 10f;
    [Tooltip("Small random variation (±) added to each phoneme weight for organic feel.")]
    [Range(0f, 0.4f)] [SerializeField] private float phonemeNoise = 0.18f;

    [Header("Audio Analysis Mode  (Auto Move OFF only)")]
    [SerializeField] private AudioSource audioSource;
    [Range(0f, 5f)]   [SerializeField] private float gain = 2.5f;
    [Range(0f, 1f)]   [SerializeField] private float smoothing = 0.25f;
    [SerializeField] private float silentDecaySpeed = 12f;
    [SerializeField] private int sampleSize = 512;

    [Header("References  (auto-found if blank)")]
    [SerializeField] private PcmAudioPlayer pcmAudioPlayer;
    [SerializeField] private OpenAIRealtimeClient realtimeClient;

    [Header("Blendshape Names — Mouth / Visemes  (case-sensitive)")]
    [SerializeField] private string mouthOpenShape = "h_expressions.MouthOpen_h";
    [SerializeField] private string[] vowelVisemes =
    {
        "h_expressions.AE_AA_h",   // 0 → A, Ä  (wide open)
        "h_expressions.AO_a_h",    // 1 → O, Ö  (rounded)
        "h_expressions.Ax_E_h",    // 2 → E      (medium)
        "h_expressions.TD_I_h",    // 3 → I, Y   (narrow)
        "h_expressions.UH_OO_h",   // 4 → UH / OO  (small round)
        "h_expressions.UW_U_h"     // 5 → U, Ü   (pursed)
    };
    [SerializeField] private string[] consonantVisemes =
    {
        "h_expressions.FV_h",       // 0 → F, V, W
        "h_expressions.S_h",        // 1 → S, Z, ß
        "h_expressions.SH_CH_h",    // 2 → Sch, Ch, J
        "h_expressions.MPB_Up_h",   // 3 → M, B, P  upper lip  }  pair
        "h_expressions.MPB_Down_h", // 4 → M, B, P  lower lip  }
        "h_expressions.KG_h"        // 5 → K, G, H, X, Q
    };
    [SerializeField] private string[] extraMouthShapes = { "h_expressions.Shout_h" };

    [Range(2f, 20f)]    [SerializeField] private float blinkIntervalMin  = 3f;
    [Range(2f, 20f)]    [SerializeField] private float blinkIntervalMax  = 8f;
    [Range(0.06f, 0.25f)] [SerializeField] private float blinkDuration   = 0.12f;
    [Tooltip("Probability (0–1) that each blink becomes a double-blink.")]
    [Range(0f, 0.5f)]   [SerializeField] private float doubleBlinkChance = 0.15f;

    // ───────── Eyes (close / open / squint / lids) ─────────
    [Header("Eyes — Close / Open / Squint / Lids")]
    [SerializeField] private string rightEyeClose = "h_expressions.ReyeClose_h";
    [SerializeField] private string leftEyeClose  = "h_expressions.LeyeClose_h";
    [SerializeField] private string rightEyeOpen  = "h_expressions.ReyeOpen_h";
    [SerializeField] private string leftEyeOpen   = "h_expressions.LeyeOpen_h";
    [SerializeField] private string rightSquint  = "h_expressions.Rsquint_h";
    [SerializeField] private string leftSquint   = "h_expressions.Lsquint_h";
    [SerializeField] private string rightLowLid  = "h_expressions.RlowLid_h";
    [SerializeField] private string leftLowLid   = "h_expressions.LlowLid_h";

    // ───────── Eyebrows ─────────
    [Header("Eyebrows — Up / Down")]
    [SerializeField] private string[] browUpShapes = {
        "h_expressions.RRbrowUp_h", "h_expressions.RbrowUp_h",
        "h_expressions.LbrowUp_h",  "h_expressions.LLbrowUp_h"
    };
    [SerializeField] private string[] browDownShapes = {
        "h_expressions.RRbrowDown_h", "h_expressions.RbrowDown_h",
        "h_expressions.LbrowDown_h",  "h_expressions.LLbrowDown_h"
    };

    // ───────── Nose ─────────
    [Header("Nostrils")]
    [SerializeField] private string rightNostril = "h_expressions.Rnostril_h";
    [SerializeField] private string leftNostril  = "h_expressions.Lnostril_h";

    // ───────── Jaw / Chin ─────────
    [Header("Jaw / Chin")]
    [SerializeField] private string jawCompress = "h_expressions.JawCompress_h";
    [SerializeField] private string rightJaw    = "h_expressions.Rjaw_h";
    [SerializeField] private string leftJaw     = "h_expressions.Ljaw_h";
    [SerializeField] private string jawFront    = "h_expressions.JawFront_h";
    [SerializeField] private string chin        = "h_expressions.Chin_h";
    [SerializeField] private string chew        = "h_expressions.Chew_h";

    // ───────── Lips directional ─────────
    [Header("Lips — Directional")]
    [SerializeField] private string[] lipShapes = {
        "h_expressions.RlipUp_h",     "h_expressions.LlipUp_h",
        "h_expressions.RlipDown_h",   "h_expressions.LlipDown_h",
        "h_expressions.RlipSide_h",   "h_expressions.LlipSide_h",
        "h_expressions.RlipCorner_h", "h_expressions.LlipCorner_h"
    };

    // ───────── Smile / Expression Mouths ─────────
    [Header("Smile Shapes")]
    [SerializeField] private string[] smileShapes = {
        "h_expressions.RsmileClose_h", "h_expressions.LsmileClose_h",
        "h_expressions.RsmileOpen_h"
    };

    // ───────── Emotional presets ─────────
    [Header("Emotional Presets")]
    [SerializeField] private string[] emotionShapes = {
        "h_expressions.Rsad_h",     "h_expressions.Lsad_h",
        "h_expressions.Rpityful_h", "h_expressions.Lpityful_h",
        "h_expressions.Rdisgust_h", "h_expressions.Ldisgust_h",
        "h_expressions.Kiss_h"
    };

    // ───────── Neck / Throat ─────────
    [Header("Neck / Throat Tension")]
    [SerializeField] private string glotis          = "h_expressions.Glotis_h";
    [SerializeField] private string rightNeckTension = "h_expressions.RneckTension_h";
    [SerializeField] private string leftNeckTension  = "h_expressions.LneckTension_h";

    // ───────── Expression tuning ─────────
    [Header("Micro-Expression Tuning")]
    [Tooltip("Max weight for subtle expressions (brows, squint, emotions, etc). Keep 1–20 for realism.")]
    [Range(1f, 30f)] [SerializeField] private float maxExpressionWeight = 15f;
    [Tooltip("How often (seconds) a new random idle/speaking expression triggers.")]
    [Range(0.5f, 6f)] [SerializeField] private float expressionChangeMin = 1.5f;
    [Range(1f, 10f)]  [SerializeField] private float expressionChangeMax = 4f;
    [Tooltip("Smooth speed for expression transitions.")]
    [Range(1f, 20f)]  [SerializeField] private float expressionSmooth = 4f;

    [Header("Emotion Beats  (timed named expressions)")]
    [Tooltip("Min seconds between emotion beats.")]
    [Range(2f, 20f)]  [SerializeField] private float beatIntervalMin  = 4f;
    [Range(3f, 25f)]  [SerializeField] private float beatIntervalMax  = 10f;
    [Tooltip("Peak blendshape weight for emotion beats (0–100). Keep 10–25 for realism.")]
    [Range(5f, 60f)]  [SerializeField] private float beatIntensityMax = 20f;

    [Range(0f, 100f)] [SerializeField] private float maxWeight = 100f;

    // ─────────────────────────────────────────────────────────────────────────
    // CACHED BLENDSHAPE INDICES
    // ─────────────────────────────────────────────────────────────────────────

    private int   _mouthOpenIndex    = -1;
    private int[] _vowelIndices      = Array.Empty<int>();
    private int[] _consonantIndices  = Array.Empty<int>();
    private int[] _extraMouthIndices = Array.Empty<int>();
    private int   _rightEyeIndex     = -1;
    private int   _leftEyeIndex      = -1;
    private int   _rightEyeOpenIdx   = -1;
    private int   _leftEyeOpenIdx    = -1;
    private int   _rightSquintIdx    = -1;
    private int   _leftSquintIdx     = -1;
    private int   _rightLowLidIdx    = -1;
    private int   _leftLowLidIdx     = -1;
    private int[] _browUpIndices     = Array.Empty<int>();
    private int[] _browDownIndices   = Array.Empty<int>();
    private int   _rightNostrilIdx   = -1;
    private int   _leftNostrilIdx    = -1;
    private int   _jawCompressIdx    = -1;
    private int   _rightJawIdx       = -1;
    private int   _leftJawIdx        = -1;
    private int   _jawFrontIdx       = -1;
    private int   _chinIdx           = -1;
    private int   _chewIdx           = -1;
    private int[] _lipIndices        = Array.Empty<int>();
    private int[] _smileIndices      = Array.Empty<int>();
    private int[] _emotionIndices    = Array.Empty<int>();
    private int   _glotisIdx         = -1;
    private int   _rightNeckIdx      = -1;
    private int   _leftNeckIdx       = -1;

    // ─────────────────────────────────────────────────────────────────────────
    // TRANSCRIPT-DRIVEN PHONEME STATE
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A single phoneme event to be played at a scheduled time.</summary>
    private struct PhonemeEvent
    {
        /// <summary>Wall-clock Time.time when this phoneme should become active.</summary>
        public float scheduledTime;
        /// <summary>Target MouthOpen blendshape weight (0–100, pre-scaled by maxMouth).</summary>
        public float mouthOpen;
        /// <summary>Index into vowelVisemes array, −1 = no vowel.</summary>
        public int   vowelIdx;
        /// <summary>Target weight for the vowel viseme (0–100).</summary>
        public float vowelWeight;
        /// <summary>
        /// Index into consonantVisemes array.
        /// -1 = no consonant.  -2 = MPB special (activates both indices 3 and 4).
        /// </summary>
        public int   consonantIdx;
        /// <summary>Target weight for the consonant viseme (0–100).</summary>
        public float consonantWeight;
    }

    private readonly Queue<PhonemeEvent> _phonemeQueue = new Queue<PhonemeEvent>();
    private float _nextPhonemeSlot   = 0f;
    private float _lastTranscriptTime = -999f;
    private const float TRANSCRIPT_TIMEOUT  = 2.5f;
    private const int   MAX_PHONEME_QUEUE   = 150;

    // Per-viseme smooth target / current weights (transcript mode)
    private float[] _vowelWeightTarget   = null;
    private float[] _vowelWeightCurrent  = null;
    private float[] _consWeightTarget    = null;
    private float[] _consWeightCurrent   = null;
    private float   _phonemeMouthTarget  = 0f;
    private float   _phonemeMouthCurrent = 0f;

    // ─────────────────────────────────────────────────────────────────────────
    // PROCEDURAL FALLBACK STATE
    // ─────────────────────────────────────────────────────────────────────────

    private float   _phase;
    private float   _currentMouth;
    private float[] _vowelTargets;
    private float[] _consonantTargets;
    private float   _nextShuffleTime;

    // ─────────────────────────────────────────────────────────────────────────
    // BLINK STATE
    // ─────────────────────────────────────────────────────────────────────────

    private float _nextBlinkTime;
    private float _blinkTimer;
    private float _currentBlink;
    private int   _pendingBlinks;

    // ─────────────────────────────────────────────────────────────────────────
    // EYE SACCADE STATE
    // ─────────────────────────────────────────────────────────────────────────

    private float _nextSaccadeTime;
    private float _saccadeAsymR,       _saccadeAsymL;
    private float _saccadeAsymRTarget, _saccadeAsymLTarget;

    // ─────────────────────────────────────────────────────────────────────────
    // MICRO-EXPRESSION STATE
    // ─────────────────────────────────────────────────────────────────────────

    private float   _nextExpressionTime;
    private float[] _browUpCurrent,   _browUpTarget;
    private float[] _browDownCurrent, _browDownTarget;
    private float _squintRCur, _squintRTgt, _squintLCur, _squintLTgt;
    private float _lowLidRCur, _lowLidRTgt, _lowLidLCur, _lowLidLTgt;
    private float _nostrilRCur, _nostrilRTgt, _nostrilLCur, _nostrilLTgt;
    private float[] _lipCurrent,     _lipTarget;
    private float[] _smileCurrent,   _smileTarget;
    private float[] _emotionCurrent, _emotionTarget;
    private float _neckRCur, _neckRTgt, _neckLCur, _neckLTgt;
    private float _glotisCur, _glotisTgt;
    private float _jawCompCur, _jawCompTgt;
    private float _chinCur, _chinTgt;

    // ─────────────────────────────────────────────────────────────────────────
    // EMOTION BEAT STATE
    // ─────────────────────────────────────────────────────────────────────────

    private enum EmotionBeatType { None, Smile, BrowRaise, Concern, Disgust, Thoughtful, Playful }

    private struct EmotionBeat
    {
        public EmotionBeatType type;
        public float intensity;
        public float holdDuration;
        public float fadeInDur;
        public float fadeOutDur;
        public float phaseTimer;
    }

    private EmotionBeat _currentBeat;
    private float       _nextBeatTime;
    private bool        _beatActive;

    // ─────────────────────────────────────────────────────────────────────────
    // AUDIO ANALYSIS STATE (legacy)
    // ─────────────────────────────────────────────────────────────────────────

    private float   _currentWeight;
    private float[] _samples;
    private float[] _waveform;
    private AudioSource _audioSource_cached;

    // External one-shot lip-sync source (used for welcome TTS).
    private bool _useExternalAudioLipSync;
    private AudioSource _externalLipSyncAudioSource;

    public void BeginExternalAudioLipSync(AudioSource source)
    {
        _externalLipSyncAudioSource = source;
        _useExternalAudioLipSync = _externalLipSyncAudioSource != null;
    }

    public void EndExternalAudioLipSync()
    {
        _useExternalAudioLipSync = false;
        _externalLipSyncAudioSource = null;
    }

    private void Awake()
    {
        // --- Auto-find references ---
        if (pcmAudioPlayer == null)
        {
#if UNITY_2023_1_OR_NEWER
            pcmAudioPlayer = FindFirstObjectByType<PcmAudioPlayer>();
#else
            pcmAudioPlayer = FindObjectOfType<PcmAudioPlayer>();
#endif
        }

        if (realtimeClient == null)
        {
#if UNITY_2023_1_OR_NEWER
            realtimeClient = FindFirstObjectByType<OpenAIRealtimeClient>();
#else
            realtimeClient = FindObjectOfType<OpenAIRealtimeClient>();
#endif
        }

        if (!autoMove)
        {
            if (audioSource == null && pcmAudioPlayer != null)
                audioSource = pcmAudioPlayer.AudioSource;
            if (audioSource == null)
                audioSource = GetComponent<AudioSource>();
            _audioSource_cached = audioSource;
        }

        if (skinnedMeshRenderer == null)
            skinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();

        _samples  = new float[Mathf.Max(128, sampleSize)];
        _waveform = new float[Mathf.Max(128, sampleSize)];

        CacheBlendshapeIndices();

        // Phoneme weight arrays (sized after CacheBlendshapeIndices knows array lengths)
        int vc = vowelVisemes.Length;
        int cc = consonantVisemes.Length;
        _vowelWeightTarget  = new float[vc];
        _vowelWeightCurrent = new float[vc];
        _consWeightTarget   = new float[cc];
        _consWeightCurrent  = new float[cc];

        _vowelTargets     = new float[vc];
        _consonantTargets = new float[cc];

        _browUpCurrent   = new float[browUpShapes.Length];
        _browUpTarget    = new float[browUpShapes.Length];
        _browDownCurrent = new float[browDownShapes.Length];
        _browDownTarget  = new float[browDownShapes.Length];
        _lipCurrent      = new float[lipShapes.Length];
        _lipTarget       = new float[lipShapes.Length];
        _smileCurrent    = new float[smileShapes.Length];
        _smileTarget     = new float[smileShapes.Length];
        _emotionCurrent  = new float[emotionShapes.Length];
        _emotionTarget   = new float[emotionShapes.Length];

        _nextBlinkTime      = Time.time + UnityEngine.Random.Range(blinkIntervalMin, blinkIntervalMax);
        _nextExpressionTime = Time.time + UnityEngine.Random.Range(0.5f, 2f);
        _nextSaccadeTime    = Time.time + UnityEngine.Random.Range(0.3f, 1.5f);
        _nextBeatTime       = Time.time + UnityEngine.Random.Range(beatIntervalMin, beatIntervalMax);

        if (autoMove)
            Debug.Log("[maherlips] Auto Move + Transcript Phoneme mode enabled.");
        else if (audioSource != null)
            Debug.Log($"[maherlips] Audio Analysis mode — linked to '{audioSource.gameObject.name}'");
        else
            Debug.LogWarning("[maherlips] No AudioSource found and Auto Move is off! Lip sync won't work.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ENABLE / DISABLE — subscribe / unsubscribe transcript events
    // ─────────────────────────────────────────────────────────────────────────

    private void OnEnable()
    {
        if (realtimeClient != null)
            realtimeClient.OnTranscriptDelta += OnTranscriptDelta;
    }

    private void OnDisable()
    {
        if (realtimeClient != null)
            realtimeClient.OnTranscriptDelta -= OnTranscriptDelta;

        ResetTrackedBlendshapes();
        ResetAllExpressions();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BLENDSHAPE INDEX CACHE
    // ─────────────────────────────────────────────────────────────────────────

    private void CacheBlendshapeIndices()
    {
        if (skinnedMeshRenderer == null || skinnedMeshRenderer.sharedMesh == null)
            return;


        var mesh = skinnedMeshRenderer.sharedMesh;

        _mouthOpenIndex = mesh.GetBlendShapeIndex(mouthOpenShape);
        if (_mouthOpenIndex < 0)
            Debug.LogWarning($"[maherlips] Blendshape '{mouthOpenShape}' not found!");

        _vowelIndices = new int[vowelVisemes.Length];
        for (int i = 0; i < vowelVisemes.Length; i++)
            _vowelIndices[i] = mesh.GetBlendShapeIndex(vowelVisemes[i]);

        _consonantIndices = new int[consonantVisemes.Length];
        _vowelIndices      = CacheArray(mesh, vowelVisemes);
        _consonantIndices  = CacheArray(mesh, consonantVisemes);
        _extraMouthIndices = CacheArray(mesh, extraMouthShapes);

        _rightEyeIndex = mesh.GetBlendShapeIndex(rightEyeClose);
        _leftEyeIndex  = mesh.GetBlendShapeIndex(leftEyeClose);

        _rightEyeOpenIdx = mesh.GetBlendShapeIndex(rightEyeOpen);
        _leftEyeOpenIdx  = mesh.GetBlendShapeIndex(leftEyeOpen);
        _rightSquintIdx  = mesh.GetBlendShapeIndex(rightSquint);
        _leftSquintIdx   = mesh.GetBlendShapeIndex(leftSquint);
        _rightLowLidIdx  = mesh.GetBlendShapeIndex(rightLowLid);
        _leftLowLidIdx   = mesh.GetBlendShapeIndex(leftLowLid);

        _browUpIndices   = CacheArray(mesh, browUpShapes);
        _browDownIndices = CacheArray(mesh, browDownShapes);

        _rightNostrilIdx = mesh.GetBlendShapeIndex(rightNostril);
        _leftNostrilIdx  = mesh.GetBlendShapeIndex(leftNostril);

        _jawCompressIdx = mesh.GetBlendShapeIndex(jawCompress);
        _rightJawIdx    = mesh.GetBlendShapeIndex(rightJaw);
        _leftJawIdx     = mesh.GetBlendShapeIndex(leftJaw);
        _jawFrontIdx    = mesh.GetBlendShapeIndex(jawFront);
        _chinIdx        = mesh.GetBlendShapeIndex(chin);
        _chewIdx        = mesh.GetBlendShapeIndex(chew);

        _lipIndices     = CacheArray(mesh, lipShapes);
        _smileIndices   = CacheArray(mesh, smileShapes);
        _emotionIndices = CacheArray(mesh, emotionShapes);

        _glotisIdx    = mesh.GetBlendShapeIndex(glotis);
        _rightNeckIdx = mesh.GetBlendShapeIndex(rightNeckTension);
        _leftNeckIdx  = mesh.GetBlendShapeIndex(leftNeckTension);
    }

    private static int[] CacheArray(Mesh mesh, string[] names)
    {
        var result = new int[names.Length];
        for (int i = 0; i < names.Length; i++)
            result[i] = mesh.GetBlendShapeIndex(names[i]);
        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UPDATE
    // ─────────────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (skinnedMeshRenderer == null)
        {
            skinnedMeshRenderer = GetComponentInChildren<SkinnedMeshRenderer>();
            if (skinnedMeshRenderer == null) return;
            CacheBlendshapeIndices();
        }

        if (_useExternalAudioLipSync)
        {
            UpdateExternalAudioLipSync();
            UpdateBlink();
            UpdateMicroExpressions();
            return;
        }

        if (autoMove)
            UpdateAutoMove();
        else
            UpdateAudioAnalysis();

        UpdateBlink();
        UpdateMicroExpressions();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TRANSCRIPT PHONEME RECEIVER
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Receives AI transcript fragments from OpenAIRealtimeClient (main-thread).
    /// Each character / digraph is mapped to a phoneme and queued in time.
    /// </summary>
    private void OnTranscriptDelta(string delta)
    {
        if (string.IsNullOrEmpty(delta)) return;

        _lastTranscriptTime = Time.time;

        // Re-sync queue head to now if it has drifted behind
        if (_nextPhonemeSlot < Time.time)
            _nextPhonemeSlot = Time.time;

        // Safety cap to prevent memory runaway
        while (_phonemeQueue.Count >= MAX_PHONEME_QUEUE)
            _phonemeQueue.Dequeue();

        int i = 0;
        while (i < delta.Length)
        {
            // German trigraph SCH
            if (i + 2 < delta.Length)
            {
                string tri = delta.Substring(i, 3).ToUpperInvariant();
                if (tri == "SCH") { EnqueuePhoneme(MakeConsonant(2, 30f, 65f)); i += 3; continue; }
            }

            // Digraphs (German + general)
            if (i + 1 < delta.Length)
            {
                string di = delta.Substring(i, 2).ToUpperInvariant();
                switch (di)
                {
                    case "CH":            EnqueuePhoneme(MakeConsonant(2, 28f, 60f)); i += 2; continue;
                    case "AU": case "ÄU": EnqueuePhoneme(MakeVowel(0, 72f, 75f));    i += 2; continue;
                    case "EU":            EnqueuePhoneme(MakeVowel(1, 68f, 72f));    i += 2; continue;
                    case "EI": case "AI": EnqueuePhoneme(MakeVowel(0, 65f, 68f));    i += 2; continue;
                    case "IE":            EnqueuePhoneme(MakeVowel(3, 42f, 70f));    i += 2; continue;
                    case "NG":            EnqueuePhoneme(MakeConsonant(5, 18f, 35f)); i += 2; continue;
                    case "ST": case "SP": EnqueuePhoneme(MakeConsonant(2, 22f, 45f)); i += 2; continue;
                    case "PH":            EnqueuePhoneme(MakeConsonant(0, 20f, 55f)); i += 2; continue;
                    case "QU":            EnqueuePhoneme(MakeConsonant(5, 24f, 40f)); i += 2; continue;
                }
            }

            EnqueuePhoneme(ClassifyChar(delta[i]));
            i++;
        }
    }

    private void EnqueuePhoneme(PhonemeEvent ev)
    {
        ev.scheduledTime  = _nextPhonemeSlot;
        _nextPhonemeSlot += phonemeDuration;
        _phonemeQueue.Enqueue(ev);
    }

    private PhonemeEvent MakeVowel(int vowelIdx, float mouthOpen, float vowelWeight)
    {
        float noise = 1f + UnityEngine.Random.Range(-phonemeNoise, phonemeNoise);
        return new PhonemeEvent
        {
            mouthOpen       = Mathf.Clamp(mouthOpen   * noise, 0f, 100f),
            vowelIdx        = Mathf.Clamp(vowelIdx, 0, vowelVisemes.Length - 1),
            vowelWeight     = Mathf.Clamp(vowelWeight * noise, 0f, 100f),
            consonantIdx    = -1,
            consonantWeight = 0f
        };
    }

    private PhonemeEvent MakeConsonant(int consIdx, float mouthOpen, float consWeight)
    {
        float noise = 1f + UnityEngine.Random.Range(-phonemeNoise, phonemeNoise);
        return new PhonemeEvent
        {
            mouthOpen       = Mathf.Clamp(mouthOpen   * noise, 0f, 100f),
            vowelIdx        = -1,
            vowelWeight     = 0f,
            consonantIdx    = consIdx,
            consonantWeight = Mathf.Clamp(consWeight  * noise, 0f, 100f)
        };
    }

    private PhonemeEvent ClassifyChar(char c)
    {
        switch (char.ToUpperInvariant(c))
        {
            // German / Latin vowels
            case 'A': case 'Ä':  return MakeVowel(0, 75f, 80f); // AE_AA — very wide
            case 'O': case 'Ö':  return MakeVowel(1, 65f, 78f); // AO    — rounded
            case 'E':            return MakeVowel(2, 50f, 72f); // Ax_E  — medium
            case 'I': case 'Y':  return MakeVowel(3, 38f, 68f); // TD_I  — narrow
            case 'U': case 'Ü':  return MakeVowel(5, 42f, 72f); // UW_U  — pursed

            // Labial consonants
            case 'F': case 'V': case 'W':
                return MakeConsonant(0, 18f, 60f); // FV
            case 'M': case 'B': case 'P':
                // MPB: lips nearly close — special index -2 activates both MPB_Up and MPB_Down
                return new PhonemeEvent
                {
                    mouthOpen       = 5f,
                    vowelIdx        = -1,
                    vowelWeight     = 0f,
                    consonantIdx    = -2,
                    consonantWeight = 50f * (1f + UnityEngine.Random.Range(-phonemeNoise, phonemeNoise))
                };

            // Sibilants
            case 'S': case 'Z': case 'ß':
                return MakeConsonant(1, 15f, 55f); // S
            case 'J':
                return MakeConsonant(2, 22f, 48f); // SH_CH

            // Velar / back consonants
            case 'K': case 'G': case 'Q': case 'X':
                return MakeConsonant(5, 25f, 50f); // KG
            case 'H':
                return MakeConsonant(5, 28f, 28f); // KG light (aspirate)

            // Dental / alveolar
            case 'T': case 'D': return new PhonemeEvent { mouthOpen = 28f, vowelIdx = -1, consonantIdx = -1 };
            case 'N': case 'L': return new PhonemeEvent { mouthOpen = 32f, vowelIdx = -1, consonantIdx = -1 };
            case 'R':           return new PhonemeEvent { mouthOpen = 35f, vowelIdx = -1, consonantIdx = -1 };

            // Pause / punctuation
            case ' ': case '\t':
                return new PhonemeEvent { mouthOpen = 8f, vowelIdx = -1, consonantIdx = -1 };
            case ',': case ';': case ':':
                return new PhonemeEvent { mouthOpen = 4f, vowelIdx = -1, consonantIdx = -1 };
            case '.': case '!': case '?': case '\n': case '\r':
                return new PhonemeEvent { mouthOpen = 2f, vowelIdx = -1, consonantIdx = -1 };

            default:
                return new PhonemeEvent { mouthOpen = 28f, vowelIdx = -1, consonantIdx = -1 };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IS AI SPEAKING?
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsAISpeaking()
    {
        if (pcmAudioPlayer != null && pcmAudioPlayer.IsPlaying) return true;
        if (realtimeClient  != null && realtimeClient.IsAudioPlaying) return true;
        return false;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUTO MOVE — transcript-driven + procedural fallback
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateAutoMove()
    {
        bool  speaking        = IsAISpeaking();
        bool  transcriptFresh = (Time.time - _lastTranscriptTime) < TRANSCRIPT_TIMEOUT;
        bool  hasPhonemes     = _phonemeQueue.Count > 0;
        float dt              = Time.deltaTime;

        // phonemeActive = true only while audio is playing (with a fresh transcript),
        // OR in the brief pre-audio window where transcript just arrived but audio hasn't started yet (<0.4s).
        // Deliberately does NOT include _phonemeMouthCurrent to avoid self-sustaining loop.
        bool phonemeActive = (speaking && transcriptFresh)
                          || (!speaking && hasPhonemes && (Time.time - _lastTranscriptTime) < 0.4f);

        // Dequeue phoneme events whose scheduled time has arrived
        while (_phonemeQueue.Count > 0 && _phonemeQueue.Peek().scheduledTime <= Time.time)
            SetPhonemeTargets(_phonemeQueue.Dequeue());

        if (phonemeActive)
        {
            // ── TRANSCRIPT MODE ──────────────────────────────────────────
            float inF  = 1f - Mathf.Exp(-phonemeSmoothIn  * dt);
            float outF = 1f - Mathf.Exp(-phonemeSmoothOut * dt);

            _phonemeMouthCurrent = Mathf.Lerp(_phonemeMouthCurrent, _phonemeMouthTarget, inF);

            for (int i = 0; i < _vowelWeightCurrent.Length; i++)
            {
                float lf = (_vowelWeightTarget[i] > _vowelWeightCurrent[i]) ? inF : outF;
                _vowelWeightCurrent[i] = Mathf.Lerp(_vowelWeightCurrent[i], _vowelWeightTarget[i], lf);
            }
            for (int i = 0; i < _consWeightCurrent.Length; i++)
            {
                float lf = (_consWeightTarget[i] > _consWeightCurrent[i]) ? inF : outF;
                _consWeightCurrent[i] = Mathf.Lerp(_consWeightCurrent[i], _consWeightTarget[i], lf);
            }

            ApplyPhonemeWeights();
        }
        else if (speaking)
        {
            // ── PROCEDURAL FALLBACK (audio playing but no transcript yet) ──
            _phase += dt * talkSpeed;

            if (Time.time >= _nextShuffleTime)
            {
                _nextShuffleTime = Time.time + UnityEngine.Random.Range(0.08f, 0.16f);
                ShuffleProceduralTargets();
            }

            float wave     = Mathf.Sin(_phase * 3.1f) * 0.5f + 0.5f;
            float perlin   = Mathf.PerlinNoise(_phase * 1.7f, 0.5f);
            float combined = Mathf.Lerp(wave, perlin, 0.4f);
            float target   = Mathf.Lerp(minMouth, maxMouth, combined);
            _currentMouth  = Mathf.Lerp(_currentMouth, target, 1f - Mathf.Exp(-12f * dt));

            ApplyProceduralWeights(speaking: true);
        }
        else
        {
            // ── SILENT — audio stopped, close mouth immediately ──
            _phonemeQueue.Clear();
            _phonemeMouthTarget  = 0f;
            _phonemeMouthCurrent = Mathf.MoveTowards(_phonemeMouthCurrent, 0f, closeSpeed * dt * 25f);
            _currentMouth        = Mathf.MoveTowards(_currentMouth,        0f, closeSpeed * dt * 25f);

            float outF = 1f - Mathf.Exp(-phonemeSmoothOut * dt);
            for (int i = 0; i < _vowelWeightTarget.Length; i++)  _vowelWeightTarget[i]  = 0f;
            for (int i = 0; i < _consWeightTarget.Length; i++)   _consWeightTarget[i]   = 0f;
            for (int i = 0; i < _vowelWeightCurrent.Length; i++)
                _vowelWeightCurrent[i] = Mathf.Lerp(_vowelWeightCurrent[i], 0f, outF);
            for (int i = 0; i < _consWeightCurrent.Length; i++)
                _consWeightCurrent[i]  = Mathf.Lerp(_consWeightCurrent[i],  0f, outF);

            ApplyProceduralWeights(speaking: false);
        }
    }

    /// <summary>Transfers a dequeued PhonemeEvent into the smooth-target arrays.</summary>
    private void SetPhonemeTargets(PhonemeEvent ev)
    {
        // Scale from design-space maxes (mouth 75, vowel 80, consonant 65) to user's inspector maxes
        _phonemeMouthTarget = Mathf.Clamp(ev.mouthOpen * (maxMouth / 75f), 0f, maxMouth);

        for (int i = 0; i < _vowelWeightTarget.Length; i++)  _vowelWeightTarget[i]  = 0f;
        for (int i = 0; i < _consWeightTarget.Length; i++)   _consWeightTarget[i]   = 0f;

        if (ev.vowelIdx >= 0 && ev.vowelIdx < _vowelWeightTarget.Length)
            _vowelWeightTarget[ev.vowelIdx] = Mathf.Clamp(ev.vowelWeight * (maxVowel / 80f), 0f, maxVowel);

        if (ev.consonantIdx >= 0 && ev.consonantIdx < _consWeightTarget.Length)
        {
            _consWeightTarget[ev.consonantIdx] =
                Mathf.Clamp(ev.consonantWeight * (maxConsonant / 65f), 0f, maxConsonant);
        }
        else if (ev.consonantIdx == -2) // MPB special pair
        {
            float w = Mathf.Clamp(ev.consonantWeight * (maxConsonant / 65f), 0f, maxConsonant);
            if (3 < _consWeightTarget.Length) _consWeightTarget[3] = w;
            if (4 < _consWeightTarget.Length) _consWeightTarget[4] = w * 0.85f;
            _phonemeMouthTarget = Mathf.Min(_phonemeMouthTarget, maxMouth * 0.12f);
        }
    }

    private void ApplyPhonemeWeights()
    {
        ResetTrackedBlendshapes();

        float mouth = Mathf.Clamp(_phonemeMouthCurrent, 0f, maxWeight);
        if (_mouthOpenIndex >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(_mouthOpenIndex, mouth);
        for (int i = 0; i < _extraMouthIndices.Length; i++)
            if (_extraMouthIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_extraMouthIndices[i], mouth * 0.18f);

        for (int i = 0; i < _vowelIndices.Length; i++)
            if (_vowelIndices[i] >= 0 && i < _vowelWeightCurrent.Length)
                skinnedMeshRenderer.SetBlendShapeWeight(_vowelIndices[i],
                    Mathf.Clamp(_vowelWeightCurrent[i], 0f, maxWeight));

        for (int i = 0; i < _consonantIndices.Length; i++)
            if (_consonantIndices[i] >= 0 && i < _consWeightCurrent.Length)
                skinnedMeshRenderer.SetBlendShapeWeight(_consonantIndices[i],
                    Mathf.Clamp(_consWeightCurrent[i], 0f, maxWeight));
    }

    private void ShuffleProceduralTargets()
    {
        for (int i = 0; i < _vowelTargets.Length; i++)
            _vowelTargets[i] = UnityEngine.Random.Range(0.15f, 1f);
        for (int i = 0; i < _consonantTargets.Length; i++)
            _consonantTargets[i] = UnityEngine.Random.Range(0.05f, 0.6f);
    }

    private void ApplyProceduralWeights(bool speaking)
    {
        ResetTrackedBlendshapes();

        float mouth = Mathf.Clamp(_currentMouth, 0f, maxWeight);
        if (_mouthOpenIndex >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(_mouthOpenIndex, mouth);
        for (int i = 0; i < _extraMouthIndices.Length; i++)
            if (_extraMouthIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_extraMouthIndices[i], mouth * 0.25f);

        if (!speaking && mouth < 0.5f) return;

        for (int i = 0; i < _vowelIndices.Length; i++)
        {
            if (_vowelIndices[i] >= 0)
            {
                float t = (i < _vowelTargets.Length) ? _vowelTargets[i] : 0.5f;
                float w = Mathf.Clamp(mouth * t * (maxVowel / Mathf.Max(maxMouth, 1f)), 0f, maxWeight);
                skinnedMeshRenderer.SetBlendShapeWeight(_vowelIndices[i], w);
            }
        }
        for (int i = 0; i < _consonantIndices.Length; i++)
        {
            if (_consonantIndices[i] >= 0)
            {
                float t = (i < _consonantTargets.Length) ? _consonantTargets[i] : 0.3f;
                float w = Mathf.Clamp(mouth * t * (maxConsonant / Mathf.Max(maxMouth, 1f)), 0f, maxWeight);
                skinnedMeshRenderer.SetBlendShapeWeight(_consonantIndices[i], w);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BLINK — eyes fully close to weight 100 with optional double-blink
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateBlink()
    {
        if (_blinkTimer <= 0f && _pendingBlinks == 0 && Time.time >= _nextBlinkTime)
        {
            _pendingBlinks = (UnityEngine.Random.value < doubleBlinkChance) ? 2 : 1;
            _blinkTimer    = blinkDuration;
            float gap = (_pendingBlinks > 1)
                ? blinkDuration + 0.07f
                : UnityEngine.Random.Range(blinkIntervalMin, blinkIntervalMax);
            _nextBlinkTime = Time.time + gap;
        }

        // Triangle ramp 0 → 100 → 0 over blinkDuration
        float targetBlink = 0f;
        if (_blinkTimer > 0f)
        {
            _blinkTimer -= Time.deltaTime;
            float elapsed = blinkDuration - Mathf.Max(_blinkTimer, 0f);
            float half    = blinkDuration * 0.5f;
            float t = elapsed < half
                ? elapsed / half
                : 1f - (elapsed - half) / half;
            targetBlink = Mathf.Clamp01(t);

            if (_blinkTimer <= 0f && _pendingBlinks > 1)
            {
                _pendingBlinks--;
                _blinkTimer = blinkDuration;
            }
            else if (_blinkTimer <= 0f)
            {
                _pendingBlinks = 0;
            }
        }

        // MoveTowards guarantees the eye ACTUALLY reaches 100 (unlike Lerp which asymptotes)
        float blinkSpeed = 100f / (blinkDuration * 0.5f + 0.001f);
        _currentBlink = Mathf.MoveTowards(_currentBlink, targetBlink * 100f, blinkSpeed * Time.deltaTime);

        float bw = Mathf.Clamp(_currentBlink, 0f, 100f);
        if (_rightEyeIndex >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_rightEyeIndex, bw);
        if (_leftEyeIndex  >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_leftEyeIndex,  bw);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EYE SACCADE — subtle random squint asymmetry for "alive" look
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateEyeSaccade(float dt)
    {
        if (Time.time >= _nextSaccadeTime)
        {
            _nextSaccadeTime    = Time.time + UnityEngine.Random.Range(0.25f, 1.4f);
            _saccadeAsymRTarget = UnityEngine.Random.Range(-4f, 4f);
            _saccadeAsymLTarget = UnityEngine.Random.Range(-4f, 4f);
        }
        float sf = 1f - Mathf.Exp(-5f * dt);
        _saccadeAsymR = Mathf.Lerp(_saccadeAsymR, _saccadeAsymRTarget, sf);
        _saccadeAsymL = Mathf.Lerp(_saccadeAsymL, _saccadeAsymLTarget, sf);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MICRO-EXPRESSION SYSTEM
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateMicroExpressions()
    {
        bool  speaking = IsAISpeaking();
        float dt       = Time.deltaTime;

        UpdateEyeSaccade(dt);

        UpdateEmotionBeats(speaking, dt);

        if (Time.time >= _nextExpressionTime)
        {
            _nextExpressionTime = Time.time + UnityEngine.Random.Range(expressionChangeMin, expressionChangeMax);
            RandomizeExpressionTargets(speaking);
        }

        float lf = 1f - Mathf.Exp(-expressionSmooth * dt);

        for (int i = 0; i < _browUpCurrent.Length; i++)
            _browUpCurrent[i] = Mathf.Lerp(_browUpCurrent[i], _browUpTarget[i], lf);
        for (int i = 0; i < _browDownCurrent.Length; i++)
            _browDownCurrent[i] = Mathf.Lerp(_browDownCurrent[i], _browDownTarget[i], lf);

        // Saccade adds subtle asymmetry offset to squints
        _squintRCur = Mathf.Lerp(_squintRCur, _squintRTgt + Mathf.Max(0f, _saccadeAsymR), lf);
        _squintLCur = Mathf.Lerp(_squintLCur, _squintLTgt + Mathf.Max(0f, _saccadeAsymL), lf);

        _lowLidRCur  = Mathf.Lerp(_lowLidRCur,  _lowLidRTgt,  lf);
        _lowLidLCur  = Mathf.Lerp(_lowLidLCur,  _lowLidLTgt,  lf);
        _nostrilRCur = Mathf.Lerp(_nostrilRCur, _nostrilRTgt, lf);
        _nostrilLCur = Mathf.Lerp(_nostrilLCur, _nostrilLTgt, lf);

        for (int i = 0; i < _lipCurrent.Length; i++)
            _lipCurrent[i] = Mathf.Lerp(_lipCurrent[i], _lipTarget[i], lf);
        for (int i = 0; i < _smileCurrent.Length; i++)
            _smileCurrent[i] = Mathf.Lerp(_smileCurrent[i], _smileTarget[i], lf);
        for (int i = 0; i < _emotionCurrent.Length; i++)
            _emotionCurrent[i] = Mathf.Lerp(_emotionCurrent[i], _emotionTarget[i], lf);

        _neckRCur   = Mathf.Lerp(_neckRCur,   _neckRTgt,   lf);
        _neckLCur   = Mathf.Lerp(_neckLCur,   _neckLTgt,   lf);
        _glotisCur  = Mathf.Lerp(_glotisCur,  _glotisTgt,  lf);
        _jawCompCur = Mathf.Lerp(_jawCompCur, _jawCompTgt, lf);
        _chinCur    = Mathf.Lerp(_chinCur,    _chinTgt,    lf);

        ApplyExpressionWeights();
    }

    private void RandomizeExpressionTargets(bool speaking)
    {
        float max        = maxExpressionWeight;
        float speakMul   = speaking ? 1f : 0.5f;
        float zeroChance = speaking ? 0.2f : 0.5f;

        bool browsUp = UnityEngine.Random.value > 0.5f;
        for (int i = 0; i < _browUpTarget.Length; i++)
            _browUpTarget[i] = (browsUp && UnityEngine.Random.value > zeroChance)
                ? UnityEngine.Random.Range(1f, max * speakMul) : 0f;
        for (int i = 0; i < _browDownTarget.Length; i++)
            _browDownTarget[i] = (!browsUp && UnityEngine.Random.value > zeroChance)
                ? UnityEngine.Random.Range(1f, max * speakMul * 0.7f) : 0f;

        if (UnityEngine.Random.value > zeroChance)
        {
            float sq = UnityEngine.Random.Range(1f, max * speakMul * 0.6f);
            _squintRTgt = sq;
            _squintLTgt = Mathf.Clamp(sq + UnityEngine.Random.Range(-2f, 2f), 0f, max);
        }
        else { _squintRTgt = 0f; _squintLTgt = 0f; }

        if (UnityEngine.Random.value > 0.65f)
        { float lid = UnityEngine.Random.Range(1f, max * 0.4f); _lowLidRTgt = lid; _lowLidLTgt = lid; }
        else { _lowLidRTgt = 0f; _lowLidLTgt = 0f; }

        if (speaking && UnityEngine.Random.value > 0.6f)
        { float n = UnityEngine.Random.Range(1f, max * 0.35f); _nostrilRTgt = n; _nostrilLTgt = n; }
        else { _nostrilRTgt = 0f; _nostrilLTgt = 0f; }

        for (int i = 0; i < _lipTarget.Length; i++)
            _lipTarget[i] = (UnityEngine.Random.value > 0.6f)
                ? UnityEngine.Random.Range(0f, max * speakMul * 0.4f) : 0f;

        bool doSmile = !_beatActive && UnityEngine.Random.value > 0.7f;
        for (int i = 0; i < _smileTarget.Length; i++)
            _smileTarget[i] = doSmile ? UnityEngine.Random.Range(2f, max * speakMul * 0.6f) : 0f;

        if (!_beatActive)
        {
            for (int i = 0; i < _emotionTarget.Length; i++) _emotionTarget[i] = 0f;
            if (UnityEngine.Random.value > 0.7f && _emotionTarget.Length > 0)
            {
                int pick = UnityEngine.Random.Range(0, _emotionTarget.Length);
                _emotionTarget[pick] = UnityEngine.Random.Range(2f, max * speakMul * 0.5f);
                int pair = (pick % 2 == 0 && pick + 1 < _emotionTarget.Length) ? pick + 1 : pick - 1;
                if (pair >= 0 && pair < _emotionTarget.Length)
                    _emotionTarget[pair] = Mathf.Clamp(_emotionTarget[pick] + UnityEngine.Random.Range(-2f, 2f), 0f, max);
            }
        }

        if (speaking && UnityEngine.Random.value > 0.8f)
        {
            float nt = UnityEngine.Random.Range(1f, max * 0.3f);
            _neckRTgt = nt; _neckLTgt = nt;
            _glotisTgt = UnityEngine.Random.Range(0f, max * 0.2f);
        }
        else { _neckRTgt = 0f; _neckLTgt = 0f; _glotisTgt = 0f; }

        _jawCompTgt = (speaking && UnityEngine.Random.value > 0.7f)
            ? UnityEngine.Random.Range(1f, max * 0.3f) : 0f;
        _chinTgt = (UnityEngine.Random.value > 0.75f)
            ? UnityEngine.Random.Range(1f, max * 0.25f) : 0f;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // EMOTION BEAT SYSTEM — timed named expression arcs
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateEmotionBeats(bool speaking, float dt)
    {
        // Schedule a new beat when idle
        if (!_beatActive && Time.time >= _nextBeatTime)
        {
            if (speaking || UnityEngine.Random.value < 0.45f)
                TriggerRandomEmotionBeat(speaking);
            else
                _nextBeatTime = Time.time + UnityEngine.Random.Range(beatIntervalMin, beatIntervalMax);
        }

        if (!_beatActive) return;

        _currentBeat.phaseTimer += dt;
        float fadeIn  = _currentBeat.fadeInDur;
        float hold    = _currentBeat.holdDuration;
        float fadeOut = _currentBeat.fadeOutDur;
        float total   = fadeIn + hold + fadeOut;
        float t       = _currentBeat.phaseTimer;

        float envelope;
        if (t < fadeIn)
            envelope = t / Mathf.Max(fadeIn, 0.001f);
        else if (t < fadeIn + hold)
            envelope = 1f;
        else if (t < total)
            envelope = 1f - (t - fadeIn - hold) / Mathf.Max(fadeOut, 0.001f);
        else
        {
            // Beat finished — zero targets and wait for next
            _beatActive   = false;
            _nextBeatTime = Time.time + UnityEngine.Random.Range(beatIntervalMin, beatIntervalMax);
            for (int i = 0; i < _smileTarget.Length; i++) _smileTarget[i]   = 0f;
            for (int i = 0; i < _emotionTarget.Length; i++) _emotionTarget[i] = 0f;
            for (int i = 0; i < _browUpTarget.Length; i++) _browUpTarget[i]   = 0f;
            for (int i = 0; i < _browDownTarget.Length; i++) _browDownTarget[i] = 0f;
            _squintRTgt  = 0f; _squintLTgt  = 0f;
            _nostrilRTgt = 0f; _nostrilLTgt = 0f;
            _chinTgt     = 0f;
            return;
        }

        ApplyBeatTargets(_currentBeat.type, envelope * _currentBeat.intensity);
    }

    private void TriggerRandomEmotionBeat(bool speaking)
    {
        EmotionBeatType[] choices;
        float[]           weights;

        if (speaking)
        {
            choices = new[] { EmotionBeatType.Smile, EmotionBeatType.BrowRaise, EmotionBeatType.Thoughtful,
                              EmotionBeatType.Playful, EmotionBeatType.Concern, EmotionBeatType.Disgust };
            weights = new[] { 0.35f, 0.25f, 0.20f, 0.10f, 0.07f, 0.03f };
        }
        else
        {
            choices = new[] { EmotionBeatType.Smile, EmotionBeatType.Thoughtful, EmotionBeatType.Concern,
                              EmotionBeatType.BrowRaise, EmotionBeatType.Playful, EmotionBeatType.Disgust };
            weights = new[] { 0.30f, 0.30f, 0.20f, 0.10f, 0.07f, 0.03f };
        }

        float roll  = UnityEngine.Random.value;
        float accum = 0f;
        EmotionBeatType picked = choices[0];
        for (int i = 0; i < weights.Length; i++)
        {
            accum += weights[i];
            if (roll <= accum) { picked = choices[i]; break; }
        }

        _currentBeat = new EmotionBeat
        {
            type         = picked,
            intensity    = UnityEngine.Random.Range(beatIntensityMax * 0.4f, beatIntensityMax),
            holdDuration = UnityEngine.Random.Range(0.8f, 2.2f),
            fadeInDur    = UnityEngine.Random.Range(0.25f, 0.55f),
            fadeOutDur   = UnityEngine.Random.Range(0.4f, 0.9f),
            phaseTimer   = 0f
        };
        _beatActive = true;
    }

    /// <summary>Set smooth targets that represent one named emotion at the given weight.</summary>
    private void ApplyBeatTargets(EmotionBeatType type, float w)
    {
        // Reset all beat-driven targets before writing so only the active type is expressed
        for (int i = 0; i < _smileTarget.Length; i++)   _smileTarget[i]   = 0f;
        for (int i = 0; i < _emotionTarget.Length; i++) _emotionTarget[i] = 0f;
        for (int i = 0; i < _browUpTarget.Length; i++)   _browUpTarget[i]  = 0f;
        for (int i = 0; i < _browDownTarget.Length; i++) _browDownTarget[i] = 0f;
        _squintRTgt  = 0f; _squintLTgt  = 0f;
        _nostrilRTgt = 0f; _nostrilLTgt = 0f;
        _chinTgt     = 0f;

        switch (type)
        {
            case EmotionBeatType.Smile:
                // Genuine smile: corners pull, slight cheek squint, inner brows very lightly up
                for (int i = 0; i < _smileTarget.Length; i++) _smileTarget[i] = w;
                _squintRTgt = w * 0.35f;
                _squintLTgt = w * 0.35f;
                if (_browUpTarget.Length >= 3) { _browUpTarget[1] = w * 0.2f; _browUpTarget[2] = w * 0.2f; }
                break;

            case EmotionBeatType.BrowRaise:
                // Surprise / interest: all brows lift, chin drops a touch
                for (int i = 0; i < _browUpTarget.Length; i++) _browUpTarget[i] = w;
                _chinTgt = w * 0.12f;
                break;

            case EmotionBeatType.Concern:
                // Empathy / worry: inner brows down, sad + pity shapes
                if (_browDownTarget.Length >= 3)
                { _browDownTarget[1] = w * 0.85f; _browDownTarget[2] = w * 0.85f; }
                // emotionShapes: 0=Rsad, 1=Lsad, 2=Rpityful, 3=Lpityful
                if (_emotionTarget.Length >= 4)
                {
                    _emotionTarget[0] = w * 0.70f;
                    _emotionTarget[1] = w * 0.70f;
                    _emotionTarget[2] = w * 0.50f;
                    _emotionTarget[3] = w * 0.50f;
                }
                break;

            case EmotionBeatType.Disgust:
                // Subtle disgust: nostrils, corners, brow outer down
                _nostrilRTgt = w * 0.6f;
                _nostrilLTgt = w * 0.6f;
                if (_emotionTarget.Length >= 6) { _emotionTarget[4] = w * 0.8f; _emotionTarget[5] = w * 0.8f; }
                if (_browDownTarget.Length >= 4) { _browDownTarget[0] = w * 0.45f; _browDownTarget[3] = w * 0.45f; }
                break;

            case EmotionBeatType.Thoughtful:
                // Asymmetric: one brow up, slight squint on opposite side
                if (_browUpTarget.Length >= 2) _browUpTarget[1] = w * 0.9f; // RbrowUp only
                _squintLTgt = w * 0.28f;
                _chinTgt    = w * 0.1f;
                break;

            case EmotionBeatType.Playful:
                // Lopsided smile + outer brow flash on same side
                if (_smileTarget.Length >= 2) { _smileTarget[0] = w; _smileTarget[1] = w * 0.4f; }
                if (_browUpTarget.Length >= 1) _browUpTarget[0] = w * 0.55f; // RRbrowUp outer
                _squintRTgt = w * 0.2f;
                break;
        }
    }

    private void ApplyExpressionWeights()
    {
        if (skinnedMeshRenderer == null) return;

        for (int i = 0; i < _browUpIndices.Length; i++)
            if (_browUpIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_browUpIndices[i], _browUpCurrent[i]);
        for (int i = 0; i < _browDownIndices.Length; i++)
            if (_browDownIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_browDownIndices[i], _browDownCurrent[i]);

        if (_rightSquintIdx >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_rightSquintIdx, Mathf.Clamp(_squintRCur, 0f, maxWeight));
        if (_leftSquintIdx  >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_leftSquintIdx,  Mathf.Clamp(_squintLCur, 0f, maxWeight));
        if (_rightLowLidIdx >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_rightLowLidIdx, _lowLidRCur);
        if (_leftLowLidIdx  >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_leftLowLidIdx,  _lowLidLCur);
        if (_rightNostrilIdx >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_rightNostrilIdx, _nostrilRCur);
        if (_leftNostrilIdx  >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_leftNostrilIdx,  _nostrilLCur);

        for (int i = 0; i < _lipIndices.Length; i++)
            if (_lipIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_lipIndices[i], _lipCurrent[i]);
        for (int i = 0; i < _smileIndices.Length; i++)
            if (_smileIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_smileIndices[i], _smileCurrent[i]);
        for (int i = 0; i < _emotionIndices.Length; i++)
            if (_emotionIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_emotionIndices[i], _emotionCurrent[i]);

        if (_rightNeckIdx  >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_rightNeckIdx,  _neckRCur);
        if (_leftNeckIdx   >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_leftNeckIdx,   _neckLCur);
        if (_glotisIdx     >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_glotisIdx,     _glotisCur);
        if (_jawCompressIdx >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_jawCompressIdx, _jawCompCur);
        if (_chinIdx       >= 0) skinnedMeshRenderer.SetBlendShapeWeight(_chinIdx,       _chinCur);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // AUDIO ANALYSIS MODE  (Auto Move OFF — classic amplitude lip sync)
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateAudioAnalysis()
    {
        if (audioSource == null)
        {
            if (_audioSource_cached != null)       audioSource = _audioSource_cached;
            else if (pcmAudioPlayer != null)       audioSource = pcmAudioPlayer.AudioSource;
            else audioSource = GetComponent<AudioSource>() ?? GetComponentInChildren<AudioSource>();
            _audioSource_cached = audioSource;
            if (audioSource == null) return;
        }

        float target = CalculateAmplitudeWeight();
        _currentWeight = Mathf.Lerp(_currentWeight, target, 1f - Mathf.Exp(-smoothing * Time.deltaTime * 60f));
        _currentWeight = Mathf.Clamp(_currentWeight, 0f, maxWeight);
        ApplyAnalysisWeights(_currentWeight);
    }

    private void UpdateExternalAudioLipSync()
    {
        if (_externalLipSyncAudioSource == null)
        {
            _currentWeight = Mathf.MoveTowards(_currentWeight, 0f, silentDecaySpeed * Time.deltaTime);
            ApplyAnalysisWeights(_currentWeight);
            return;
        }

        float target = CalculateAmplitudeWeightFromSource(_externalLipSyncAudioSource);
        _currentWeight = Mathf.Lerp(_currentWeight, target, 1f - Mathf.Exp(-smoothing * Time.deltaTime * 60f));
        _currentWeight = Mathf.Clamp(_currentWeight, 0f, maxWeight);
        ApplyAnalysisWeights(_currentWeight);
    }

    private float CalculateAmplitudeWeight()
    {
        return CalculateAmplitudeWeightFromSource(audioSource);
    }

    private float CalculateAmplitudeWeightFromSource(AudioSource source)
    {
        if (source == null)
            return Mathf.MoveTowards(_currentWeight, 0f, silentDecaySpeed * Time.deltaTime);

        if (!source.isPlaying && source.clip == null)
            return Mathf.MoveTowards(_currentWeight, 0f, silentDecaySpeed * Time.deltaTime);

        source.GetSpectrumData(_samples, 0, FFTWindow.BlackmanHarris);

        float energy     = 0f;
        float sampleRate = AudioSettings.outputSampleRate;
        for (int i = 0; i < _samples.Length; i++)
        {
            float freq = (i * sampleRate) / (2f * _samples.Length);
            if (freq >= 200f && freq <= 2000f) energy += _samples[i];
        }

        source.GetOutputData(_waveform, 0);
        float rms = 0f;
        for (int i = 0; i < _waveform.Length; i++) { float s = _waveform[i]; rms += s * s; }
        rms = Mathf.Sqrt(rms / _waveform.Length);

        return Mathf.Clamp((energy * gain) + (rms * gain * 50f), 0f, maxWeight);
    }

    private void ApplyAnalysisWeights(float weight)
    {
        ResetTrackedBlendshapes();

        float clamped = Mathf.Clamp(weight, 0f, maxWeight);
        if (_mouthOpenIndex >= 0)
            skinnedMeshRenderer.SetBlendShapeWeight(_mouthOpenIndex, clamped);

        float extra = clamped * 0.3f;
        for (int i = 0; i < _extraMouthIndices.Length; i++)
            if (_extraMouthIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_extraMouthIndices[i], extra);

        float vw = clamped * 0.9f;
        for (int i = 0; i < _vowelIndices.Length; i++)
            if (_vowelIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_vowelIndices[i],
                    Mathf.Clamp(vw * (0.8f + 0.2f * (i % 2)), 0f, maxWeight));

        float cw = clamped * 0.35f;
        for (int i = 0; i < _consonantIndices.Length; i++)
            if (_consonantIndices[i] >= 0)
                skinnedMeshRenderer.SetBlendShapeWeight(_consonantIndices[i],
                    Mathf.Clamp(cw, 0f, maxWeight));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // RESET HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    private void ResetTrackedBlendshapes()
    {
        if (skinnedMeshRenderer == null) return;
        ZeroIdx(_mouthOpenIndex);
        ZeroArr(_extraMouthIndices);
        ZeroArr(_vowelIndices);
        ZeroArr(_consonantIndices);
    }

    private void ResetAllExpressions()
    {
        if (skinnedMeshRenderer == null) return;
        ZeroArr(_browUpIndices);
        ZeroArr(_browDownIndices);
        ZeroIdx(_rightSquintIdx); ZeroIdx(_leftSquintIdx);
        ZeroIdx(_rightLowLidIdx); ZeroIdx(_leftLowLidIdx);
        ZeroIdx(_rightNostrilIdx); ZeroIdx(_leftNostrilIdx);
        ZeroIdx(_jawCompressIdx); ZeroIdx(_rightJawIdx); ZeroIdx(_leftJawIdx);
        ZeroIdx(_jawFrontIdx); ZeroIdx(_chinIdx); ZeroIdx(_chewIdx);
        ZeroArr(_lipIndices);
        ZeroArr(_smileIndices);
        ZeroArr(_emotionIndices);
        ZeroIdx(_glotisIdx); ZeroIdx(_rightNeckIdx); ZeroIdx(_leftNeckIdx);
        ZeroIdx(_rightEyeOpenIdx); ZeroIdx(_leftEyeOpenIdx);
    }

    private void ZeroIdx(int idx)
    {
        if (idx >= 0) skinnedMeshRenderer.SetBlendShapeWeight(idx, 0f);
    }

    private void ZeroArr(int[] arr)
    {
        for (int i = 0; i < arr.Length; i++) ZeroIdx(arr[i]);
    }
}
