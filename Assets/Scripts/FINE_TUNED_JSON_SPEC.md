# Fine-Tuned GPT-4 Evaluation JSON Specification

## Expected Response Format

The fine-tuned GPT-4 model **MUST** return ONLY valid JSON (no preamble or explanation text).

### C# Data Structure
```csharp
[Serializable]
public class EvaluationResponse
{
    public float terminologie;                  // 1.0 to 5.0
    public float verstaendlichkeit;             // 1.0 to 5.0
    public float aussprache;                    // 1.0 to 5.0
    public int overallScore;                    // 0 to 100
    public string generalFeedback;              // Examiner feedback (TTS voice this)
    public string terminologieFeedback;         // Optional: specific feedback
    public string verstaendlichkeitFeedback;    // Optional: specific feedback
    public string ausspracheeFeedback;          // Optional: specific feedback
}
```

### JSON Format Examples

#### Example 1: Full Response
```json
{
  "terminologie": 3.5,
  "verstaendlichkeit": 3.8,
  "aussprache": 4.0,
  "overallScore": 72,
  "generalFeedback": "You demonstrated good communication skills with clear pronunciation. Your medical terminology was appropriate but could be more precise in differential diagnosis. Overall, a solid performance with room for improvement in clinical reasoning.",
  "terminologieFeedback": "You used appropriate medical terms overall. However, you confused 'Stenose' and 'Verschluss' which are clinically distinct. Practice these distinctions.",
  "verstaendlichkeitFeedback": "Your explanations were well-structured and easy to follow. Good use of systematic approach. Could improve clarity when describing complex pathophysiology.",
  "ausspracheeFeedback": "Excellent pronunciation of most medical terms. Minor difficulty with German compound words like 'Herzbeutelentzündung'. Overall very clear speech."
}
```

#### Example 2: Minimal Response (Required Fields Only)
```json
{
  "terminologie": 2.5,
  "verstaendlichkeit": 2.0,
  "aussprache": 3.0,
  "overallScore": 48,
  "generalFeedback": "Your response lacked medical accuracy and proper terminology. You missed several critical red flags and did not structure your examination appropriately. Focus on improving your clinical knowledge before the next attempt."
}
```

#### Example 3: High Performer
```json
{
  "terminologie": 4.8,
  "verstaendlichkeit": 4.7,
  "aussprache": 4.9,
  "overallScore": 94,
  "generalFeedback": "Excellent performance. You demonstrated comprehensive medical knowledge, clear communication, and systematic approach to patient examination. Your differential diagnosis was complete and well-reasoned. Minor: could have mentioned one additional test option. Overall, this is exam-ready performance.",
  "terminologieFeedback": "Outstanding use of precise medical terminology. Shows deep understanding of the subject matter.",
  "verstaendlichkeitFeedback": "Excellent structural clarity. Your explanations were logical and easy to follow.",
  "ausspracheeFeedback": "Excellent pronunciation throughout. No issues with any medical terms."
}
```

---

## Slider Mapping

### Scale: 1.0 - 5.0

| Score | UI Display | Color | Interpretation |
|-------|-----------|-------|-----------------|
| 1.0 - 1.5 | ⭕ | Red | Poor |
| 1.5 - 2.5 | ⭕⭕ | Orange | Below Average |
| 2.5 - 3.5 | ⭕⭕⭕ | Yellow | Average |
| 3.5 - 4.5 | ⭕⭕⭕⭕ | Light Green | Good |
| 4.5 - 5.0 | ⭕⭕⭕⭕⭕ | Green | Excellent |

---

## Overall Score Calculation

| overallScore | Grade | Status |
|--------------|-------|--------|
| 0 - 20 | F | Fail - Significant gaps |
| 20 - 40 | D | Fail - Below expectations |
| 40 - 60 | C | Pass - Adequate |
| 60 - 75 | B | Pass - Good |
| 75 - 90 | A | Pass - Very Good |
| 90 - 100 | A+ | Pass - Excellent |

---

## Training Data Format

When fine-tuning the model, format training data as JSONL (one JSON per line):

```jsonl
{"messages": [{"role": "system", "content": "You are a medical university examiner..."}, {"role": "user", "content": "EXAM SCENARIO: Chest Pain Presentation\n\nCONVERSATION:\nDoctor: Good morning, I'd like to examine a 55-year-old male with chest pain.\nExaminer: Please proceed with your anamnesis.\nDoctor: I would start by asking about the characteristics of the pain - onset, duration, location, radiation, severity, and associated symptoms.\nExaminer: Good approach. What red flags are you considering?\nDoctor: Given the patient's age and chest pain, I'm thinking about acute coronary syndrome first, then pulmonary embolism, aortic dissection...\n\nREALTIME OBSERVATIONS:\nPronunciation: clear\nHesitations: minimal, only one pause when listing differential\nFiller words: 'uh' used twice\nSentence clarity: very clear structure, used systematic approach"}, {"role": "assistant", "content": "{\"terminologie\": 4.2, \"verstaendlichkeit\": 4.5, \"aussprache\": 4.0, \"overallScore\": 85, \"generalFeedback\": \"You demonstrated excellent command of cardiac examination approach with systematic thinking about red flags. Your communication was clear and professional. Minor: could have mentioned specific vital sign targets for initial assessment.\", \"terminologieFeedback\": \"Excellent use of medical terminology. Correctly identified key differential diagnoses.\", \"verstaendlichkeitFeedback\": \"Clear, well-structured presentation with good logical flow.\", \"ausspracheeFeedback\": \"Clear pronunciation, minimal hesitation, professional delivery.\"}"}
```

---

## Parsing Instructions (C#)

The `FineTunedGPT4EvaluationService` does this automatically:

```csharp
// Receive string response from API
string responseText = request.downloadHandler.text;

// Parse API wrapper
JObject jsonResponse = JObject.Parse(responseText);

// Extract assistant message
string assistantMessage = jsonResponse["choices"][0]["message"]["content"].ToString().Trim();

// Deserialize to EvaluationResponse
EvaluationResponse evaluation = JsonConvert.DeserializeObject<EvaluationResponse>(assistantMessage);

// Use the evaluation
if (evaluation != null)
{
    // Update UI sliders
    uiSlider_terminologie.value = evaluation.terminologie;
    uiSlider_verstaendlichkeit.value = evaluation.verstaendlichkeit;
    uiSlider_aussprache.value = evaluation.aussprache;
    
    // Display feedback
    feedbackText.text = evaluation.generalFeedback;
    
    // Optional: display per-slider feedback
    if (!string.IsNullOrEmpty(evaluation.terminologieFeedback))
        Debug.Log("Terminology: " + evaluation.terminologieFeedback);
}
```

---

## API Request Format

### Full HTTP Request

```http
POST https://api.openai.com/v1/chat/completions
Authorization: Bearer sk-proj-YOUR_API_KEY
Content-Type: application/json

{
  "model": "ft:gpt-4-turbo-2024-04-09:default:your-id",
  "messages": [
    {
      "role": "system",
      "content": "You are a medical university examiner..."
    },
    {
      "role": "user",
      "content": "Please evaluate this exam performance:\n\nSCENARIO: [name]\n\nTRANSCRIPT: [full conversation]\n\nOBSERVATIONS: [realtime speech data]\n\nReturn JSON only."
    }
  ],
  "temperature": 0.7,
  "max_tokens": 1500
}
```

### cURL Example

```bash
curl -X POST https://api.openai.com/v1/chat/completions \
  -H "Authorization: Bearer sk-proj-YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "ft:gpt-4-turbo-2024-04-09:default:your-id",
    "messages": [
      {
        "role": "system",
        "content": "You are a medical examiner. Return JSON only."
      },
      {
        "role": "user",
        "content": "Evaluate this conversation: [conversation text]"
      }
    ]
  }'
```

---

## Error Responses

### Model Not Found
```json
{
  "error": {
    "message": "The model 'ft:gpt-4-turbo-2024-04-09:default:invalid-id' does not exist",
    "type": "invalid_request_error"
  }
}
```
**Fix:** Update `fineTunedModelId` in OpenAIConfig with correct ID

### Invalid API Key
```json
{
  "error": {
    "message": "Incorrect API key provided. You passed: sk-proj-xxxxx",
    "type": "authentication_error"
  }
}
```
**Fix:** Verify API key in OpenAIConfig

### Rate Limited
```json
{
  "error": {
    "message": "Rate limit exceeded. Retry after 30 seconds.",
    "type": "server_error"
  }
}
```
**Fix:** Add retry logic with exponential backoff

### Invalid JSON Response
```
Response: "Let me evaluate this exam..."
```
**Problem:** Model included preamble text

**Fix:** Ensure system prompt says "Return ONLY valid JSON" (no other text)

---

## Field Constraints

| Field | Type | Range | Required | Notes |
|-------|------|-------|----------|-------|
| `terminologie` | float | 1.0 - 5.0 | Yes | Medical terminology accuracy |
| `verstaendlichkeit` | float | 1.0 - 5.0 | Yes | Clarity of communication |
| `aussprache` | float | 1.0 - 5.0 | Yes | Pronunciation quality |
| `overallScore` | int | 0 - 100 | Yes | Weighted overall score |
| `generalFeedback` | string | 50-500 chars | Yes | Main examiner feedback (TTS) |
| `terminologieFeedback` | string | Any | No | Optional specific feedback |
| `verstaendlichkeitFeedback` | string | Any | No | Optional specific feedback |
| `ausspracheeFeedback` | string | Any | No | Optional specific feedback |

---

## Validation Rules

The C# code validates:

```csharp
// Range checks
if (evaluation.terminologie < 1.0 || evaluation.terminologie > 5.0)
    throw new ArgumentException("terminologie out of range");

if (evaluation.overallScore < 0 || evaluation.overallScore > 100)
    throw new ArgumentException("overallScore out of range");

// Required field checks
if (string.IsNullOrEmpty(evaluation.generalFeedback))
    throw new ArgumentException("generalFeedback is required");
```

---

## Example: Doctor-to-Doctor Exam

### Input Conversation
```
Scenario: Cardiac Auscultation
Duration: 3 minutes

Examiner: Please present this patient case.
Student: A 65-year-old male with known hypertension and diabetes presents with 
shortness of breath for 2 days. Exertion-induced initially, now at rest.

Examiner: What are your differential diagnoses?
Student: Given the patient's history... uh... I'm thinking about acute heart failure,
acute coronary syndrome, and also... pulmonary embolism.

Examiner: What would be your approach?
Student: I would start with physical examination, particularly auscultation to look for
rales, checking for signs of pulmonary edema and heart failure. I would also assess
for any irregular heartbeats.

Examiner: Good. Any red flags?
Student: Yes, definitely chest pain with exertion could suggest ACS, and acute onset
at rest is concerning for PE. The combination with risk factors for both conditions
requires rapid assessment.
```

### Output JSON
```json
{
  "terminologie": 4.0,
  "verstaendlichkeit": 3.8,
  "aussprache": 4.1,
  "overallScore": 79,
  "generalFeedback": "You demonstrated solid clinical knowledge with appropriate differential diagnoses. Your systematic approach to history and examination was well-structured. You correctly identified red flags. Some minor hesitation when listing diagnoses, but recovered well. Overall good performance.",
  "terminologieFeedback": "Good use of medical terminology. Correctly used 'rales', 'pulmonary edema', 'auscultation'. However, could have been more specific about 'irregular heartbeats' (mention 'arrhythmia' or 'atrial fibrillation' specifically).",
  "verstaendlichkeitFeedback": "Clear explanation of your reasoning. Logical flow from symptoms to differentials. One minor hesitation ('uh') which slightly interrupted the flow, but overall very understandable.",
  "ausspracheeFeedback": "Very clear pronunciation throughout. Slight pause before differential list but otherwise fluent delivery."
}
```

---

## Common Mistakes to Avoid

### ❌ Don't
- Return text before/after JSON: `"Let me evaluate: { ... }"`
- Use numeric strings: `"terminologie": "4.0"` (should be `4.0`)
- Omit required fields (model will fail to deserialize)
- Use invalid scale values: `terminologie: 6.0` (should be 1-5)
- Have mismatched closing braces/brackets in JSON

### ✅ Do
- Return ONLY valid JSON: `{ ... }`
- Use proper numeric types: `"terminologie": 4.0`
- Include all required fields
- Keep values within specified ranges
- Validate JSON before sending (paste into [jsonlint.com](https://jsonlint.com))

---

## Testing Your JSON Response

1. **Validate JSON syntax:**
   ```bash
   echo '{"terminologie": 4.0, ...}' | jq .
   ```

2. **Deserialize in C#:**
   ```csharp
   var response = JsonConvert.DeserializeObject<EvaluationResponse>(jsonString);
   Debug.Log($"Score: {response.overallScore}");
   ```

3. **Check ranges:**
   ```csharp
   assert(response.terminologie >= 1 && response.terminologie <= 5);
   assert(response.overallScore >= 0 && response.overallScore <= 100);
   ```

---

## FAQ

**Q: What if scores should be very specific (e.g., 3.33)?**  
A: Use floats. `"terminologie": 3.33` will deserialize correctly.

**Q: Should I round overallScore to int?**  
A: Yes, it's defined as `int overallScore` in the C# class.

**Q: Can I include extra fields?**  
A: Yes, JSON deserializer will ignore unknown fields.

**Q: What if a feedback field is null?**  
A: Set it to empty string `""` or omit if truly optional.

**Q: How do I ensure consistent formatting?**  
A: Train fine-tuned model with examples using this exact JSON format.

---

This specification ensures reliable parsing and consistent evaluation delivery to your users! 🎯
