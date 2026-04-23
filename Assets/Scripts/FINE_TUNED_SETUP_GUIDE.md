# Fine-Tuned GPT-4 Medical Evaluation Setup Guide

## Overview

The dual-layer evaluation system consists of:
1. **Realtime AI** — Converses naturally, observes pronunciation/speech quality (hidden)
2. **Fine-Tuned GPT-4** — Reviews full transcript, provides medical evaluation with structured JSON output

## Current Implementation Status

✅ **Implemented:**
- `FineTunedGPT4EvaluationService.cs` - Service to call fine-tuned model API
- `OpenAIConfig.cs` - Updated with `fineTunedModelId` field
- `RealtimeConversationManager.cs` - System prompt updated for pronunciation focus
- `MedicalExamManager.cs` - New method `EvaluateWithFineTunedGPT4()` 

⚠️ **Next Steps:**
- Train fine-tuned model with your medical exam data
- Update `fineTunedModelId` in OpenAIConfig
- Integrate with TTS system for voice feedback
- Add logic to call `EvaluateWithFineTunedGPT4()` at evaluation trigger

---

## Step 1: Prepare Your Training Data

You mentioned having doctor documents and exam examples. Structure them as conversation + evaluation pairs:

```json
{
  "messages": [
    {
      "role": "system",
      "content": "You are a medical university examiner..."
    },
    {
      "role": "user",
      "content": "[EXAM CONVERSATION TRANSCRIPT]"
    },
    {
      "role": "assistant",
      "content": "{\"terminologie\": 4, \"verstaendlichkeit\": 3.5, \"aussprache\": 3, \"overallScore\": 75, \"generalFeedback\": \"...\"}"
    }
  ]
}
```

### Expected JSON Output Format

The fine-tuned model should return (see `FineTunedGPT4EvaluationService.EvaluationResponse`):

```json
{
  "terminologie": 4.0,
  "verstaendlichkeit": 3.5,
  "aussprache": 3.5,
  "overallScore": 75,
  "generalFeedback": "You demonstrated solid terminology knowledge but could improve clarity in your explanations. Your pronunciation was mostly clear with minor issues on 'Ösophagus'.",
  "terminologieFeedback": "Good use of medical terms, minor confusion on differential diagnosis",
  "verstaendlichkeitFeedback": "Clear structure but some hesitation when explaining findings",
  "ausspracheeFeedback": "Clear pronunciation overall, some difficulty with compound medical terms"
}
```

---

## Step 2: Create Fine-Tuned Model with OpenAI

### Using OpenAI CLI:

```bash
# Install OpenAI CLI
pip install --upgrade openai

# Prepare training file (JSONL format)
openai tools fine_tunes.prepare_data -f training_data.jsonl

# Create fine-tuned model
openai api fine_tunes.create \
  -t training_data_prepared.jsonl \
  -m gpt-4-turbo-2024-04-09

# Check status
openai api fine_tunes.list
openai api fine_tunes.get -i ft-<ID>
```

### Or Using OpenAI API:

```python
import openai

with open("training_data.jsonl", "rb") as f:
    response = openai.File.create(
        file=f,
        purpose="fine-tune"
    )

openai.FineTune.create(
    training_file=response["id"],
    model="gpt-4-turbo-2024-04-09"
)
```

### You'll Get a Model ID Like:
```
ft:gpt-4-turbo-2024-04-09:default:abc123:65def456
```

---

## Step 3: Update OpenAI Config

1. In Unity Inspector, select `OpenAIConfig` asset
2. Find the **Fine-Tuned Model** section
3. Paste your fine-tuned model ID:
   ```
   ft:gpt-4-turbo-2024-04-09:default:abc123:65def456
   ```
4. Or update the C# code directly if needed

---

## Step 4: Integrate TTS (Text-to-Speech)

### Option A: Use Existing ElevenLabs Integration

The project already has ElevenLabs support. Update `FineTunedGPT4EvaluationService.PlayFeedbackAsVoice()`:

```csharp
public void PlayFeedbackAsVoice(string feedbackText, System.Action onComplete = null)
{
    if (!ttsAudioSource)
    {
        Debug.LogWarning("[FineTunedGPT4] TTS AudioSource not assigned");
        onComplete?.Invoke();
        return;
    }

    // Call ElevenLabs API to convert feedback to speech
    StartCoroutine(ConvertToSpeech(feedbackText, ttsAudioSource, onComplete));
}

private IEnumerator ConvertToSpeech(string text, AudioSource audioSource, System.Action onComplete)
{
    // TODO: Implement ElevenLabs TTS call
    // Fetch audio from ElevenLabs API
    // Play via audioSource
    // Call onComplete when done
    
    yield return null;
    onComplete?.Invoke();
}
```

### Option B: Use OpenAI TTS

```csharp
private IEnumerator ConvertToSpeechOpenAI(string text, AudioSource audioSource, System.Action onComplete)
{
    using (UnityWebRequest request = UnityWebRequest.Post("https://api.openai.com/v1/audio/speech", "POST"))
    {
        var payload = new { model = "tts-1", input = text, voice = "nova" };
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload)));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Authorization", $"Bearer {openAIConfig.apiKey}");
        request.SetRequestHeader("Content-Type", "application/json");
        
        yield return request.SendWebRequest();
        
        if (request.result == UnityWebRequest.Result.Success)
        {
            AudioClip clip = WavUtility.ToAudioClip(request.downloadHandler.data);
            audioSource.clip = clip;
            audioSource.Play();
            yield return new WaitForSeconds(clip.length);
        }
        
        onComplete?.Invoke();
    }
}
```

---

## Step 5: Wire Up Evaluation Trigger

Currently, `RequestEvaluation()` in MedicalExamManager waits for Realtime AI feedback. Update it to call the fine-tuned service:

### Option A: Replace Current Flow
```csharp
public void RequestEvaluation()
{
    if (_evaluationRequested)
        return;
        
    _evaluationRequested = true;
    _examActive = false;
    
    Debug.Log("[MedicalExamManager] Evaluation requested - using fine-tuned GPT-4");
    
    // Directly call fine-tuned service (skip waiting for realtime feedback)
    EvaluateWithFineTunedGPT4();
}
```

### Option B: Keep Dual Approach
```csharp
public void RequestEvaluation()
{
    if (_evaluationRequested)
        return;
        
    _evaluationRequested = true;
    _examActive = false;
    
    // Still let Realtime AI speak feedback
    // But also queue fine-tuned evaluation to run after
    
    Invoke(nameof(EvaluateWithFineTunedGPT4), 3f); // After realtime finishes
}
```

---

## Step 6: System Prompts Reference

### Realtime AI (Conversation Only)
Located in `RealtimeConversationManager.cs`:
- Focuses on: pronunciation, hesitations, filler words, sentence clarity
- Does NOT evaluate: medical correctness, completeness, safety
- Outputs: Natural speech, hidden observations

### Fine-Tuned GPT-4 (Evaluation Only)
Located in `FineTunedGPT4EvaluationService.cs`:
- Focuses on: medical terminology accuracy, clinical reasoning, exam structure, critical omissions
- Uses: Realtime observations as SECONDARY input only
- Outputs: Structured JSON with 3 sliders + feedback

---

## Step 7: JSON Response Mapping

The `FineTunedGPT4EvaluationService.EvaluationResponse` class maps to your sliders:

| Field | UI Slider | Range | Notes |
|-------|-----------|-------|-------|
| `terminologie` | Terminologie | 1.0-5.0 | Medical term accuracy |
| `verstaendlichkeit` | Verständlichkeit | 1.0-5.0 | Clarity of communication |
| `aussprache` | Aussprache | 1.0-5.0 | Pronunciation (influenced by Realtime) |
| `overallScore` | Display only | 0-100 | Calculated score |
| `generalFeedback` | TTS + Text | String | What examiner would say |
| `terminologieFeedback` | Optional sub-feedback | String | Specific to terminology |
| `verstaendlichkeitFeedback` | Optional sub-feedback | String | Specific to clarity |
| `ausspracheeFeedback` | Optional sub-feedback | String | Specific to pronunciation |

---

## Testing Checklist

- [ ] Fine-tuned model created and trained
- [ ] `fineTunedModelId` updated in OpenAIConfig
- [ ] `FineTunedGPT4EvaluationService` assigned in MedicalExamManager Inspector
- [ ] TTS AudioSource assigned in FineTunedGPT4EvaluationService
- [ ] Test conversation → evaluation trigger → fine-tuned call succeeds
- [ ] JSON response parsed correctly
- [ ] Sliders show correct scores
- [ ] TTS feedback plays (or gracefully skips if not implemented)
- [ ] Results display in UI

---

## Troubleshooting

### "Model not found" error
- Check that `fineTunedModelId` is correctly formatted
- Verify model exists: `openai api fine_tunes.list`
- Ensure API key has access to fine-tuned models

### JSON parse error
- Fine-tuned model must return ONLY valid JSON (no preamble/explanation)
- Test model output: `curl -X POST ... -d '{"model": "ft:...", "messages": [...]}'`
- Add more training examples if model inconsistent

### Slow evaluation (~10-20s)
- Fine-tuning calls are slower than Realtime API
- Show "Doctor reviewing your exam..." loading indicator
- Consider async parsing in background

### TTS not playing
- Assign AudioSource in Inspector
- Implement ElevenLabs/OpenAI TTS integration
- Test with Unity's built-in AudioSource.clip playback first

---

## Next: Adding More Training Examples

As you gather more exam examples:

1. **Format** as conversation + evaluation JSON pairs
2. **Append** to training dataset JSONL file
3. **Retrain** fine-tuned model: `openai api fine_tunes.create -t file.jsonl -m gpt-4-turbo-2024-04-09`
4. **Update** `fineTunedModelId` with new model ID
5. **Test** in-app evaluation quality

Each iteration improves medical accuracy and consistency!

---

## Architecture Summary

```
[Conversation Phase]
User speaks → Realtime AI converses (observes pronunciation silently)

↓ [Eval Trigger]

[Evaluation Phase]
Transcript + Realtime observations → Fine-Tuned GPT-4 API
                                        ↓
                                   JSON Response
                                        ↓
                            TTS Voice Feedback + UI Sliders
```

Good luck with your medical training app! 🏥
