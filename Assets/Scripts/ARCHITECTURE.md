# System Architecture Diagram

## Component Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         MEDICAL EXAM VR APP                          │
├─────────────────────────────────────────────────────────────────────┤
│                                                                       │
│  ┌─────────────────┐                                                │
│  │  GameManager    │  (Application Controller)                      │
│  │                 │  - App lifecycle                                │
│  │  - Restart      │  - Keyboard shortcuts                           │
│  │  - Quit         │  - Integration point                            │
│  └────────┬────────┘                                                │
│           │                                                           │
│           ▼                                                           │
│  ┌─────────────────────────────────────────────────────────┐        │
│  │         MedicalExamManager                              │        │
│  │                                                          │        │
│  │  Main Controller:                                       │        │
│  │  • Role selection logic                                 │        │
│  │  • System prompt generation                             │        │
│  │  • Timer management                                     │        │
│  │  • Conversation logging                                 │        │
│  │  • Evaluation triggering                                │        │
│  │  • UI coordination                                      │        │
│  └──┬────────┬──────────┬──────────┬────────────┬─────────┘        │
│     │        │          │          │            │                   │
│     ▼        ▼          ▼          ▼            ▼                   │
│  ┌─────┐ ┌──────┐  ┌────────┐ ┌────────┐  ┌─────────┐             │
│  │ UI  │ │Avatar│  │Scenario│ │ConvoMgr│  │Eval     │             │
│  │Panel│ │ Show │  │  Data  │ │  GPT   │  │System   │             │
│  └─────┘ └──────┘  └────────┘ └────────┘  └─────────┘             │
│                                                                       │
└─────────────────────────────────────────────────────────────────────┘
```

## Data Flow Diagram

```
START: App Launch
    │
    ▼
┌────────────────────┐
│ Role Selection UI  │  [Doctor Button] [Patient Button]
└────────┬───────────┘
         │ User clicks
         ▼
┌────────────────────────────────────────┐
│  MedicalExamManager.OnRoleSelected()   │
│                                        │
│  1. Hide selection UI                  │
│  2. Show appropriate avatar            │
│  3. Generate system prompt             │
│  4. Start timer                        │
│  5. Initialize conversation            │
└────────┬───────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│  System Prompt Generation               │
│                                         │
│  IF Doctor-to-Doctor:                   │
│    "You are examiner, ask questions"    │
│  ELSE Patient:                          │
│    "You are patient, present symptoms"  │
│                                         │
│  Include: Topic, Duration, Language     │
└────────┬────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────┐
│  RealtimeConversationManager            │
│                                         │
│  • Injects system prompt                │
│  • Connects to OpenAI API               │
│  • Starts conversation                  │
└────────┬────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  Active Conversation                     │
│                                          │
│  AI speaks  ──► onAgentTranscript        │
│               └──► Log conversation      │
│                   └──► Check evaluation  │
│                                          │
│  User speaks ──► onUserTranscript        │
│                └──► Log conversation     │
│                   └──► Check end trigger │
│                                          │
│  Timer updates ──► Color coding          │
│                  └──► Check time up      │
└────────┬─────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  Evaluation Trigger                      │
│                                          │
│  Triggered by:                           │
│  • Timer reaches 0                       │
│  • User says "this is all"               │
│  • Manual button press                   │
└────────┬─────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  AI Evaluation Response                  │
│                                          │
│  Format:                                 │
│  Terminologie: 4/5                       │
│  Verständlichkeit: 3/5                   │
│  Aussprache: 4/5                         │
│  Overall Score: 78/100                   │
│  Feedback: [detailed text]               │
└────────┬─────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  ExamEvaluation.ParseFromAIResponse()    │
│                                          │
│  • Regex extraction of scores            │
│  • Store conversation transcript         │
│  • Create evaluation object              │
└────────┬─────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  ExamHistory.AddEvaluation()             │
│                                          │
│  • Save to history                       │
│  • Update statistics                     │
└────────┬─────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────┐
│  Display Evaluation UI                   │
│                                          │
│  • Show evaluation panel                 │
│  • Color-coded scores                    │
│  • Feedback text                         │
│  • Restart button                        │
└────────┬─────────────────────────────────┘
         │
         ▼
END: User can restart or exit
```

## Scene Hierarchy

```
Scene: Medical Exam VR
│
├── GameManager (GameObject)
│   └── GameManager.cs
│
├── MedicalExamManager (GameObject)
│   └── MedicalExamManager.cs
│       • References UI
│       • References Avatars
│       • References ConversationManager
│       • References Scenario asset
│
├── RealtimeConversationManager (GameObject)
│   ├── OpenAI.RealtimeConversationManager
│   ├── MicrophoneStreamer
│   └── PcmAudioPlayer
│
├── Avatars
│   ├── DoctorAvatar (3D Model)
│   │   └── NPCDoctor.cs
│   │       • Animator
│   │       • Head tracking
│   └── PatientAvatar (3D Model)
│       └── NPCDoctor.cs (reusable)
│
├── VR Rig
│   ├── XR Origin
│   ├── Camera
│   └── Controllers
│
└── UI Canvas
    │
    ├── RoleSelectionPanel (Panel)
    │   ├── Background (Image)
    │   ├── Title (TextMeshPro)
    │   │   Text: "Select Your Role"
    │   ├── ScenarioTitle (TextMeshPro)
    │   │   Text: "[Scenario Name]"
    │   ├── DoctorButton (Button)
    │   │   └── Text: "Doctor Exam"
    │   └── PatientButton (Button)
    │       └── Text: "Patient Consultation"
    │
    ├── ExamHUD (Panel)
    │   └── TimerText (TextMeshPro)
    │       Text: "Time: 05:00"
    │       Color: White → Yellow → Red
    │
    └── EvaluationPanel (Panel)
        ├── Background (Image)
        ├── Title (TextMeshPro)
        │   Text: "Evaluation Results"
        ├── EvaluationText (TextMeshPro)
        │   • Scrollable
        │   • Shows formatted evaluation
        ├── (Optional) ScoreDisplays
        │   ├── TerminologieScore
        │   ├── VerstaendlichkeitScore
        │   └── AusspracheScore
        └── RestartButton (Button)
            Text: "Restart Exam"
            OnClick: MedicalExamManager.RestartExam()
```

## Project Structure

```
Assets/
│
├── Scripts/
│   ├── MedicalExamScenario.cs         [ScriptableObject]
│   ├── MedicalExamManager.cs          [Main Controller]
│   ├── ExamEvaluation.cs              [Data & Parsing]
│   ├── EvaluationDisplayUI.cs         [UI Helper]
│   │
│   ├── Editor/
│   │   └── ScenarioCreator.cs         [Editor Tool]
│   │
│   ├── MEDICAL_EXAM_README.md         [Full Documentation]
│   ├── QUICK_START.md                 [5-min Setup Guide]
│   ├── IMPLEMENTATION_SUMMARY.md      [Technical Summary]
│   └── ARCHITECTURE.md                [This file]
│
├── Scenarios/                          [Scenario Assets]
│   ├── Cardiology_Basics.asset
│   ├── Neurology_Exam.asset
│   └── ...
│
├── ExamHistory.asset                   [History Tracking]
│
└── GameManager.cs                      [App Controller]
```

## Class Relationships

```
┌─────────────────────────────────────────────────────────────┐
│                    Class Diagram                             │
└─────────────────────────────────────────────────────────────┘

┌──────────────────────┐
│  GameManager         │
│  (MonoBehaviour)     │
│                      │
│  - medicalExamMgr    │───────┐
│                      │       │
│  + RestartApp()      │       │
│  + QuitApp()         │       │
└──────────────────────┘       │
                               │
                               ▼
                    ┌──────────────────────────────┐
                    │  MedicalExamManager          │
                    │  (MonoBehaviour)             │
                    │                              │
                    │  - currentScenario ────────┐ │
                    │  - conversationManager     │ │
                    │  - selectedRole            │ │
                    │  - examHistory ──────────┐ │ │
                    │  - _currentEvaluation    │ │ │
                    │                          │ │ │
                    │  + OnRoleSelected()      │ │ │
                    │  + StartExam()           │ │ │
                    │  + RequestEvaluation()   │ │ │
                    │  + RestartExam()         │ │ │
                    └──┬───────────────┬────┬──┘ │ │
                       │               │    │    │ │
        ┌──────────────┘               │    │    │ │
        │                              │    │    │ │
        ▼                              ▼    │    │ │
┌──────────────────┐          ┌──────────────────┐│ │
│ MedicalExam      │          │ RealtimeConvo    ││ │
│ Scenario         │          │ Manager          ││ │
│ (ScriptableObj)  │          │ (MonoBehaviour)  ││ │
│                  │          │                  ││ │
│ + scenarioName   │          │ - systemPrompt   ││ │
│ + medicalTopic   │          │                  ││ │
│ + duration       │          │ + StartAgent()   ││ │
│ + language       │          │                  ││ │
│ + difficulty     │          │ Events:          ││ │
└──────────────────┘          │ • onAgentTrans   ││ │
                              │ • onUserTrans    ││ │
                              └──────────────────┘│ │
                                                  │ │
                 ┌────────────────────────────────┘ │
                 │                                   │
                 ▼                                   ▼
        ┌──────────────────┐              ┌──────────────────┐
        │ ExamEvaluation   │              │ ExamHistory      │
        │ (Class)          │              │ (ScriptableObj)  │
        │                  │              │                  │
        │ + terminologie   │              │ + evaluations[]  │
        │ + verstaend...   │◄─────────────│                  │
        │ + aussprache     │              │ + AddEval()      │
        │ + overallScore   │              │ + GetAverage()   │
        │ + feedbackText   │              │ + GetLatest()    │
        │                  │              └──────────────────┘
        │ + ParseFromAI()  │
        │ + GetFormatted() │
        └──────────────────┘
                 │
                 │ Used by
                 ▼
        ┌──────────────────┐
        │ EvaluationDisplayUI
        │ (MonoBehaviour)  │
        │                  │
        │ - scoreTexts[]   │
        │ - scoreBars[]    │
        │                  │
        │ + DisplayEval()  │
        │ + ClearDisplay() │
        └──────────────────┘
```

## Event Flow

```
┌─────────────────────────────────────────────────────────┐
│                    Event System                          │
└─────────────────────────────────────────────────────────┘

User Action: Click Button
    │
    ▼
┌─────────────────────────┐
│ UI Button.onClick       │
│ ↓                       │
│ MedicalExamManager      │
│  .OnRoleSelected()      │
└──────────┬──────────────┘
           │
           ├──► Hide UI
           ├──► Show Avatar
           ├──► Generate Prompt
           └──► Start Timer
                    │
                    ▼
┌────────────────────────────────┐
│ RealtimeConversationManager    │
│  .StartAgent()                 │
└──────────┬─────────────────────┘
           │
           ├──► Connect WebSocket
           ├──► Send Initial Message
           └──► Start Microphone
                    │
                    ▼
           ┌─────────────────┐
           │ Conversation    │
           └────────┬────────┘
                    │
       ┌────────────┴────────────┐
       │                         │
       ▼                         ▼
┌─────────────────┐   ┌──────────────────┐
│ AI Speaks       │   │ User Speaks      │
│                 │   │                  │
│ Event:          │   │ Event:           │
│ onAgentTrans    │   │ onUserTrans      │
│      ↓          │   │      ↓           │
│ OnAgentSpoke()  │   │ OnUserSpoke()    │
│      ↓          │   │      ↓           │
│ Log to string   │   │ Log to string    │
│ Check eval      │   │ Check end phrase │
└─────────────────┘   └──────────────────┘
           │                    │
           └────────┬───────────┘
                    │
                    ▼
           ┌─────────────────┐
           │ Evaluation      │
           │ Triggered       │
           └────────┬────────┘
                    │
                    ▼
┌────────────────────────────────┐
│ AI Evaluation Response         │
│                                │
│ Event: onAgentTranscript       │
│     ↓                          │
│ OnAgentSpoke()                 │
│     ↓                          │
│ Detect evaluation format       │
│     ↓                          │
│ ProcessEvaluation()            │
└────────┬───────────────────────┘
         │
         ▼
┌────────────────────────────────┐
│ ExamEvaluation.ParseFromAI()   │
│     ↓                          │
│ ExamHistory.AddEvaluation()    │
│     ↓                          │
│ Show Evaluation UI             │
└────────────────────────────────┘
```

## Timing Diagram

```
Time (seconds)

0s    ─┐  App Start
      │  └─► Role Selection UI Shown
      │
5s    ├─► User clicks "Doctor" button
      │  └─► OnRoleSelected()
      │      • Generate prompt
      │      • Start conversation
      │
10s   ├─► AI: "Good morning, let's discuss cardiology..."
      │
15s   ├─► User: "I'd like to talk about hypertension..."
      │
20s   ├─► AI: "Excellent choice. What are the main..."
      │
...   │   [Conversation continues]
      │
290s  ├─► Timer: 10 seconds remaining (turns red)
      │
295s  ├─► User: "That's all for this conversation"
      │  └─► OnUserSpoke() detects end phrase
      │      • RequestEvaluation()
      │
297s  ├─► AI: "Good enough for now. Let's evaluate..."
      │      "Terminologie: 4/5"
      │      "Verständlichkeit: 3/5"
      │      "Aussprache: 4/5"
      │      "Overall Score: 78/100"
      │      "Feedback: Your medical terminology..."
      │
298s  ├─► OnAgentSpoke() detects evaluation
      │  └─► ProcessEvaluation()
      │      • Parse scores
      │      • Save to history
      │      • Show UI
      │
300s  ─┘  Evaluation displayed, waiting for restart
```

## Memory/Data Lifecycle

```
Session Start
    │
    ▼
┌─────────────────────────────┐
│ Persistent Data (Assets)    │
│                             │
│ • MedicalExamScenario       │
│ • ExamHistory               │
│ • OpenAIConfig              │
└──────────┬──────────────────┘
           │ Loaded at runtime
           ▼
┌─────────────────────────────┐
│ Runtime Data (Scene)        │
│                             │
│ • selectedRole              │
│ • _examActive               │
│ • _examStartTime            │
│ • _fullConversationLog      │
└──────────┬──────────────────┘
           │ During exam
           ▼
┌─────────────────────────────┐
│ Temporary Data              │
│                             │
│ • Current conversation      │
│ • Timer state               │
│ • UI state                  │
└──────────┬──────────────────┘
           │ On evaluation
           ▼
┌─────────────────────────────┐
│ Evaluation Created          │
│                             │
│ • ExamEvaluation object     │
│ • Scores parsed             │
│ • Transcript stored         │
└──────────┬──────────────────┘
           │ Saved to
           ▼
┌─────────────────────────────┐
│ Persistent Data Updated     │
│                             │
│ • ExamHistory.evaluations   │
│   += new evaluation         │
└─────────────────────────────┘
           │
           ▼
Session End (Data persists)
```

---

## Key Design Decisions

### 1. ScriptableObjects for Data
- **Why**: Reusable, inspector-editable, persistent
- **Used for**: Scenarios, History, Config

### 2. UnityEvents for Communication
- **Why**: Decoupled, inspector-visible, flexible
- **Used for**: AI↔Manager communication

### 3. Reflection for Prompt Injection
- **Why**: Avoid modifying external package code
- **Tradeoff**: Less type-safe, but more maintainable

### 4. Regex for Evaluation Parsing
- **Why**: Simple, fast, no dependencies
- **Tradeoff**: Fragile if AI format changes

### 5. Single Manager Pattern
- **Why**: Centralized control, easy to debug
- **Tradeoff**: Could be split for larger systems

---

This architecture provides:
- ✅ **Modularity**: Easy to add features
- ✅ **Maintainability**: Clear responsibilities
- ✅ **Scalability**: Can handle more scenarios
- ✅ **Testability**: Components can be tested independently
- ✅ **Usability**: Simple setup, clear workflow
