# Implementation Quick Reference

## What Was Created

### 1. `FineTunedGPT4EvaluationService.cs`
**Location:** `Assets/Scripts/`

**Purpose:** Calls fine-tuned GPT-4 API and parses JSON evaluation response

**Key Methods:**
- `EvaluateConversation()` — Main entry point, sends transcript to fine-tuned model
- `PlayFeedbackAsVoice()` — Converts evaluation feedback to speech (TTS integration point)

**Configuration:**
- `useFineTunedModel` — Toggle between fine-tuned vs standard GPT-4
- `evaluationSystemPrompt` — System instruction for the evaluator
- `openAIConfig` — Reference to OpenAI API credentials

**Output Structure:**
```csharp
public class EvaluationResponse
{
    public float terminologie;          // 1-5 scale
    public float verstaendlichkeit;     // 1-5 scale
    public float aussprache;            // 1-5 scale
    public int overallScore;            // 0-100 scale
    public string generalFeedback;      // Main feedback text (TTS voice this)
    public string terminologieFeedback; // Optional: specific feedback
    public string verstaendlichkeitFeedback; // Optional
    public string ausspracheeFeedback;  // Optional
}
```

---

### 2. `OpenAIConfig.cs` (Updated)
**Location:** `com.convai.openai/Runtime/Scripts/`

**Added Field:**
```csharp
[Tooltip("Fine-tuned GPT-4 model ID. Example: ft:gpt-4-turbo-2024-04-09:default")]
public string fineTunedModelId = "ft:gpt-4-turbo-2024-04-09:default";
```

**Usage:**
Update this field with your trained model ID in the Inspector.

---

### 3. `RealtimeConversationManager.cs` (Updated)
**Location:** `com.convai.openai/Runtime/Scripts/`

**Updated System Prompt:**
Now focuses on speech observation (pronunciation, hesitations, clarity) rather than evaluation.

**No code changes needed** — Just the system prompt text was updated.

---

### 4. `MedicalExamManager.cs` (Updated)
**Location:** `Assets/Scripts/`

**Added Field:**
```csharp
[SerializeField] private FineTunedGPT4EvaluationService fineTunedEvalService;
```

**Added Methods:**
- `EvaluateWithFineTunedGPT4()` — Call this to trigger evaluation
- `OnFineTunedEvaluationReceived()` — Callback when evaluation returns
- `OnFineTunedEvaluationError()` — Fallback error handling

**Usage:**
```csharp
// When you want to trigger evaluation:
EvaluateWithFineTunedGPT4();
```

---

## Setup Instructions

### Step 1: Assign References in Inspector

1. **MedicalExamManager** GameObject:
   - Find the `Fine Tuned Eval Service` field
   - Drag your `FineTunedGPT4EvaluationService` component into it
   - (Or create a new GameObject with FineTunedGPT4EvaluationService if doesn't exist)

2. **FineTunedGPT4EvaluationService** Component:
   - **OpenAI Config:** Drag `OpenAIConfig` asset
   - **TTS Audio Source:** Drag the Audio Source that will play voice feedback

### Step 2: Update Fine-Tuned Model ID

1. Select `OpenAIConfig` asset (in Assets folder)
2. Find **Fine-Tuned Model** section
3. Paste your model ID: `ft:gpt-4-turbo-2024-04-09:your-id-here`

### Step 3: Train Your Model (Optional for MVP)

For now, the system uses `gpt-4-turbo` as fallback if fine-tuned model fails.
Once you have training data, follow the guide in `FINE_TUNED_SETUP_GUIDE.md`.

---

## Data Flow Diagram

```
┌─────────────────────────────────────────────────────────────┐
│               CONVERSATION PHASE (Realtime AI)              │
│                                                             │
│  User Speech → Realtime Conversation Manager → AI Response │
│                (observes pronunciation silently)           │
│                                                             │
│  Full Transcript & Realtime Observations Collected         │
└─────────────────────────────────────────────────────────────┘
                            ↓
                  [EVALUATION TRIGGERED]
                            ↓
┌─────────────────────────────────────────────────────────────┐
│          EVALUATION PHASE (Fine-Tuned GPT-4)                │
│                                                             │
│  Transcript + Observations → Fine-Tuned GPT-4 API          │
│                                  ↓                          │
│                          JSON Response Parsed               │
│                          (Scores + Feedback)                │
│                                  ↓                          │
│                         TTS Voice Feedback                  │
│                                  ↓                          │
│                   UI Sliders Updated + Results             │
└─────────────────────────────────────────────────────────────┘
```

---

## JSON Example

**Request Sent to Fine-Tuned Model:**
```json
{
  "model": "ft:gpt-4-turbo-2024-04-09:default:abc123",
  "messages": [
    {
      "role": "system",
      "content": "You are a medical university examiner..."
    },
    {
      "role": "user",
      "content": "EXAM SCENARIO: First Exam\n\nCONVERSATION TRANSCRIPT:\nDoctor: How would you approach this patient?\nStudent: The patient presents with chest pain...\n\nREALTIME OBSERVATIONS:\nPronunciation: clear\nHesitations: minimal\nFiller words: few\n\nPlease evaluate as JSON."
    }
  ]
}
```

**Response Received:**
```json
{
  "terminologie": 3.5,
  "verstaendlichkeit": 3.8,
  "aussprache": 4.0,
  "overallScore": 72,
  "generalFeedback": "You demonstrated good communication skills with clear pronunciation. Your medical terminology was appropriate but could be more precise in certain areas. Consider practicing differential diagnosis frameworks.",
  "terminologieFeedback": "Good use of clinical terms but missed some specific diagnoses",
  "verstaendlichkeitFeedback": "Your explanations were clear and well-structured",
  "ausspracheeFeedback": "Excellent pronunciation overall"
}
```

---

## Calling the Evaluation

### Current Workflow (Keep Realtime Feedback)
```csharp
// In MedicalExamManager.RequestEvaluation():
public void RequestEvaluation()
{
    _evaluationRequested = true;
    _examActive = false;
    
    // Realtime AI will generate feedback...
    // (keep existing flow)
    
    // Then after realtime finishes, call fine-tuned:
    Invoke(nameof(EvaluateWithFineTunedGPT4), 3f);
}
```

### New Workflow (Skip Realtime Feedback, Go Direct)
```csharp
public void RequestEvaluation()
{
    _evaluationRequested = true;
    _examActive = false;
    
    // Skip realtime feedback, go straight to fine-tuned:
    EvaluateWithFineTunedGPT4();
}
```

---

## Files Modified/Created

| File | Type | Change |
|------|------|--------|
| `FineTunedGPT4EvaluationService.cs` | 🆕 Created | New evaluation service |
| `OpenAIConfig.cs` | ✏️ Updated | Added `fineTunedModelId` field |
| `RealtimeConversationManager.cs` | ✏️ Updated | Updated system prompt |
| `MedicalExamManager.cs` | ✏️ Updated | Added fine-tuned evaluation integration |
| `FINE_TUNED_SETUP_GUIDE.md` | 🆕 Created | Detailed setup instructions |
| `IMPLEMENTATION_QUICK_REFERENCE.md` | 🆕 Created | This file |

---

## Troubleshooting

### `FineTunedGPT4EvaluationService not assigned!` error
**Solution:** Assign the service in MedicalExamManager Inspector → Fine Tuned Eval Service

### `API Error: Invalid model` 
**Solution:** Update `fineTunedModelId` in OpenAIConfig with correct model ID

### `JSON parse error`
**Solution:** Fine-tuned model response must be valid JSON only (no text before/after)

### `TTS not playing`
**Solution:** 
1. Assign AudioSource to FineTunedGPT4EvaluationService
2. Implement `PlayFeedbackAsVoice()` with ElevenLabs/OpenAI TTS

### Evaluation takes 10-20 seconds
**Normal!** Fine-tuned models are slower than real-time API. Show a loading indicator.

---

## Next Steps

1. ✅ Implementation complete
2. 📋 Train fine-tuned model with your medical examples
3. 🔑 Update `fineTunedModelId` in OpenAIConfig
4. 🎤 Implement TTS integration (ElevenLabs or OpenAI)
5. 🧪 Test evaluation pipeline end-to-end
6. 📊 Iterate on training data to improve accuracy

---

## Key Points

- **Realtime AI** = Speech observer + conversation partner
- **Fine-Tuned GPT-4** = Medical expert evaluator
- **Realtime observations** = Secondary input to evaluation (influences Aussprache)
- **JSON output** = Structured scores + feedback text
- **TTS** = Voice the general feedback to user
- **UI** = Display 3 sliders + overall score

Happy training! 🚀
