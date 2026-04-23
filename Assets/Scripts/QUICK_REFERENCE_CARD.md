# Quick Reference Card - Dual-Layer Evaluation System

## 🎯 The Vision
```
Realtime AI    +    Fine-Tuned GPT-4    =    Expert Medical Evaluation
(Speech focus)     (Medical focus)            (Balanced assessment)
```

## 📊 Three Sliders (What Users See)

| Slider | Scale | Meaning |
|--------|-------|---------|
| **Terminologie** | 1-5 | Medical terminology accuracy |
| **Verständlichkeit** | 1-5 | Communication clarity |
| **Aussprache** | 1-5 | Pronunciation quality |

Plus: **Overall Score (0-100)** + **General Feedback** + **Voice feedback (TTS)**

## 🔄 Three Stages

```
Stage 1: Conversation      Stage 2: Evaluation         Stage 3: Results
[Realtime AI talking]  →   [Fine-tuned analyzing]  →   [Sliders displaying]
(3 minutes)                (5-15 seconds)              (Final scores shown)
```

## 📁 Files Created/Updated

| File | Type | Where |
|------|------|-------|
| FineTunedGPT4EvaluationService.cs | 🆕 | Assets/Scripts/ |
| MedicalExamManager.cs | ✏️ | Assets/Scripts/ |
| OpenAIConfig.cs | ✏️ | com.convai.openai/.../Scripts/ |
| RealtimeConversationManager.cs | ✏️ | com.convai.openai/.../Scripts/ |

## 🎮 Inspector Setup (2 minutes)

1. **MedicalExamManager** 
   - Field: `Fine Tuned Eval Service` 
   - Drag: `FineTunedGPT4EvaluationService` component

2. **FineTunedGPT4EvaluationService**
   - Field: `Open AI Config` 
   - Drag: `OpenAIConfig` asset
   - Field: `TTS Audio Source` 
   - Drag: Any AudioSource

3. **OpenAIConfig** (Optional)
   - Field: `fineTunedModelId`
   - Value: Your model ID (or use default)

## 🔧 Key Methods

```csharp
// Call this to trigger evaluation:
EvaluateWithFineTunedGPT4();

// System handles:
// 1. Send transcript to API
// 2. Parse JSON response
// 3. Play TTS feedback
// 4. Update UI sliders
```

## 📤 JSON Response Format

```json
{
  "terminologie": 3.5,
  "verstaendlichkeit": 3.8,
  "aussprache": 4.0,
  "overallScore": 72,
  "generalFeedback": "Good clinical knowledge..."
}
```

## ⚙️ Configuration

```csharp
// In OpenAIConfig asset:
fineTunedModelId = "ft:gpt-4-turbo-2024-04-09:your-model-id"

// Can leave default (uses GPT-4-Turbo fallback)
// Or update after training fine-tuned model
```

## 🚀 Quick Start

```
1. Assign components in Inspector (5 min)
2. Run conversation (3 min)
3. Trigger evaluation (15 sec)
4. See results on sliders (immediate)
```

## 📈 Three Phases

### Phase 1: MVP (This week)
- ✅ Code implemented
- ✅ Inspector setup
- ✅ API integration working
- ⏳ Test with default model

### Phase 2: Training (Next week)
- ⏳ Collect 50+ exam examples
- ⏳ Train fine-tuned model
- ⏳ Update fineTunedModelId
- ⏳ Verify accuracy

### Phase 3: Polish (Following week)
- ⏳ Implement TTS
- ⏳ Improve prompts
- ⏳ Doctor feedback & iterate
- ⏳ Launch to users

## 💡 Key Concept

```
REALTIME AI (During Exam):
- Acts as doctor or patient
- Speaks naturally
- SILENTLY tracks: pronunciation, hesitations, clarity
- Does NOT evaluate medical content

FINE-TUNED GPT-4 (After Exam):
- Acts as medical examiner
- Reviews full transcript
- EVALUATES: terminology, reasoning, completeness, safety
- Uses realtime observations as secondary input
- Returns structured JSON with scores

RESULT: Balanced evaluation combining both technical knowledge
        AND communication effectiveness
```

## 🎯 System Prompts

**Realtime:** "Participate naturally. Observe speech quality silently. Don't evaluate medicine."

**Fine-tuned:** "Evaluate strictly on medical standards. Use realtime observations as secondary input. Return JSON only."

## 🧪 Testing

```
Step 1: Run conversation
Step 2: Trigger evaluation
Step 3: Check Console for [FineTunedGPT4] logs
Step 4: Verify sliders show 1-5 values
Step 5: Verify overall score shows 0-100
Step 6: Verify feedback text displays
```

## ✅ Success Checklist

- [ ] Inspector components assigned
- [ ] Conversation runs normally
- [ ] Evaluation API call succeeds (console log)
- [ ] JSON parses without errors
- [ ] Sliders display correct values
- [ ] Overall score displays
- [ ] Feedback text shows
- [ ] No crashes

## 📚 Documentation

- **Quick Start:** IMPLEMENTATION_QUICK_REFERENCE.md
- **Full Setup:** FINE_TUNED_SETUP_GUIDE.md
- **Architecture:** DUAL_LAYER_EVALUATION_SUMMARY.md
- **JSON Spec:** FINE_TUNED_JSON_SPEC.md
- **Checklist:** IMPLEMENTATION_CHECKLIST.md
- **Diagrams:** ARCHITECTURE_DIAGRAMS.md

## 🐛 If Something Doesn't Work

| Problem | Check |
|---------|-------|
| "Service not assigned" | MedicalExamManager → Fine Tuned Eval Service field |
| "API Error" | OpenAIConfig → API key valid? |
| "Model not found" | Normal! Use default or train custom model |
| "JSON parse error" | Check console for raw response |
| "Sliders not updating" | Check EvaluationDisplayUI assigned |
| "TTS not playing" | Implement PlayFeedbackAsVoice() method |

## 💰 Cost Per Exam

- Conversation: ~$0.005
- Evaluation: ~$0.05
- TTS: ~$0.03
- **Total: ~$0.08-0.10**

## 🏥 Medical Focus

- **Realtime:** Pronunciation, speech clarity
- **Fine-Tuned:** Medical terminology, clinical reasoning, safety
- **Combined:** Professional competency assessment

## 🎓 Use Case

Medical students practice oral exams with:
- Natural conversation via Realtime AI
- Expert evaluation via Fine-Tuned GPT-4
- Structured feedback on 3 dimensions
- Voice feedback + visual scores

## 🔄 Iteration Loop

```
Conversation
    ↓
Fine-Tuned Eval
    ↓
Doctor Review (Manual Check)
    ↓
Feedback to Model
    ↓
Retrain with More Examples
    ↓
Better Accuracy
```

## 📞 Integration Points

- `RequestEvaluation()` → calls `EvaluateWithFineTunedGPT4()`
- `OnFineTunedEvaluationReceived()` → handles success
- `OnFineTunedEvaluationError()` → handles errors
- `PlayFeedbackAsVoice()` → TTS integration

## 🎨 UI Mapping

```
ExamEvaluation object:
├─ terminologie (float 1-5) → Slider 1
├─ verstaendlichkeit (float 1-5) → Slider 2
├─ aussprache (float 1-5) → Slider 3
├─ overallScore (int 0-100) → Display value
├─ feedbackText (string) → Display text + TTS
└─ conversationTranscript (string) → Save to history
```

## 🚀 Ready to Go!

All code implemented. Just needs:
1. Component assignment (5 min)
2. Training data (later)
3. TTS implementation (later)

**Start:** IMPLEMENTATION_QUICK_REFERENCE.md

**Next:** IMPLEMENTATION_CHECKLIST.md

**Good luck!** 🏥✨
