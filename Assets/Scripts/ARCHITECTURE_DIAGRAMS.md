# System Architecture Diagrams

## 1. Complete Evaluation Pipeline (Visual)

```
┌────────────────────────────────────────────────────────────────────┐
│                     PHASE 1: CONVERSATION                          │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│  STUDENT                                                           │
│     ↓                                                              │
│   🎤 Microphone Input                                             │
│     ↓                                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   MicrophoneStreamer.cs                  │                    │
│   │   (Captures 16kHz PCM audio)             │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   OpenAI Realtime API (WebSocket)        │                    │
│   │   • Student speech → AI text             │                    │
│   │   • AI text → Student speech (audio)     │                    │
│   │   • Hidden: tracks pronunciation         │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   RealtimeConversationManager.cs          │                    │
│   │   • Manages WebSocket connection         │                    │
│   │   • Fires onAgentTranscript event        │                    │
│   │   • Fires onUserTranscript event         │                    │
│   │   • Fires onAgentSpeaking event          │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   MedicalExamManager.cs                  │                    │
│   │   • Collects full conversation log       │                    │
│   │   • Stores in: _fullConversationLog      │                    │
│   │   • Stores realtime obs: _realtimeAI... │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   🗣️ NPCDoctor Avatar + Lip Sync (via maherlips.cs)             │
│                      ↓                                              │
│              ⏱️ TIMER RUNNING                                     │
│              (3 min or until user says "end")                     │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
                              ↓
                    [EVALUATION TRIGGERED]
                              ↓
┌────────────────────────────────────────────────────────────────────┐
│                   PHASE 2: FINE-TUNED EVALUATION                   │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│   ┌──────────────────────────────────────────┐                    │
│   │   MedicalExamManager.cs                  │                    │
│   │   EvaluateWithFineTunedGPT4()            │                    │
│   │   (Called when evaluation triggered)     │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   FineTunedGPT4EvaluationService.cs      │                    │
│   │   EvaluateConversation()                 │                    │
│   │                                          │                    │
│   │   Sends to API:                          │                    │
│   │   • Transcript (required)                │                    │
│   │   • Realtime observations (secondary)    │                    │
│   │   • Scenario info                        │                    │
│   │   • System prompt (examiner role)        │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   OpenAI API (HTTPS)                     │                    │
│   │   POST /v1/chat/completions              │                    │
│   │   Model: ft:gpt-4-turbo:default:xyz      │                    │
│   │   (or gpt-4-turbo fallback)              │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│        ⚙️  Fine-Tuned GPT-4 Evaluates                             │
│        Medical terminology accuracy                               │
│        Clinical reasoning quality                                 │
│        Exam structure & completeness                              │
│        Red flags & safety issues                                  │
│        (Uses realtime obs. as secondary input)                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   JSON Response Received:                │                    │
│   │   {                                      │                    │
│   │     "terminologie": 3.5,                 │                    │
│   │     "verstaendlichkeit": 3.8,            │                    │
│   │     "aussprache": 4.0,                   │                    │
│   │     "overallScore": 72,                  │                    │
│   │     "generalFeedback": "...",            │                    │
│   │     "terminologieFeedback": "...",       │                    │
│   │     "verstaendlichkeitFeedback": "...",  │                    │
│   │     "ausspracheeFeedback": "..."         │                    │
│   │   }                                      │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   FineTunedGPT4EvaluationService         │                    │
│   │   OnFineTunedEvaluationReceived()        │                    │
│   │                                          │                    │
│   │   Parse JSON → EvaluationResponse        │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   PlayFeedbackAsVoice()                  │                    │
│   │   (TTS: ElevenLabs or OpenAI)            │                    │
│   │                                          │                    │
│   │   Voice feedback to student              │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
│   ┌──────────────────────────────────────────┐                    │
│   │   MedicalExamManager.cs                  │                    │
│   │   OnEvaluationParsed()                   │                    │
│   │                                          │                    │
│   │   Creates ExamEvaluation object          │                    │
│   │   Saves to ExamHistory                   │                    │
│   └──────────────────┬───────────────────────┘                    │
│                      ↓                                              │
└────────────────────────────────────────────────────────────────────┘
                              ↓
┌────────────────────────────────────────────────────────────────────┐
│                   PHASE 3: RESULTS DISPLAY                         │
├────────────────────────────────────────────────────────────────────┤
│                                                                    │
│   ┌──────────────────────────────────────────┐                    │
│   │   EvaluationDisplayUI.cs                 │                    │
│   │   DisplayEvaluation()                    │                    │
│   │                                          │                    │
│   │   ┌──────────────────────────────────┐   │                    │
│   │   │ Terminologie Slider: ████ 3.5/5 │   │                    │
│   │   └──────────────────────────────────┘   │                    │
│   │                                          │                    │
│   │   ┌──────────────────────────────────┐   │                    │
│   │   │ Verständlichkeit Slider: ████ 3.8  │                    │
│   │   └──────────────────────────────────┘   │                    │
│   │                                          │                    │
│   │   ┌──────────────────────────────────┐   │                    │
│   │   │ Aussprache Slider: ████ 4.0/5   │   │                    │
│   │   └──────────────────────────────────┘   │                    │
│   │                                          │                    │
│   │   Overall Score: 72/100                 │                    │
│   │                                          │                    │
│   │   General Feedback:                      │                    │
│   │   "Good clinical knowledge, could       │                    │
│   │   improve clarity..."                    │                    │
│   │                                          │                    │
│   │   [Restart Exam Button]                 │                    │
│   └──────────────────────────────────────────┘                    │
│                                                                    │
└────────────────────────────────────────────────────────────────────┘
```

---

## 2. Class Interaction Diagram

```
                      ┌─────────────────────────┐
                      │  GameManager.cs         │
                      │  (App controller)       │
                      └────────────┬────────────┘
                                   │
                 ┌─────────────────┼─────────────────┐
                 ↓                 ↓                 ↓
        ┌────────────────┐ ┌──────────────────┐  ┌──────────────────┐
        │ Medical        │ │ Realtime         │  │ FineTuned        │
        │ ExamManager    │ │ Conversation     │  │ GPT4Evaluation   │
        │ .cs            │ │ Manager.cs       │  │ Service.cs       │
        │                │ │                  │  │                  │
        │ Controls flow  │ │ Manages voice    │  │ Handles eval API │
        │ Collects logs  │ │ conversation     │  │ Parses JSON      │
        │ Triggers eval  │ │ with OpenAI      │  │ Plays TTS        │
        └────────┬───────┘ └──────┬───────────┘  └────────┬─────────┘
                 │                │                       │
                 │                ├───────────────────────┤
                 │                │                       │
                 └────────────────┼───────────────────────┘
                          ┌───────↓────────┐
                          │ OpenAIConfig   │
                          │ .cs (Asset)    │
                          │                │
                          │ API key        │
                          │ Model ID       │
                          │ Endpoints      │
                          └────────────────┘
                 │
        ┌────────┴──────────┐
        │                   │
        ↓                   ↓
  ┌──────────────┐   ┌──────────────────┐
  │ ExamEvalu... │   │ Evaluation       │
  │ ation.cs     │   │ DisplayUI.cs     │
  │              │   │                  │
  │ Stores:      │   │ Shows sliders:   │
  │ • Scores     │   │ • 1-5 values     │
  │ • Feedback   │   │ • Overall score  │
  │ • Transcript │   │ • Feedback text  │
  └──────────────┘   └──────────────────┘
        │                   │
        ↓                   ↓
  ┌──────────────┐   ┌──────────────────┐
  │ ExamHistory  │   │ AudioSource      │
  │ (Track perf) │   │ (Plays TTS)      │
  └──────────────┘   └──────────────────┘
```

---

## 3. Data Flow: From Conversation to Evaluation

```
Timeline:

[0 min] User starts exam
         └─→ Realtime AI begins listening

[0-3 min] Conversation happens
         └─→ Student: "The patient has..."
         └─→ AI: "Tell me more about..."
         └─→ Realtime AI observes pronunciation silently
         └─→ MedicalExamManager collects transcript

[3 min] Timer runs out OR user says "end"
        └─→ RequestEvaluation() called
        └─→ Show loading spinner: "Doctor reviewing..."

[3+ 5-10 sec] Fine-tuned evaluation
        └─→ Send transcript to fine-tuned GPT-4
        └─→ GPT-4 analyzes medical correctness
        └─→ GPT-4 considers realtime observations
        └─→ Returns JSON with scores

[3+ 10-15 sec] Results display
        └─→ Parse JSON
        └─→ Play TTS voice feedback
        └─→ Update UI sliders
        └─→ Show overall score
        └─→ Display feedback text

[3+ 20 sec] Student reviews results
        └─→ Can save, restart, or exit
```

---

## 4. Dual-Layer Evaluation Concept

```
┌──────────────────────────────────────────────────────────────┐
│                    REALTIME AI                               │
│        (During Conversation - Hidden Observer)               │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  "Hello, I'm examining a patient with chest pain..."        │
│                                                              │
│  ✓ Pronunciation: Clear, proper emphasis                    │
│  ✓ Hesitations: Minimal (one slight pause)                  │
│  ✗ Filler words: "uh" used once                             │
│  ✓ Sentence clarity: Well-structured                        │
│                                                              │
│  [Observation stored but NOT revealed to student]           │
│                                                              │
└──────────────────────────────────────────────────────────────┘
                             ↓
         [At evaluation trigger, both layers combine]
                             ↓
┌──────────────────────────────────────────────────────────────┐
│               FINE-TUNED GPT-4                               │
│     (Post-Conversation - Medical Expert Evaluator)           │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  Full Conversation Analyzed:                                 │
│  ✓ Terminology: Correctly used "thoracic aorta"             │
│  ✓ Reasoning: Mentioned 3 appropriate differentials         │
│  ✗ Completeness: Missed asking about family history         │
│  ✓ Red flags: Correctly identified need for EKG             │
│                                                              │
│  Realtime Observations Considered:                           │
│  • Pronunciation data → influences Aussprache score         │
│  • Hesitations → noted but not penalized if content OK      │
│  • Clarity → confirms communication effectiveness           │
│                                                              │
│  Final Scores:                                               │
│  • Terminologie: 4/5 (good, proper medical language)        │
│  • Verständlichkeit: 3.5/5 (clear but one missed area)      │
│  • Aussprache: 4.2/5 (clear, minimal hesitation)            │
│  • Overall: 78/100                                           │
│                                                              │
│  Feedback:                                                   │
│  "Strong medical knowledge with good terminology.           │
│  Consider including family history in future exams. Your    │
│  pronunciation and clarity were excellent throughout."      │
│                                                              │
└──────────────────────────────────────────────────────────────┘
                             ↓
                     [Display to Student]
                             ↓
              Sliders + Voice Feedback + Score
```

---

## 5. Error Handling Flow

```
┌─────────────────────────────────────────┐
│  EvaluateWithFineTunedGPT4() called      │
└────────────────┬────────────────────────┘
                 │
                 ↓
    ┌────────────────────────┐
    │ Check preconditions:   │
    │ • Service assigned?    │
    │ • Conversation exists? │
    │ • Config ready?        │
    └────────────┬───────────┘
                 │
        ┌────────┴────────┐
        │                 │
        ↓                 ↓
    [PASS]          [FAIL]
        │                 │
        ↓                 ↓
  Send to API      Log error
        │           Return
        │
        ↓
  ┌──────────────────────────┐
  │ API Response              │
  │ • Success? Valid JSON?    │
  │ • Required fields?        │
  │ • Valid ranges?           │
  └────────────┬─────────────┘
               │
        ┌──────┴──────┐
        │             │
        ↓             ↓
    [SUCCESS]    [ERROR]
        │             │
        ↓             ↓
   Parse JSON   Log error
        │
        ↓
   Display       Fallback to:
   results       • Regex parsing
                 • Realtime feedback
                 • Default values
```

---

## 6. Component Dependency Graph

```
                    ┌─────────────┐
                    │  OpenAIConfig│
                    │   (Shared)  │
                    └──────┬──────┘
                           │
            ┌──────────────┼──────────────┐
            │              │              │
            ↓              ↓              ↓
    ┌──────────────┐ ┌──────────────┐ ┌──────────────────────┐
    │ Realtime     │ │ MedicalExam  │ │ FineTunedGPT4        │
    │ Conversation │ │ Manager      │ │ EvaluationService    │
    │ Manager      │ │              │ │                      │
    └──────┬───────┘ └──────┬───────┘ └──────────┬───────────┘
           │                │                     │
           │                │                     │
           └────────────────┼─────────────────────┘
                            │
                    ┌───────┴────────┐
                    │                │
                    ↓                ↓
            ┌──────────────┐  ┌──────────────────┐
            │ Exam         │  │ Evaluation       │
            │ Evaluation   │  │ DisplayUI        │
            │              │  │                  │
            └──────────────┘  └─────────┬────────┘
                    │                   │
                    └───────────┬───────┘
                                │
                        ┌───────↓────────┐
                        │ ExamHistory    │
                        │ (Optional)     │
                        └────────────────┘
```

---

## 7. State Machine: Evaluation States

```
                    ┌─────────┐
                    │  START  │
                    └────┬────┘
                         │
                         ↓
              ┌──────────────────────┐
              │  CONVERSATION_ACTIVE │
              │  (Realtime AI talking)│
              └──────────┬───────────┘
                         │
                    [Timer ends or
                     user says "end"]
                         │
                         ↓
              ┌──────────────────────┐
              │ EVALUATION_REQUESTED │
              │ (Show loading...)    │
              └──────────┬───────────┘
                         │
                   [Call fine-tuned API]
                         │
              ┌──────────┴──────────┐
              │                     │
              ↓                     ↓
      ┌───────────────┐     ┌──────────────┐
      │ EVAL_SUCCESS  │     │ EVAL_ERROR   │
      │ (JSON parsed) │     │ (API failed) │
      └───────┬───────┘     └──────┬───────┘
              │                    │
              │            [Fallback to
              │             regex parsing]
              │                    │
              └────────┬───────────┘
                       │
                       ↓
              ┌──────────────────────┐
              │ RESULTS_DISPLAYING   │
              │ (Sliders updating)   │
              └──────────┬───────────┘
                         │
                  [TTS playing...]
                         │
                         ↓
              ┌──────────────────────┐
              │  RESULTS_COMPLETE    │
              │ (Student can review) │
              └──────────┬───────────┘
                         │
                    [Restart exam]
                         │
                         ↓
                    ┌─────────┐
                    │  START  │ (cycle repeats)
                    └─────────┘
```

---

Perfect reference! Use these diagrams to understand and explain the system. 📊
