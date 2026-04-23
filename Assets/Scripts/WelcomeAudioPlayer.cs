using System.Collections;
using UnityEngine;
using TMPro;

namespace MedicalExam
{
    /// <summary>
    /// Speaks welcome introduction text via TTS when a role is selected
    /// Introduces Frau Marie (teacher NPC) and the scenario
    /// Call PlayDoctorWelcome() or PlayPatientWelcome() from Unity onClick events
    /// </summary>
    public class WelcomeAudioPlayer : MonoBehaviour
    {
        // ── Step 1: OSCE instructions (played when scenario card D2D / D2P is clicked) ──
        [Header("Step 1 — Scenario Card Click: OSCE Instructions")]
        [TextArea(3, 10)]
        [Tooltip("Spoken by realtime AI when the D2D scenario card is clicked. No conversation start yet.")]
        [SerializeField] private string doctorToDoctorInstructionsText =
            "Stellen Sie der ärztlichen Leitung den vorliegenden Patientenfall vor und beantworten Sie mögliche Rückfragen. Verwenden Sie dabei die medizinische Fachterminologie.\n\n" +
            "Im Anschluss werden nach dem Zufallsprinzip fünf gängige medizinische Fachbegriffe ausgewählt. Bitte erklären Sie deren jeweilige Bedeutung einmal mündlich.";

        [TextArea(3, 10)]
        [Tooltip("Spoken by realtime AI when the D2P scenario card is clicked. No conversation start yet.")]
        [SerializeField] private string doctorToPatientInstructionsText =
            "Führen Sie ein strukturiertes Anamnesegespräch und erheben Sie dabei die persönlichen Daten, aktuellen Beschwerden, Vorerkrankungen, die Medikation, und Familienanamnese.\n\n" +
            "Leiten Sie daraus mögliche Verdachtsdiagnosen ab, unterbreiten Sie Vorschläge zur weiterführenden Diagnostik und Therapie und erläutern Sie die Maßnahmen.\n\n" +
            "Verwenden Sie dabei allgemein verständliche Formulierungen und verzichten Sie, soweit möglich, auf medizinische Fachterminologie. Schriftliche Notizen sind zulässig.";

        // ── Step 2: Welcome message (played when Start button is clicked, before conversation begins) ──
        [Header("Step 2 — Start Button: Welcome Message")]
        [TextArea(3, 10)]
        [Tooltip("Spoken by realtime AI when Start is clicked for D2D — then conversation begins.")]
        [SerializeField] private string doctorToDoctorWelcomeText =
            "Hallo, Sie können mit der Fallvorstellung zum gegebenen Fall beginnen. Ich spiele Ihre ärztliche Leitung und meine Kollegin macht Notizen. Nach dem Gespräch erhalten Sie sofort Ihr Ergebnis. Die Zeit geht jetzt los. Viel Erfolg!";

        [TextArea(3, 10)]
        [Tooltip("Spoken by realtime AI when Start is clicked for D2P — then conversation begins.")]
        [SerializeField] private string doctorToPatientWelcomeText =
            "Hallo, Sie können mit dem Anamnesegespräch zum gegebenen Fall beginnen. Ich spiele die Patientin und meine Kollegin macht Notizen. Nach dem Gespräch erhalten Sie sofort Ihr Ergebnis. Die Zeit geht jetzt los. Viel Erfolg!";

        [Header("Realtime Welcome Voice (preferred)")]
        [Tooltip("OpenAIRealtimeClient used to speak the welcome in the same voice as the conversation. If assigned, takes priority over TTS.")]
        [SerializeField] private OpenAIRealtimeClient realtimeClient;

        [Header("TTS Service (fallback)")]
        [Tooltip("Reference to OpenAI TTS player — used only when realtimeClient is not assigned.")]
        [SerializeField] private OpenAITTSPlayer ttsPlayer;
        
        [Header("Scenario Reference")]
        [Tooltip("Reference to MedicalExamManager to get scenario description")]
        [SerializeField] private MedicalExamManager examManager;

        [Header("Welcome Avatar Lip Sync")]
        [Tooltip("Avatar GameObject used for Doctor-to-Doctor welcome lip sync.")]
        [SerializeField] private GameObject doctorToDoctorAvatar;

        [Tooltip("Avatar GameObject used for Doctor-to-Patient welcome lip sync.")]
        [SerializeField] private GameObject doctorToPatientAvatar;

        [Header("Start Behavior")]
        [Tooltip("If true, speaks the welcome text before starting the realtime conversation. Disable to skip intro.")]
        [SerializeField] private bool playWelcomeBeforeStart = true;

        [Header("Welcome Text Preview UI")]
        [Tooltip("Optional UI text field that shows the resolved Doctor-to-Doctor welcome text when PlayDoctorWelcome is triggered.")]
        [SerializeField] private TMP_Text doctorWelcomePreviewText;

        [Tooltip("Optional UI text field that shows the resolved Doctor-to-Patient welcome text when PlayPatientWelcome is triggered.")]
        [SerializeField] private TMP_Text patientWelcomePreviewText;

        [Tooltip("Small delay before StartConversation after welcome finishes. Helps when remote prompts are still initializing.")]
        [SerializeField] private float startConversationDelaySeconds = 0.75f;
        
        private bool _isPlaying = false;
        private bool _isStartingConversation = false;
        private maherlips _activeWelcomeLips;
        private bool _selectedD2D = true; // set by PlayD2DInstructions / PlayD2PInstructions

        private string GetVoice(MedicalExamManager.RoleType role)
        {
            if (examManager != null)
                return examManager.GetRealtimeVoiceForRole(role);
            return role == MedicalExamManager.RoleType.DoctorToDoctor ? "shimmer" : "coral";
        }

        private void Start()
        {
            // Auto-find references if not assigned in Inspector
            if (realtimeClient == null)
            {
                realtimeClient = FindFirstObjectByType<OpenAIRealtimeClient>();
                if (realtimeClient != null)
                    Debug.Log("[WelcomeAudioPlayer] Auto-found OpenAIRealtimeClient in scene.");
            }
            if (ttsPlayer == null)
            {
                ttsPlayer = FindFirstObjectByType<OpenAITTSPlayer>();
                if (ttsPlayer != null)
                    Debug.Log("[WelcomeAudioPlayer] Auto-found OpenAITTSPlayer in scene.");
            }
            if (examManager == null)
            {
                examManager = FindFirstObjectByType<MedicalExamManager>();
            }
        }
        
        // ── STEP 1: Scenario card buttons ────────────────────────────────────────────

        /// <summary>
        /// Step 1 — Wire to D2D scenario card button.
        /// Sets role to D2D, speaks OSCE instructions. Does NOT start conversation.
        /// </summary>
        public void PlayD2DInstructions()
        {
            Debug.Log("[WelcomeAudioPlayer] PlayD2DInstructions clicked.");
            EnsureReferences();
            _selectedD2D = true;
            if (examManager != null)
                examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToDoctor);
            StartCoroutine(PlayInstructionsCoroutine(doctorToDoctorInstructionsText, GetVoice(MedicalExamManager.RoleType.DoctorToDoctor)));
        }

        /// <summary>
        /// Step 1 — Wire to D2P scenario card button.
        /// Sets role to D2P, speaks OSCE instructions. Does NOT start conversation.
        /// </summary>
        public void PlayD2PInstructions()
        {
            Debug.Log("[WelcomeAudioPlayer] PlayD2PInstructions clicked.");
            EnsureReferences();
            _selectedD2D = false;
            if (examManager != null)
                examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToPatient);
            StartCoroutine(PlayInstructionsCoroutine(doctorToPatientInstructionsText, GetVoice(MedicalExamManager.RoleType.DoctorToPatient)));
        }

        private IEnumerator PlayInstructionsCoroutine(string text, string voice)
        {
            if (realtimeClient != null)
            {
                bool done = false;
                realtimeClient.PlayWelcomeAndDisconnect(text, voice, () => done = true);
                while (!done) yield return null;
            }
            else if (ttsPlayer != null)
            {
                var vt = _selectedD2D ? OpenAITTSPlayer.VoiceType.DoctorToDoctor : OpenAITTSPlayer.VoiceType.DoctorToPatient;
                yield return StartCoroutine(ttsPlayer.SpeakAndWait(text, vt));
            }
        }

        // ── STEP 2: Start button ─────────────────────────────────────────────────────

        /// <summary>
        /// Step 2 — Wire to the Start button.
        /// Speaks the welcome text for whichever role was selected in Step 1, then starts conversation.
        /// </summary>
        public void PlayWelcomeForSelectedRole()
        {
            Debug.Log($"[WelcomeAudioPlayer] PlayWelcomeForSelectedRole — role={(_selectedD2D ? "D2D" : "D2P")}");
            EnsureReferences();
            string scenarioDesc = GetScenarioDescription();
            if (_selectedD2D)
            {
                SetWelcomePreview(PrepareDoctorToDoctorText(scenarioDesc));
                if (!playWelcomeBeforeStart) { StartConversationImmediately(); return; }
                StartCoroutine(PlayWelcomeAndStartConversation(scenarioDesc, true, OpenAITTSPlayer.VoiceType.DoctorToDoctor));
            }
            else
            {
                SetWelcomePreview(PrepareDoctorToPatientText(scenarioDesc));
                if (!playWelcomeBeforeStart) { StartConversationImmediately(); return; }
                StartCoroutine(PlayWelcomeAndStartConversation(scenarioDesc, false, OpenAITTSPlayer.VoiceType.DoctorToPatient));
            }
        }

        // ── Legacy: role + welcome + start in one click (kept for backwards-compat) ──

        /// <summary>
        /// PUBLIC: Play Doctor-to-Doctor welcome. Call this from Unity onClick event.
        /// </summary>
        public void PlayDoctorWelcome()
        {
            Debug.Log("[WelcomeAudioPlayer] PlayDoctorWelcome clicked.");
            EnsureReferences();
            if (examManager != null)
            {
                examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToDoctor);
            }

            string scenarioDesc = GetScenarioDescription();
            string welcomeText = PrepareDoctorToDoctorText(scenarioDesc);
            SetWelcomePreview(welcomeText);

            if (!playWelcomeBeforeStart)
            {
                StartConversationImmediately();
                return;
            }
            StartCoroutine(PlayWelcomeAndStartConversation(scenarioDesc, true, OpenAITTSPlayer.VoiceType.DoctorToDoctor));
        }
        
        /// <summary>
        /// PUBLIC: Play Doctor-to-Patient welcome. Call this from Unity onClick event.
        /// </summary>
        public void PlayPatientWelcome()
        {
            Debug.Log("[WelcomeAudioPlayer] PlayPatientWelcome clicked.");
            EnsureReferences();
            if (examManager != null)
            {
                examManager.OnRoleSelected(MedicalExamManager.RoleType.DoctorToPatient);
            }

            string scenarioDesc = GetScenarioDescription();
            string welcomeText = PrepareDoctorToPatientText(scenarioDesc);
            SetWelcomePreview(welcomeText);

            if (!playWelcomeBeforeStart)
            {
                StartConversationImmediately();
                return;
            }
            StartCoroutine(PlayWelcomeAndStartConversation(scenarioDesc, false, OpenAITTSPlayer.VoiceType.DoctorToPatient));
        }
        
        /// <summary>
        /// Plays welcome audio then triggers the conversation start.
        /// Uses realtime AI voice when realtimeClient is assigned; falls back to TTS.
        /// </summary>
        private IEnumerator PlayWelcomeAndStartConversation(string scenarioDescription, bool isDoctorToDoctor, OpenAITTSPlayer.VoiceType voiceType)
        {
            Debug.Log($"[WelcomeAudioPlayer] Playing welcome for {(isDoctorToDoctor ? "Doctor-to-Doctor" : "Doctor-to-Patient")}");

            string welcomeText = isDoctorToDoctor
                ? PrepareDoctorToDoctorText(scenarioDescription)
                : PrepareDoctorToPatientText(scenarioDescription);

            if (realtimeClient != null)
            {
                // Use realtime AI voice (same voice used during conversation)
                string voice = GetVoice(isDoctorToDoctor ? MedicalExamManager.RoleType.DoctorToDoctor : MedicalExamManager.RoleType.DoctorToPatient);
                bool done = false;
                realtimeClient.PlayWelcomeAndDisconnect(welcomeText, voice, () => done = true);
                while (!done)
                    yield return null;
            }
            else if (ttsPlayer != null)
            {
                // Fallback: OpenAI TTS
                BeginWelcomeLipSync(isDoctorToDoctor ? doctorToDoctorAvatar : doctorToPatientAvatar);
                if (isDoctorToDoctor)
                    yield return StartCoroutine(PlayDoctorToDoctorWelcomeCoroutine(scenarioDescription, voiceType));
                else
                    yield return StartCoroutine(PlayDoctorToPatientWelcomeCoroutine(scenarioDescription, voiceType));
                EndWelcomeLipSync();
            }
            else
            {
                Debug.LogWarning("[WelcomeAudioPlayer] No realtimeClient or ttsPlayer assigned — skipping audio welcome.");
            }

            Debug.Log("[WelcomeAudioPlayer] Welcome finished. Starting realtime conversation...");
            StartConversationImmediately();
        }
        
        /// <summary>
        /// Gets the scenario description from the exam manager
        /// </summary>
        private string GetScenarioDescription()
        {
            if (examManager != null)
            {
                return examManager.GetScenarioDescription();
            }
            return "ein medizinisches Szenario";
        }
        
        /// <summary>
        /// Speaks the doctor-to-doctor welcome text and waits for completion
        /// </summary>
        private IEnumerator PlayDoctorToDoctorWelcomeCoroutine(string scenarioDescription = "", OpenAITTSPlayer.VoiceType voiceType = OpenAITTSPlayer.VoiceType.DoctorToDoctor)
        {
            if (_isPlaying)
            {
                Debug.LogWarning("WelcomeAudioPlayer: Already playing, skipping...");
                yield break;
            }
            
            string welcomeText = PrepareDoctorToDoctorText(scenarioDescription);
            yield return StartCoroutine(SpeakWelcomeText(welcomeText, voiceType));
        }
        
        /// <summary>
        /// Speaks the doctor-to-patient welcome text and waits for completion
        /// </summary>
        private IEnumerator PlayDoctorToPatientWelcomeCoroutine(string scenarioDescription = "", OpenAITTSPlayer.VoiceType voiceType = OpenAITTSPlayer.VoiceType.DoctorToPatient)
        {
            if (_isPlaying)
            {
                Debug.LogWarning("WelcomeAudioPlayer: Already playing, skipping...");
                yield break;
            }
            
            string welcomeText = PrepareDoctorToPatientText(scenarioDescription);
            yield return StartCoroutine(SpeakWelcomeText(welcomeText, voiceType));
        }
        
        /// <summary>
        /// Prepares the doctor-to-doctor welcome text with scenario info
        /// </summary>
        private string PrepareDoctorToDoctorText(string scenarioDescription)
        {
            string text = doctorToDoctorWelcomeText;
            
            if (string.IsNullOrWhiteSpace(scenarioDescription))
                scenarioDescription = "ein medizinisches Fachgespräch";
            
            text = text.Replace("{scenario}", scenarioDescription);
            return text;
        }
        
        /// <summary>
        /// Prepares the doctor-to-patient welcome text with scenario info
        /// </summary>
        private string PrepareDoctorToPatientText(string scenarioDescription)
        {
            string text = doctorToPatientWelcomeText;
            
            if (string.IsNullOrWhiteSpace(scenarioDescription))
                scenarioDescription = "ein Patientengespräch mit wichtigen Symptomen";
            
            text = text.Replace("{scenario}", scenarioDescription);
            return text;
        }
        
        /// <summary>
        /// Speaks the welcome text using OpenAI TTS and waits for completion
        /// </summary>
        private IEnumerator SpeakWelcomeText(string text, OpenAITTSPlayer.VoiceType voiceType)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Debug.LogWarning("[WelcomeAudioPlayer] Welcome text is empty!");
                yield break;
            }
            
            if (ttsPlayer == null)
            {
                Debug.LogError("[WelcomeAudioPlayer] OpenAITTSPlayer is not assigned!");
                yield break;
            }
            
            Debug.Log($"[WelcomeAudioPlayer] Speaking welcome text via OpenAI TTS: {text}");
            
            _isPlaying = true;
            
            // Use SpeakAndWait which handles the full lifecycle: API call → play → wait for finish
            yield return StartCoroutine(ttsPlayer.SpeakAndWait(text, voiceType));
            
            Debug.Log("[WelcomeAudioPlayer] Welcome audio finished.");
            _isPlaying = false;
        }

        private void SetWelcomePreview(string text)
        {
            // Support either a single preview text field or separate doctor/patient fields.
            if (doctorWelcomePreviewText != null)
                doctorWelcomePreviewText.text = text ?? string.Empty;

            if (patientWelcomePreviewText != null)
                patientWelcomePreviewText.text = text ?? string.Empty;
        }

        private void EnsureReferences()
        {
            if (realtimeClient == null)
                realtimeClient = FindFirstObjectByType<OpenAIRealtimeClient>();
            if (ttsPlayer == null)
                ttsPlayer = FindFirstObjectByType<OpenAITTSPlayer>();
            if (examManager == null)
                examManager = FindFirstObjectByType<MedicalExamManager>();
        }

        private void BeginWelcomeLipSync(GameObject avatar)
        {
            EndWelcomeLipSync();

            if (avatar == null || ttsPlayer == null)
                return;

            var lips = avatar.GetComponentInChildren<maherlips>(true);
            if (lips == null)
                return;

            var source = ttsPlayer.PlaybackAudioSource;
            if (source == null)
                return;

            lips.BeginExternalAudioLipSync(source);
            _activeWelcomeLips = lips;
        }

        private void EndWelcomeLipSync()
        {
            if (_activeWelcomeLips != null)
            {
                _activeWelcomeLips.EndExternalAudioLipSync();
                _activeWelcomeLips = null;
            }
        }

        private void StartConversationImmediately()
        {
            if (_isStartingConversation)
            {
                Debug.LogWarning("[WelcomeAudioPlayer] Conversation start already in progress, skipping duplicate trigger.");
                return;
            }

            if (examManager == null)
            {
                Debug.LogError("[WelcomeAudioPlayer] Cannot start conversation - examManager not assigned!");
                return;
            }

            StartCoroutine(StartConversationWithDelayCoroutine());
        }

        private IEnumerator StartConversationWithDelayCoroutine()
        {
            _isStartingConversation = true;

            float delay = Mathf.Max(0f, startConversationDelaySeconds);
            if (delay > 0f)
            {
                Debug.Log($"[WelcomeAudioPlayer] Waiting {delay:0.00}s before StartConversation.");
                yield return new WaitForSeconds(delay);
            }

            if (examManager == null)
            {
                Debug.LogError("[WelcomeAudioPlayer] StartConversation aborted - examManager became null.");
                _isStartingConversation = false;
                yield break;
            }

            Debug.Log("[WelcomeAudioPlayer] Triggering MedicalExamManager.StartConversation().");
            examManager.StartConversation();
            _isStartingConversation = false;
        }
        
        /// <summary>
        /// Stops any currently playing welcome audio
        /// </summary>
        public void Stop()
        {
            EndWelcomeLipSync();

            if (ttsPlayer != null)
            {
                ttsPlayer.Stop();
            }
            _isPlaying = false;
        }
    }
}
